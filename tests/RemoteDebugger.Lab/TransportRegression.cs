using System.IO;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task LoopbackTransportAsync()
    {
        string root = Path.Combine(output, "transport-agent");
        Directory.CreateDirectory(root);
        RemoteClient? client = null;
        try
        {
            product = loopbackAgent = LaunchLoopbackProduct(true, root);
            await WaitUiAsync();
            await CliAsync(["pair", "--host", "127.0.0.1", "--data-root", root], await WaitPairingCodeAsync());
            string hash = await HashFileAsync(application);
            client = new RemoteClient(RemoteClient.Load().Connection, hash);
            await using (var channel = await client.OpenTransportAsync(stop.Token))
            {
                foreach (string operation in new[] { "update.snapshot", "status" })
                {
                    var request = new Request(Guid.NewGuid().ToString(), client.Connection.Token, operation,
                        Json.Element(new { versionOnly = true }), BinarySha256: hash, KeepAlive: true);
                    await Wire.WriteAsync(channel, request, stop.Token);
                    var reply = await Wire.ReadAsync<Reply>(channel, stop.Token);
                    RemoteClient.Require(reply);
                    if (!reply.KeepAlive || reply.Id != request.Id) throw new IOException("Command connection was not reusable.");
                }
                await Wire.WriteAsync(channel, new Request(Guid.NewGuid().ToString(), "invalid-token", "status",
                    Json.Element(new { }), BinarySha256: hash, KeepAlive: true), stop.Token);
                var denied = await Wire.ReadAsync<Reply>(channel, stop.Token);
                if (denied.Ok || denied.KeepAlive) throw new IOException("Reused connection bypassed per-request authentication.");
            }
            Pass("transport.command_reuse", "Two authenticated commands share one TLS connection; a changed token is rejected on that connection");

            await ProbeAuditRegressionsAsync();

            // Seed the already-authorized transfer state; this checks transport,
            // resumption and authentication without staging or installing a binary.
            string source = Path.Combine(output, "transfer-32MiB.bin");
            string transaction = Guid.NewGuid().ToString("N");
            string transfers = Path.Combine(root, "updates");
            Directory.CreateDirectory(transfers);
            string partial = Path.Combine(transfers, transaction + ".partial.exe");
            long size = new FileInfo(source).Length;
            string expected = await HashFileAsync(source);
            var candidate = new ExecutableSnapshot(source, size, expected, "99.0.0", new string('A', 64));
            await File.WriteAllTextAsync(Path.Combine(transfers, transaction + ".json"),
                JsonSerializer.Serialize(new AgentUpdateTransfer(candidate, DateTimeOffset.UtcNow), Json.Options), stop.Token);
            byte[] prefix = new byte[BulkTransfer.WindowSize];
            await using (var input = File.OpenRead(source)) await input.ReadExactlyAsync(prefix, stop.Token);
            await File.WriteAllBytesAsync(partial, prefix, stop.Token);
            var unauthorized = new RemoteClient(client.Connection with { Token = "invalid-token" }, hash);
            var rejected = await unauthorized.CallAsync("update.transfer", new { transactionId = transaction }, stop.Token);
            if (rejected.Ok || new FileInfo(partial).Length != prefix.Length) throw new IOException("Unauthenticated update stream was accepted.");
            await using (var input = File.OpenRead(source))
                await client.TransferUpdateAsync(transaction, input, size, null, stop.Token);
            if (await HashFileAsync(partial) != expected) throw new IOException("Streamed update changed the resumed payload.");
            Pass("transport.update_resume", "Authenticated update streaming resumes a 32 MiB payload exactly; invalid tokens cannot modify it",
                new { resumedOffset = prefix.Length, size, sha256 = expected, installationTested = false });

            CaptureDesktop("transport-agent.png");
        }
        finally
        {
            if (client != null) await client.CloseHeartbeatChannelAsync();
            await CleanupLoopbackProcessesAsync();
        }
        await FinishAsync();
    }
}

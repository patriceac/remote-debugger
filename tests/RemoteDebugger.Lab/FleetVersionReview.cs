using System.Diagnostics;
using System.IO;
using RemoteDebugger.Core;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task LoopbackVersionsAsync()
    {
        string root = Path.Combine(output, "version-agent");
        Directory.CreateDirectory(root);
        try
        {
            product = loopbackAgent = LaunchLoopbackProduct(true, root);
            await WaitUiAsync();
            string code = await WaitPairingCodeAsync();
            await CliAsync(["pair", "--host", "127.0.0.1", "--data-root", root], code);
            string hash = await HashFileAsync(application);
            var client = new RemoteClient(RemoteClient.Load().Connection, hash);
            var full = RemoteClient.Require(await client.CallAsync("update.snapshot", ct: stop.Token));
            var light = RemoteClient.Require(await client.CallAsync("update.snapshot", new { versionOnly = true }, stop.Token));
            bool identityMatches = full.GetProperty("agent").GetRawText() == light.GetProperty("agent").GetRawText()
                && light.GetProperty("agent").Str("sha256") == hash;
            bool lightweight = !light.TryGetProperty("platform", out _) && !light.TryGetProperty("transaction", out _)
                && light.TryGetProperty("wakeAdapters", out _);
            if (!identityMatches || !lightweight || !full.TryGetProperty("platform", out _))
                throw new IOException("Version-only response changed executable identity or retained the full platform audit.");
            Pass("versions.compatibility", "Full snapshots remain available; version-only requests return the same signed identity without platform or transaction checks", new { identityMatches, lightweight, hash });

            var watch = Stopwatch.StartNew();
            var warm = RemoteClient.Require(await client.CallAsync("update.snapshot", new { versionOnly = true }, stop.Token, seconds: 5));
            watch.Stop();
            if (warm.GetProperty("agent").GetRawText() != light.GetProperty("agent").GetRawText() || watch.Elapsed > TimeSpan.FromSeconds(2))
                throw new IOException("Cached version check exceeded two seconds or changed identity: " + watch.Elapsed);
            Pass("versions.cached_roundtrip", "A repeated version request completes within two seconds", new { milliseconds = watch.Elapsed.TotalMilliseconds });

            var unauthenticated = new RemoteClient(client.Connection with { Token = "" }, hash);
            var denied = await unauthenticated.CallAsync("update.snapshot", new { versionOnly = true }, stop.Token, seconds: 5);
            if (denied.Ok) throw new IOException("The lightweight version path bypassed authentication.");
            Pass("versions.authentication", "Version-only snapshots still require an authenticated session", new { denied.Error });
            CaptureDesktop("versions-agent.png");
        }
        finally { await CleanupLoopbackProcessesAsync(); }
        await FinishAsync();
    }
}

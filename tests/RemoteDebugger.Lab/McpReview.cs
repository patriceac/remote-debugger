using System.Diagnostics;
using System.IO;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task McpReviewAsync(string package)
    {
        string connection = Path.Combine(output, "mcp.connection");
        async Task Probe(string mode)
        {
            var start = new ProcessStartInfo(Path.Combine(package, "node.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string arg in new[] { Path.Combine(package, "app", "test", "probe.mjs"), application, connection, output, mode }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("MCP probe did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(stop.Token); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            string errors = await stderr; await stdout;
            var result = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(Path.Combine(output, "mcp-" + mode + ".json"), stop.Token));
            if (process.ExitCode != 0 || !result.GetProperty("passed").GetBoolean()) throw new IOException("MCP " + mode + " failed: " + errors + Json.Text(result));
            Pass("mcp." + mode, "The packaged MCP stdio adapter passes " + mode + " checks against the signed Release CLI", result);
        }
        try
        {
            await Probe("disconnected");
            string root = Path.Combine(output, "mcp-agent");
            Directory.CreateDirectory(root);
            product = loopbackAgent = LaunchLoopbackProduct(true, root);
            await WaitUiAsync();
            await CliAsync(["pair", "--host", "127.0.0.1", "--data-root", root, "--connection", connection], await WaitPairingCodeAsync());
            string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RemoteDebugger", "RemoteDebugger.exe");
            bool provisioned = string.Equals(application, installed, StringComparison.OrdinalIgnoreCase);
            if (provisioned)
            {
                JsonElement maintenance = default;
                DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
                do
                {
                    var reply = await CliAsync(["call", "--request", "-", "--connection", connection],
                        Json.Text(new { operation = "maintenance.status", args = new { } }), requireSuccess: false);
                    if (!reply.GetProperty("ok").GetBoolean())
                        throw new IOException("Administrator maintenance status failed: " + reply.GetRawText());
                    maintenance = reply.GetProperty("data");
                    if (maintenance.GetProperty("active").GetBoolean()) break;
                    await Task.Delay(500, stop.Token);
                } while (DateTimeOffset.UtcNow < deadline);
                if (!maintenance.GetProperty("active").GetBoolean())
                    throw new IOException("Administrator maintenance did not become active: " + maintenance.GetRawText());
                Pass("mcp.admin_ready", "The installed agent opened its administrator maintenance lease", maintenance);
            }
            await Probe(provisioned ? "connected-admin" : "connected");
        }
        finally { await CleanupLoopbackProcessesAsync(); }
        await FinishAsync();
    }
}

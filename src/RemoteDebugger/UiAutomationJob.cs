using System.Diagnostics;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

// An unresponsive accessibility provider must not pin an agent thread indefinitely.
public static class UiAutomationJob
{
    private sealed record Job(string Operation, JsonElement Args, DateTimeOffset Expires);
    public static async Task<object> RunAsync(string operation, JsonElement args, string root, CancellationToken ct)
    {
        string dir = Path.Combine(root, "ui-jobs"); Directory.CreateDirectory(dir); string path = Path.Combine(dir, Guid.NewGuid().ToString("N"));
        Vault.Save(path, JsonSerializer.SerializeToUtf8Bytes(new Job(operation, args, DateTimeOffset.UtcNow.AddMinutes(5)), Json.Options));
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true }; psi.ArgumentList.Add("--ui-job"); psi.ArgumentList.Add(path);
        try
        {
            using var helper = Process.Start(psi) ?? throw new IOException("UI Automation helper did not start.");
            try { await helper.WaitForExitAsync(ct); }
            catch (OperationCanceledException) { if (!helper.HasExited) helper.Kill(true); await helper.WaitForExitAsync(CancellationToken.None); throw; }
            if (!File.Exists(path + ".result")) throw new IOException("UI Automation helper exited without a result.");
            return RemoteClient.Require(JsonSerializer.Deserialize<Reply>(Vault.Read(path + ".result"), Json.Options)!);
        }
        finally { foreach (string file in new[] { path, path + ".result" }) if (File.Exists(file)) File.Delete(file); }
    }
    public static int Execute(string path)
    {
        var job = JsonSerializer.Deserialize<Job>(Vault.Read(path), Json.Options)!; Reply result;
        try
        {
            if (job.Expires < DateTimeOffset.UtcNow || job.Expires > DateTimeOffset.UtcNow.AddMinutes(6)) throw new InvalidOperationException("Expired UI job.");
            object data;
            switch (job.Operation)
            {
                case "ui.inspect": data = Native.Inspect(job.Args.Int("pid")); break;
                case "ui.click": Native.Click(job.Args.Int("pid"), job.Args.Str("automationId"), job.Args.Str("name")); data = new { clicked = true }; break;
                default: throw new ArgumentException("Unsupported UI job.");
            }
            result = Reply.Success("ui", data);
        }
        catch (Exception ex) { result = Reply.Failure("ui", "ui_automation_failed", ex.Message); }
        Vault.Save(path + ".result", JsonSerializer.SerializeToUtf8Bytes(result, Json.Options)); return result.Ok ? 0 : 1;
    }
}

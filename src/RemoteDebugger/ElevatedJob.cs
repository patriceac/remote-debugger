using System.Diagnostics;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

// Explicit per-operation UAC helper. No service, startup task, or persistent elevated listener.
public static class ElevatedJob
{
    private sealed record Job(string File, string[] Arguments, DateTimeOffset Expires);
    public static async Task<object> RunAsync(string file, string[] args, string root, CancellationToken ct)
    {
        string dir = Path.Combine(root, "elevation"); Directory.CreateDirectory(dir);
        string job = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".job");
        Vault.Save(job, JsonSerializer.SerializeToUtf8Bytes(new Job(file, args, DateTimeOffset.UtcNow.AddMinutes(4)), Json.Options));
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
            psi.ArgumentList.Add("--elevated-job"); psi.ArgumentList.Add(job);
            using var helper = Process.Start(psi) ?? throw new IOException("UAC helper did not start.");
            try { await helper.WaitForExitAsync(ct); }
            catch (OperationCanceledException) { File.WriteAllText(job + ".cancel", "cancel"); throw; }
            if (!File.Exists(job + ".result")) throw new IOException("Elevation declined, different Windows account, or helper failed.");
            var reply = JsonSerializer.Deserialize<Reply>(Vault.Read(job + ".result"), Json.Options)!;
            return RemoteClient.Require(reply);
        }
        finally
        {
            // The helper owns files once launched; keep cancellation visible until it exits.
            if (File.Exists(job + ".result")) foreach (string p in new[] { job, job + ".result", job + ".cancel" }) if (File.Exists(p)) File.Delete(p);
        }
    }
    public static async Task<int> ExecuteAsync(string path)
    {
        if (!Native.IsElevated()) return 3;
        var job = JsonSerializer.Deserialize<Job>(Vault.Read(path), Json.Options)!;
        if (job.Expires < DateTimeOffset.UtcNow || job.Expires > DateTimeOffset.UtcNow.AddMinutes(5)) return 3;
        using var cts = new CancellationTokenSource(job.Expires - DateTimeOffset.UtcNow);
        var watch = Task.Run(async () => { while (!cts.IsCancellationRequested) { if (File.Exists(path + ".cancel")) { cts.Cancel(); break; } await Task.Delay(250); } });
        Reply reply;
        try { reply = Reply.Success("elevated", await Operations.RunAsync(job.File, job.Arguments, cts.Token)); }
        catch (Exception ex) { reply = Reply.Failure("elevated", "elevated_operation_failed", ex.Message); }
        finally { cts.Cancel(); }
        Vault.Save(path + ".result", JsonSerializer.SerializeToUtf8Bytes(reply, Json.Options)); await watch; return reply.Ok ? 0 : 1;
    }
}

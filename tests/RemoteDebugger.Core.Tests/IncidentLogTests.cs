using RemoteDebugger.Core;
using Xunit;

public sealed class IncidentLogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rd-diagnostics-" + Guid.NewGuid().ToString("N"));
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static readonly SupportDiagnosticContext Context = new("test-pc", "controller", "1.0", "session-1");

    [Fact]
    public void RepeatedFaultsShareOneReportAndExceptionMessagesCannotLeakClipboardOrPasswords()
    {
        var clock = new Clock(); var log = new IncidentLog(root, clock);
        log.Record("heartbeat", Context);
        log.Record("input_failed", Context, "input_blocked", new Exception("secret clipboard password"), true);
        clock.Now = clock.Now.AddSeconds(10);
        log.Record("input_failed", Context, "input_blocked", new Exception("secret clipboard password"), true);
        var report = Assert.Single(log.Read());
        Assert.Equal(2, report.Occurrences);
        Assert.Contains(report.History, entry => entry.Name == "heartbeat");
        Assert.All(Directory.GetFiles(log.DirectoryPath), path => Assert.DoesNotContain("secret clipboard password", File.ReadAllText(path)));
        File.WriteAllText(Path.Combine(log.DirectoryPath, $"{clock.Now:yyyyMMdd}-incident-{Guid.NewGuid():N}.json"), "interrupted JSON");
        Assert.Single(log.Read());
    }

    [Fact]
    public void RetentionRemovesOnlyOwnedOldReportsAndPlannedRestartsAreNotIncidents()
    {
        var clock = new Clock(); var log = new IncidentLog(root, clock);
        log.Record("update_failed", Context, incident: true);
        File.WriteAllText(Path.Combine(log.DirectoryPath, "keep-user-file.txt"), "keep");
        clock.Now = clock.Now.AddDays(30);
        Assert.Empty(log.Read());
        Assert.True(File.Exists(Path.Combine(log.DirectoryPath, "keep-user-file.txt")));
        log.BeginRun(Context); log.PlannedExit(Context);
        new IncidentLog(root, clock).BeginRun(Context);
        Assert.Empty(log.Read());
        log.PlannedExit(Context);
        log.CancelPlannedExit(Context);
        new IncidentLog(root, clock).BeginRun(Context);
        Assert.Equal("previous_process_ended_unexpectedly", Assert.Single(log.Read()).Failure.Name);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

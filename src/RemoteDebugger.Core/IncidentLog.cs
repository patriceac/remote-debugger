using System.Globalization;
using System.Text.Json;

namespace RemoteDebugger.Core;

public sealed record SupportDiagnosticContext(string Machine, string Role, string Version, string Session = "",
    string Route = "", bool Connected = false, bool FreshFrame = false, bool InputEnabled = false,
    bool InputSuspended = false, bool Focused = false, double? FrameAgeMs = null);
public sealed record DiagnosticEvent(DateTimeOffset At, string Name, SupportDiagnosticContext Context,
    string? ErrorCode = null, string? ExceptionType = null, int? HResult = null, string? StackTrace = null);
public sealed record IncidentReport(string Id, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, int Occurrences,
    DiagnosticEvent Failure, DiagnosticEvent[] History);

/// <summary>Local, payload-free diagnostics. No clipboard, command arguments, credentials or screenshots enter this API.</summary>
public sealed class IncidentLog
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private readonly string directory, writerId = Guid.NewGuid().ToString("N");
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly Queue<DiagnosticEvent> recent = new();
    private readonly Dictionary<string, IncidentReport> incidents = new();
    private DateOnly lastPruned;
    public string DirectoryPath => directory;

    public IncidentLog(string dataRoot, TimeProvider? clock = null)
    {
        directory = Path.Combine(dataRoot, "support-reports"); time = clock ?? TimeProvider.System;
        TryIo(() => { Directory.CreateDirectory(directory); Prune(); });
    }

    public void Record(string name, SupportDiagnosticContext context, string? errorCode = null, Exception? exception = null, bool incident = false)
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            var entry = new DiagnosticEvent(now, name, context, errorCode, exception?.GetType().Name, exception?.HResult, exception?.StackTrace);
            recent.Enqueue(entry);
            while (recent.Count > 256 || recent.TryPeek(out var oldest) && now - oldest.At > TimeSpan.FromMinutes(1)) recent.Dequeue();
            TryIo(() =>
            {
                Prune();
                File.AppendAllText(Path.Combine(directory, $"{now:yyyyMMdd}-events-{writerId}.jsonl"), JsonSerializer.Serialize(entry, Json.Options) + "\n");
                if (!incident) return;
                string key = string.Join('|', context.Machine, context.Session, name, errorCode, entry.ExceptionType);
                var report = incidents.TryGetValue(key, out var previous) && now - previous.LastSeen < TimeSpan.FromMinutes(2) && now - previous.FirstSeen < Retention
                    ? previous with { LastSeen = now, Occurrences = previous.Occurrences + 1 }
                    : new IncidentReport(Guid.NewGuid().ToString("N"), now, now, 1, entry, recent.ToArray());
                incidents[key] = report;
                foreach (string old in incidents.Where(item => now - item.Value.LastSeen >= TimeSpan.FromMinutes(2)).Select(item => item.Key).ToArray()) incidents.Remove(old);
                string path = ReportPath(report);
                File.WriteAllText(path + ".new", JsonSerializer.Serialize(report, Json.Options));
                File.Move(path + ".new", path, true);
            });
        }
    }

    public void BeginRun(SupportDiagnosticContext context)
    {
        TryIo(() =>
        {
            string marker = Path.Combine(directory, "running.json");
            if (File.Exists(marker))
            {
                var previous = JsonSerializer.Deserialize<RunMarker>(File.ReadAllText(marker), Json.Options);
                if (previous != null && time.GetUtcNow() - previous.At <= Retention)
                    Record(previous.Planned ? "returned_after_planned_restart" : "previous_process_ended_unexpectedly", previous.Context, incident: !previous.Planned);
            }
            File.WriteAllText(marker, JsonSerializer.Serialize(new RunMarker(time.GetUtcNow(), context, false), Json.Options));
        });
        Record("application_started", context);
    }

    public void PlannedExit(SupportDiagnosticContext context) => TryIo(() =>
        File.WriteAllText(Path.Combine(directory, "running.json"), JsonSerializer.Serialize(new RunMarker(time.GetUtcNow(), context, true), Json.Options)));

    public void CancelPlannedExit(SupportDiagnosticContext context) => TryIo(() =>
        File.WriteAllText(Path.Combine(directory, "running.json"), JsonSerializer.Serialize(new RunMarker(time.GetUtcNow(), context, false), Json.Options)));

    public void EndRun(SupportDiagnosticContext context)
    {
        Record("application_closed", context);
        TryIo(() => File.Delete(Path.Combine(directory, "running.json")));
    }

    public IncidentReport[] Read(string? id = null)
    {
        if (id != null && !Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid incident ID.");
        lock (gate)
        {
            Prune();
            if (!Directory.Exists(directory)) return [];
            return Directory.EnumerateFiles(directory, "*-incident-*.json")
                .Where(path => id == null || Path.GetFileName(path).EndsWith("-" + id + ".json", StringComparison.Ordinal))
                .Select(ReadReport)
                .OfType<IncidentReport>().Where(report => report.LastSeen >= time.GetUtcNow() - Retention)
                .OrderByDescending(report => report.LastSeen).Take(100).ToArray();
        }
    }

    public void Prune()
    {
        var now = time.GetUtcNow(); var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (lastPruned == today || !Directory.Exists(directory)) return;
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            string name = Path.GetFileName(path);
            if (name.Length < 8 || !DateTime.TryParseExact(name[..8], "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) continue;
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^\d{8}-(events-[a-f0-9]{32}\.jsonl|incident-[a-f0-9]{32}\.json(?:\.new)?)$")) continue;
            // Retain the current date and preceding 29 UTC dates. Never keep a
            // full extra day's sensitive diagnostic history past the retention window.
            if (date <= (now - Retention).UtcDateTime.Date) TryIo(() => File.Delete(path));
        }
        lastPruned = today;
    }

    private string ReportPath(IncidentReport report) => Path.Combine(directory, $"{report.FirstSeen:yyyyMMdd}-incident-{report.Id}.json");
    private static IncidentReport? ReadReport(string path)
    {
        IncidentReport? report = null;
        TryIo(() => report = JsonSerializer.Deserialize<IncidentReport>(File.ReadAllText(path), Json.Options));
        return report;
    }
    private static void TryIo(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { System.Diagnostics.Trace.WriteLine("Diagnostic storage unavailable: " + ex.GetType().Name); }
    }
    private sealed record RunMarker(DateTimeOffset At, SupportDiagnosticContext Context, bool Planned);
}

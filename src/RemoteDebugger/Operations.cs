using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

public sealed class Operations
{
    private static readonly string Version = typeof(Operations).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    public string Root { get; }
    public string Workspace => Path.Combine(Root, "workspace");
    private string Transfers => Path.Combine(Root, "transfers");
    private readonly object historyLock = new();
    private readonly SemaphoreSlim transferLock = new(1);
    private readonly ConcurrentDictionary<int, string> started = new();
    public MaintenanceSession Maintenance { get; }
    public Operations(string root) { Root = root; Maintenance = new(root); Directory.CreateDirectory(Workspace); Directory.CreateDirectory(Transfers); }
    public void Record(string id, string operation, DateTimeOffset start, bool ok, string? error, JsonElement data)
    {
        lock (historyLock)
        {
            string path = Path.Combine(Root, "history.jsonl");
            if (File.Exists(path) && new FileInfo(path).Length > 8 * 1024 * 1024) File.Move(path, path + ".previous", true);
            JsonElement binary = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("binary", out var b) ? b : operation == "upload.commit" ? data : Json.Element(null);
            object? versionEvidence = binary.ValueKind == JsonValueKind.Object ? new { file = Path.GetFileName(binary.Str("path")), sha256 = binary.Str("sha256"), fileVersion = binary.Str("fileVersion"), size = binary.Long("size"), pid = data.Int("pid") } : null;
            File.AppendAllText(path, Json.Text(new { id, operation, start, end = DateTimeOffset.UtcNow, ok, error, toolVersion = Version, versionEvidence }) + Environment.NewLine);
        }
    }
    public async Task<object?> ExecuteAsync(string op, JsonElement a, CancellationToken ct)
    {
        switch (op)
        {
            case "status": return new { machine = Environment.MachineName, user = Environment.UserName, version = Version, os = Environment.OSVersion.VersionString, workspace = Workspace, elevated = Native.IsElevated(), processId = Environment.ProcessId, agentBinarySha256 = ExecutableIdentity.Sha256 };
            case "history": lock (historyLock) return File.Exists(Path.Combine(Root, "history.jsonl")) ? File.ReadLines(Path.Combine(Root, "history.jsonl")).TakeLast(200).Select(x => JsonSerializer.Deserialize<JsonElement>(x)).ToArray() : [];
            case "upload.begin": case "upload.chunk": case "upload.commit": case "upload.status": case "upload.abort":
                await transferLock.WaitAsync(ct); try { return await UploadAsync(op, a, ct); } finally { transferLock.Release(); }
            case "file.info": return await FileInfoAsync(Resolve(a.Str("path")), ct);
            case "file.read":
                await using (var f = File.Open(Resolve(a.Str("path")), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long offset = a.Long("offset"); if (offset < 0 || offset > f.Length) throw new ArgumentException("Invalid offset."); f.Position = offset;
                    byte[] data = new byte[256 * 1024]; int n = await f.ReadAsync(data, ct); return new { offset, data = Convert.ToBase64String(data, 0, n) };
                }
            case "files": return new DirectoryInfo(string.IsNullOrWhiteSpace(a.Str("path")) ? Workspace : Resolve(a.Str("path"))).EnumerateFileSystemInfos().Take(1000).Select(x => new { x.Name, path = x.FullName, directory = x.Attributes.HasFlag(FileAttributes.Directory), size = x is FileInfo file ? (long?)file.Length : null, modifiedUtc = x.LastWriteTimeUtc }).ToArray();
            case "processes": return await ResourceSampling.ProcessesAsync(ct);
            case "process.info": return await ProcessInfoAsync(a.Int("pid"), ct);
            case "start":
            {
                string path = Resolve(a.Str("path")); var info = await FileInfoAsync(path, ct);
                var psi = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! };
                foreach (string arg in a.Strings("arguments")) psi.ArgumentList.Add(arg);
                var p = Process.Start(psi) ?? throw new IOException("Windows did not start the application."); started[p.Id] = path;
                return new { pid = p.Id, binary = info, startedUtc = p.StartTime.ToUniversalTime() };
            }
            case "stop":
            {
                using var p = Process.GetProcessById(a.Int("pid")); if (p.Id == Environment.ProcessId) throw new ArgumentException("Use the agent window to disconnect.");
                if (a.Str("mode") == "force") p.Kill(true);
                else { if (!p.CloseMainWindow()) throw new InvalidOperationException("No closeable window. Use mode=force explicitly."); }
                await p.WaitForExitAsync(ct); started.TryRemove(p.Id, out _); return new { exited = true, p.ExitCode };
            }
            case "restart":
            {
                int pid = a.Int("pid"); if (!started.TryGetValue(pid, out var path)) throw new InvalidOperationException("Restart requires a process launched by this agent; use stop and start for other processes.");
                await ExecuteAsync("stop", Json.Element(new { pid, mode = a.Str("mode") }), ct);
                return await ExecuteAsync("start", Json.Element(new { path, arguments = a.Strings("arguments") }), ct);
            }
            case "windows": return Native.Windows(a.Int("pid"));
            case "ui.inspect": case "ui.click": return await UiAutomationJob.RunAsync(op, a, Root, ct);
            case "ui.text": Native.TypeText(a.Int("pid"), a.Str("text")); return new { typed = true };
            case "ui.key": Native.Key(a.Int("pid"), a.Str("key")); return new { sent = true };
            case "ui.mouse": Native.Mouse(a.Int("pid"), a.Int("x"), a.Int("y")); return new { clicked = true };
            case "ui.input": Native.HandleInput(a); return new { sent = true };
            case "monitors": return DesktopCapture.Monitors();
            case "screenshot": return DesktopCapture.Capture(a.Int("monitor"), a.Int("maxWidth"), a.Int("quality", 85));
            case "debug.attach": return await Task.Run(() => Native.Debug(a.Int("pid"), Math.Clamp(a.Int("seconds", 3), 1, 30), ct), ct);
            case "debug.dump":
            {
                string path = Safety.UnderRoot(Workspace, "diagnostics/" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + a.Int("pid") + ".dmp"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Native.Dump(a.Int("pid"), path); return await FileInfoAsync(path, ct);
            }
            case "command": return await RunAsync(a.Str("file"), a.Strings("arguments"), ct);
            case "maintenance.status": return Maintenance.Status;
            case "maintenance.session": return await Maintenance.RunAsync(a.Str("file"), a.Strings("arguments"), ct);
            case "maintenance.elevated": return await ElevatedJob.RunAsync(a.Str("file"), a.Strings("arguments"), Root, ct);
            case "system": return await ResourceSampling.SystemAsync(ct);
            case "network": return await PowerShellAsync("[pscustomobject]@{Adapters=@(Get-NetIPConfiguration | Select-Object InterfaceAlias,IPv4Address,IPv4DefaultGateway,DNSServer);Statistics=@(Get-NetAdapterStatistics | Select-Object Name,ReceivedBytes,SentBytes)} | ConvertTo-Json -Depth 5 -Compress", ct);
            case "services": return await PowerShellAsync("Get-Service | Select-Object Name,DisplayName,Status,StartType | ConvertTo-Json -Compress", ct);
            case "events":
                string log = a.Str("log", "Application"); if (log is not ("Application" or "System")) throw new ArgumentException("Log must be Application or System.");
                return await PowerShellAsync($"Get-WinEvent -LogName {log} -MaxEvents {Math.Clamp(a.Int("count", 30), 1, 100)} -ErrorAction Stop | Select-Object TimeCreated,Id,LevelDisplayName,ProviderName,Message | ConvertTo-Json -Compress", ct);
            default: throw new ArgumentException("Unknown operation: " + op);
        }
    }
    public string Resolve(string path) => Path.IsPathRooted(path) ? Path.GetFullPath(path) : Safety.UnderRoot(Workspace, path);
    private sealed record Upload(string Path, long Size, string Sha256);
    private async Task<object> UploadAsync(string op, JsonElement a, CancellationToken ct)
    {
        string id = a.Str("transfer"); if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Transfer must be a UUID in N format.");
        string meta = Path.Combine(Transfers, id + ".json"), partial = Path.Combine(Transfers, id + ".partial");
        if (op == "upload.abort") { if (File.Exists(partial)) File.Delete(partial); if (File.Exists(meta)) File.Delete(meta); return new { aborted = true, transfer = id }; }
        if (op == "upload.begin")
        {
            string path = Safety.UnderRoot(Workspace, a.Str("path")); long size = a.Long("size"); string hash = a.Str("sha256");
            if (size < 0 || size > 16L * 1024 * 1024 * 1024 || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid size or SHA-256.");
            if (File.Exists(meta)) throw new IOException("Transfer already exists; use upload.status to resume.");
            await File.WriteAllTextAsync(meta, Json.Text(new Upload(path, size, hash.ToUpperInvariant())), ct); using (File.Create(partial)) { }
            return new { transfer = id, offset = 0 };
        }
        var upload = JsonSerializer.Deserialize<Upload>(await File.ReadAllTextAsync(meta, ct), Json.Options)!;
        if (op == "upload.status") return new { transfer = id, offset = new FileInfo(partial).Length, upload.Size };
        if (op == "upload.chunk")
        {
            var data = Convert.FromBase64String(a.Str("data")); if (data.Length > 256 * 1024) throw new ArgumentException("Chunk exceeds 256 KiB."); long offset = a.Long("offset");
            await using var f = File.Open(partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (offset < 0 || offset + data.Length > upload.Size) throw new ArgumentException("Chunk outside declared file size.");
            if (offset < f.Length)
            {
                f.Position = offset; byte[] old = new byte[data.Length]; await f.ReadExactlyAsync(old, ct);
                if (!old.SequenceEqual(data)) throw new IOException("Conflicting retry chunk.");
            }
            else { if (offset != f.Length) throw new IOException("Unexpected offset. Query upload.status."); f.Position = offset; await f.WriteAsync(data, ct); }
            return new { offset = f.Length };
        }
        string validated = Safety.UnderRoot(Workspace, Path.GetRelativePath(Workspace, upload.Path));
        var actual = await FileInfoAsync(partial, ct); if (actual.Size != upload.Size || !Safety.Equal(actual.Sha256, upload.Sha256)) throw new IOException("Incomplete transfer or SHA-256 mismatch.");
        Directory.CreateDirectory(Path.GetDirectoryName(validated)!); File.Move(partial, validated, true); File.Delete(meta); return await FileInfoAsync(validated, ct);
    }
    public sealed record BinaryInfo(string Path, long Size, string Sha256, string? FileVersion);
    public static async Task<BinaryInfo> FileInfoAsync(string path, CancellationToken ct)
    {
        await using var f = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new BinaryInfo(path, f.Length, Convert.ToHexString(await SHA256.HashDataAsync(f, ct)), FileVersionInfo.GetVersionInfo(path).FileVersion);
    }
    private static object ProcessSummary(Process p)
    {
        using (p) { try { return new { pid = p.Id, name = p.ProcessName, window = p.MainWindowTitle, responding = p.Responding, memoryBytes = p.WorkingSet64 }; } catch { return new { pid = p.Id, name = "<access unavailable>", window = "", responding = false, memoryBytes = 0L }; } }
    }
    private static async Task<object> ProcessInfoAsync(int pid, CancellationToken ct)
    {
        using var p = Process.GetProcessById(pid); string path = p.MainModule?.FileName ?? throw new UnauthorizedAccessException();
        return new { pid, name = p.ProcessName, startUtc = p.StartTime.ToUniversalTime(), responding = p.Responding, threads = p.Threads.Count, binary = await FileInfoAsync(path, ct) };
    }
    private static async Task<object> PowerShellAsync(string script, CancellationToken ct) => await RunAsync(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell/v1.0/powershell.exe"), ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script], ct);
    public static async Task<object> RunAsync(string file, string[] args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(file)) throw new ArgumentException("Executable file is required.");
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi) ?? throw new IOException("Process could not start.");
        var output = ReadBoundedAsync(p.StandardOutput, ct); var error = ReadBoundedAsync(p.StandardError, ct);
        try { await p.WaitForExitAsync(ct); await Task.WhenAll(output, error).WaitAsync(ct); }
        catch { try { p.Kill(true); await p.WaitForExitAsync(CancellationToken.None); } catch (InvalidOperationException) { } throw; }
        return new { pid = p.Id, exitCode = p.ExitCode, stdout = await output, stderr = await error };
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        const int limit = 256 * 1024; var sb = new StringBuilder(); char[] chunk = new char[4096]; int n; bool truncated = false;
        while ((n = await reader.ReadAsync(chunk.AsMemory(), ct)) > 0) { int keep = Math.Min(n, limit - sb.Length); if (keep > 0) sb.Append(chunk, 0, keep); if (keep != n) truncated = true; }
        return sb + (truncated ? "\n[output truncated at 256 Ki characters]" : "");
    }
}

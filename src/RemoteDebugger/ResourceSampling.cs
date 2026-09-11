using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RemoteDebugger;

public static class ResourceSampling
{
    private sealed record Sample(int Pid, string Name, DateTime? StartUtc, double? CpuMs, long? WorkingSetBytes, string? Window, bool? Responding, string? UnavailableReason);
    [StructLayout(LayoutKind.Sequential)] private struct FILETIME { public uint Low, High; public readonly ulong Value => ((ulong)High << 32) | Low; }
    [StructLayout(LayoutKind.Sequential)] private struct MEMORY { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORY memory);
    private static Dictionary<int, Sample> Snapshot()
    {
        var values = new Dictionary<int, Sample>();
        foreach (var p in Process.GetProcesses())
        using (p)
        {
            string name = "<unavailable>";
            try { name = p.ProcessName; values[p.Id] = new Sample(p.Id, name, p.StartTime.ToUniversalTime(), p.TotalProcessorTime.TotalMilliseconds, p.WorkingSet64, p.MainWindowTitle, p.MainWindowHandle != IntPtr.Zero ? p.Responding : null, null); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or ArgumentException) { values[p.Id] = new Sample(p.Id, name, null, null, null, null, null, ex.GetType().Name); }
        }
        return values;
    }
    public static async Task<object> ProcessesAsync(CancellationToken ct)
    {
        DateTimeOffset start = DateTimeOffset.UtcNow; var before = Snapshot(); var elapsed = Stopwatch.StartNew();
        await Task.Delay(600, ct); var after = Snapshot(); double ms = elapsed.Elapsed.TotalMilliseconds;
        return new { sampleStartUtc = start, sampleEndUtc = DateTimeOffset.UtcNow, intervalMs = ms, logicalProcessors = Environment.ProcessorCount, cpuScale = "percent of total logical CPU capacity (0..100)", processes = after.Values.Select(p => new { pid = p.Pid, name = p.Name, startUtc = p.StartUtc, cpuPercentTotalMachine = p.CpuMs.HasValue && ms > 0 && before.TryGetValue(p.Pid, out var previous) && previous.CpuMs.HasValue && previous.StartUtc == p.StartUtc ? Math.Clamp((p.CpuMs.Value - previous.CpuMs.Value) / ms / Environment.ProcessorCount * 100, 0, 100) : (double?)null, workingSetBytes = p.WorkingSetBytes, window = p.Window, responding = p.Responding, unavailableReason = p.UnavailableReason }).OrderByDescending(p => p.cpuPercentTotalMachine).ToArray() };
    }
    public static async Task<object> SystemAsync(CancellationToken ct)
    {
        DateTimeOffset start = DateTimeOffset.UtcNow; bool first = GetSystemTimes(out var idle1, out var kernel1, out var user1); var sw = Stopwatch.StartNew(); await Task.Delay(600, ct);
        bool second = GetSystemTimes(out var idle2, out var kernel2, out var user2); ulong total = 0;
        if (first && second)
        {
            ulong previous = kernel1.Value + user1.Value;
            ulong current = kernel2.Value + user2.Value;
            if (current >= previous) total = current - previous;
        }
        var memory = new MEMORY { Length = (uint)Marshal.SizeOf<MEMORY>() }; bool hasMemory = GlobalMemoryStatusEx(ref memory);
        return new { sampleStartUtc = start, sampleEndUtc = DateTimeOffset.UtcNow, intervalMs = sw.Elapsed.TotalMilliseconds, cpuPercentTotalMachine = first && second && total > 0 && idle2.Value >= idle1.Value ? Math.Clamp(100.0 * (1 - (idle2.Value - idle1.Value) / (double)total), 0, 100) : (double?)null, logicalProcessors = Environment.ProcessorCount, physicalMemoryTotalBytes = hasMemory ? memory.TotalPhysical : (ulong?)null, physicalMemoryAvailableBytes = hasMemory ? memory.AvailablePhysical : (ulong?)null, volumes = DriveInfo.GetDrives().Select(d => { try { return new { name = d.Name, ready = d.IsReady, totalBytes = d.IsReady ? d.TotalSize : (long?)null, freeBytes = d.IsReady ? d.AvailableFreeSpace : (long?)null }; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new { name = d.Name, ready = false, totalBytes = (long?)null, freeBytes = (long?)null }; } }).ToArray(), os = Environment.OSVersion.VersionString, uptimeSeconds = Environment.TickCount64 / 1000 };
    }
}

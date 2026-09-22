using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RemoteDebugger;

public sealed class ResourceSampling : IDisposable
{
    private readonly object processGate = new(), systemGate = new();
    private Dictionary<int, Sample>? previousProcesses;
    private DateTimeOffset processTime, systemTime, volumeTime;
    private long processTick, systemTick;
    private object? processResult, systemResult, volumes;
    private ResourceCounters? counters;
    private FILETIME previousIdle, previousKernel, previousUser;
    private bool previousSystemValid, disposed;
    private sealed record Sample(int Pid, string Name, DateTime? StartUtc, double? CpuMs, long? WorkingSetBytes, string? Window, bool? Responding, string? UnavailableReason);
    [StructLayout(LayoutKind.Sequential)] private struct FILETIME { public uint Low, High; public readonly ulong Value => ((ulong)High << 32) | Low; }
    [StructLayout(LayoutKind.Sequential)] private struct MEMORY { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORY memory);
    private Dictionary<int, Sample> Snapshot()
    {
        var values = new Dictionary<int, Sample>();
        foreach (var p in Process.GetProcesses())
        using (p)
        {
            string name = "<unavailable>";
            try
            {
                var start = p.StartTime.ToUniversalTime();
                name = previousProcesses != null && previousProcesses.TryGetValue(p.Id, out var old) && old.StartUtc == start ? old.Name : p.ProcessName;
                values[p.Id] = new Sample(p.Id, name, start, p.TotalProcessorTime.TotalMilliseconds, p.WorkingSet64, p.MainWindowTitle, p.MainWindowHandle != IntPtr.Zero ? p.Responding : null, null);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or ArgumentException) { values[p.Id] = new Sample(p.Id, name, null, null, null, null, null, ex.GetType().Name); }
        }
        return values;
    }
    public Task<object> ProcessesAsync(CancellationToken ct)
    {
        lock (processGate)
        {
            ct.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(disposed, this);
            double ms = Stopwatch.GetElapsedTime(processTick).TotalMilliseconds;
            if (processResult != null && ms < 1000) return Task.FromResult(processResult);
            var after = Snapshot(); var now = DateTimeOffset.UtcNow; long tick = Stopwatch.GetTimestamp();
            ms = Stopwatch.GetElapsedTime(processTick, tick).TotalMilliseconds;
            var before = previousProcesses;
            processResult = new { sampleStartUtc = before == null ? now : processTime, sampleEndUtc = now, intervalMs = before == null ? 0 : ms, logicalProcessors = Environment.ProcessorCount, cpuScale = "percent of total logical CPU capacity (0..100)", processes = after.Values.Select(p => new { pid = p.Pid, name = p.Name, startUtc = p.StartUtc, cpuPercentTotalMachine = before != null && before.TryGetValue(p.Pid, out var previous) ? CpuPercent(previous.CpuMs, p.CpuMs, previous.StartUtc, p.StartUtc, ms, Environment.ProcessorCount) : null, workingSetBytes = p.WorkingSetBytes, window = p.Window, responding = p.Responding, unavailableReason = p.UnavailableReason }).ToArray() };
            previousProcesses = after; processTime = now; processTick = tick;
            return Task.FromResult(processResult);
        }
    }
    internal static double? CpuPercent(double? before, double? after, DateTime? beforeStart, DateTime? afterStart, double ms, int processors) =>
        before.HasValue && after.HasValue && beforeStart.HasValue && beforeStart == afterStart && after >= before && ms is > 0 and <= 15000 && processors > 0
            ? Math.Clamp((after.Value - before.Value) / ms / processors * 100, 0, 100) : null;

    public Task<object> SystemAsync(CancellationToken ct)
    {
        lock (systemGate)
        {
            ct.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(disposed, this);
            double ms = Stopwatch.GetElapsedTime(systemTick).TotalMilliseconds;
            if (systemResult != null && ms < 1000) return Task.FromResult(systemResult);
            bool warm = counters != null && previousSystemValid && ms <= 15000;
            counters ??= new ResourceCounters();
            var now = DateTimeOffset.UtcNow;
            bool second = GetSystemTimes(out var idle2, out var kernel2, out var user2);
            ulong previous = previousKernel.Value + previousUser.Value;
            ulong current = kernel2.Value + user2.Value;
            ulong total = current >= previous ? current - previous : 0;
            var memory = new MEMORY { Length = (uint)Marshal.SizeOf<MEMORY>() }; bool hasMemory = GlobalMemoryStatusEx(ref memory);
            var utilization = counters.Read();
            if (volumes == null || now - volumeTime >= TimeSpan.FromSeconds(30))
            {
                volumes = DriveInfo.GetDrives().Select(d => { try { return new { name = d.Name, ready = d.IsReady, totalBytes = d.IsReady ? d.TotalSize : (long?)null, freeBytes = d.IsReady ? d.AvailableFreeSpace : (long?)null }; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new { name = d.Name, ready = false, totalBytes = (long?)null, freeBytes = (long?)null }; } }).ToArray();
                volumeTime = now;
            }
            systemResult = new { sampleStartUtc = systemResult == null ? now : systemTime, sampleEndUtc = now, intervalMs = systemResult == null ? 0 : ms, cpuPercentTotalMachine = warm && second && total > 0 && idle2.Value >= previousIdle.Value ? Math.Clamp(100.0 * (1 - (idle2.Value - previousIdle.Value) / (double)total), 0, 100) : (double?)null, logicalProcessors = Environment.ProcessorCount, physicalMemoryTotalBytes = hasMemory ? memory.TotalPhysical : (ulong?)null, physicalMemoryAvailableBytes = hasMemory ? memory.AvailablePhysical : (ulong?)null, machine = Environment.MachineName, diskBusyPercent = warm ? utilization.Disk : null, gpuPercent = warm ? utilization.Gpu : null, volumes, os = Environment.OSVersion.VersionString, uptimeSeconds = Environment.TickCount64 / 1000 };
            previousIdle = idle2; previousKernel = kernel2; previousUser = user2; previousSystemValid = second; systemTime = now; systemTick = Stopwatch.GetTimestamp();
            return Task.FromResult(systemResult);
        }
    }
    public void Dispose() { lock (processGate) lock (systemGate) { disposed = true; counters?.Dispose(); counters = null; previousProcesses = null; } }
}

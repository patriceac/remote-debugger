using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace RemoteDebugger;

internal sealed class ResourceCounters : IDisposable
{
    private IntPtr query, disk, gpu;
    public ResourceCounters()
    {
        if (PdhOpenQuery(null, UIntPtr.Zero, out query) != 0) return;
        _ = PdhAddEnglishCounter(query, @"\PhysicalDisk(_Total)\% Idle Time", UIntPtr.Zero, out disk);
        _ = PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", UIntPtr.Zero, out gpu);
        _ = PdhCollectQueryData(query);
    }

    public (double? Disk, double? Gpu) Read()
    {
        if (query == IntPtr.Zero || PdhCollectQueryData(query) != 0) return (null, null);
        double? idle = disk != IntPtr.Zero && PdhGetFormattedCounterValue(disk, 0x200, out _, out var value) == 0 && value.Status <= 1 ? value.Number : null;
        return (idle is { } percent ? Math.Clamp(100 - percent, 0, 100) : null, GpuUsage(ReadArray(gpu)));
    }

    internal static double? GpuUsage(IEnumerable<(string Name, double Value)> samples)
    {
        // Sum processes on each physical engine, then use the busiest engine,
        // matching Task Manager instead of adding independent engines together.
        var engines = samples.Where(s => double.IsFinite(s.Value) && s.Value >= 0)
            .Select(s => (Key: Regex.Match(s.Name, @"luid_.+?_phys_\d+_eng_\d+").Value, s.Value))
            .Where(s => s.Key.Length > 0).GroupBy(s => s.Key).Select(g => g.Sum(s => s.Value)).ToArray();
        return engines.Length == 0 ? null : Math.Clamp(engines.Max(), 0, 100);
    }

    private static List<(string, double)> ReadArray(IntPtr counter)
    {
        var samples = new List<(string, double)>();
        uint bytes = 0;
        if (counter == IntPtr.Zero || PdhGetFormattedCounterArray(counter, 0x200, ref bytes, out _, IntPtr.Zero) != 0x800007D2 || bytes is 0 or > 2097152) return samples;
        IntPtr buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            if (PdhGetFormattedCounterArray(counter, 0x200, ref bytes, out uint count, buffer) != 0) return samples;
            int size = Marshal.SizeOf<CounterItem>();
            for (int i = 0; i < count && (i + 1) * size <= bytes; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + i * size);
                if (item.Value.Status <= 1) samples.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value.Number));
            }
            return samples;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose() { if (query != IntPtr.Zero) { PdhCloseQuery(query); query = IntPtr.Zero; } }
    [StructLayout(LayoutKind.Sequential)] private struct CounterValue { public uint Status; public double Number; }
    [StructLayout(LayoutKind.Sequential)] private struct CounterItem { public IntPtr Name; public CounterValue Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")] private static extern uint PdhOpenQuery(string? source, UIntPtr data, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, UIntPtr data, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);
    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")] private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}

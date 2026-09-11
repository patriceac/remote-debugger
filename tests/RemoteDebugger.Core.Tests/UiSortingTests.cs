using RemoteDebugger.Core;
using Xunit;

namespace RemoteDebugger.Core.Tests;

public sealed class UiSortingTests
{
    [Fact]
    public void ProcessPidSortUsesNumbersAndKeepsEqualRowsStable()
    {
        var rows = new[]
        {
            Row(12, "twelve"),
            Row(2, "first two"),
            Row(2, "second two"),
            Row(101, "one-oh-one")
        };

        var sorted = UiSorting.SortProcesses(rows, new SortState<ProcessSortColumn>(ProcessSortColumn.Pid, false));

        Assert.Equal(new[] { "first two", "second two", "twelve", "one-oh-one" }, sorted.Select(row => row.Name));
    }

    [Fact]
    public void ProcessMetricSortPlacesUnavailableValuesLastInBothDirections()
    {
        var rows = new[]
        {
            Row(1, "unavailable", cpu: null),
            Row(2, "low", cpu: 3.2),
            Row(3, "high", cpu: 81.4),
            Row(4, "tie", cpu: 3.2)
        };

        var ascending = UiSorting.SortProcesses(rows, new SortState<ProcessSortColumn>(ProcessSortColumn.CpuPercentTotalMachine, false));
        var descending = UiSorting.SortProcesses(rows, new SortState<ProcessSortColumn>(ProcessSortColumn.CpuPercentTotalMachine, true));

        Assert.Equal(new[] { "low", "tie", "high", "unavailable" }, ascending.Select(row => row.Name));
        Assert.Equal(new[] { "high", "low", "tie", "unavailable" }, descending.Select(row => row.Name));
    }

    [Fact]
    public void ProcessMemorySortIsNumericRatherThanDisplayTextSort()
    {
        var rows = new[]
        {
            Row(1, "one hundred", memory: 100),
            Row(2, "two megabytes", memory: 2 * 1024 * 1024),
            Row(3, "ten megabytes", memory: 10 * 1024 * 1024)
        };

        var sorted = UiSorting.SortProcesses(rows, new SortState<ProcessSortColumn>(ProcessSortColumn.WorkingSetBytes, true));

        Assert.Equal(new[] { "ten megabytes", "two megabytes", "one hundred" }, sorted.Select(row => row.Name));
    }

    [Fact]
    public void FileDateSortIsChronologicalAndMissingDatesAreLast()
    {
        var rows = new[]
        {
            new FileSortRow("unknown.log", false, 12, null, "unknown.log"),
            new FileSortRow("older.log", false, 12, new DateTimeOffset(2026, 1, 3, 12, 0, 0, TimeSpan.Zero), "older.log"),
            new FileSortRow("newer.log", false, 12, new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero), "newer.log")
        };

        var sorted = UiSorting.SortFiles(rows, new SortState<FileSortColumn>(FileSortColumn.ModifiedUtc, true));

        Assert.Equal(new[] { "newer.log", "older.log", "unknown.log" }, sorted.Select(row => row.Name));
    }

    [Fact]
    public void FileTypeSortKeepsDirectoriesTogetherAndStable()
    {
        var rows = new[]
        {
            new FileSortRow("zeta", false, 1, null, "zeta"),
            new FileSortRow("alpha", true, null, null, "alpha"),
            new FileSortRow("beta", true, null, null, "beta"),
            new FileSortRow("another file", false, 2, null, "another file")
        };

        var ascending = UiSorting.SortFiles(rows, new SortState<FileSortColumn>(FileSortColumn.Type, false));
        var descending = UiSorting.SortFiles(rows, new SortState<FileSortColumn>(FileSortColumn.Type, true));

        Assert.Equal(new[] { "alpha", "beta", "zeta", "another file" }, ascending.Select(row => row.Name));
        Assert.Equal(new[] { "zeta", "another file", "alpha", "beta" }, descending.Select(row => row.Name));
    }

    [Fact]
    public void SortStateTogglesOnlyTheClickedColumn()
    {
        var state = new SortState<ProcessSortColumn>(ProcessSortColumn.Name, false);

        Assert.Equal(new SortState<ProcessSortColumn>(ProcessSortColumn.Name, true), state.Toggle(ProcessSortColumn.Name));
        Assert.Equal(new SortState<ProcessSortColumn>(ProcessSortColumn.Pid, false), state.Toggle(ProcessSortColumn.Pid));
    }

    private static ProcessSortRow Row(int pid, string name, double? cpu = 0, long? memory = 0) =>
        new(pid, name, cpu, memory, true, string.Empty);
}

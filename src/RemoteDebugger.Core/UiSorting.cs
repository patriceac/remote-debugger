namespace RemoteDebugger.Core;

/// <summary>
/// The process row values shown by the support UI. Nullable metrics remain nullable
/// all the way through sorting so an unavailable sample is never treated as zero.
/// </summary>
public sealed record ProcessSortRow(
    int Pid,
    string Name,
    double? CpuPercentTotalMachine,
    long? WorkingSetBytes,
    bool? Responding,
    string Window,
    DateTimeOffset? StartUtc = null);

/// <summary>
/// The remote file row values shown by the support UI.
/// </summary>
public sealed record FileSortRow(
    string Name,
    bool IsDirectory,
    long? SizeBytes,
    DateTimeOffset? ModifiedUtc,
    string Path);

public enum ProcessSortColumn
{
    Pid,
    Name,
    CpuPercentTotalMachine,
    WorkingSetBytes,
    Responding,
    Window
}

public enum FileSortColumn
{
    Name,
    Type,
    SizeBytes,
    ModifiedUtc
}

public readonly record struct SortState<TColumn>(TColumn Column, bool Descending)
    where TColumn : struct, Enum
{
    public SortState<TColumn> Toggle(TColumn column) =>
        EqualityComparer<TColumn>.Default.Equals(Column, column)
            ? this with { Descending = !Descending }
            : new SortState<TColumn>(column, false);
}

/// <summary>
/// Stable, typed sort helpers used by the WinForms tables. The original order is
/// retained for equal values, making repeated refreshes predictable. Null values
/// are always placed after available values in either direction.
/// </summary>
public static class UiSorting
{
    public static IReadOnlyList<ProcessSortRow> SortProcesses(
        IEnumerable<ProcessSortRow> rows,
        SortState<ProcessSortColumn> state)
    {
        ArgumentNullException.ThrowIfNull(rows);
        IReadOnlyList<ProcessSortRow> ordered = state.Column switch
        {
            ProcessSortColumn.Pid => Order(rows, row => (int?)row.Pid, state.Descending),
            ProcessSortColumn.Name => Order(rows, row => row.Name, state.Descending, StringComparer.CurrentCultureIgnoreCase),
            ProcessSortColumn.CpuPercentTotalMachine => Order(rows, row => row.CpuPercentTotalMachine, state.Descending),
            ProcessSortColumn.WorkingSetBytes => Order(rows, row => row.WorkingSetBytes, state.Descending),
            ProcessSortColumn.Responding => Order(rows, row => row.Responding, state.Descending),
            ProcessSortColumn.Window => Order(rows, row => row.Window, state.Descending, StringComparer.CurrentCultureIgnoreCase),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state.Column, "Unknown process sort column.")
        };
        return ordered.ToArray();
    }

    public static IReadOnlyList<FileSortRow> SortFiles(
        IEnumerable<FileSortRow> rows,
        SortState<FileSortColumn> state)
    {
        ArgumentNullException.ThrowIfNull(rows);
        IReadOnlyList<FileSortRow> ordered = state.Column switch
        {
            FileSortColumn.Name => Order(rows, row => row.Name, state.Descending, StringComparer.CurrentCultureIgnoreCase),
            FileSortColumn.Type => Order(rows, row => (int?)(row.IsDirectory ? 0 : 1), state.Descending),
            FileSortColumn.SizeBytes => Order(rows, row => row.SizeBytes, state.Descending),
            FileSortColumn.ModifiedUtc => Order(rows, row => row.ModifiedUtc, state.Descending),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state.Column, "Unknown file sort column.")
        };
        return ordered.ToArray();
    }

    private static IReadOnlyList<TRow> Order<TRow, TValue>(
        IEnumerable<TRow> rows,
        Func<TRow, TValue?> selector,
        bool descending,
        IComparer<TValue>? comparer = null)
        where TValue : struct
    {
        // A pair is used instead of a custom comparer so null placement is explicit
        // and consistent for ascending and descending views.
        var decorated = rows.Select((row, index) => (row, index, value: selector(row)));
        var ordered = descending
            ? decorated.OrderBy(item => item.value.HasValue ? 0 : 1)
                .ThenByDescending(item => item.value, NullableComparer<TValue>.Create(comparer))
                .ThenBy(item => item.index)
            : decorated.OrderBy(item => item.value.HasValue ? 0 : 1)
                .ThenBy(item => item.value, NullableComparer<TValue>.Create(comparer))
                .ThenBy(item => item.index);
        return ordered.Select(item => item.row).ToArray();
    }

    private static IReadOnlyList<TRow> Order<TRow>(
        IEnumerable<TRow> rows,
        Func<TRow, string?> selector,
        bool descending,
        StringComparer comparer)
    {
        var decorated = rows.Select((row, index) => (row, index, value: selector(row)));
        return descending
            ? decorated.OrderBy(item => item.value is null ? 1 : 0)
                .ThenByDescending(item => item.value, comparer)
                .ThenBy(item => item.index)
                .Select(item => item.row)
                .ToArray()
            : decorated.OrderBy(item => item.value is null ? 1 : 0)
                .ThenBy(item => item.value, comparer)
                .ThenBy(item => item.index)
                .Select(item => item.row)
                .ToArray();
    }

    private static class NullableComparer<T>
        where T : struct
    {
        public static IComparer<T?> Create(IComparer<T>? comparer) =>
            comparer is null ? Comparer<T?>.Default : new NullableValueComparer<T>(comparer);
    }

    private sealed class NullableValueComparer<T>(IComparer<T> comparer) : IComparer<T?>
        where T : struct
    {
        public int Compare(T? x, T? y) =>
            x.HasValue && y.HasValue ? comparer.Compare(x.Value, y.Value) :
            x.HasValue ? 1 : y.HasValue ? -1 : 0;
    }
}

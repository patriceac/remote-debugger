using System.Text.Json;

namespace RemoteDebugger;

internal sealed record TableColumnLayout(string Id, double Width, int Order);

/// <summary>Column widths are logical pixels, independent of the display DPI.</summary>
internal static class TableLayoutStore
{
    internal static string PathFor(string root, string table) => Path.Combine(root, "tables", table + ".json");

    public static TableColumnLayout[] Load(string root, string table, TableColumnLayout[] defaults)
    {
        try
        {
            var saved = JsonSerializer.Deserialize<TableColumnLayout?[]>(File.ReadAllText(PathFor(root, table)));
            return Normalize(saved, defaults);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return defaults;
        }
    }

    internal static TableColumnLayout[] Normalize(TableColumnLayout?[]? saved, TableColumnLayout[] defaults)
    {
        var known = defaults.Select(column => column.Id).ToHashSet(StringComparer.Ordinal);
        var valid = (saved ?? []).Where(column => column != null && column.Id != null && known.Contains(column.Id))
            .Select(column => column!).DistinctBy(column => column.Id).OrderBy(column => column.Order < 0 ? int.MaxValue : column.Order).ToArray();
        var order = valid.Select(column => column.Id).Concat(defaults.Select(column => column.Id)).Distinct().ToList();
        return defaults.Select(column =>
        {
            var previous = valid.FirstOrDefault(item => item.Id == column.Id);
            double width = previous is { Width: >= 32 and <= 4000 } ? previous.Width : column.Width;
            return new TableColumnLayout(column.Id, width, order.IndexOf(column.Id));
        }).ToArray();
    }

    public static void Save(string root, string table, TableColumnLayout[] columns)
    {
        string path = PathFor(root, table);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(columns));
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Personal layout preferences must not interrupt a support session.
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

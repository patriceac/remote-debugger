using System.Text.Json;

namespace RemoteDebugger.Core;

/// <summary>Per-user choice controlling whether paired sessions may open administrator maintenance.</summary>
public static class AdminMaintenancePreference
{
    public const bool DefaultEnabled = true;
    private const string FileName = "admin-maintenance.json";

    public static bool Load(string root)
    {
        try
        {
            var saved = JsonSerializer.Deserialize<SavedPreference>(
                File.ReadAllText(Path.Combine(root, FileName)));
            return saved?.Enabled ?? DefaultEnabled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return DefaultEnabled;
        }
    }

    public static void Save(string root, bool enabled)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, FileName);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new SavedPreference(enabled)));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record SavedPreference(bool Enabled);
}

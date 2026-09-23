namespace RemoteDebugger.Core;

/// <summary>A per-user appearance choice; System follows the Windows app theme.</summary>
public static class ThemePreference
{
    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() is "light" or "dark" ? value.Trim().ToLowerInvariant() : "system";
    public static bool IsDark(string? preference, bool systemDark) => Normalize(preference) switch
    {
        "dark" => true, "light" => false, _ => systemDark
    };
    public static string Load(string root)
    {
        try { return Normalize(File.ReadAllText(Path.Combine(root, "theme.txt"))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "system"; }
    }
    public static void Save(string root, string preference)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "theme.txt"), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, Normalize(preference)); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

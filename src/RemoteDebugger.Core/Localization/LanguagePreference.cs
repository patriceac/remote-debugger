using System.Globalization;
using System.Text.Json;

namespace RemoteDebugger.Core;

/// <summary>A per-user interface choice; null follows the Windows display language.</summary>
public static class LanguagePreference
{
    public static string? Normalize(string? language) => language?.ToLowerInvariant() switch
    {
        "en" => "en",
        "fr" => "fr",
        "es" => "es",
        _ => null
    };

    public static CultureInfo Resolve(CultureInfo systemLanguage, string? preference, string? launchOverride = null) =>
        UiCulture.Select(systemLanguage, launchOverride ?? Normalize(preference));

    public static string? Load(string root)
    {
        try
        {
            var saved = JsonSerializer.Deserialize<SavedLanguage>(File.ReadAllText(Path.Combine(root, "language.json")));
            return Normalize(saved?.Language);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static void Save(string root, string? language)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "language.json");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new SavedLanguage(Normalize(language))));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record SavedLanguage(string? Language);
}

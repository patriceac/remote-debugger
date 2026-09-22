using System.Text.Json;

namespace RemoteDebugger.Core;

public sealed record ViewerPreferences(bool Control = true, bool Clipboard = true)
{
    private static string PathFor(string root, string fingerprint) =>
        Path.Combine(root, "viewer-preferences", Safety.Hash(fingerprint.ToUpperInvariant()) + ".json");

    public static ViewerPreferences Load(string root, string fingerprint)
    {
        try { return JsonSerializer.Deserialize<ViewerPreferences>(File.ReadAllText(PathFor(root, fingerprint))) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save(string root, string fingerprint)
    {
        string path = PathFor(root, fingerprint);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, path, true);
    }
}

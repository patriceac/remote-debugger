using System.Security.Cryptography;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

internal sealed class DeviceCursorNames
{
    private readonly string path;
    private Dictionary<string, string> names = [];
    internal DeviceCursorNames(string root)
    {
        path = Path.Combine(root, "device-cursor-names.dpapi");
        try { if (File.Exists(path)) names = JsonSerializer.Deserialize<Dictionary<string, string>>(Vault.Read(path), Json.Options) ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException) { }
    }

    internal string Get(string fingerprint) => names.GetValueOrDefault(fingerprint.ToUpperInvariant()) ?? "";
    internal CursorPosition? Apply(string fingerprint, CursorPosition? cursor) =>
        cursor != null && Get(fingerprint) is { Length: > 0 } nickname ? cursor with { Name = nickname } : cursor;

    internal void Save(string fingerprint, string nickname)
    {
        if (!PairingExchange.ValidHash(fingerprint)) throw new ArgumentException("Select a device with a verified identity.");
        nickname = nickname.Trim();
        if (nickname.Length > 28 || nickname.Any(char.IsControl)) throw new ArgumentException(UiText.Get("InvalidCursorNickname"));
        var updated = new Dictionary<string, string>(names);
        string key = fingerprint.ToUpperInvariant();
        if (nickname.Length == 0) updated.Remove(key); else updated[key] = nickname;
        Vault.Save(path, JsonSerializer.SerializeToUtf8Bytes(updated, Json.Options));
        names = updated;
    }
}

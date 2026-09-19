using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

internal sealed record WakeSettings(string MacAddress, string Destination = "", int Port = 9, string HelperFingerprint = "");

internal static class DeviceWakeSettings
{
    internal static WakeSettings Validate(WakeSettings value)
    {
        string destination = value.Destination.Trim();
        bool validHost = destination.Length == 0 || destination.Length <= 253 &&
            (IPAddress.TryParse(destination, out var ip) ? ip.AddressFamily == AddressFamily.InterNetwork
                : Uri.CheckHostName(destination) == UriHostNameType.Dns);
        if (!validHost || value.Port is < 1 or > 65535) throw new ArgumentException(UiText.InvalidWakeDestination);
        if (value.HelperFingerprint.Length > 0 && !PairingExchange.ValidHash(value.HelperFingerprint))
            throw new ArgumentException(UiText.WakeHelperUnavailable);
        return value with { MacAddress = WakeOnLan.NormalizeMac(value.MacAddress), Destination = destination.ToLowerInvariant(),
            HelperFingerprint = value.HelperFingerprint.ToUpperInvariant() };
    }

    private static Dictionary<string, WakeSettings> Read(string root)
    {
        string path = Path.Combine(root, "device-wake-settings.dpapi");
        return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, WakeSettings>>(Vault.Read(path), Json.Options) ?? [] : [];
    }

    internal static WakeSettings? Load(string root, string fingerprint)
    {
        try { return Read(root).GetValueOrDefault(fingerprint.ToUpperInvariant()) is { } value ? Validate(value) : null; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or JsonException or CryptographicException) { return null; }
    }

    internal static void Save(string root, string fingerprint, WakeSettings value)
    {
        if (!PairingExchange.ValidHash(fingerprint)) throw new ArgumentException("Select a device with a verified identity.");
        var settings = Read(root);
        settings[fingerprint.ToUpperInvariant()] = Validate(value);
        Vault.Save(Path.Combine(root, "device-wake-settings.dpapi"), JsonSerializer.SerializeToUtf8Bytes(settings, Json.Options));
    }
}

using System.Globalization;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

internal static class DeviceWanAddress
{
    internal static DirectEndpoint? Parse(string value)
    {
        value = value.Trim();
        if (value.Length == 0) return null;
        string[] parts = value.Split(':');
        int port = 45832;
        if (parts.Length > 2 || parts[0].Length > 253 ||
            Uri.CheckHostName(parts[0]) is not (UriHostNameType.Dns or UriHostNameType.IPv4) ||
            parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
            throw new ArgumentException(UiText.InvalidWanAddress);
        return new(parts[0].ToLowerInvariant(), port);
    }

    internal static string Format(DirectEndpoint? address) => address == null ? "" : $"{address.Host}:{address.Port}";

    private static Dictionary<string, DirectEndpoint> Read(string root)
    {
        string path = Path.Combine(root, "device-wan-addresses.dpapi");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, DirectEndpoint>>(Vault.Read(path), Json.Options) ?? []
            : [];
    }

    internal static DirectEndpoint? Load(string root, string fingerprint)
    {
        try { return Parse(Format(Read(root).GetValueOrDefault(fingerprint.ToUpperInvariant()))); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or JsonException or System.Security.Cryptography.CryptographicException)
        { return null; }
    }

    internal static void Save(string root, string fingerprint, DirectEndpoint? address)
    {
        if (!PairingExchange.ValidHash(fingerprint)) throw new ArgumentException("Select a device with a verified identity.");
        var addresses = Read(root);
        string key = fingerprint.ToUpperInvariant();
        if (address == null) addresses.Remove(key);
        else addresses[key] = Parse(Format(address))!;
        Vault.Save(Path.Combine(root, "device-wan-addresses.dpapi"), JsonSerializer.SerializeToUtf8Bytes(addresses, Json.Options));
    }
}

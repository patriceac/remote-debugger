using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RemoteDebugger.Core;

namespace RemoteDebugger;

internal sealed record WakeAdapter(string Name, string MacAddress, string Address, string Broadcast);
internal sealed record WakeSendResult(int PacketsSent, string[] Destinations, int Port);

internal static class WakeOnLan
{
    internal static string NormalizeMac(string value)
    {
        string hex = value.Trim().Replace(":", "").Replace("-", "").ToUpperInvariant();
        if (hex.Length != 12 || !hex.All(Uri.IsHexDigit) || hex is "000000000000" or "FFFFFFFFFFFF" ||
            (Convert.ToByte(hex[..2], 16) & 1) != 0)
            throw new ArgumentException(UiText.InvalidWakeMac);
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    internal static byte[] CreatePacket(string macAddress)
    {
        byte[] mac = Convert.FromHexString(NormalizeMac(macAddress).Replace(":", ""));
        byte[] packet = new byte[102];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (int i = 0; i < 16; i++) mac.CopyTo(packet, 6 + i * 6);
        return packet;
    }

    internal static WakeAdapter[] GetAdapters()
    {
        try { return ReadAdapters(); }
        catch (NetworkInformationException) { return []; }
    }

    private static WakeAdapter[] ReadAdapters() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up &&
            n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
            n.GetPhysicalAddress().GetAddressBytes() is { Length: 6 } mac && mac.Any(b => b != 0) && (mac[0] & 1) == 0)
        .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
        .ThenBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && a.PrefixLength is > 0 and < 31)
            .Select(a => new WakeAdapter(n.Name, NormalizeMac(n.GetPhysicalAddress().ToString()), a.Address.ToString(),
                new IPAddress(a.Address.GetAddressBytes().Zip(a.IPv4Mask.GetAddressBytes(), (ip, mask) => (byte)(ip | ~mask)).ToArray()).ToString())))
        .Take(32).ToArray();

    internal static async Task<WakeSendResult> SendAsync(string macAddress, string destination, int port, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var settings = DeviceWakeSettings.Validate(new(macAddress, destination, port));
        byte[] packet = CreatePacket(settings.MacAddress);
        var endpoints = new List<(IPAddress? Local, IPAddress Target)>();
        if (settings.Destination.Length == 0)
            endpoints.AddRange(GetAdapters().Select(a => ((IPAddress?)IPAddress.Parse(a.Address), IPAddress.Parse(a.Broadcast))).Distinct());
        else
            endpoints.AddRange((await Dns.GetHostAddressesAsync(settings.Destination, AddressFamily.InterNetwork, ct))
                .Distinct().Take(8).Select(ip => ((IPAddress?)null, ip)));
        if (endpoints.Count == 0) throw new IOException(UiText.WakeNoNetwork);
        int sent = 0;
        var destinations = new HashSet<string>();
        SocketException? lastError = null;
        foreach (var endpoint in endpoints)
        {
            try
            {
                using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
                if (endpoint.Local != null) udp.Client.Bind(new IPEndPoint(endpoint.Local, 0));
                for (int repeat = 0; repeat < 3; repeat++)
                {
                    await udp.SendAsync(packet, new IPEndPoint(endpoint.Target, settings.Port), ct);
                    sent++;
                }
                destinations.Add(endpoint.Target.ToString());
            }
            catch (SocketException ex) { lastError = ex; }
        }
        if (sent == 0) throw new IOException(UiText.WakeSendFailed, lastError);
        return new(sent, destinations.ToArray(), settings.Port);
    }
}

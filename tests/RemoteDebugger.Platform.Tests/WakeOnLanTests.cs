using System.Security.Cryptography;
using System.Text;
using RemoteDebugger;
using RemoteDebugger.Core;
using Xunit;

public sealed class WakeOnLanTests
{
    [Theory]
    [InlineData("00:11:22:33:44:55", "00:11:22:33:44:55")]
    [InlineData("001122334455", "00:11:22:33:44:55")]
    [InlineData("aa-bb-cc-dd-ee-ff", "AA:BB:CC:DD:EE:FF")]
    public void NormalizeMacReturnsColonSeparatedUppercase(string value, string expected) =>
        Assert.Equal(expected, WakeOnLan.NormalizeMac(value));

    [Theory]
    [InlineData("")]
    [InlineData("00:11:22:33:44")]
    [InlineData("00:11:22:33:44:55:66")]
    [InlineData("gg:11:22:33:44:55")]
    [InlineData("00:00:00:00:00:00")]
    [InlineData("FF:FF:FF:FF:FF:FF")]
    [InlineData("01:11:22:33:44:55")]
    public void NormalizeMacRejectsMalformedAndNonUnicastValues(string value) =>
        Assert.Throws<ArgumentException>(() => WakeOnLan.NormalizeMac(value));

    [Fact]
    public void CreatePacketContainsSixSyncBytesAndSixteenMacRepeats()
    {
        byte[] packet = WakeOnLan.CreatePacket("00:11:22:33:44:55");
        byte[] mac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];

        Assert.Equal(102, packet.Length);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, packet[..6]);
        for (int repeat = 0; repeat < 16; repeat++)
            Assert.Equal(mac, packet[(6 + repeat * mac.Length)..(6 + (repeat + 1) * mac.Length)]);
    }

    [Fact]
    public void SettingsAreProtectedAndBoundToTheSavedDeviceFingerprint()
    {
        string root = Path.Combine(Path.GetTempPath(), "RemoteDebugger-Wake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string fingerprint = new('a', 64);
            var value = new WakeSettings("aa-bb-cc-dd-ee-ff", "192.168.1.255", 9, new('b', 64));
            DeviceWakeSettings.Save(root, fingerprint.ToLowerInvariant(), value);

            var loaded = DeviceWakeSettings.Load(root, fingerprint.ToUpperInvariant());
            Assert.Equal(DeviceWakeSettings.Validate(value), loaded);
            Assert.Null(DeviceWakeSettings.Load(root, new('c', 64)));

            byte[] protectedFile = File.ReadAllBytes(Path.Combine(root, "device-wake-settings.dpapi"));
            string raw = Encoding.UTF8.GetString(protectedFile);
            Assert.DoesNotContain("AA:BB:CC:DD:EE:FF", raw);
            Assert.DoesNotContain("192.168.1.255", raw);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CanceledSendStopsBeforeNetworkResolutionOrTransmit()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WakeOnLan.SendAsync("00:11:22:33:44:55", "127.0.0.1", 9, cancellation.Token));
    }

    [Fact]
    public void AdminWakeProofBindsTheOperationAndRejectsReuseForAnotherOperation()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string nonce = new('a', 64), target = new('b', 64), binary = new('c', 64);
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        string proof = UpdateAdminProof.Sign(key, nonce, target, "admin.wake", binary);

        UpdateAdminProof.Verify(proof, nonce, target, "admin.wake", binary, publicKey);
        Assert.Throws<UnauthorizedAccessException>(() =>
            UpdateAdminProof.Verify(proof, nonce, target, "admin.inspect", binary, publicKey));
    }
}

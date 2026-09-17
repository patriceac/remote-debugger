using System.Security.Cryptography;
using System.Text.Json;
using RemoteDebugger;
using RemoteDebugger.Core;
using Xunit;

public sealed class ProtectedSetupImportTests
{
    [Fact]
    public void MigrationReusesOnlyASavedSessionForTheSameComputerAndLegacyProfile()
    {
        string root = Path.Combine(Path.GetTempPath(), "RemoteDebugger-Session-" + Guid.NewGuid().ToString("N"));
        try
        {
            var previous = new InternetSettings("https://relay.example", new string('a', 64), new string('b', 64));
            var device = new SecurityDevice("Computer", "RD-0123-4567-89AB-CDEF");
            var connection = new Connection(device.LegacyHost, 443, new string('c', 64), "test-session-token", previous.RelayUrl, previous.AccessKey);
            new RemoteClient(connection).Save(Path.Combine(root, "controller.connection"));
            var store = new SecurityMigrationStore(root);
            Assert.Equal(connection, store.PreviousConnection(previous, device));
            Assert.Null(store.PreviousConnection(previous, device with { LegacyHost = "RD-1111-2222-3333-4444" }));
            Assert.Null(store.PreviousConnection(previous, device with { Fingerprint = new string('d', 64) }));
            Assert.Null(store.PreviousConnection(previous with { AccessKey = new string('e', 64) }, device));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void EncryptedImportWaitsForPassphrasePreservesExistingAccessAndRemembersUnlock()
    {
        string root = Path.Combine(Path.GetTempPath(), "RemoteDebugger-Protected-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var previous = new InternetSettings("https://relay.example", new string('a', 64), new string('b', 64));
            previous.Save(root);
            var current = new InternetSettings(previous.RelayUrl, new string('c', 64), new string('d', 64), Guid.NewGuid().ToString("N"));
            const string password = "several randomly selected test words";
            var envelope = ProtectedSetup.Seal(JsonSerializer.SerializeToUtf8Bytes(current, Json.Options), password, current.SecurityId);
            string profile = Path.Combine(root, "new.rdrelay");
            File.WriteAllBytes(profile, JsonSerializer.SerializeToUtf8Bytes(envelope, Json.Options));
            InternetSettings.Import(profile, root);
            Assert.Equal(previous, InternetSettings.Load(root));
            var store = new SecurityMigrationStore(root);
            Assert.True(File.Exists(store.PendingSetupPath));
            Assert.Throws<CryptographicException>(() => store.Unlock("wrong password"));
            Assert.Equal(previous, InternetSettings.Load(root));
            store.Unlock(password);
            Assert.Equal(current, InternetSettings.Load(root));
            Assert.False(File.Exists(store.PendingSetupPath));
            InternetSettings.Import(profile, root);
            Assert.False(File.Exists(store.PendingSetupPath));
            File.WriteAllBytes(profile, JsonSerializer.SerializeToUtf8Bytes(previous, Json.Options));
            Assert.Throws<InvalidOperationException>(() => InternetSettings.Import(profile, root));
            Assert.Equal(current, InternetSettings.Load(root));
        }
        finally { Directory.Delete(root, true); }
    }
}

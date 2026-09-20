using System.Security.Cryptography;
using System.Text.Json;
using RemoteDebugger;
using RemoteDebugger.Core;
using RemoteDebugger.Testing;
using Xunit;

public sealed class UpdateAdminTests
{
    [Fact]
    public void DisposableFixtureAuthorityCannotAuthorizeAProductionRelease()
    {
        string root = Path.Combine(Path.GetTempPath(), "rd-test-authority-" + Guid.NewGuid().ToString("N"));
        try
        {
            UpdateAcceptanceAuthority.Enroll(root);
            var fixture = new UpdateAdminStore(root, UpdateAcceptanceAuthority.PublicKey);
            Assert.True(fixture.IsAdmin);
            Assert.False(new UpdateAdminStore(root).IsAdmin);
            string proof = fixture.Sign(new('A', 64), new('B', 64), "update.begin", new('C', 64));
            UpdateAdminProof.Verify(proof, new('A', 64), new('B', 64), "update.begin", new('C', 64), UpdateAcceptanceAuthority.PublicKey);
            Assert.Throws<UnauthorizedAccessException>(() => UpdateAdminProof.Verify(proof, new('A', 64), new('B', 64), "update.begin", new('C', 64)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("update.begin", true)]
    [InlineData("update.confirm", true)]
    [InlineData("update.release", true)]
    [InlineData("platform.ensureCurrent", true)]
    [InlineData("command", false)]
    [InlineData("file.upload", false)]
    [InlineData("ui.input", false)]
    [InlineData("maintenance.elevated", false)]
    public void UpdateGrantDoesNotAllowRemoteControl(string operation, bool allowed) =>
        Assert.Equal(allowed, AgentServer.IsUpdateSessionOperation(operation));

    [Fact]
    public void InstallerCredentialRestoresSameAdminAndRejectsWrongPasswordWithoutChangingAccess()
    {
        string root = Path.Combine(Path.GetTempPath(), "rd-admin-" + Guid.NewGuid().ToString("N"));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        string source = Path.Combine(root, "source"), target = Path.Combine(root, "target");
        try
        {
            Vault.Save(Path.Combine(source, "update-admin.dpapi"), key.ExportPkcs8PrivateKey());
            var settings = new InternetSettings("https://example.test", new('A', 64), new('B', 64), Guid.NewGuid().ToString("N"));
            settings.Save(source);
            string backup = Path.Combine(root, "admin.rdadmin");
            new UpdateAdminStore(source, publicKey).Export(backup, "fixture password 1234");
            var restored = new UpdateAdminStore(target, publicKey);
            restored.Import(backup);
            string pendingSetup = new SecurityMigrationStore(target).PendingSetupPath;
            var clientSetup = ProtectedSetup.Seal(JsonSerializer.SerializeToUtf8Bytes(settings, Json.Options), "fixture password 1234", settings.SecurityId);
            File.WriteAllBytes(pendingSetup, JsonSerializer.SerializeToUtf8Bytes(clientSetup, Json.Options));
            Assert.False(restored.IsAdmin);
            Assert.Throws<CryptographicException>(() => restored.Restore(restored.PendingPath, "wrong password"));
            Assert.False(File.Exists(Path.Combine(target, "update-admin.dpapi")));
            Assert.True(File.Exists(pendingSetup));
            restored.Restore(restored.PendingPath, "fixture password 1234");
            Assert.True(restored.IsAdmin);
            Assert.Equal(settings, InternetSettings.Load(target));
            Assert.False(File.Exists(pendingSetup));
            string proof = restored.Sign(new('C', 64), new('D', 64), "update.begin", new('E', 64));
            UpdateAdminProof.Verify(proof, new('C', 64), new('D', 64), "update.begin", new('E', 64), publicKey);
            byte[] before = File.ReadAllBytes(Path.Combine(target, "update-admin.dpapi"));
            restored.Import(backup);
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(target, "update-admin.dpapi")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

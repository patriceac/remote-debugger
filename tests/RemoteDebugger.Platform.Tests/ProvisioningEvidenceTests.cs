using System.Text.Json;
using System.Text.Json.Nodes;
using RemoteDebugger.Lab;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class ProvisioningEvidenceTests
{
    private const string RequestId = "executable-test-fixture";
    private const string RelativePath = @"update-fixtures\older\RemoteDebugger.exe";
    private static readonly string Hash = new('A', 64);

    [Fact]
    public void GenericSetupBindsTheExactProductProvisionerToItsRequest()
    {
        var receipt = Validate(Evidence());
        Assert.Equal("S-1-5-21-111-222-333-1001", receipt.UserSid);
        Assert.EndsWith(@"\stage\RemoteDebugger.exe", receipt.StagedExecutablePath);
    }

    [Theory]
    [InlineData("RequestId", "another-request")]
    [InlineData("Contract", "RemoteDebuggerProvisionV1")]
    [InlineData("ExecutableRelativePath", @"release\RemoteDebugger.exe")]
    [InlineData("ExecutableSha256", "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")]
    [InlineData("StagedExecutableSha256", "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")]
    [InlineData("StagedExecutablePath", @"C:\Temp\RemoteDebugger.exe")]
    public void RejectsEvidenceForDifferentRequestsOrBytes(string field, string replacement)
    {
        JsonObject evidence = Evidence();
        evidence[field] = replacement;
        Assert.Throws<InvalidDataException>(() => Validate(evidence));
    }

    [Fact]
    public void RejectsUnprivilegedFailedOrDifferentSetupCommands()
    {
        foreach (Action<JsonObject> tamper in new Action<JsonObject>[]
        {
            value => value["Identity"]!["IsAdministrator"] = false,
            value => value["Succeeded"] = false,
            value => value["ExitCode"] = 2,
            value => value["Arguments"] = new JsonArray("cli", "platform-status")
        })
        {
            JsonObject evidence = Evidence();
            tamper(evidence);
            Assert.Throws<InvalidDataException>(() => Validate(evidence));
        }
    }

    [Fact]
    public void PowerSetupBindsBothLabBytesAndTheExactReleaseArguments()
    {
        string labHash = new('B', 64), release = @"P:\release\RemoteDebugger.exe", lab = @"P:\lab\RemoteDebugger.Lab.exe";
        JsonObject evidence = Evidence();
        evidence["ExecutableRelativePath"] = @"lab\RemoteDebugger.Lab.exe";
        evidence["ExecutableSha256"] = labHash; evidence["StagedExecutableSha256"] = labHash;
        evidence["StagedExecutablePath"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CodexHarness", "GuestSetup", RequestId, "stage", "RemoteDebugger.Lab.exe");
        evidence["Arguments"] = new JsonArray("powersetup", release, lab, Hash);
        ProvisioningEvidence.SetupReceipt Check() => ProvisioningEvidence.ValidatePowerSetupReceipt(JsonSerializer.SerializeToElement(evidence), RequestId, labHash, release, lab, Hash);
        Assert.Equal("S-1-5-21-111-222-333-1001", Check().UserSid);
        Assert.Throws<InvalidDataException>(() => Validate(evidence));
        foreach (int index in new[] { 1, 2, 3 })
        {
            string original = evidence["Arguments"]![index]!.GetValue<string>();
            evidence["Arguments"]![index] = "different-fixture";
            Assert.Throws<InvalidDataException>(() => Check());
            evidence["Arguments"]![index] = original;
        }
        evidence["StagedExecutableSha256"] = Hash;
        Assert.Throws<InvalidDataException>(() => Check());
    }

    private static ProvisioningEvidence.SetupReceipt Validate(JsonObject evidence)
        => ProvisioningEvidence.ValidateSetupReceipt(JsonSerializer.SerializeToElement(evidence), RequestId, RelativePath, Hash);

    [Fact]
    public void PowerCredentialRequiresMatchingAccountSidAndPoolBaseline()
    {
        var local = new PowerCredentialFixture.Binding("CodexTest", "S-1-5-21-111-222-333-1001", "dd8f84c4-af2f-4542-b8a3-c2f8ed8327bf");
        PowerCredentialFixture.RequirePeerBinding(local, local);
        foreach (var peer in new[] { local with { UserName = "Other" }, local with { UserSid = "S-1-5-21-111-222-333-1002" },
            local with { PoolBaselineId = "cc8f84c4-af2f-4542-b8a3-c2f8ed8327bf" }, local with { PoolBaselineId = "" } })
            Assert.Throws<IOException>(() => PowerCredentialFixture.RequirePeerBinding(local, peer));
    }

    private static JsonObject Evidence() => new()
    {
        ["FormatVersion"] = 1, ["Contract"] = "GuestSetupV1", ["RequestId"] = RequestId,
        ["ExecutableRelativePath"] = RelativePath, ["ExecutableSha256"] = Hash, ["StagedExecutableSha256"] = Hash,
        ["StagedExecutablePath"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CodexHarness", "GuestSetup", RequestId, "stage", "RemoteDebugger.exe"),
        ["Arguments"] = new JsonArray("cli", "platform-provision"), ["Succeeded"] = true, ["ExitCode"] = 0,
        ["Identity"] = new JsonObject { ["UserSid"] = "S-1-5-21-111-222-333-1001", ["IsAdministrator"] = true },
        ["StartedUtc"] = "2026-09-20T00:00:00Z", ["CompletedUtc"] = "2026-09-20T00:00:01Z"
    };
}

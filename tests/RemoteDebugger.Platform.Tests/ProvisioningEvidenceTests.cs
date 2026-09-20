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

    private static ProvisioningEvidence.SetupReceipt Validate(JsonObject evidence)
        => ProvisioningEvidence.ValidateSetupReceipt(JsonSerializer.SerializeToElement(evidence), RequestId, RelativePath, Hash);

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

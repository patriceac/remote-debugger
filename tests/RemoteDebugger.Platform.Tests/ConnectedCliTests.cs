using System.Text.Json;
using RemoteDebugger.Core;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class ConnectedCliTests
{
    private sealed class Remote : IConnectedRemote
    {
        public string ConnectionId { get; set; } = new('A', 64);
        public int Pid { get; set; } = 123;
        public bool Connected { get; set; } = true;
        public bool Matched { get; set; } = true;
        public int ExitCode { get; set; }
        public bool CancelCommand { get; set; }
        public List<(string Operation, string Id, JsonElement Args)> Calls { get; } = [];
        public Task<Reply> CallAsync(string op, object args, CancellationToken ct, string id, int seconds)
        {
            Calls.Add((op, id, Json.Element(args)));
            if (op == "command" && CancelCommand) throw new OperationCanceledException();
            object data = op switch
            {
                "session.heartbeat" => new { session = new { connected = Connected, startedUtc = "2026-09-20T12:00:00Z" }, binaryMatched = Matched, processId = Pid },
                "status" => new { machine = "TEST-PC", processId = Pid },
                "command" => new { exitCode = ExitCode, stdout = "result", stderr = "" },
                _ => new { }
            };
            return Task.FromResult(Reply.Success(id, data));
        }
        public Task<JsonElement> UploadAsync(string local, string remote, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonElement> DownloadAsync(string remote, string local, CancellationToken ct) => throw new NotSupportedException();
    }

    private static Task<ConnectedReply> Call(Remote remote, string operation = "status", string? targetId = null, object? args = null, string? id = null) =>
        ConnectedCli.ExecuteAsync(Json.Element(new { id = id ?? Guid.NewGuid().ToString(), operation, targetId, args = args ?? new { }, timeoutSeconds = 30 }), () => remote, default);

    [Fact]
    public async Task StatusIdentifiesTheSessionAndNeverSynchronizes()
    {
        var remote = new Remote { Matched = false };
        var reply = await Call(remote);
        Assert.True(reply.Ok);
        Assert.Equal("TEST-PC", reply.Machine);
        Assert.StartsWith(remote.ConnectionId + ".", reply.TargetId);
        Assert.False(reply.Data.GetProperty("binaryMatched").GetBoolean());
        Assert.Equal(["session.heartbeat", "status"], remote.Calls.Select(c => c.Operation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangingComputerOrAgentProcessPreventsCommand(bool changeProcess)
    {
        var remote = new Remote();
        string target = (await Call(remote)).TargetId!;
        remote.Calls.Clear();
        if (changeProcess) remote.Pid++; else remote.ConnectionId = new('B', 64);
        var reply = await Call(remote, "command", target, new { file = "whoami.exe", arguments = Array.Empty<string>() });
        Assert.False(reply.Ok);
        Assert.Equal("target_changed", reply.Error);
        Assert.DoesNotContain(remote.Calls, c => c.Operation == "command");
        if (!changeProcess) Assert.Empty(remote.Calls);
    }

    [Theory]
    [InlineData("command", "{\"file\":\"whoami.exe\",\"arguments\":[]}")]
    [InlineData("maintenance.session", "{}")]
    [InlineData("session.end", "{}")]
    [InlineData("pair", "{}")]
    [InlineData("status", "{\"unexpected\":true}")]
    public async Task InvalidRequestsAreRejectedBeforeLoadingAnyConnection(string operation, string args)
    {
        bool loaded = false;
        var reply = await ConnectedCli.ExecuteAsync(Json.Element(new { operation, args = JsonSerializer.Deserialize<JsonElement>(args) }),
            () => { loaded = true; return new Remote(); }, default);
        Assert.False(reply.Ok);
        Assert.Equal("invalid_request", reply.Error);
        Assert.False(loaded);
    }

    [Theory]
    [InlineData(false, true, "not_connected")]
    [InlineData(true, false, "binary_mismatch")]
    public async Task DisconnectedOrMismatchedSessionCannotRun(bool connected, bool matched, string error)
    {
        var remote = new Remote();
        string target = (await Call(remote)).TargetId!;
        remote.Calls.Clear(); remote.Connected = connected; remote.Matched = matched;
        var reply = await Call(remote, "command", target, new { file = "whoami.exe", arguments = Array.Empty<string>() });
        Assert.False(reply.Ok);
        Assert.Equal(error, reply.Error);
        Assert.DoesNotContain(remote.Calls, c => c.Operation is "command" or "update.open");
    }

    [Fact]
    public async Task CommandKeepsExactArgumentsAndRetryIdAndReportsExitFailure()
    {
        var remote = new Remote { ExitCode = 7 };
        string target = (await Call(remote)).TargetId!, id = Guid.NewGuid().ToString();
        string[] arguments = ["a b", "\"quoted\"", "$env:USERNAME", "é"];
        var reply = await Call(remote, "command", target, new { file = "tool.exe", arguments }, id);
        Assert.False(reply.Ok); Assert.True(reply.RpcOk); Assert.Equal(7, reply.ExitCode);
        await Call(remote, "command", target, new { file = "tool.exe", arguments }, id);
        var commands = remote.Calls.Where(c => c.Operation == "command").ToArray();
        Assert.Equal(2, commands.Length);
        Assert.All(commands, call => { Assert.Equal(id, call.Id); Assert.Equal(arguments, call.Args.Strings("arguments")); });
    }

    [Fact]
    public async Task CancellationRequestsCancellationOfTheOriginalCommand()
    {
        var remote = new Remote { CancelCommand = true };
        string target = (await Call(remote)).TargetId!, id = Guid.NewGuid().ToString();
        var reply = await Call(remote, "command", target, new { file = "tool.exe", arguments = Array.Empty<string>() }, id);
        Assert.Equal("cancelled_or_timeout", reply.Error);
        Assert.Equal(id, Assert.Single(remote.Calls, c => c.Operation == "cancel").Args.Str("id"));
    }

    [Fact]
    public async Task MissingProfileIsAnExplicitNoSessionResult()
    {
        var reply = await ConnectedCli.ExecuteAsync(Json.Element(new { operation = "status" }), () => throw new FileNotFoundException(), default);
        Assert.False(reply.Ok); Assert.Equal("not_connected", reply.Error);
    }

    [Fact]
    public async Task MissingAgentIdentityCannotProduceATarget()
    {
        var reply = await Call(new Remote { Pid = 0 });
        Assert.False(reply.Ok); Assert.Equal("invalid_reply", reply.Error); Assert.Null(reply.TargetId);
    }
}

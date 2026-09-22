using System.Text.Json;
using RemoteDebugger;
using RemoteDebugger.Core;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class ConnectedWorkerTests
{
    [Fact]
    public async Task WorkerCancelsTheActiveRequestAndProcessesTheNextLineOnce()
    {
        var remote = new FakeRemote();
        string firstId = Guid.NewGuid().ToString();
        string secondId = Guid.NewGuid().ToString();
        string first = Json.Text(new
        {
            id = firstId,
            operation = "maintenance.session",
            targetId = remote.TargetId,
            timeoutSeconds = 30,
            args = new { file = "tool.exe", arguments = Array.Empty<string>() }
        });
        string cancel = Json.Text(new { cancel = firstId });
        string second = Json.Text(new { id = secondId, operation = "status", timeoutSeconds = 30, args = new { } });
        using var input = new StringReader(string.Join(Environment.NewLine, first, cancel, second));
        using var output = new StringWriter();

        await ConnectedWorker.RunAsync(input, output, () => remote, CancellationToken.None);

        JsonElement[] replies = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        Assert.Equal(2, replies.Length);
        Assert.Equal(firstId, replies[0].GetProperty("id").GetString());
        Assert.Equal("cancelled_or_timeout", replies[0].GetProperty("error").GetString());
        Assert.False(replies[0].GetProperty("ok").GetBoolean());
        Assert.Equal(secondId, replies[1].GetProperty("id").GetString());
        Assert.True(replies[1].GetProperty("ok").GetBoolean());
        Assert.Equal(2, remote.Calls.Count(call => call.Operation == "session.heartbeat"));
        Assert.Single(remote.Calls, call => call.Operation == "cancel");
        Assert.Single(remote.Calls, call => call.Operation == "maintenance.session");
    }

    [Fact]
    public async Task HeartbeatMachineAvoidsRedundantStatusPreflight()
    {
        var remote = new FakeRemote { HeartbeatIncludesMachine = true };
        string id = Guid.NewGuid().ToString();
        var request = Json.Element(new
        {
            id,
            operation = "system",
            targetId = remote.TargetId,
            timeoutSeconds = 30,
            args = new { }
        });

        ConnectedReply reply = await ConnectedCli.ExecuteAsync(request, () => remote, CancellationToken.None);

        Assert.True(reply.Ok);
        Assert.DoesNotContain(remote.Calls, call => call.Operation == "status");
    }

    [Fact]
    public async Task DownloadReturnsTheVerifiedResultExactlyOnce()
    {
        var remote = new FakeRemote { HeartbeatIncludesMachine = true };
        string target = (await StatusAsync(remote)).TargetId!;
        string id = Guid.NewGuid().ToString();
        var request = Json.Element(new
        {
            id,
            operation = "download",
            targetId = target,
            timeoutSeconds = 30,
            args = new { localPath = Path.Combine(Path.GetTempPath(), "verified-download.bin"), remotePath = "reports/result.bin" }
        });

        ConnectedReply reply = await ConnectedCli.ExecuteAsync(request, () => remote, CancellationToken.None);

        Assert.True(reply.Ok);
        Assert.Equal(1, remote.DownloadCalls);
        Assert.Equal(remote.DownloadResult.File, reply.Data.GetProperty("file").GetString());
        Assert.Equal(remote.DownloadResult.Size, reply.Data.GetProperty("size").GetInt64());
        Assert.Equal(remote.DownloadResult.Sha256, reply.Data.GetProperty("sha256").GetString());
    }

    private static Task<ConnectedReply> StatusAsync(FakeRemote remote) =>
        ConnectedCli.ExecuteAsync(Json.Element(new { id = Guid.NewGuid().ToString(), operation = "status", args = new { } }),
            () => remote, CancellationToken.None);

    private sealed class FakeRemote : IConnectedRemote
    {
        private const string StartedUtc = "2026-09-20T12:00:00Z";
        public string ConnectionId { get; } = new('A', 64);
        public bool HeartbeatIncludesMachine { get; init; }
        public VerifiedDownload DownloadResult { get; } = new("C:\\verified-download.bin", 321, new string('C', 64));
        public int DownloadCalls { get; private set; }
        public List<(string Operation, string Id)> Calls { get; } = [];
        public string TargetId => ConnectionId + "." + Safety.Hash("123|" + StartedUtc);

        public async Task<Reply> CallAsync(string operation, object args, CancellationToken ct, string id, int seconds)
        {
            Calls.Add((operation, id));
            if (operation == "maintenance.session")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Reply.Success(id, new { exitCode = 0 });
            }
            if (operation == "session.heartbeat")
            {
                object data = HeartbeatIncludesMachine
                    ? new { session = new { connected = true, startedUtc = StartedUtc }, processId = 123, binaryMatched = true, machine = "TEST-PC" }
                    : new { session = new { connected = true, startedUtc = StartedUtc }, processId = 123, binaryMatched = true };
                return Reply.Success(id, data);
            }
            if (operation == "status") return Reply.Success(id, new { processId = 123, machine = "TEST-PC" });
            if (operation == "cancel") return Reply.Success(id, new { cancelled = true });
            return Reply.Success(id, new { });
        }

        public Task<JsonElement> UploadAsync(string local, string remote, CancellationToken ct) => throw new NotSupportedException();

        public Task<JsonElement> DownloadAsync(string remote, string local, CancellationToken ct)
        {
            DownloadCalls++;
            return Task.FromResult(Json.Element(DownloadResult));
        }
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

internal interface IConnectedRemote
{
    string ConnectionId { get; }
    Task<Reply> CallAsync(string operation, object args, CancellationToken ct, string id, int seconds);
    Task<JsonElement> UploadAsync(string local, string remote, CancellationToken ct);
    Task<JsonElement> DownloadAsync(string remote, string local, CancellationToken ct);
}

// Reuse the CLI's authenticated client and verified transfer implementation.
internal sealed class ConnectedRemote(RemoteClient client) : IConnectedRemote
{
    public string ConnectionId { get; } = Safety.Hash(client.Connection.Fingerprint + "|" + client.Connection.Token);
    public Task<Reply> CallAsync(string operation, object args, CancellationToken ct, string id, int seconds) =>
        client.CallAsync(operation, args, ct, id, seconds);
    public Task<JsonElement> UploadAsync(string local, string remote, CancellationToken ct) => client.UploadAsync(local, remote, ct);
    public async Task<JsonElement> DownloadAsync(string remote, string local, CancellationToken ct)
    {
        await client.DownloadAsync(remote, local, ct);
        await using var file = File.OpenRead(local);
        return Json.Element(new { file = local, size = file.Length, sha256 = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)) });
    }
}

internal sealed record ConnectedReply(string Id, bool Ok, string? TargetId, string? Machine,
    JsonElement Data, string? Error = null, string? Message = null, bool? RpcOk = null, int? ExitCode = null);

internal static class ConnectedCli
{
    internal static async Task<ConnectedReply> ExecuteAsync(JsonElement request, Func<IConnectedRemote> load, CancellationToken ct)
    {
        string id = Guid.NewGuid().ToString(), operation = "", expected = "";
        string? target = null, machine = null;
        IConnectedRemote? remote = null;
        bool dispatched = false;
        try
        {
            id = request.Str("id", id);
            if (!Guid.TryParse(id, out _)) throw new ArgumentException("id must be a UUID.");
            operation = request.Str("operation");
            expected = request.Str("targetId");
            int seconds = request.Int("timeoutSeconds", 60);
            if (seconds is < 1 or > 300) throw new ArgumentException("timeoutSeconds must be between 1 and 300.");
            var args = request.TryGetProperty("args", out var a) ? a : Json.Element(new { });
            Validate(operation, args);
            if (operation != "status" && expected.Length == 0)
                throw new ArgumentException("Call remote_status first and supply its targetId.");

            // Load exactly once: a GUI session switch cannot retarget an in-flight request.
            remote = load();
            if (expected.Length > 0 && !expected.StartsWith(remote.ConnectionId + ".", StringComparison.Ordinal))
                throw new RemoteOperationException("target_changed", "The selected computer changed. Call remote_status again.");
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(seconds));
            var heartbeat = await remote.CallAsync("session.heartbeat", new { }, budget.Token, Guid.NewGuid().ToString(), Math.Min(seconds, 15));
            var state = RemoteClient.Require(heartbeat);
            if (!state.TryGetProperty("session", out var session) || !session.TryGetProperty("connected", out var connected) || connected.ValueKind != JsonValueKind.True)
                throw new RemoteOperationException("not_connected", "No authenticated support session is connected.");
            if (state.Int("processId") <= 0 || !DateTimeOffset.TryParse(session.Str("startedUtc"), out _))
                throw new RemoteOperationException("invalid_reply", "The agent did not identify its support session.");
            target = remote.ConnectionId + "." + Safety.Hash(state.Int("processId") + "|" + session.Str("startedUtc"));
            if (expected.Length > 0 && !Safety.Equal(target, expected))
                throw new RemoteOperationException("target_changed", "The support session changed. Call remote_status again.");

            var status = RemoteClient.Require(await remote.CallAsync("status", new { }, budget.Token, Guid.NewGuid().ToString(), Math.Min(seconds, 15)));
            machine = status.Str("machine");
            if (string.IsNullOrWhiteSpace(machine)) throw new IOException("The agent did not identify its computer.");
            bool matched = state.TryGetProperty("binaryMatched", out var match) && match.ValueKind == JsonValueKind.True;
            if (operation == "status")
                return new(id, true, target, machine, Json.Element(new { status, session, binaryMatched = matched }), RpcOk: true);
            if (!matched)
                throw new RemoteOperationException("binary_mismatch", "The controller and agent binaries differ. Synchronize them in Remote Debugger before using MCP tools.");

            dispatched = true;
            if (operation is "upload" or "download")
            {
                var data = operation == "upload"
                    ? await remote.UploadAsync(args.Str("localPath"), args.Str("remotePath"), budget.Token)
                    : await remote.DownloadAsync(args.Str("remotePath"), args.Str("localPath"), budget.Token);
                return new(id, true, target, machine, data, RpcOk: true);
            }
            var reply = await remote.CallAsync(operation, args, budget.Token, id, seconds);
            int? exit = reply.Data.ValueKind == JsonValueKind.Object && reply.Data.TryGetProperty("exitCode", out var code) ? code.GetInt32() : null;
            bool succeeded = reply.Ok && (operation != "command" || exit == 0);
            return new(id, succeeded, target, machine, reply.Data,
                reply.Error ?? (succeeded ? null : "command_failed"),
                reply.Message ?? (succeeded ? null : "The remote command did not exit successfully."), reply.Ok, exit);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && dispatched && remote != null && operation == "command")
            {
                try { using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await remote.CallAsync("cancel", new { id }, cancel.Token, Guid.NewGuid().ToString(), 5); }
                catch (Exception) { /* Keep the original failure and request ID for inspection. */ }
            }
            string error = ex switch
            {
                RemoteOperationException rpc => rpc.Code,
                FileNotFoundException when remote == null => "not_connected",
                OperationCanceledException => "cancelled_or_timeout",
                ArgumentException or InvalidOperationException or JsonException => "invalid_request",
                _ => "transport_or_input"
            };
            return new(id, false, target ?? (expected.Length > 0 ? expected : null), machine, Json.Element(null), error,
                error == "not_connected" ? "No saved authenticated connection. Connect a computer in Remote Debugger first." : ex.Message);
        }
    }

    private static void Validate(string operation, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("args must be an object.");
        string[] allowed = operation switch
        {
            "status" or "system" or "processes" => [],
            "process.info" => ["pid"],
            "file.info" => ["path"],
            "screenshot" => ["monitor", "maxWidth", "quality"],
            "command" => ["file", "arguments"],
            "upload" or "download" => ["localPath", "remotePath"],
            _ => throw new ArgumentException("This operation is not exposed by the connected-session interface.")
        };
        if (args.EnumerateObject().Any(p => !allowed.Contains(p.Name))) throw new ArgumentException("Unexpected operation argument.");
        void Text(string key) { if (string.IsNullOrWhiteSpace(args.Str(key)) || args.Str(key).Contains('\0')) throw new ArgumentException(key + " must be nonempty text without NUL."); }
        if (operation == "process.info" && args.Int("pid") <= 0) throw new ArgumentException("pid must be positive.");
        if (operation == "file.info") Text("path");
        if (operation == "command")
        {
            Text("file");
            if (!args.TryGetProperty("arguments", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 256 ||
                list.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || v.GetString()!.Contains('\0')))
                throw new ArgumentException("arguments must be an array of at most 256 strings without NUL.");
        }
        if (operation is "upload" or "download")
        {
            Text("localPath"); Text("remotePath");
            if (!Path.IsPathFullyQualified(args.Str("localPath"))) throw new ArgumentException("localPath must be an absolute path on the controlling PC.");
        }
        if (operation == "screenshot" && (args.Int("monitor") < -1 || args.Int("maxWidth", 1920) is < 320 or > 3840 || args.Int("quality", 80) is < 1 or > 100))
            throw new ArgumentException("Invalid screenshot monitor, width or quality.");
    }
}

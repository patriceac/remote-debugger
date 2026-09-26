using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using RemoteDebugger.Core;

namespace RemoteDebugger;

internal static class InteractiveInputBroker
{
    private const string Prefix = "RemoteDebugger.Input.";

    internal static void ValidateRequest(Request request)
    {
        if (request.Operation != "ui.input" || !Guid.TryParse(request.Id, out _) || request.Args.GetRawText().Length > 32768)
            throw new ArgumentException("The interactive helper accepts bounded input requests only.");
    }

    internal static async Task ServeAsync(NamedPipeServerStream controller, VerifiedProcessIdentity caller, Request open, CancellationToken ct)
    {
        string name = Prefix + Guid.NewGuid().ToString("N");
        using var helper = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            0, 0, SupportPipeSecurity.Create(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value));
        using var process = Process.GetProcessById(InteractiveProcessLauncher.StartInputHelper(caller.SessionId, caller.UserSid, name));
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct); connect.CancelAfter(TimeSpan.FromSeconds(15));
            await helper.WaitForConnectionAsync(connect.Token);
            if (!SupportPipeIdentity.GetNamedPipeClientProcessId(helper.SafePipeHandle, out uint pid) || pid != process.Id)
                throw new UnauthorizedAccessException("Unexpected interactive helper process.");
            await Wire.WriteAsync(controller, Reply.Success(open.Id, new { ready = true }), ct);
            while (controller.IsConnected && !ct.IsCancellationRequested)
            {
                var request = await Wire.ReadAsync<Request>(controller, ct);
                ValidateRequest(request);
                var current = ProcessIdentity.Capture(caller.ProcessId);
                if (current != caller) throw new UnauthorizedAccessException("The support process identity changed.");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(SupportOperationTimeouts.InputSeconds(request.Args.Str("kind"))));
                if (request.Args.Str("kind") == "secureAttention")
                {
                    await Wire.WriteAsync(helper, request with { Args = Json.Element(new { kind = "release" }) }, deadline.Token);
                    RemoteClient.Require(await Wire.ReadAsync<Reply>(helper, deadline.Token));
                    Reply reply;
                    try { SecureAttention.Send(controller); reply = Reply.Success(request.Id, new { sent = true }); }
                    catch (Exception ex) { reply = Reply.Failure(request.Id, "input_blocked", ex.Message); }
                    await Wire.WriteAsync(controller, reply, deadline.Token);
                    continue;
                }
                await Wire.WriteAsync(helper, request, deadline.Token);
                await Wire.WriteAsync(controller, await Wire.ReadAsync<Reply>(helper, deadline.Token), deadline.Token);
            }
        }
        finally
        {
            helper.Dispose();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await process.WaitForExitAsync(stop.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); }
        }
    }

    internal static async Task<int> RunHelperAsync(string pipeName)
    {
        if (!WindowsIdentity.GetCurrent().IsSystem || Process.GetCurrentProcess().SessionId <= 0 ||
            !pipeName.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(pipeName[Prefix.Length..], "N", out _)) return 3;
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var connect = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await pipe.ConnectAsync(connect.Token);
            SupportPipeIdentity.VerifyServer(pipe);
            while (pipe.IsConnected)
            {
                var request = await Wire.ReadAsync<Request>(pipe, CancellationToken.None);
                Reply reply;
                try { ValidateRequest(request); reply = Reply.Success(request.Id, Native.HandleSharedInput(request.Args)); }
                catch (Exception ex) { reply = Reply.Failure(request.Id, "input_blocked", ex.Message); }
                await Wire.WriteAsync(pipe, reply, CancellationToken.None);
            }
            return 0;
        }
        catch (IOException) { return 0; }
        catch { return 2; }
        finally { Native.ReleaseAllInput(); }
    }
}

internal sealed class PrivilegedInputSession
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private readonly Func<CancellationToken, Task<Stream>> open;
    private readonly Action releaseLocal;
    private Stream? pipe;
    private int generation;
    private int ready;

    public PrivilegedInputSession() : this(async ct => await SupportPlatform.OpenBrokerPipeAsync(ct, TokenImpersonationLevel.Impersonation), () => Native.ReleaseAllInput()) { }
    internal PrivilegedInputSession(Func<CancellationToken, Task<Stream>> open, Action releaseLocal)
    { this.open = open; this.releaseLocal = releaseLocal; }

    public bool Ready => Volatile.Read(ref ready) != 0;
    public Task PrepareAsync(Func<bool> permitted, CancellationToken ct) =>
        SendAsync(new { kind = "release" }, permitted, ct, prepareOnly: true);

    public async Task ReleaseAsync()
    {
        if (!Ready) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await SendAsync(new { kind = "release" }, () => true, deadline.Token, releaseOnly: true); }
        catch { }
    }

    public async Task<System.Text.Json.JsonElement> SendAsync(object args, Func<bool> permitted, CancellationToken ct, bool prepareOnly = false, bool releaseOnly = false)
    {
        await gate.WaitAsync(ct);
        try
        {
            int expectedGeneration = Volatile.Read(ref generation);
            if (!permitted()) throw new InputBlockedException(MaintenanceSession.DisabledMessage);
            if (prepareOnly && Ready) return Json.Element(new { ready = true });
            if (pipe == null)
            {
                if (releaseOnly) return Json.Element(new { sent = true });
                var candidate = await open(ct);
                try
                {
                    await Wire.WriteAsync(candidate, new Request(Guid.NewGuid().ToString(), "", "input.open", Json.Element(new { })), ct);
                    RemoteClient.Require(await Wire.ReadAsync<Reply>(candidate, ct));
                    lock (sync)
                    {
                        if (!permitted() || expectedGeneration != generation) throw new OperationCanceledException("Input session ended.");
                        releaseLocal(); pipe = candidate;
                    }
                }
                catch { candidate.Dispose(); throw; }
            }
            var current = pipe ?? throw new OperationCanceledException("Input session ended.");
            var payload = Json.Element(args);
            await Wire.WriteAsync(current, new Request(Guid.NewGuid().ToString(), "", "ui.input", payload, SupportOperationTimeouts.InputSeconds(payload.Str("kind"))), ct);
            var result = RemoteClient.Require(await Wire.ReadAsync<Reply>(current, ct));
            lock (sync)
            {
                if (expectedGeneration != generation || !ReferenceEquals(pipe, current)) throw new OperationCanceledException("Input session ended.");
                Volatile.Write(ref ready, 1);
            }
            return result;
        }
        catch { End(); throw; }
        finally { gate.Release(); }
    }

    public void End()
    {
        lock (sync) { generation++; Volatile.Write(ref ready, 0); pipe?.Dispose(); pipe = null; }
    }
}

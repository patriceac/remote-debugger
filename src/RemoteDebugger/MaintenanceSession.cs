using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

// Local UAC consent opens a visible, parent-bound, one-hour command helper.
// The normal interactive agent remains the only network listener.
public sealed class MaintenanceSession(string root) : IDisposable
{
    private sealed record Active(MaintenanceLease Lease, Process Helper, string StopFile);
    private Active? active;
    private readonly SemaphoreSlim startGate = new(1);
    public object Status
    {
        get { var current = Volatile.Read(ref active); bool enabled = current != null && !current.Helper.HasExited && current.Lease.ExpiresUtc > DateTimeOffset.UtcNow && !File.Exists(current.StopFile); return new { active = enabled, expiresUtc = enabled ? current!.Lease.ExpiresUtc : (DateTimeOffset?)null, localConsentRequired = !enabled }; }
    }
    public async Task StartAsync(CancellationToken ct)
    {
        await startGate.WaitAsync(ct);
        try
        {
            if (Json.Element(Status).GetProperty("active").GetBoolean()) return;
            Dispose();
            using var parent = Process.GetCurrentProcess();
            var lease = new MaintenanceLease(parent.Id, parent.StartTime.ToUniversalTime().Ticks, "RemoteDebugger.Admin." + Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddHours(1));
            string dir = Path.Combine(root, "maintenance"); Directory.CreateDirectory(dir);
            string stopFile = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".stop");
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            psi.ArgumentList.Add("--maintenance-session"); psi.ArgumentList.Add(Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(lease, Json.Options))); psi.ArgumentList.Add(stopFile);
            var helper = await Task.Run(() => Process.Start(psi) ?? throw new IOException("Maintenance helper did not start."), ct);
            var current = new Active(lease, helper, stopFile); Volatile.Write(ref active, current);
            try
            {
                using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(35)); using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, ready.Token);
                using var pipe = await ConnectAsync(current, linked.Token);
                await Wire.WriteAsync(pipe, new Request(Guid.NewGuid().ToString(), "", "status", Json.Element(new { })), linked.Token);
                RemoteClient.Require(await Wire.ReadAsync<Reply>(pipe, linked.Token));
            }
            catch { Dispose(); throw; }
        }
        finally { startGate.Release(); }
    }
    private static async Task<NamedPipeClientStream> ConnectAsync(Active current, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", current.Lease.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(ct);
            if (!MaintenanceHelper.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid) || serverPid != current.Helper.Id) throw new UnauthorizedAccessException("Maintenance helper identity mismatch.");
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }
    public async Task<object> RunAsync(string file, string[] arguments, CancellationToken ct)
    {
        var current = Volatile.Read(ref active);
        if (current == null || !Json.Element(Status).GetProperty("active").GetBoolean()) throw new InvalidOperationException("On the agent, enable the one-hour administrator maintenance session first. Local UAC consent is required once.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var pipe = await ConnectAsync(current, timeout.Token);
        await Wire.WriteAsync(pipe, new Request(Guid.NewGuid().ToString(), "", "command", Json.Element(new { file, arguments }), 300), timeout.Token);
        return RemoteClient.Require(await Wire.ReadAsync<Reply>(pipe, timeout.Token));
    }
    public void Dispose()
    {
        var current = Interlocked.Exchange(ref active, null);
        if (current != null) File.WriteAllText(current.StopFile, "Local authorization ended.");
    }
}

internal static class MaintenanceHelper
{
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    internal static int Run(string encoded, string stopFile)
    {
        if (!Native.IsElevated()) return 3;
        var lease = JsonSerializer.Deserialize<MaintenanceLease>(Convert.FromBase64String(encoded), Json.Options)!; lease.Validate(DateTimeOffset.UtcNow);
        using var parent = Process.GetProcessById(lease.ParentPid);
        if (parent.StartTime.ToUniversalTime().Ticks != lease.ParentStartTicks || !string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return 3;
        Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2); Forms.Application.EnableVisualStyles();
        using var window = new MaintenanceWindow(lease, parent, stopFile); Forms.Application.Run(window); return 0;
    }
    private sealed class MaintenanceWindow : Forms.Form
    {
        private readonly CancellationTokenSource lifetime = new();
        private readonly Forms.Timer timer = new() { Interval = 500 };
        private readonly Forms.Label state = new() { Dock = Forms.DockStyle.Fill, Padding = new Forms.Padding(20), AutoSize = false };
        private Task? server;
        private bool closing, drained;
        public MaintenanceWindow(MaintenanceLease lease, Process parent, string stopFile)
        {
            Text = "Remote Debugger — Maintenance administrateur autorisée"; Width = 660; Height = 260; StartPosition = Forms.FormStartPosition.CenterScreen; Font = new System.Drawing.Font("Segoe UI", 11);
            var end = new Forms.Button { Text = "Couper la maintenance administrateur", Dock = Forms.DockStyle.Bottom, Height = 50 }; end.Click += (_, _) => Close(); Controls.Add(state); Controls.Add(end);
            state.Text = "Le pilote appairé peut exécuter des commandes administrateur pendant cette session.\r\nAucun service ni démarrage automatique n’est installé.";
            timer.Tick += (_, _) => { if (parent.HasExited || DateTimeOffset.UtcNow >= lease.ExpiresUtc || File.Exists(stopFile)) Close(); else state.Text = $"MAINTENANCE ADMINISTRATEUR AUTORISÉE\r\nFin à {lease.ExpiresUtc.ToLocalTime():HH:mm:ss}. Le pilote appairé peut enchaîner les commandes.\r\nFermer cette fenêtre ou couper l’agent annule les commandes en cours."; };
            Shown += async (_, _) => { timer.Start(); server = Task.Run(() => ServeAsync(lease, lifetime.Token)); try { await server; } catch (OperationCanceledException) { } catch (Exception ex) { state.Text = "Auxiliaire arrêté : " + ex.Message; } };
            FormClosing += async (_, e) => { if (drained) return; e.Cancel = true; if (closing) return; closing = true; timer.Stop(); lifetime.Cancel(); if (server != null) try { await server.WaitAsync(TimeSpan.FromSeconds(8)); } catch (Exception) { } drained = true; Close(); };
            FormClosed += (_, _) => { timer.Dispose(); if (File.Exists(stopFile)) File.Delete(stopFile); };
        }
        private async Task ServeAsync(MaintenanceLease lease, CancellationToken ct)
        {
            var acl = new PipeSecurity(); acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            acl.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
            var pending = new List<Task>();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var pipe = NamedPipeServerStreamAcl.Create(lease.PipeName, PipeDirection.InOut, 16, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, acl);
                    try { await pipe.WaitForConnectionAsync(ct); }
                    catch { pipe.Dispose(); throw; }
                    pending.RemoveAll(t => t.IsCompleted); pending.Add(HandleAsync(pipe, lease, ct));
                }
            }
            finally { try { await Task.WhenAll(pending); } catch (OperationCanceledException) { } }
        }
        private static async Task HandleAsync(NamedPipeServerStream pipe, MaintenanceLease lease, CancellationToken ct)
        {
            using (pipe)
            using (var budget = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                budget.CancelAfter(TimeSpan.FromSeconds(305));
                try
                {
                    if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint caller) || caller != lease.ParentPid) return;
                    var request = await Wire.ReadAsync<Request>(pipe, budget.Token); Reply reply;
                    if (request.Operation == "status") reply = Reply.Success(request.Id, new { active = true, elevated = true, lease.ExpiresUtc });
                    else
                    {
                        MaintenanceLease.ValidateCommand(request); budget.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
                        // The controller closes its per-command pipe on cancellation. Watch it while the process runs.
                        var disconnected = Task.Run(async () => { try { byte[] b = new byte[1]; await pipe.ReadAsync(b, budget.Token); budget.Cancel(); } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { if (!budget.IsCancellationRequested) budget.Cancel(); } });
                        try { reply = Reply.Success(request.Id, await Operations.RunAsync(request.Args.Str("file"), request.Args.Strings("arguments"), budget.Token)); }
                        catch (Exception ex) { reply = Reply.Failure(request.Id, "maintenance_failed", ex.Message); }
                        await Wire.WriteAsync(pipe, reply, ct); budget.Cancel(); await disconnected; return;
                    }
                    await Wire.WriteAsync(pipe, reply, budget.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or ArgumentException) { }
            }
        }
    }
}

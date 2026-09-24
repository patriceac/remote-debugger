using System.Text.Json;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

public sealed partial class MainForm
{
    private readonly Forms.Button restartRemote = RailSubButton(() => UiText.RestartRemotePc, "restartRemotePc");
    private readonly Forms.Button shutdownRemote = RailSubButton(() => UiText.ShutdownRemotePc, "shutdownRemotePc");
    private CancellationTokenSource? powerLifetime;
    private bool powerBusy;

    private void InitializePowerControls(Forms.Control container)
    {
        container.Controls.Add(restartRemote); container.Controls.Add(shutdownRemote);
        restartRemote.Click += async (_, _) => await RequestRemotePowerAsync(true);
        shutdownRemote.Click += async (_, _) => await RequestRemotePowerAsync(false);
    }

    private void RefreshPowerControls()
    {
        restartRemote.Visible = shutdownRemote.Visible = rolePages.SelectedIndex == 1 && supportSession;
        restartRemote.Enabled = shutdownRemote.Enabled = supportSession && heartbeatHealthy && !powerBusy && !clientUpdateBusy &&
            !pairingBusy && !terminating && action == null && fileTransferLifetime == null;
    }

    private async Task RequestRemotePowerAsync(bool restart)
    {
        if (!restartRemote.Enabled || client is not { } target) return;
        powerBusy = true; RefreshControllerControls();
        using var lifetime = new CancellationTokenSource(); powerLifetime = lifetime;
        string id = Guid.NewGuid().ToString("N"), password = "";
        bool requested = false, paused = false, restored = false, once = false, automatic = false;
        PowerProgressForm? progress = null;
        try
        {
            var preflight = RemoteClient.Require(await target.CallAsync("power.preflight", ct: lifetime.Token, seconds: 30))
                .Deserialize<PowerPreflight>(Json.Options) ?? throw new InvalidDataException("Missing restart preflight.");
            automatic = preflight.ExpectedReturn == "existing_automatic_desktop";
            string expectation = automatic ? UiText.ExistingAutomaticDesktopExpected : UiText.ManualSignInExpected;
            if (restart)
            {
                using var options = new RestartOptionsForm(preflight);
                if (options.ShowDialog(this) != Forms.DialogResult.OK) return;
                once = options.OneTimeLogin;
                password = options.Password;
                if (once)
                {
                    _ = RemoteClient.Require(await target.CallAsync("power.validateLogin", new { password }, lifetime.Token, seconds: 30));
                    expectation = UiText.AutomaticDesktopExpected; automatic = true;
                }
            }
            string question = UiText.Format(restart ? UiText.ConfirmRemoteRestart : UiText.ConfirmRemoteShutdown, preflight.Machine);
            if (restart) question += "\n\n" + expectation;
            if (Forms.MessageBox.Show(this, question, restart ? UiText.RestartRemotePc : UiText.ShutdownRemotePc,
                Forms.MessageBoxButtons.OKCancel, Forms.MessageBoxIcon.Warning, Forms.MessageBoxDefaultButton.Button2) != Forms.DialogResult.OK) return;

            progress = new PowerProgressForm(restart, restart ? expectation : preflight.Machine + " · " + UiText.ShutdownRemotePc, lifetime.Cancel);
            progress.Show(this); progress.SetStage(0, UiText.PreparingPowerOperation);
            clipboardLifetime?.Cancel(); localClipboard?.Pause(); heartbeatLifetime?.Cancel();
            StopStream(() => UiText.PreparingPowerOperation); inputRecoveryTimer.Stop(); InvalidateInputSession();
            await ReleaseHeldInputAsync(target); await target.CloseHeartbeatChannelAsync();
            heartbeatHealthy = liveFrameFresh = false; paused = true;
            requested = true;
            var result = RemoteClient.Require(await target.CallAsync("power.issue", new
            {
                operationId = id, action = restart ? "restart" : "shutdown", oneTimeLogin = once, password
            }, lifetime.Token, seconds: 30));
            password = "";
            var issued = result.GetProperty("issued");
            var receivedAt = DateTimeOffset.UtcNow;
            var serverNow = issued.GetProperty("serverUtc").GetDateTimeOffset();
            var countdownEnd = receivedAt + (issued.GetProperty("countdownEndUtc").GetDateTimeOffset() - serverNow);
            progress.SetDeadline(countdownEnd, countdown: true);
            progress.SetStage(1, restart ? UiText.RestartCountdown : UiText.ShutdownCountdown);
            diagnostics.Record(restart ? "restart_accepted" : "shutdown_accepted", DiagnosticContext());
            while (DateTimeOffset.UtcNow < countdownEnd) await Task.Delay(100, lifetime.Token);
            if (!restart)
            {
                ClearPowerControllerSession(target);
                SetFooterMessage(() => UiText.ShutdownAccepted); SetFooterDetail(() => UiText.PowerOffNotConfirmed); RefreshFooter();
                progress.Complete(UiText.PowerOffNotConfirmed);
                return;
            }

            string ticket = result.Str("reconnectTicket");
            var deadline = receivedAt + (result.GetProperty("reconnectExpiresUtc").GetDateTimeOffset() - serverNow);
            progress.SetDeadline(deadline, countdown: false);
            progress.SetStage(2, automatic ? UiText.WaitingForRemotePc : UiText.WaitingForManualSignIn);
            lifetime.CancelAfter(deadline - DateTimeOffset.UtcNow > TimeSpan.Zero ? deadline - DateTimeOffset.UtcNow : TimeSpan.FromMilliseconds(1));
            while (!lifetime.IsCancellationRequested)
            {
                bool agentReturned = false;
                try
                {
                    _ = RemoteClient.Require(await target.CallAsync("power.resume", new { ticket }, lifetime.Token, seconds: 8));
                    agentReturned = true;
                    progress.SetStage(3, UiText.RestoringSupport);
                    var heartbeat = await target.HeartbeatAsync(lifetime.Token);
                    if (!heartbeat.TryGetProperty("binaryMatched", out var matched) || matched.ValueKind != JsonValueKind.True)
                        throw new InvalidOperationException("The returning agent identity does not match.");
                    var frame = RemoteClient.Require(await target.CallAsync("screenshot", new { monitor = MonitorValue() }, lifetime.Token, seconds: 15))
                        .Deserialize<ScreenFrame>(Json.Options) ?? throw new InvalidDataException("A fresh desktop frame is required.");
                    RemoteClient.Require(await target.SendInputAsync(new { kind = "release" }, lifetime.Token, seconds: 30));
                    progress.SetStage(4, UiText.RestoringSupport);
                    _ = RemoteClient.Require(await target.CallAsync("power.ready", ct: lifetime.Token, seconds: 30));
                    if (!ReferenceEquals(client, target)) return;
                    Present(frame); inputState.Released(); heartbeatHealthy = true; clientUpToDate = true;
                    restored = true; powerBusy = false; StartHeartbeat(); _ = StartStreamAsync();
                    progress.Complete(UiText.DesktopReady);
                    diagnostics.Record("restart_desktop_ready", DiagnosticContext());
                    SetFooterMessage(() => UiText.DesktopReady); RefreshFooter();
                    return;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    await target.CloseHeartbeatChannelAsync();
                    progress.SetStage(agentReturned ? 3 : 2, agentReturned ? UiText.WindowsNeedsSignInOrUnlock : automatic ? UiText.WaitingForRemotePc : UiText.WaitingForManualSignIn);
                    await Task.Delay(2000, lifetime.Token);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            bool cancelled = requested && await TryCancelRemotePowerAsync(target, id);
            if (cancelled && ReferenceEquals(client, target) && !terminating)
            { restored = true; powerBusy = false; StartHeartbeat(); _ = StartStreamAsync(); }
            else if (requested)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { _ = await target.CallAsync("power.cancelWait", ct: stop.Token, seconds: 4); } catch { }
                ClearPowerControllerSession(target);
                connectionState.SetText(() => restart
                    ? UiText.RestartWaitStopped + ". " + UiText.IssuedRestartCannotBeUndone
                    : UiText.ShutdownAccepted + " " + UiText.PowerOffNotConfirmed);
            }
            SetFooterMessage(() => cancelled ? UiText.PowerCountdownCancelled : restart ? UiText.RestartWaitStopped : UiText.ShutdownAccepted);
            SetFooterDetail(() => cancelled ? UiText.SessionEstablished : restart ? UiText.IssuedRestartCannotBeUndone : UiText.PowerOffNotConfirmed); RefreshFooter();
        }
        catch (Exception ex)
        {
            if (requested)
            {
                bool cancelled = await TryCancelRemotePowerAsync(target, id);
                if (!cancelled) ClearPowerControllerSession(target);
            }
            diagnostics.Record("power_operation_failed", DiagnosticContext(), exception: ex, incident: true);
            Forms.MessageBox.Show(this, ex.Message, restart ? UiText.RestartRemotePc : UiText.ShutdownRemotePc,
                Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
        }
        finally
        {
            password = ""; powerBusy = false;
            if (ReferenceEquals(powerLifetime, lifetime)) powerLifetime = null;
            if (progress is { Finished: false }) progress.CloseAfterOperation();
            if (paused && !restored && ReferenceEquals(client, target) && supportSession && !terminating)
            { StartHeartbeat(); _ = StartStreamAsync(); }
            RefreshControllerControls(); RefreshClipboardSharing();
        }
    }

    private static async Task<bool> TryCancelRemotePowerAsync(RemoteClient target, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try
        {
            var reply = RemoteClient.Require(await target.CallAsync("power.cancel", new { operationId = id }, timeout.Token, seconds: 5));
            return reply.TryGetProperty("cancelled", out var cancelled) && cancelled.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    private void ClearPowerControllerSession(RemoteClient target)
    {
        if (!ReferenceEquals(client, target)) return;
        heartbeatLifetime?.Cancel(); clipboardLifetime?.Cancel(); localClipboard?.Pause();
        StopStream(() => UiText.NoActiveConnection); supportSession = heartbeatHealthy = false; sessionGeneration++;
        // Forget this grant locally, even when the target is unreachable. A new support action must pair again.
        try
        {
            if (File.Exists(RemoteClient.DefaultPath) && RemoteClient.Load().Connection.Fingerprint == target.Connection.Fingerprint)
                File.Delete(RemoteClient.DefaultPath);
        }
        catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException) { }
        ClearControllerSession(); SelectControllerPage(0);
    }
}

internal sealed class RestartOptionsForm : Forms.Form
{
    private readonly Forms.CheckBox once;
    private readonly Forms.TextBox password;
    public bool OneTimeLogin => once.Checked;
    public string Password => password.Text;
    internal RestartOptionsForm(PowerPreflight preflight)
    {
        Text = UiText.RestartRemotePc; Name = "restartOptions"; StartPosition = Forms.FormStartPosition.CenterParent;
        FormBorderStyle = Forms.FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ClientSize = new(640, 340);
        Font = new("Segoe UI", 10); Padding = new(20);
        var layout = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, FlowDirection = Forms.FlowDirection.TopDown, WrapContents = false };
        layout.Controls.Add(new Forms.Label { AutoSize = true, MaximumSize = new(590, 0), Text = preflight.ExpectedReturn == "existing_automatic_desktop" ? UiText.ExistingAutomaticDesktopExpected : UiText.ManualSignInExpected });
        once = new() { Name = "oneTimeLogin", AutoSize = true, Text = UiText.SignInOnce, Enabled = preflight.OneTimeLoginAvailable, Margin = new(0, 16, 0, 4) };
        layout.Controls.Add(once);
        layout.Controls.Add(new Forms.Label { AutoSize = true, MaximumSize = new(590, 0), Text = preflight.Account + "\n" +
            (preflight.OneTimeLoginAvailable ? UiText.WindowsPasswordForOneRestart : UiText.OneTimeLoginUnavailable) });
        password = new() { Name = "oneTimePassword", UseSystemPasswordChar = true, Enabled = false, Width = 360, MaxLength = 512 };
        layout.Controls.Add(password); once.CheckedChanged += (_, _) => { password.Enabled = once.Checked; if (!once.Checked) password.Clear(); };
        var buttons = new Forms.FlowLayoutPanel { AutoSize = true, Margin = new(0, 14, 0, 0) };
        var ok = new Forms.Button { Name = "continueRestart", Text = UiText.Continue, AutoSize = true, DialogResult = Forms.DialogResult.OK };
        var cancel = new Forms.Button { Text = UiText.Cancel, AutoSize = true, DialogResult = Forms.DialogResult.Cancel };
        buttons.Controls.Add(ok); buttons.Controls.Add(cancel); layout.Controls.Add(buttons); Controls.Add(layout);
        AcceptButton = ok; CancelButton = cancel;
        AppTheme.Apply(this);
    }
    protected override void Dispose(bool disposing) { if (disposing) password.Clear(); base.Dispose(disposing); }
}

internal sealed class PowerProgressForm : Forms.Form
{
    private readonly Forms.Label stages = new() { Name = "powerStages", AutoSize = true, MaximumSize = new(540, 0) };
    private readonly Forms.Label status = new() { Name = "powerState", AutoSize = true, MaximumSize = new(540, 0) };
    private readonly Forms.Label countdown = new() { Name = "powerCountdown", AutoSize = true };
    private readonly Forms.ProgressBar activity = new() { Name = "powerProgress", Width = 530, Height = 8, Style = Forms.ProgressBarStyle.Marquee };
    private readonly Forms.Button cancel = new() { Name = "cancelPowerWait", AutoSize = true };
    private readonly Forms.Timer timer = new() { Interval = 250 };
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private readonly Action cancelOperation;
    private readonly bool restart;
    private DateTimeOffset? deadline;
    private bool closingAllowed, powerCountdown;
    public bool Finished { get; private set; }
    internal PowerProgressForm(bool restart, string expectation, Action cancelOperation)
    {
        this.restart = restart; this.cancelOperation = cancelOperation;
        Text = restart ? UiText.RestartRemotePc : UiText.ShutdownRemotePc; Name = "powerProgressWindow";
        StartPosition = Forms.FormStartPosition.CenterParent; ClientSize = new(590, 375); MinimumSize = new(530, 375);
        MaximizeBox = MinimizeBox = false; Font = new("Segoe UI", 10); Padding = new(20);
        var layout = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, FlowDirection = Forms.FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        layout.Controls.Add(new Forms.Label { AutoSize = true, MaximumSize = new(540, 0), Text = expectation, Margin = new(0, 0, 0, 16) });
        layout.Controls.Add(stages); layout.Controls.Add(activity); layout.Controls.Add(status); layout.Controls.Add(countdown);
        cancel.Text = UiText.Cancel; cancel.Margin = new(0, 15, 0, 0);
        cancel.Click += (_, _) => { if (Finished) CloseAfterOperation(); else { cancel.Enabled = false; cancelOperation(); } };
        layout.Controls.Add(cancel); Controls.Add(layout);
        timer.Tick += (_, _) => RenderCountdown(); timer.Start();
        FormClosing += (_, e) => { if (!closingAllowed && !Finished) { e.Cancel = true; cancel.Enabled = false; cancelOperation(); } };
        AppTheme.Apply(this);
    }
    internal void SetDeadline(DateTimeOffset value, bool countdown)
    { deadline = value; powerCountdown = countdown; cancel.Text = countdown ? UiText.Cancel : UiText.CancelReconnectWait; RenderCountdown(); }
    internal void SetStage(int stage, string message)
    {
        string[] steps = restart ? [UiText.PreparingPowerOperation, UiText.RestartRequestAccepted, UiText.WaitingForRemotePc, UiText.WindowsSignIn, UiText.RestoringSupport, UiText.DesktopReady]
            : [UiText.PreparingPowerOperation, UiText.ShutdownCountdown];
        stages.Text = string.Join("\n", steps.Select((label, index) => (index < stage ? "✓  " : index == stage ? "›  " : "    ") + label));
        status.Text = message;
    }
    private void RenderCountdown()
    {
        var remaining = deadline is { } value ? value - DateTimeOffset.UtcNow : TimeSpan.Zero;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        countdown.Text = deadline == null ? UiText.Format(UiText.PowerElapsed, DateTimeOffset.UtcNow - started) :
            UiText.Format(powerCountdown ? UiText.PowerCountdownRemaining : UiText.ReconnectTimeRemaining, remaining);
    }
    internal void Complete(string message)
    {
        Finished = true; timer.Stop(); activity.Style = Forms.ProgressBarStyle.Continuous; activity.Value = 100;
        if (restart) SetStage(6, message); else status.Text = message;
        countdown.Text = ""; cancel.Text = UiText.Close; cancel.Enabled = true;
    }
    internal void CloseAfterOperation() { closingAllowed = true; Close(); }
    protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
}

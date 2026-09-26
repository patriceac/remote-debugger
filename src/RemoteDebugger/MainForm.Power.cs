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
            using (var options = new PowerConfirmationForm(preflight, restart))
            {
                if (options.ShowDialog(this) != Forms.DialogResult.OK) return;
                once = options.OneTimeLogin;
                password = options.Password;
                if (once)
                {
                    _ = RemoteClient.Require(await target.CallAsync("power.validateLogin", new { password }, lifetime.Token, seconds: 30));
                    expectation = UiText.AutomaticDesktopExpected; automatic = true;
                }
            }
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
            if (File.Exists(ConnectionPath) && RemoteClient.Load(ConnectionPath).Connection.Fingerprint == target.Connection.Fingerprint)
                File.Delete(ConnectionPath);
        }
        catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException) { }
        ClearControllerSession(); SelectControllerPage(0);
    }
}

internal sealed class PowerConfirmationForm : Forms.Form
{
    private readonly Forms.CheckBox once = new SignInCheckBox { Name = "oneTimeLogin", TabIndex = 0 };
    private readonly Forms.TextBox password = new() { Name = "oneTimePassword", UseSystemPasswordChar = true, MaxLength = 512, BorderStyle = Forms.BorderStyle.None };
    private readonly Forms.Label heading = Copy(), summary = Copy(), countdown = Copy(), unsaved = Copy(), hint = Copy(), account = Copy(), passwordLabel = Copy();
    private readonly Forms.Panel notice = new(), footer = new(), passwordField;
    private readonly Forms.Control glyph;
    private readonly Forms.Button confirm, cancel;
    private readonly bool restart;
    private bool arranging, ready;
    public bool OneTimeLogin => restart && once.Enabled && once.Checked;
    public string Password => OneTimeLogin ? password.Text : "";

    internal PowerConfirmationForm(PowerPreflight preflight, bool restart)
    {
        this.restart = restart;
        SuspendLayout();
        Text = restart ? UiText.RestartRemotePc : UiText.ShutdownRemotePc; Name = restart ? "restartOptions" : "shutdownConfirm";
        StartPosition = Forms.FormStartPosition.CenterParent; FormBorderStyle = Forms.FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        AutoScaleDimensions = new(96, 96); AutoScaleMode = Forms.AutoScaleMode.Dpi;
        Font = new("Segoe UI", 12.5f); BackColor = Color.White; ForeColor = Color.FromArgb(9, 18, 38);
        ClientSize = new(restart ? 630 : 570, 390);
        heading.Name = "powerQuestion"; heading.Font = new("Segoe UI", 19.5f, FontStyle.Bold); heading.ForeColor = ForeColor;
        heading.Text = UiText.Format(UiText.Get(restart ? "RestartPcQuestion" : "ShutdownPcQuestion"), preflight.Machine);
        glyph = UiGlyph.Icon(restart ? UiGlyph.Restart : UiGlyph.Wake, 42, restart ? AppTheme.Accent : Color.FromArgb(231, 188, 106));
        countdown.Text = UiText.Get("PowerCountdownNotice"); unsaved.Text = UiText.Get("PowerUnsavedNotice");
        notice.BackColor = Color.FromArgb(248, 253, 253); notice.Controls.AddRange([countdown, unsaved]);
        notice.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            int d = Px(10), w = notice.Width - 1, h = notice.Height - 1;
            path.AddArc(0, 0, d, d, 180, 90); path.AddArc(w - d, 0, d, d, 270, 90);
            path.AddArc(w - d, h - d, d, d, 0, 90); path.AddArc(0, h - d, d, d, 90, 90); path.CloseFigure();
            using var pen = new Pen(AppTheme.Line(Color.FromArgb(218, 230, 237))); e.Graphics.DrawPath(pen, path);
        };
        once.Text = UiText.Get("PowerSignInOnce"); once.Enabled = preflight.OneTimeLoginAvailable; once.Visible = restart;
        hint.Text = restart ? preflight.OneTimeLoginAvailable ? UiText.Get("PowerSignInHint") : UiText.OneTimeLoginUnavailable : UiText.Get("PowerReconnectStops");
        hint.Font = new("Segoe UI", restart ? 11.5f : 12.5f);
        account.Name = "oneTimeAccount"; account.Text = preflight.Account;
        passwordLabel.Text = UiText.WindowsPasswordForOneRestart;
        password.AccessibleName = UiText.WindowsPasswordForOneRestart;
        passwordField = ConnectionField.Wrap(password, 42); passwordField.Dock = Forms.DockStyle.None; passwordField.TabIndex = 1;
        confirm = ActionButton(UiText.Get(restart ? "RestartPcAction" : "ShutdownPcAction"), restart ? AppTheme.Accent : Color.FromArgb(184, 61, 73));
        confirm.Name = restart ? "continueRestart" : "confirmShutdown"; confirm.DialogResult = Forms.DialogResult.OK; confirm.TabIndex = 1;
        cancel = ActionButton(UiText.Cancel, Color.FromArgb(237, 242, 245)); cancel.Name = "cancelPower"; cancel.ForeColor = ForeColor;
        cancel.FlatAppearance.BorderSize = 1; cancel.DialogResult = Forms.DialogResult.Cancel; cancel.TabIndex = 0;
        footer.TabIndex = 2; footer.Controls.AddRange([cancel, confirm]);
        footer.Paint += (_, e) => { using var pen = new Pen(AppTheme.Line(Color.FromArgb(218, 230, 237))); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
        Controls.AddRange([glyph, heading, summary, notice, once, hint, account, passwordLabel, passwordField, footer]);
        void UpdateLogin()
        {
            password.Enabled = account.Visible = passwordLabel.Visible = passwordField.Visible = OneTimeLogin;
            if (!OneTimeLogin) password.Clear();
            summary.Text = UiText.Get(!restart ? "PowerSessionEnds" : OneTimeLogin || preflight.ExpectedReturn == "existing_automatic_desktop" ? "PowerAutomaticReturn" : "PowerManualReturn");
            PerformLayout();
        }
        once.CheckedChanged += (_, _) => { UpdateLogin(); if (OneTimeLogin) password.Focus(); };
        AcceptButton = cancel; CancelButton = cancel; ActiveControl = cancel;
        ready = true; UpdateLogin(); ResumeLayout(true); AppTheme.Apply(this);
    }

    private static Forms.Label Copy() => new() { UseMnemonic = false, ForeColor = Color.FromArgb(74, 93, 107) };
    private static Forms.Button ActionButton(string text, Color color) => new WorkspaceButton
    {
        Text = text, BackColor = color, ForeColor = Color.White, FlatStyle = Forms.FlatStyle.Flat,
        FlatAppearance = { BorderSize = 0, BorderColor = Color.FromArgb(218, 230, 237) }, UseMnemonic = false
    };
    private int Px(int value) => (int)Math.Round(value * DeviceDpi / 96f);
    private int Place(Forms.Label label, int x, int y, int width)
    {
        int height = Forms.TextRenderer.MeasureText(label.Text, label.Font, new(width, 0), Forms.TextFormatFlags.WordBreak | Forms.TextFormatFlags.NoPrefix).Height;
        label.SetBounds(x, y, width, height); return label.Bottom;
    }
    private sealed class SignInCheckBox : Forms.CheckBox
    {
        internal SignInCheckBox() { FlatStyle = Forms.FlatStyle.Flat; SetStyle(Forms.ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnPaint(Forms.PaintEventArgs e)
        {
            if (Forms.SystemInformation.HighContrast) { base.OnPaint(e); return; }
            e.Graphics.Clear(BackColor); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int size = (int)Math.Round(24 * DeviceDpi / 96f), top = (Height - size) / 2;
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            int d = Math.Max(4, size / 3), right = size - 1, bottom = top + size - 1;
            path.AddArc(0, top, d, d, 180, 90); path.AddArc(right - d, top, d, d, 270, 90);
            path.AddArc(right - d, bottom - d, d, d, 0, 90); path.AddArc(0, bottom - d, d, d, 90, 90); path.CloseFigure();
            using var pen = new Pen(Checked ? AppTheme.Accent : AppTheme.Dark ? AppTheme.Muted : Color.FromArgb(103, 124, 137), DeviceDpi / 96f);
            if (Checked)
            {
                using var fill = new SolidBrush(AppTheme.Accent); e.Graphics.FillPath(fill, path);
                using var check = new Pen(Color.White, 2 * DeviceDpi / 96f);
                e.Graphics.DrawLines(check, new PointF[] { new(size * .22f, top + size * .50f), new(size * .43f, top + size * .72f), new(size * .80f, top + size * .28f) });
            }
            e.Graphics.DrawPath(pen, path);
            int indent = (int)Math.Round(44 * DeviceDpi / 96f);
            var text = new Rectangle(indent, 0, Width - indent, Height);
            Forms.TextRenderer.DrawText(e.Graphics, Text, Font, text, Enabled ? ForeColor : AppTheme.Ink(Color.Gray),
                Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.NoPrefix | Forms.TextFormatFlags.VerticalCenter);
            if (Focused && ShowFocusCues) Forms.ControlPaint.DrawFocusRectangle(e.Graphics, text, ForeColor, BackColor);
        }
    }
    protected override void OnLayout(Forms.LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (!ready || arranging) return;
        arranging = true;
        try
        {
            int pad = Px(30), width = ClientSize.Width - pad * 2, textLeft = pad + Px(68);
            glyph.SetBounds(pad, Px(47), Px(42), Px(42));
            int y = Place(heading, textLeft, Px(44), width - Px(68));
            y = Place(summary, textLeft, y + Px(8), width - Px(68)) + Px(26);
            int noticeY = Place(countdown, Px(20), Px(16), width - Px(40));
            noticeY = Place(unsaved, Px(20), noticeY + Px(6), width - Px(40));
            notice.SetBounds(pad, y, width, noticeY + Px(16)); y = notice.Bottom + Px(26);
            if (restart)
            {
                once.SetBounds(pad, y, width, Math.Max(Px(26), once.GetPreferredSize(new(width, 0)).Height));
                y = Place(hint, pad + Px(44), once.Bottom + Px(4), width - Px(44));
                if (OneTimeLogin)
                {
                    y = Place(account, pad + Px(44), y + Px(18), width - Px(44));
                    y = Place(passwordLabel, pad + Px(44), y + Px(6), width - Px(44));
                    passwordField.SetBounds(pad + Px(44), y + Px(8), width - Px(44), Px(42)); y = passwordField.Bottom;
                }
            }
            else y = Place(hint, pad, y, width);
            footer.SetBounds(0, y + Px(32), ClientSize.Width, Px(84));
            int buttonWidth = Math.Max(Px(134), Forms.TextRenderer.MeasureText(confirm.Text, confirm.Font).Width + Px(32));
            confirm.SetBounds(ClientSize.Width - pad - buttonWidth, Px(20), buttonWidth, Px(44));
            int cancelWidth = Math.Max(Px(112), Forms.TextRenderer.MeasureText(cancel.Text, cancel.Font).Width + Px(32));
            cancel.SetBounds(confirm.Left - Px(12) - cancelWidth, confirm.Top, cancelWidth, confirm.Height);
            ClientSize = new(ClientSize.Width, footer.Bottom);
        }
        finally { arranging = false; }
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

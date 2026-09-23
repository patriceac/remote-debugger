using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

public sealed partial class MainForm
{
    private RemoteKeyboardCapture? keyboardCapture;
    private readonly Forms.ToolTip viewerTips = new() { ShowAlways = true, InitialDelay = 1000, ReshowDelay = 1000 };
    private readonly Forms.Panel economyHost = new() { AutoSize = true, Margin = Forms.Padding.Empty };
    private readonly Forms.Button secureAttention = Button(() => "Ctrl+Alt+Del", "secureAttention", 116);
    private readonly Forms.Button fullScreenButton = Button(() => UiText.FullScreen, "fullScreen", 124);
    private readonly Forms.Button exitFullScreen = Button(() => UiText.ExitFullScreen, "exitFullScreen", 190);
    private readonly Forms.Label fullScreenStatus = new() { Name = "fullScreenStatus", Dock = Forms.DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = SecondaryText };
    private readonly ResourceMiniCharts headerCharts = new() { Name = "resourceMiniCharts", Visible = false, ForeColor = Color.FromArgb(15, 35, 64), BackColor = Surface, Font = new Font("Segoe UI", 9.5F), Margin = Forms.Padding.Empty };
    private readonly ResourceMiniCharts fullScreenCharts = new() { Name = "fullScreenResourceCharts", Width = 264, Height = 34, ForeColor = PrimaryText, Anchor = Forms.AnchorStyles.Left };
    private readonly Forms.Timer resourceRefreshTimer = new() { Interval = 5000 };
    private bool economyPreferred = true, updatingEconomy, loadingViewerPreferences;
    private string viewerFingerprint = "";
    private string remoteDeviceName = "";
    private BitmapLease? displayedFrame;
    private long lastStreamStatistics;

    private void ShowFrame(BitmapLease image)
    {
        var previous = displayedFrame;
        displayedFrame = image; screen.Image = image.Image;
        previous?.Dispose(); screen.Invalidate();
    }

    private void ClearDisplayedFrame()
    { screen.Image = null; displayedFrame?.Dispose(); displayedFrame = null; }

    private void UpdateStreamStatistics(int bytes, double captureEncodeMs)
    {
        if (liveStream == null || streamStartedUtc is not { } started) return;
        streamFrames++; streamBytes += bytes;
        if (streamFrames != 1 && System.Diagnostics.Stopwatch.GetElapsedTime(lastStreamStatistics).TotalMilliseconds < 250) return;
        lastStreamStatistics = System.Diagnostics.Stopwatch.GetTimestamp();
        double seconds = Math.Max(0.001, (DateTimeOffset.UtcNow - started).TotalSeconds);
        streamStatus.SetText(() => StreamStatusWithRoute(UiText.Format(UiText.StreamMetrics, streamCodec, streamFrames / seconds, streamBytes * 8 / seconds / 1_000_000d, captureEncodeMs)));
        SetFooterDetail(() => streamStatus.Text); RefreshFooter();
    }
    private Forms.Panel? fullScreenHost;
    private Forms.TableLayoutPanel? viewerHost;
    private Rectangle windowedBounds;
    private Forms.FormWindowState windowedState;
    private readonly ReconnectNotice reconnectNotice = new();
    private Func<string>? reconnectMessage;

    private void DelayReconnectWarning(Func<string> message)
    {
        reconnectMessage = message;
        reconnectNotice.Interrupt(Environment.TickCount64);
        streamOverlay.Visible = false;
        RefreshReconnectWarning();
    }

    private void RefreshReconnectWarning()
    {
        if (reconnectMessage != null && reconnectNotice.IsVisible(Environment.TickCount64))
        {
            streamOverlay.SetText(reconnectMessage); streamOverlay.Visible = true;
        }
    }

    private void ClearReconnectWarning() { reconnectNotice.Recover(); reconnectMessage = null; }

    private void InitializeViewer()
    {
        resourceRefreshTimer.Tick += async (_, _) => { if (CanRefreshResourcesAutomatically()) await RefreshResourcesAsync(automatic: true); };
        resourceRefreshTimer.Start();
        keyboardCapture = new(CanSendInput, value => QueueInput(value), ReleaseHeldInputForCurrentSession,
            () => SetFullScreen(fullScreenHost == null),
            () => Forms.Form.ActiveForm == this && (fullScreenHost != null || fullScreenButton.Enabled && rolePages.SelectedIndex == 1 && controllerPages.SelectedIndex == 1));
        Shown += (_, _) =>
        {
            try { keyboardCapture.Start(); }
            catch (System.ComponentModel.Win32Exception ex) { inputBlockMessage = ex.Message; inputState.Suspend(); RefreshInputStatus(); }
        };
        remoteText.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Forms.Keys.Enter || e.Modifiers != Forms.Keys.None) return;
            e.SuppressKeyPress = true; QueueFocusedText();
        };
        secureAttention.Click += (_, _) => { if (CanSendFocusedInput()) QueueInput(new { kind = "secureAttention" }); };
        fullScreenButton.Click += (_, _) => SetFullScreen(true);
        exitFullScreen.Click += (_, _) => SetFullScreen(false);
    }

    private void RefreshViewerControls()
    {
        RefreshReconnectWarning();
        headerCharts.Visible = supportSession && rolePages.SelectedIndex == 1;
        if (!CanRefreshResourcesAutomatically() || lastMeasurementUtc == null || DateTimeOffset.UtcNow - lastMeasurementUtc > TimeSpan.FromSeconds(15))
        { headerCharts.SetStale(); fullScreenCharts.SetStale(); }
        LoadViewerPreferences();
        bool applicable = supportSession && liveStream != null && client?.ScreenRoute == "Relay";
        updatingEconomy = true;
        relayEconomy.Enabled = applicable;
        relayEconomy.Checked = applicable && economyPreferred;
        updatingEconomy = false;
        string economyTip = applicable ? UiText.EconomyExplanation : UiText.EconomyUnavailable;
        // Disabled controls do not receive hover messages: the enabled parent owns the tooltip.
        viewerTips.SetToolTip(economyHost, economyTip);
        viewerTips.SetToolTip(relayEconomy, economyTip);
        viewerTips.SetToolTip(fullScreenButton, UiText.ReleaseKeyboard);
        viewerTips.SetToolTip(typeText, UiText.SendTextHint);
        viewerTips.SetToolTip(enterKey, UiText.RemoteEnterHint);
        viewerTips.SetToolTip(secureAttention, UiText.SecureAttentionHint);
        viewerTips.SetToolTip(exitFullScreen, UiText.ReleaseKeyboard);
        secureAttention.Enabled = CanSendFocusedInput();
        fullScreenButton.Enabled = supportSession && liveFrameFresh;
        fullScreenStatus.Text = (remoteDeviceName.Length > 0 ? remoteDeviceName : selectedPeer?.Name ?? client?.Connection.Host ?? "") + " · " + streamStatus.Text;
        if (fullScreenHost != null && !supportSession) SetFullScreen(false);
    }

    private void LoadViewerPreferences()
    {
        string fingerprint = client?.Connection.Fingerprint ?? "";
        if (fingerprint == viewerFingerprint) return;
        viewerFingerprint = fingerprint;
        remoteDeviceName = "";
        headerCharts.Reset(); fullScreenCharts.Reset();
        var saved = ViewerPreferences.Load(root, fingerprint);
        loadingViewerPreferences = true;
        try { mouseEnabled.Checked = saved.Control; shareClipboard.Checked = saved.Clipboard; }
        finally { loadingViewerPreferences = false; }
    }

    private void SaveViewerPreferences()
    {
        if (loadingViewerPreferences || viewerFingerprint.Length == 0 || client?.Connection.Fingerprint != viewerFingerprint) return;
        try { new ViewerPreferences(mouseEnabled.Checked, shareClipboard.Checked).Save(root, viewerFingerprint); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetFooterDetail(() => ex.Message); }
    }

    private void SetFullScreen(bool enabled)
    {
        if (enabled == (fullScreenHost != null)) return;
        ReleaseHeldInputForCurrentSession();
        SuspendLayout();
        if (enabled)
        {
            windowedState = WindowState;
            windowedBounds = WindowState == Forms.FormWindowState.Normal ? Bounds : RestoreBounds;
            var display = Forms.Screen.FromControl(this).Bounds;
            viewerHost = (Forms.TableLayoutPanel)screenSurface.Parent!;
            fullScreenHost = new Forms.Panel { Name = "fullScreenViewer", Dock = Forms.DockStyle.Fill, BackColor = screen.BackColor };
            var bar = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 1, Padding = new Forms.Padding(16, 4, 8, 4), BackColor = Canvas };
            bar.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
            bar.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.AutoSize));
            bar.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.AutoSize));
            bar.Controls.Add(fullScreenStatus, 0, 0); bar.Controls.Add(fullScreenCharts, 1, 0); bar.Controls.Add(exitFullScreen, 2, 0);
            fullScreenHost.Controls.Add(screenSurface); fullScreenHost.Controls.Add(bar);
            Controls.Add(fullScreenHost); shell.Visible = false; fullScreenHost.BringToFront();
            WindowState = Forms.FormWindowState.Normal; FormBorderStyle = Forms.FormBorderStyle.None; Bounds = display;
        }
        else
        {
            var previous = fullScreenHost!; fullScreenHost = null;
            viewerHost!.Controls.Add(screenSurface, 0, 0);
            exitFullScreen.Parent!.Controls.Remove(exitFullScreen);
            fullScreenStatus.Parent!.Controls.Remove(fullScreenStatus);
            fullScreenCharts.Parent!.Controls.Remove(fullScreenCharts);
            Controls.Remove(previous); previous.Dispose(); shell.Visible = true;
            FormBorderStyle = Forms.FormBorderStyle.Sizable; Bounds = windowedBounds; WindowState = windowedState;
        }
        ResumeLayout(true);
        if (enabled) screen.Focus(); else remoteText.Focus();
    }

    private bool CanRefreshResourcesAutomatically() => supportSession && heartbeatHealthy && rolePages.SelectedIndex == 1 &&
        !clientUpdateBusy && !powerBusy && !terminating && !trayVisible && WindowState != Forms.FormWindowState.Minimized &&
        IsDirectResourceRoute(client?.ActiveRoute);

    internal static bool IsDirectResourceRoute(string? route) => route is "Direct LAN" or "Direct WAN";

    private void UpdateResourceCharts(System.Text.Json.JsonElement sample)
    {
        remoteDeviceName = sample.Str("machine", remoteDeviceName);
        double? cpu = NullableDouble(sample, "cpuPercentTotalMachine"), total = NullableDouble(sample, "physicalMemoryTotalBytes"), available = NullableDouble(sample, "physicalMemoryAvailableBytes");
        double? memory = total is > 0 && available is >= 0 ? Math.Clamp((total.Value - available.Value) / total.Value * 100, 0, 100) : null;
        double? disk = NullableDouble(sample, "diskBusyPercent"), gpu = NullableDouble(sample, "gpuPercent");
        headerCharts.Add(cpu, memory, disk, gpu); fullScreenCharts.Add(cpu, memory, disk, gpu);
    }
}

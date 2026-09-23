using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

public sealed partial class MainForm
{
    private readonly Forms.Label computerCount = ConnectionLabel("computerCount", 20.5f, true);
    private readonly Forms.Label computerTotal = ConnectionLabel("computerTotal", 20.5f, true);
    private readonly Forms.Label computerSummary = ConnectionLabel("computerSummary", 11.5f);
    private readonly Forms.Label updateExplanation = ConnectionLabel("updateExplanation", 12.5f);
    private readonly Forms.Label updateElapsed = ConnectionLabel("updateElapsed", 22.5f, true);
    private readonly Forms.Label updateRemaining = ConnectionLabel("updateRemaining", 22.5f, true);
    private readonly Forms.Label remainingCaption = ConnectionLabel("updateRemainingCaption", 11.5f);
    private readonly Forms.Label connectionReadyNote = ConnectionLabel("connectionReadyNote", 10.5f);
    private readonly UpdateTimeline updateTimeline = new() { Dock = Forms.DockStyle.Top, Height = 208, BackColor = Canvas };
    private readonly WorkspaceButton connectionSettingsToggle = new() { Name = "connectionSettingsToggle", Glyph = UiGlyph.Right, Dock = Forms.DockStyle.Top,
        Height = 38, DisclosureStyle = true, Font = new Font("Segoe UI", 12), FlatStyle = Forms.FlatStyle.Flat, FlatAppearance = { BorderSize = 0 }, BackColor = Canvas, ForeColor = PrimaryText, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Forms.TableLayoutPanel connectionSettings = ConnectionStack("connectionSettings");
    private readonly Forms.Label connectionSettingsNote = ConnectionLabel("connectionSettingsNote", 10.5f);
    private Forms.Panel? peerViewport;
    private Forms.Panel? computerHeader;
    private Forms.Control? connectionPairRow;
    private Size roundedProgressSize;
    private bool showingFleetUpdate;
    private FleetDevice? SelectedFleetUpdate => FleetBusy && selectedPeer != null &&
        fleet.TryGetValue(DeviceKey(selectedPeer), out var device) && NeedsFleetProgressAnimation(device.State) ? device : null;

    private static Forms.Label ConnectionLabel(string name, float size = 11.5f, bool bold = false) => new WorkspaceLabel
    {
        Name = name, AutoSize = true, Dock = Forms.DockStyle.Top, Margin = Forms.Padding.Empty,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = bold ? PrimaryText : SecondaryText
    };
    private static Forms.TableLayoutPanel ConnectionStack(string name) => new ConnectionLayoutPanel
    {
        Name = name, Dock = Forms.DockStyle.Top, AutoSize = true, AutoSizeMode = Forms.AutoSizeMode.GrowAndShrink,
        ColumnCount = 1, RowCount = 0, ColumnStyles = { new(Forms.SizeType.Percent, 100) }, Size = Size.Empty, Margin = Forms.Padding.Empty
    };
    private static void ConnectionRow(Forms.TableLayoutPanel stack, Forms.Control control, int gap = 0)
    {
        int row = stack.RowCount++;
        stack.RowStyles.Add(new(Forms.SizeType.AutoSize));
        control.Margin = new(0, gap, 0, 0); stack.Controls.Add(control, 0, row);
    }
    private static Forms.Control ConnectionRule() => new Forms.Panel { Dock = Forms.DockStyle.Top, Height = 1, BackColor = Divider, Margin = new(0, 16, 0, 12) };

    private PagePanel BuildConnectionPage()
    {
        var page = new PagePanel(() => UiText.Connection) { BackColor = Canvas, Padding = new(28, 16, 28, 12) };
        var columns = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Forms.Padding.Empty };
        columns.ColumnStyles.Add(new(Forms.SizeType.Percent, PrivateInternet ? 62.75f : 50));
        columns.ColumnStyles.Add(new(Forms.SizeType.Absolute, 1));
        columns.ColumnStyles.Add(new(Forms.SizeType.Percent, PrivateInternet ? 37.25f : 50));
        columns.RowStyles.Add(new(Forms.SizeType.Percent, 100));
        columns.Controls.Add(BuildPeerList(), 0, 0);
        columns.Controls.Add(new Forms.Panel { Dock = Forms.DockStyle.Fill, BackColor = Divider, Margin = Forms.Padding.Empty }, 1, 0);
        columns.Controls.Add(BuildConnectionForm(), 2, 0); page.Controls.Add(columns); return page;
    }

    private Forms.Control BuildPeerList()
    {
        var panel = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new(0, 16, 28, 0), Margin = Forms.Padding.Empty };
        panel.RowStyles.Add(new(Forms.SizeType.Absolute, 60)); panel.RowStyles.Add(new(Forms.SizeType.Percent, 100)); panel.RowStyles.Add(new(Forms.SizeType.AutoSize));
        var toolbar = new Forms.Panel { Name = "connectionToolbar", Dock = Forms.DockStyle.Top, Height = 44, Margin = new(0, 0, 0, 16) };
        computerCount.Anchor = Forms.AnchorStyles.Top | Forms.AnchorStyles.Left; computerCount.Dock = Forms.DockStyle.None;
        computerTotal.Dock = Forms.DockStyle.None; computerTotal.ForeColor = Color.FromArgb(131, 151, 175);
        ((WorkspaceButton)discoverButton).Glyph = UiGlyph.Refresh;
        ((WorkspaceButton)updateAllDevices).Glyph = UiGlyph.Update;
        updateAllDevices.BackColor = Surface; updateAllDevices.ForeColor = PrimaryText; updateAllDevices.FlatAppearance.BorderSize = 1;
        updateAllDevices.MinimumSize = new(130, 42); discoverButton.MinimumSize = new(114, 42);
        foreach (var button in new[] { discoverButton, updateAllDevices }) { button.Font = new Font("Segoe UI", 10.5f); button.Padding = new(8, 0, 8, 0); }
        var toolbarActions = PrivateInternet ? ControlRow(discoverButton, updateAllDevices) : ControlRow(discoverButton);
        foreach (Forms.Control button in toolbarActions.Controls) button.Margin = new(0, 0, button.Margin.Right, 0);
        toolbarActions.Dock = Forms.DockStyle.None;
        toolbar.Controls.AddRange([computerCount, computerTotal, toolbarActions]);
        void LayoutToolbar()
        {
            var actions = toolbarActions.GetPreferredSize(Size.Empty);
            int captionWidth = computerCount.PreferredSize.Width + computerTotal.PreferredSize.Width + HeaderPixels(12);
            bool narrow = toolbar.Width < captionWidth + actions.Width + HeaderPixels(24);
            int rowHeight = Math.Max(HeaderPixels(44), actions.Height);
            computerCount.Location = new(0, narrow ? 0 : (rowHeight - computerCount.Height) / 2);
            computerTotal.Location = new(computerCount.Right + HeaderPixels(12), computerCount.Top);
            toolbarActions.SetBounds(narrow ? 0 : Math.Max(0, toolbar.Width - actions.Width), narrow ? computerCount.Height + HeaderPixels(8) : 0, actions.Width, actions.Height);
            toolbar.Height = narrow ? computerCount.Height + HeaderPixels(8) + rowHeight : rowHeight;
            int height = toolbar.Height + toolbar.Margin.Vertical;
            if (panel.RowStyles[0].Height != height)
            {
                panel.RowStyles[0].Height = height;
                if (panel.IsHandleCreated) panel.BeginInvoke((Action)(() => { if (!panel.IsDisposed) panel.PerformLayout(); }));
            }
        }
        toolbar.SizeChanged += (_, _) => LayoutToolbar();
        computerCount.TextChanged += (_, _) => LayoutToolbar();
        toolbarActions.SizeChanged += (_, _) => LayoutToolbar();
        panel.Controls.Add(toolbar, 0, 0);
        peerViewport = new Forms.Panel { Dock = Forms.DockStyle.Fill, Margin = Forms.Padding.Empty };
        peers.Dock = Forms.DockStyle.None; peers.LogicalRowHeight = 78; peers.HeaderStyle = Forms.ColumnHeaderStyle.None;
        peers.Columns.Add(UiText.Name, 220).WithText(() => UiText.Name);
        peers.Columns.Add(PrivateInternet ? UiText.DeviceVersion : UiText.Address, 160).WithText(() => PrivateInternet ? UiText.DeviceVersion : UiText.Address);
        peers.Columns.Add(UiText.State, 200).WithText(() => UiText.State);
        if (PrivateInternet) { peers.Columns.Add(UiText.DeviceProgress, 0); peers.Columns[2].DisplayIndex = 1; }
        peers.ShowItemToolTips = true;
        computerHeader = new Forms.Panel { Name = "computerTableHeader", Height = 38, BackColor = Color.FromArgb(235, 242, 246) };
        computerHeader.Paint += (_, e) => DrawComputerTableHeader(e.Graphics);
        peerViewport.Controls.Add(computerSummary); peerViewport.Controls.Add(peers); peerViewport.Controls.Add(computerHeader);
        computerSummary.Dock = Forms.DockStyle.None;
        peerViewport.SizeChanged += (_, _) => LayoutComputerList();
        panel.Controls.Add(peerViewport, 0, 1);
        discoveryState.Dock = Forms.DockStyle.Top; discoveryState.Margin = new(0, 8, 0, 0);
        panel.Controls.Add(discoveryState, 0, 2);
        InitializeFleet(); return panel;
    }

    private void LayoutComputerList()
    {
        if (peerViewport == null || peers.Columns.Count < 3) return;
        int headerHeight = HeaderPixels(38);
        int rowHeight = peers.Items.Count > 0 && peers.IsHandleCreated ? peers.GetItemRect(0).Height : HeaderPixels(79);
        int naturalHeight = peers.Items.Count * rowHeight + 1;
        int availableHeight = Math.Max(HeaderPixels(36), peerViewport.Height - HeaderPixels(76));
        int width = Math.Max(0, peerViewport.ClientSize.Width - (naturalHeight > availableHeight ? Forms.SystemInformation.VerticalScrollBarWidth : 0));
        peers.Columns[0].Width = width * (width < HeaderPixels(600) ? 48 : 33) / 100;
        peers.Columns[2].Width = width * (width < HeaderPixels(600) ? 25 : 32) / 100;
        peers.Columns[1].Width = width - peers.Columns[0].Width - peers.Columns[2].Width;
        if (PrivateInternet) peers.Columns[3].Width = 0;
        peers.SetBounds(0, headerHeight, peerViewport.ClientSize.Width, Math.Min(availableHeight, naturalHeight));
        computerHeader?.SetBounds(0, 0, peerViewport.Width, headerHeight); computerHeader?.Invalidate();
        computerSummary.SetBounds(0, peers.Bottom + HeaderPixels(18), peerViewport.Width, HeaderPixels(28));
    }

    private Forms.Control BuildConnectionForm()
    {
        var viewport = new Forms.Panel { Name = "connectionInspector", Dock = Forms.DockStyle.Fill, AutoScroll = true, Padding = new(28, 0, 0, 0), Margin = Forms.Padding.Empty };
        var stack = ConnectionStack("connectionContent");
        var identity = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Top, Height = 110, Padding = new(0, 10, 0, 0), ColumnCount = 2, RowCount = 1, Margin = Forms.Padding.Empty };
        identity.RowStyles.Add(new(Forms.SizeType.Percent, 100));
        identity.ColumnStyles.Add(new(Forms.SizeType.Absolute, 98)); identity.ColumnStyles.Add(new(Forms.SizeType.Percent, 100));
        var identityText = ConnectionStack("selectedComputerIdentity");
        ConnectionRow(identityText, ConnectionLabel("selectedComputerCaption", 10.5f).WithText(() => UiText.Get("ConnectionSelectedComputer")));
        selectedPeerName.Name = "selectedPeerName"; selectedPeerName.Dock = Forms.DockStyle.Top; selectedPeerName.Font = new Font("Segoe UI", 21, FontStyle.Bold);
        selectedPeerName.AutoEllipsis = true;
        selectedPeerAddress.Dock = Forms.DockStyle.Top; selectedPeerAddress.Font = new Font("Segoe UI", 12.5f);
        if (PrivateInternet) { selectedPeerName.SetText(() => UiText.SelectComputer); selectedPeerAddress.SetText(""); }
        ConnectionRow(identityText, selectedPeerName, 4); ConnectionRow(identityText, selectedPeerAddress, 4);
        var selectedIcon = UiGlyph.Icon(UiGlyph.Computer, 76, PrimaryText); selectedIcon.Margin = new(0, 4, 0, 0);
        identity.Controls.Add(selectedIcon, 0, 0); identity.Controls.Add(identityText, 1, 0);
        ConnectionRow(stack, identity); ConnectionRow(stack, ConnectionRule());
        BuildConnectionProgress(); ConnectionRow(stack, updateProgressArea, 20);
        connectionState.Dock = Forms.DockStyle.Top; connectionState.MaximumSize = Size.Empty; connectionState.Font = new Font("Segoe UI", 12.5f);
        ConnectionRow(stack, connectionState, 16);
        if (!PrivateInternet)
        {
            ConnectionRow(stack, ConnectionLabel("connectionAddressLabel").WithText(() => UiText.IpOrManual), 12);
            host.Dock = Forms.DockStyle.Top; ConnectionRow(stack, host, 5);
            ConnectionRow(stack, ConnectionLabel("connectionCodeLabel").WithText(() => UiText.SixDigitCode), 12);
            code.Width = 150; code.Font = new Font("Consolas", 20); code.MaxLength = 6; code.TextAlign = Forms.HorizontalAlignment.Center;
            ConnectionRow(stack, ControlRow(code, pairButton), 5);
        }
        else { connectionPairRow = ControlRow(pairButton); ConnectionRow(stack, connectionPairRow, 8); }
        ConnectionRow(stack, ConnectionRule(), 8);
        connectionSettingsToggle.WithText(() => UiText.Get("ConnectionSettings"));
        connectionSettingsToggle.Click += (_, _) =>
        {
            connectionSettings.Visible = !connectionSettings.Visible;
            connectionSettingsToggle.Glyph = connectionSettings.Visible ? UiGlyph.Down : UiGlyph.Right;
            connectionSettingsToggle.Invalidate();
        };
        ConnectionRow(stack, connectionSettingsToggle, 4);
        connectionSettings.Padding = new(32, 0, 0, 0);
        if (PrivateInternet) ConnectionRow(connectionSettings, BuildWanAddressFields());
        ConnectionRow(connectionSettings, ConnectionLabel("wakeSection", 10.5f).WithText(() => UiText.Get("ConnectionWakeSection")), 8);
        ((WorkspaceButton)wakePc).Glyph = UiGlyph.Wake; ((WorkspaceButton)configureWake).Glyph = UiGlyph.Settings;
        wakePc.MinimumSize = new(192, 38); configureWake.MinimumSize = new(192, 38);
        wakePc.Font = configureWake.Font = new Font("Segoe UI", 11);
        configureWake.WithText(() => UiText.WakeSettings + "…");
        wakePc.WithText(() => UiText.Get("ConnectionWakeComputer"));
        updateClientButton.Visible = false;
        ConnectionRow(connectionSettings, BuildWakeControls(), 4);
        connectionSettingsNote.WithText(() => UiText.Get("ConnectionSettingsBusy"));
        ConnectionRow(connectionSettings, connectionSettingsNote, 2);
        ConnectionRow(stack, connectionSettings);
        connectionSettings.Visible = true; connectionSettingsToggle.Glyph = UiGlyph.Down;
        viewport.Controls.Add(stack);
        void LayoutContent()
        {
            int width = Math.Max(180, viewport.ClientSize.Width - viewport.Padding.Horizontal);
            stack.Width = width;
            int iconColumn = HeaderPixels(width < HeaderPixels(340) ? 60 : 98);
            identity.ColumnStyles[0].Width = iconColumn;
            selectedIcon.Size = new(HeaderPixels(width < HeaderPixels(340) ? 48 : 76), HeaderPixels(width < HeaderPixels(340) ? 48 : 76));
            selectedPeerName.MaximumSize = new(Math.Max(80, width - iconColumn), 0);
            identity.Height = Math.Max(HeaderPixels(110), identityText.GetPreferredSize(new(Math.Max(80, width - iconColumn), 0)).Height + HeaderPixels(10));
            foreach (var label in new[] { connectionState, selectedPeerAddress, updateExplanation, connectionSettingsNote }) label.MaximumSize = new(width, 0);
            viewport.AutoScrollMinSize = new(0, stack.Height);
        }
        stack.SizeChanged += (_, _) => LayoutContent(); viewport.SizeChanged += (_, _) => LayoutContent();
        return viewport;
    }

    private void BuildConnectionProgress()
    {
        updateProgressArea.AutoSize = true; updateProgressArea.AutoSizeMode = Forms.AutoSizeMode.GrowAndShrink; updateProgressArea.RowCount = 0;
        updateProgressArea.ColumnStyles.Add(new(Forms.SizeType.Percent, 100));
        updateProgressText.AutoSize = true; updateProgressText.Font = new Font("Segoe UI", 21.5f, FontStyle.Bold); updateProgressText.ForeColor = PrimaryText;
        ConnectionRow(updateProgressArea, updateProgressText);
        ConnectionRow(updateProgressArea, updateExplanation);
        updateProgressTrack.Height = 12; updateProgressTrack.Dock = Forms.DockStyle.Top; updateProgressTrack.Controls.Add(updateProgressFill);
        updateProgressTrack.SizeChanged += (_, _) => { ControlRegions.ApplyRounded(updateProgressTrack, ref roundedProgressSize, HeaderPixels(6)); RefreshUpdateProgress(); };
        ConnectionRow(updateProgressArea, updateProgressTrack, 16);
        var metrics = new ConnectionLayoutPanel { Dock = Forms.DockStyle.Top, AutoSize = true, AutoSizeMode = Forms.AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 2, Margin = Forms.Padding.Empty };
        metrics.RowStyles.Add(new(Forms.SizeType.AutoSize)); metrics.RowStyles.Add(new(Forms.SizeType.AutoSize));
        metrics.ColumnStyles.Add(new(Forms.SizeType.Percent, 45)); metrics.ColumnStyles.Add(new(Forms.SizeType.Percent, 55));
        metrics.Controls.Add(ConnectionLabel("updateElapsedCaption", 11.5f).WithText(() => UiText.Get("ConnectionElapsed")), 0, 0);
        metrics.Controls.Add(remainingCaption, 1, 0); metrics.Controls.Add(updateElapsed, 0, 1); metrics.Controls.Add(updateRemaining, 1, 1);
        ConnectionRow(updateProgressArea, metrics, 16); ConnectionRow(updateProgressArea, updateTimeline, 8);
        ConnectionRow(updateProgressArea, connectionReadyNote.WithText(() => UiText.Get("ConnectionOpensWhenReady")), 4);
    }

    private bool PeerIsSynchronizing(Peer peer) => FleetBusy && fleet.TryGetValue(DeviceKey(peer), out var device) && NeedsFleetProgressAnimation(device.State) ||
        (synchronizingAgent || clientUpdateBusy) && (client != null && Safety.Equal(peer.Fingerprint, client.Connection.Fingerprint) || selectedPeer?.Fingerprint == peer.Fingerprint);

    private void RefreshConnectionPresentation()
    {
        bool fleetUpdating = SelectedFleetUpdate != null;
        if (fleetUpdating)
        {
            updateProgressArea.Visible = true;
            updateProgressFill.BackColor = Teal; updateProgressText.ForeColor = PrimaryText;
            RefreshUpdateProgress();
        }
        else if (showingFleetUpdate) updateProgressArea.Visible = false;
        showingFleetUpdate = fleetUpdating;
        connectionReadyNote.Visible = !fleetUpdating;
        computerCount.SetText(UiText.Get("ConnectionComputers")); computerTotal.SetText(fleet.Count.ToString());
        int syncing = fleet.Values.Count(d => PeerIsSynchronizing(d.Peer));
        int online = fleet.Values.Count(d => d.Online && !PeerIsSynchronizing(d.Peer));
        int offline = fleet.Count - online - syncing;
        var summary = new List<string>();
        if (syncing > 0) summary.Add(UiText.Format(UiText.Get(!FleetBusy && (connectedUpdateReport?.Stage is "restarting" or "finalizing") ? "ConnectionReconnectingCount" : "ConnectionSyncCount"), syncing));
        if (online > 0) summary.Add(UiText.Format(UiText.Get("ConnectionOnlineCount"), online));
        if (offline > 0 || fleet.Count == 0) summary.Add(UiText.Format(UiText.Get("ConnectionOfflineCount"), offline));
        computerSummary.SetText(string.Join(" · ", summary));
        if ((synchronizingAgent || supportSession || pairingBusy) && client != null)
        {
            var active = fleet.Values.FirstOrDefault(d => Safety.Equal(d.Peer.Fingerprint, client.Connection.Fingerprint));
            selectedPeerName.SetText(active?.Peer.Name ?? selectedPeer?.Name ?? client.Connection.Host);
        }
        if (synchronizingAgent || clientUpdateBusy || fleetUpdating) selectedPeerAddress.SetText(() => UiText.Get("ConnectionAgentUpdate"));
        else if (!pairingBusy && !supportSession && selectedPeer != null && fleet.TryGetValue(DeviceKey(selectedPeer), out var selected))
            selectedPeerAddress.SetText(selected.Online ? UiText.Available : UiText.Get("ConnectionOffline"));
        foreach (Forms.ListViewItem item in peers.Items)
        {
            if (item.Tag is not Peer peer || !fleet.TryGetValue(DeviceKey(peer), out var device)) continue;
            string state = PeerIsSynchronizing(peer) ? ConnectionStageTitle(FleetBusy ? device.State : connectedUpdateReport?.Stage ?? "preparing") : FleetState(device);
            if (item.SubItems[2].Text != state) item.SubItems[2].Text = state;
        }
        bool updating = synchronizingAgent || clientUpdateBusy || fleetUpdating;
        connectionState.Visible = !updating;
        pairButton.Visible = !PrivateInternet || !updating;
        if (connectionPairRow != null) connectionPairRow.Visible = !updating;
        connectionSettingsNote.Visible = pairingBusy || clientUpdateBusy || FleetBusy;
        updateTimeline.Interrupted = updateProgressFill.BackColor == DestructiveText;
        if (updateTimeline.Interrupted) updateTimeline.Invalidate();
        LayoutComputerList();
    }

    private void DrawComputerHeader(object? sender, Forms.DrawListViewColumnHeaderEventArgs e)
    {
        using var back = new SolidBrush(Color.FromArgb(237, 242, 245)); e.Graphics.FillRectangle(back, e.Bounds);
        using var font = new Font("Segoe UI", 9);
        Forms.TextRenderer.DrawText(e.Graphics, e.Header?.Text.ToUpperInvariant(), font, Rectangle.Inflate(e.Bounds, -12, 0), SecondaryText,
            Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.EndEllipsis | Forms.TextFormatFlags.NoPrefix);
    }

    private void DrawComputerTableHeader(Graphics graphics)
    {
        if (peers.Columns.Count < 3 || computerHeader == null) return;
        using var font = new Font("Segoe UI", 11);
        int left = 0;
        foreach (int index in PrivateInternet ? new[] { 0, 2, 1 } : new[] { 0, 1, 2 })
        {
            int width = peers.Columns[index].Width, inset = HeaderPixels(index == 0 ? 28 : 18);
            string caption = index == 0 ? UiText.Get("ConnectionComputer") : peers.Columns[index].Text;
            Forms.TextRenderer.DrawText(graphics, caption.ToUpperInvariant(), font, new Rectangle(left + inset, 0, Math.Max(0, width - inset), computerHeader.Height), SecondaryText,
                Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.EndEllipsis);
            using var pen = new Pen(Divider); graphics.DrawLine(pen, left, 0, left, computerHeader.Height);
            left += width;
        }
    }

    private void DrawComputerCell(object? sender, Forms.DrawListViewSubItemEventArgs e)
    {
        if (e.Item?.Tag is not Peer peer || e.ColumnIndex == 3) return;
        bool syncing = PeerIsSynchronizing(peer);
        Color background = e.Item.Selected || syncing ? Color.FromArgb(227, 246, 248) : Surface;
        using var back = new SolidBrush(background); e.Graphics.FillRectangle(back, e.Bounds);
        using var border = new Pen(Divider); e.Graphics.DrawLine(border, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        bool compact = peers.Width < HeaderPixels(600);
        var bounds = Rectangle.Inflate(e.Bounds, -HeaderPixels(e.ColumnIndex == 0 ? compact ? 18 : 28 : compact ? 12 : 18), 0);
        string text = e.SubItem?.Text ?? "", detail = "";
        fleet.TryGetValue(DeviceKey(peer), out var device);
        if (e.ColumnIndex == 0)
        {
            if (e.Item.Selected || syncing) { using var accent = new SolidBrush(Teal); e.Graphics.FillRectangle(accent, e.Bounds.Left, e.Bounds.Top, HeaderPixels(4), e.Bounds.Height); }
            UiGlyph.Draw(e.Graphics, UiGlyph.Computer, new(bounds.Left, bounds.Top, HeaderPixels(32), bounds.Height), PrimaryText);
            bounds.X += HeaderPixels(48); bounds.Width = Math.Max(0, bounds.Width - HeaderPixels(48));
        }
        else if (PrivateInternet && e.ColumnIndex == 1)
        {
            if (Version.TryParse(text, out var version)) text = version.ToString(3);
            if (syncing) { text += " → " + typeof(MainForm).Assembly.GetName().Version?.ToString(3); detail = UiText.Get("ConnectionConfirmationPending"); }
            else if (device is { Online: false }) detail = UiText.Get("ConnectionLastKnown");
        }
        else if (e.ColumnIndex == 2)
        {
            if (syncing) text = ConnectionStageTitle(FleetBusy ? device!.State : connectedUpdateReport?.Stage ?? "preparing");
            else if (device is { Online: false }) text = UiText.Get("ConnectionOffline");
            if (device != null && fleetProgress.TryGetValue(DeviceKey(peer), out var tracker) && NeedsFleetProgressAnimation(device.State))
            {
                detail = UpdateStepNumbers(tracker.Snapshot());
                if (device.State == "transferring" && device.Detail.Length > 0) detail += " · " + device.Detail;
            }
            else if (device?.State == "failed") detail = device.Detail;
            if (syncing)
            {
                var ring = new Rectangle(bounds.X, bounds.Y + bounds.Height / 2 - HeaderPixels(13), HeaderPixels(26), HeaderPixels(26));
                using var faded = new Pen(Color.FromArgb(188, 228, 232), HeaderPixels(3));
                using var active = new Pen(Teal, HeaderPixels(3));
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.DrawEllipse(faded, ring); e.Graphics.DrawArc(active, ring, -90, 230);
            }
            else
            {
                using var dot = new SolidBrush(device?.Online == true ? Teal : Color.FromArgb(129, 151, 174));
                e.Graphics.FillEllipse(dot, bounds.X, bounds.Y + bounds.Height / 2 - HeaderPixels(6), HeaderPixels(12), HeaderPixels(12));
            }
            int inset = HeaderPixels(syncing ? 38 : 28); bounds.X += inset; bounds.Width = Math.Max(0, bounds.Width - inset);
        }
        using var titleFont = new Font("Segoe UI", 13.5f, e.ColumnIndex == 0 ? FontStyle.Bold : FontStyle.Regular);
        var flags = Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.NoPrefix | Forms.TextFormatFlags.EndEllipsis | Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.SingleLine;
        var titleBounds = detail.Length == 0 ? bounds : new Rectangle(bounds.X, bounds.Y + HeaderPixels(15), bounds.Width, HeaderPixels(24));
        Forms.TextRenderer.DrawText(e.Graphics, text, titleFont, titleBounds, PrimaryText, flags);
        if (detail.Length > 0)
        {
            using var small = new Font("Segoe UI", 11.5f);
            Forms.TextRenderer.DrawText(e.Graphics, detail, small, new Rectangle(bounds.X, bounds.Y + HeaderPixels(40), bounds.Width, HeaderPixels(24)), SecondaryText, flags);
            e.Item.ToolTipText = text + " · " + detail;
        }
    }
}

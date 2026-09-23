using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

public sealed partial class MainForm
{
    private Action? layoutAgent;
    private Forms.Panel agentInstructions = null!;
    private Forms.Control agentReadyIcon = null!;
    private bool layingOutAgent;

    internal static bool IsAgentReadyForSupport(bool active, bool internetReady, bool lanReady) => active && (internetReady || lanReady);

    private void BuildAgentPage()
    {
        // Load the connection mode before constructing either role's presentation.
        LoadInternetMode();
        var page = new PagePanel(() => UiText.GiveControl) { BackColor = Canvas, Padding = Forms.Padding.Empty };
        var viewport = new Forms.Panel { Name = "agentWorkspace", Dock = Forms.DockStyle.Fill, AutoScroll = true, BackColor = Canvas, Padding = new(47, 26, 39, 28) };
        var columns = new Forms.Panel { Name = "agentColumns", BackColor = Canvas, Dock = Forms.DockStyle.Top };
        var left = new Forms.Panel { Name = "agentOverview" };
        var right = new Forms.Panel { Name = "agentPermissions" };
        var divider = new Forms.Panel { BackColor = Divider };
        columns.Controls.AddRange([left, divider, right]); viewport.Controls.Add(columns); page.Controls.Add(viewport); rolePages.TabPages.Add(page);

        Forms.Label Label(string name, Func<string> text, float size, bool bold = false, bool muted = false) => new WorkspaceLabel
        {
            Name = name, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = muted ? SecondaryText : PrimaryText
        }.WithText(text);
        var computer = new Forms.Panel();
        computer.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.ScaleTransform(DeviceDpi / 96f, DeviceDpi / 96f);
            using var pen = new Pen(AppTheme.Ink(PrimaryText), 4) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
            e.Graphics.DrawRectangle(pen, 7, 14, 84, 52); e.Graphics.DrawLine(pen, 49, 66, 49, 82); e.Graphics.DrawLine(pen, 30, 82, 68, 82);
        };
        var caption = Label("agentComputerCaption", () => UiText.Get("GiveThisComputer"), 12.5f, muted: true);
        var name = Label("agentComputerName", () => Environment.MachineName, 28, true);
        agentReadyIcon = new Forms.Panel { Name = "agentReadinessIcon" };
        agentReadyIcon.Paint += (_, e) =>
        {
            bool ready = agentHeading.Text == UiText.Get("GiveReady") || agent?.Session.Connected == true;
            UiGlyph.Draw(e.Graphics, ready ? UiGlyph.CheckCircle : UiGlyph.Info, agentReadyIcon.ClientRectangle, AppTheme.Ink(ready ? Teal : SecondaryText));
        };
        agentHeading.Font = new Font("Segoe UI", 19.5f, FontStyle.Bold);
        agentSubtitle.Font = new Font("Segoe UI", 13.5f);
        agentSessionNote.Font = new Font("Segoe UI", 11.5f);
        agentSessionNote.MaximumSize = Size.Empty;
        var rule = new Forms.Panel { BackColor = Divider };
        agentInstructions = new Forms.Panel { Name = "agentInstructions" };
        var instructionsHeading = Label("agentInstructionsHeading", () => UiText.Get("GiveConnectHeading"), 18.5f, true);
        var first = Label("agentInstructionOne", () => UiText.Get("GiveInstructionOne"), 13.5f);
        var second = new AgentInstructionLabel { Name = "agentInstructionTwo", Font = first.Font, ForeColor = PrimaryText, Emphasis = Environment.MachineName }
            .WithText(() => UiText.Format(UiText.Get("GiveInstructionTwo"), Environment.MachineName));
        var numberOne = new AgentStepNumber(1); var numberTwo = new AgentStepNumber(2);
        agentInstructions.Controls.AddRange([instructionsHeading, numberOne, first, numberTwo, second]);
        var noteIcon = UiGlyph.Icon(UiGlyph.Shield, 28, Color.FromArgb(78, 102, 137));
        setupNotice.Controls.Clear(); setupNotice.Controls.AddRange([setupNoticeText, preparePlatform]);
        left.Controls.AddRange([computer, caption, name, agentReadyIcon, agentHeading, agentSubtitle, agentPairCode, copyAgentCode,
            enableSupport, restartAgent, pairingCountdown, pairingCountdownText, setupNotice, rule, agentInstructions, agentState, noteIcon, agentSessionNote]);
        agentEyebrow.Visible = false;

        var settingsHeading = Label("agentPermissionsHeading", () => UiText.Get("GivePermissions"), 21, true);
        var networkIcon = UiGlyph.Icon(UiGlyph.Globe, 38, PrimaryText);
        var sleepIcon = UiGlyph.Icon(UiGlyph.Moon, 38, PrimaryText);
        var adminIcon = UiGlyph.Icon(UiGlyph.Shield, 38, PrimaryText);
        var networkLabel = Label("agentNetworkLabel", () => PrivateInternet ? UiText.InternetLabel : UiText.PrivateNetwork, 13.5f);
        var sleepLabel = Label("agentSleepLabel", () => UiText.Sleep, 13.5f);
        var adminLabel = Label("agentMaintenanceLabel", () => UiText.AdminMaintenance, 13.5f);
        var sleepHelp = Label("agentSleepHelp", () => UiText.Get("GiveSleepHelp"), 12, muted: true);
        var adminHelp = Label("agentMaintenanceHelp", () => UiText.Get("GiveMaintenanceHelp"), 12, muted: true);
        var networkDot = new Forms.Panel();
        networkDot.Paint += (_, e) => { e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; using var brush = new SolidBrush(agentNetworkState.Text == UiText.Ready ? AppTheme.Ink(Teal) : agentNetworkState.ForeColor); e.Graphics.FillEllipse(brush, 0, 0, networkDot.Width - 1, networkDot.Height - 1); };
        var networkRule = new Forms.Panel { BackColor = Divider }; var sleepRule = new Forms.Panel { BackColor = Divider }; var adminRule = new Forms.Panel { BackColor = Divider };
        foreach (var label in new[] { agentNetworkState, agentSleepState }) { label.Font = new Font("Segoe UI", 13.5f); label.ForeColor = PrimaryText; }
        agentMaintenanceState.Font = new Font("Segoe UI", 11.5f);
        adminMaintenanceToggle.Font = new Font("Segoe UI", 13.5f); adminMaintenanceToggle.AccessibleName = UiText.AdminMaintenance;
        right.Controls.AddRange([settingsHeading, networkRule, networkIcon, networkLabel, networkDot, agentNetworkState, internetState,
            sleepRule, sleepIcon, sleepLabel, agentSleepState, sleepHelp, adminRule, adminIcon, adminLabel, adminMaintenanceToggle, adminHelp, agentMaintenanceState]);
        foreach (var control in left.Controls.Cast<Forms.Control>().Concat(right.Controls.Cast<Forms.Control>()))
        {
            control.Dock = Forms.DockStyle.None; control.AutoSize = false; control.Margin = Forms.Padding.Empty;
            control.TextChanged += (_, _) => layoutAgent?.Invoke();
            control.VisibleChanged += (_, _) => layoutAgent?.Invoke();
        }

        int TextAt(Forms.Control control, int x, int y, int width, int minimum = 0)
        {
            int height = Math.Max(HeaderPixels(minimum), control.GetPreferredSize(new(Math.Max(1, width), 0)).Height);
            control.SetBounds(x, y, Math.Max(1, width), height); return y + height;
        }
        void Box(Forms.Control control, int x, int y, int width, int height) => control.SetBounds(HeaderPixels(x), HeaderPixels(y), HeaderPixels(width), HeaderPixels(height));
        layoutAgent = () =>
        {
            if (layingOutAgent || viewport.ClientSize.Width == 0) return;
            layingOutAgent = true;
            try
            {
                int width = Math.Max(HeaderPixels(300), viewport.ClientSize.Width - HeaderPixels(86));
                bool stacked = width < HeaderPixels(800);
                int leftWidth = stacked ? width : (int)((width - HeaderPixels(74)) * .545);
                left.Width = leftWidth; right.Width = stacked ? width : width - leftWidth - HeaderPixels(74);
                Box(computer, 0, 8, 104, 104);
                TextAt(caption, HeaderPixels(127), HeaderPixels(14), leftWidth - HeaderPixels(127));
                int nameBottom = TextAt(name, HeaderPixels(127), HeaderPixels(36), leftWidth - HeaderPixels(127));
                int statusTop = Math.Max(HeaderPixels(134), nameBottom + HeaderPixels(34));
                agentReadyIcon.SetBounds(0, statusTop, HeaderPixels(40), HeaderPixels(40));
                int y = TextAt(agentHeading, HeaderPixels(62), statusTop - HeaderPixels(1), leftWidth - HeaderPixels(62));
                y = TextAt(agentSubtitle, HeaderPixels(62), y + HeaderPixels(4), leftWidth - HeaderPixels(62)) + HeaderPixels(24);
                if (agentPairCode.Visible) y = TextAt(agentPairCode, 0, y, leftWidth);
                int buttonX = 0, buttonHeight = 0;
                foreach (var button in new[] { copyAgentCode, enableSupport, restartAgent }.Where(button => button.Visible))
                {
                    var size = button.GetPreferredSize(Size.Empty); button.SetBounds(buttonX, y, size.Width, Math.Max(HeaderPixels(40), size.Height));
                    buttonX += button.Width + HeaderPixels(12); buttonHeight = Math.Max(buttonHeight, button.Height + HeaderPixels(12));
                }
                y += buttonHeight;
                if (pairingCountdown.Visible) { pairingCountdown.SetBounds(0, y, leftWidth, pairingCountdown.Height); y += pairingCountdown.Height + HeaderPixels(8); }
                if (pairingCountdownText.Visible && pairingCountdownText.Text.Length > 0) y = TextAt(pairingCountdownText, 0, y, leftWidth) + HeaderPixels(12);
                if (setupNotice.Visible)
                {
                    preparePlatform.SetBounds(leftWidth - HeaderPixels(180), HeaderPixels(12), HeaderPixels(168), HeaderPixels(42));
                    int noticeBottom = TextAt(setupNoticeText, HeaderPixels(12), HeaderPixels(12), leftWidth - HeaderPixels(204));
                    setupNotice.SetBounds(0, y, leftWidth, Math.Max(HeaderPixels(66), noticeBottom + HeaderPixels(12))); y += setupNotice.Height + HeaderPixels(12);
                }
                y = Math.Max(HeaderPixels(222), y); rule.SetBounds(0, y, leftWidth, HeaderPixels(1));
                y += HeaderPixels(26);
                if (agentInstructions.Visible)
                {
                    agentInstructions.SetBounds(0, y, leftWidth, HeaderPixels(174));
                    int stepY = TextAt(instructionsHeading, 0, 0, leftWidth) + HeaderPixels(24);
                    foreach (var (number, text) in new (Forms.Control, Forms.Control)[] { (numberOne, first), (numberTwo, second) })
                    {
                        number.SetBounds(0, stepY, HeaderPixels(44), HeaderPixels(44));
                        int bottom = TextAt(text, HeaderPixels(72), stepY + HeaderPixels(8), leftWidth - HeaderPixels(72));
                        stepY = Math.Max(stepY + HeaderPixels(44), bottom) + HeaderPixels(16);
                    }
                    agentInstructions.Height = stepY - HeaderPixels(16); y += agentInstructions.Height + HeaderPixels(50);
                }
                else if (agentState.Visible) y = TextAt(agentState, 0, y, leftWidth) + HeaderPixels(28);
                noteIcon.SetBounds(HeaderPixels(4), y, HeaderPixels(28), HeaderPixels(28));
                y = TextAt(agentSessionNote, HeaderPixels(54), y + HeaderPixels(4), leftWidth - HeaderPixels(54));
                left.Height = y + HeaderPixels(24);

                int rw = right.Width, labelX = HeaderPixels(77);
                TextAt(settingsHeading, 0, HeaderPixels(13), rw);
                networkRule.SetBounds(0, HeaderPixels(70), rw, HeaderPixels(1));
                int valueX = Math.Min(rw - HeaderPixels(118), Math.Max((int)(rw * .565), labelX + adminLabel.GetPreferredSize(Size.Empty).Width + HeaderPixels(16)));
                Box(networkIcon, 8, 94, 38, 38);
                int networkLabelBottom = TextAt(networkLabel, labelX, HeaderPixels(104), valueX - labelX - HeaderPixels(12));
                networkDot.SetBounds(valueX, HeaderPixels(105), HeaderPixels(17), HeaderPixels(17));
                int networkBottom = Math.Max(networkLabelBottom, TextAt(agentNetworkState, valueX + HeaderPixels(32), HeaderPixels(104), rw - valueX - HeaderPixels(32)));
                y = Math.Max(HeaderPixels(156), networkBottom + HeaderPixels(22));
                if (internetState.Visible) y = Math.Max(y, TextAt(internetState, labelX, networkBottom + HeaderPixels(8), rw - labelX) + HeaderPixels(22));
                sleepRule.SetBounds(0, y, rw, HeaderPixels(1));
                sleepIcon.SetBounds(HeaderPixels(8), y + HeaderPixels(23), HeaderPixels(38), HeaderPixels(38));
                TextAt(sleepLabel, labelX, y + HeaderPixels(33), valueX - labelX - HeaderPixels(12));
                TextAt(agentSleepState, valueX, y + HeaderPixels(33), rw - valueX);
                y = TextAt(sleepHelp, labelX, y + HeaderPixels(70), Math.Min(rw - labelX, HeaderPixels(280))) + HeaderPixels(19);
                y = Math.Max(y, HeaderPixels(289)); adminRule.SetBounds(0, y, rw, HeaderPixels(1));
                adminIcon.SetBounds(HeaderPixels(8), y + HeaderPixels(23), HeaderPixels(38), HeaderPixels(38));
                int adminLabelBottom = TextAt(adminLabel, labelX, y + HeaderPixels(33), valueX - labelX - HeaderPixels(12));
                adminMaintenanceToggle.SetBounds(valueX, y + HeaderPixels(26), rw - valueX, HeaderPixels(36));
                y = TextAt(adminHelp, labelX, Math.Max(y + HeaderPixels(75), adminLabelBottom + HeaderPixels(14)), rw - labelX);
                if (agentMaintenanceState.Visible) y = TextAt(agentMaintenanceState, labelX, y + HeaderPixels(8), rw - labelX);
                right.Height = y + HeaderPixels(24);
                left.Location = Point.Empty; right.Location = stacked ? new(0, left.Height + HeaderPixels(36)) : new(leftWidth + HeaderPixels(74), 0);
                divider.Visible = !stacked;
                int height = Math.Max(HeaderPixels(520), Math.Max(left.Bottom, right.Bottom));
                divider.SetBounds(leftWidth + HeaderPixels(34), 0, HeaderPixels(1), height);
                columns.Height = height;
                UpdateAgentScrollExtent(viewport, height + viewport.Padding.Vertical);
            }
            finally { layingOutAgent = false; }
        };
        viewport.SizeChanged += (_, _) =>
        {
            layoutAgent();
            // Resizing can overwrite the scroll range calculated by the nested content layout.
            if (viewport.IsHandleCreated) viewport.BeginInvoke((Action)viewport.PerformLayout);
        };
        DpiChanged += (_, _) => layoutAgent();
        agentNetworkState.ForeColorChanged += (_, _) => networkDot.Invalidate();
        UpdateAgentPresentation();
    }

    private void UpdateAgentPresentation()
    {
        bool privateWaiting = PrivateInternet && !agentIdle && agent?.Session.HasPaired != true && !(agent != null && IsOngoingUpdate(agent.UpdateProgress));
        agentInstructions.Visible = privateWaiting;
        agentState.Visible = !privateWaiting;
        agentPairCode.Visible = !PrivateInternet && !agentIdle;
        agentMaintenanceState.Visible = adminMaintenanceEnabled;
        internetState.Visible = internetSetupError != null || agent?.Internet?.Error.Length > 0;
        if (agentNetworkState.Text == UiText.Ready) agentNetworkState.ForeColor = PrimaryText;
        agentSleepState.ForeColor = PrimaryText;
        adminMaintenanceToggle.AccessibleName = UiText.AdminMaintenance;
        adminMaintenanceToggle.AccessibleDescription = UiText.Get("GiveMaintenanceHelp");
        agentReadyIcon.Invalidate(); adminMaintenanceToggle.Invalidate();
        layoutAgent?.Invoke();
    }
}

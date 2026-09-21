using System.Diagnostics;
using System.Text.Json;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

/// <summary>
/// The visible support workspace. The form deliberately keeps the safety-critical
/// session state in the header and footer while each page owns its working surface.
/// </summary>
public sealed partial class MainForm : Forms.Form
{
    private static readonly Color Canvas = Color.FromArgb(247, 249, 250);
    private static readonly Color Surface = Color.White;
    private static readonly Color Rail = Color.FromArgb(24, 33, 43);
    private static readonly Color RailSecondary = Color.FromArgb(168, 186, 194);
    private static readonly Color PrimaryText = Color.FromArgb(24, 48, 57);
    private static readonly Color SecondaryText = Color.FromArgb(99, 119, 128);
    private static readonly Color Divider = Color.FromArgb(223, 230, 234);
    private static readonly Color SelectedRail = Color.FromArgb(39, 60, 70);
    private static readonly Color Teal = Color.FromArgb(8, 127, 131);
    private static readonly Color TealHover = Color.FromArgb(7, 108, 112);
    private static readonly Color ConnectedText = Color.FromArgb(32, 107, 69);
    private static readonly Color ConnectedBack = Color.FromArgb(227, 243, 233);
    private static readonly Color WarningText = Color.FromArgb(139, 94, 18);
    private static readonly Color WarningBack = Color.FromArgb(255, 242, 219);
    private static readonly Color DestructiveText = Color.FromArgb(184, 61, 73);
    private static readonly Color DestructiveBack = Color.FromArgb(255, 240, 241);

    private readonly PageSwitcher rolePages = new() { Dock = Forms.DockStyle.Fill, NavigationVisible = false };
    private readonly PageSwitcher controllerPages = new() { Dock = Forms.DockStyle.Fill, NavigationVisible = false };
    private readonly Forms.TableLayoutPanel shell = new() { Dock = Forms.DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
    private readonly Forms.Panel rail = new() { Dock = Forms.DockStyle.Fill, BackColor = Rail };
    private readonly Forms.Panel header = new() { Dock = Forms.DockStyle.Fill, BackColor = Surface };
    private readonly Forms.Panel footer = new() { Dock = Forms.DockStyle.Fill, BackColor = Surface };
    private readonly Forms.TableLayoutPanel footerContent = new() { Dock = Forms.DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Forms.Padding(24, 0, 24, 0) };
    private readonly Forms.Label headerTitle = new WorkspaceLabel() { Name = "headerTitle", AutoSize = true, ForeColor = PrimaryText };
    private readonly Forms.Label headerSubtitle = new WorkspaceLabel() { Name = "headerSubtitle", AutoSize = true, ForeColor = SecondaryText };
    private readonly Forms.Panel statusPill = new() { Name = "connectionStatus", Height = 32, Width = 184 };
    private readonly Forms.Label statusDot = new() { AutoSize = true, Text = "●", Font = new Font("Segoe UI", 9), Margin = new Forms.Padding(10, 7, 4, 0) };
    private readonly Forms.Label statusLabel = new() { AutoSize = true, Font = new Font("Segoe UI", 9.5F), Margin = new Forms.Padding(0, 7, 8, 0) };
    private readonly Forms.Button terminateSession = Button(() => UiText.EndSupport, "terminateSession", destructive: true);
    private readonly Forms.Label footerLeft = new() { Name = "footerStatus", Dock = Forms.DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = SecondaryText, Font = new Font("Segoe UI", 9.5F) };
    private readonly Forms.Label footerRight = new() { Name = "footerDetail", Dock = Forms.DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleRight, ForeColor = SecondaryText, Font = new Font("Segoe UI", 9.5F) };

    private readonly Forms.Button roleAgent = RailButton(() => UiText.GiveControl, "roleAgent");
    private readonly Forms.Button roleController = RailButton(() => UiText.TakeControl, "roleController");
    private readonly Forms.Label controllerNavCaption = RailCaption(() => UiText.WorkspaceCaption);
    private readonly Forms.Button navConnection = RailSubButton(() => UiText.Connection, "navConnection");
    private readonly Forms.Button navScreen = RailSubButton(() => UiText.RemoteScreen, "navScreen");
    private readonly Forms.Button navProcesses = RailSubButton(() => UiText.Processes, "navProcesses");
    private readonly Forms.Button navFiles = RailSubButton(() => UiText.Files, "navFiles");
    private readonly Forms.Button navDiagnostics = RailSubButton(() => UiText.Diagnostics, "navDiagnostics");

    // Agent screen.
    private readonly Forms.Label agentEyebrow = Eyebrow(() => UiText.PairingCodeCaption);
    private readonly Forms.Label agentHeading = new WorkspaceLabel() { Name = "agentHeading", AutoSize = true, Dock = Forms.DockStyle.Fill, Font = new Font("Segoe UI", 32, FontStyle.Bold), ForeColor = PrimaryText }.WithText(() => UiText.ShareCode);
    private readonly Forms.Label agentSubtitle = new WorkspaceLabel() { Name = "agentSubtitle", AutoSize = true, Dock = Forms.DockStyle.Fill, Font = new Font("Segoe UI", 13), ForeColor = SecondaryText }.WithText(() => UiText.EnterCodeOnController);
    private readonly Forms.Label agentPairCode = new WorkspaceLabel() { Name = "agentPairCode", AutoSize = true, Text = "— — —", Font = new Font("Consolas", 42, FontStyle.Bold), ForeColor = PrimaryText };
    private readonly Forms.Button copyAgentCode = Button(() => UiText.Copy, "copyAgentCode", 86);
    private readonly Forms.Button restartAgent = Button(() => UiText.NewSupport, "restartAgent", primary: true);
    private bool agentIdle;
    private readonly Forms.ProgressBar pairingCountdown = new() { Name = "pairingCountdown", Minimum = 0, Maximum = 300, Value = 0, Height = 4, Style = Forms.ProgressBarStyle.Continuous };
    private readonly Forms.Label pairingCountdownText = new WorkspaceLabel() { Name = "pairingCountdownText", AutoSize = true, ForeColor = SecondaryText };
    private readonly Forms.Label agentState = new WorkspaceLabel() { Name = "agentState", AutoSize = true, ForeColor = PrimaryText };
    private readonly Forms.Label agentNetworkState = new() { Name = "agentNetworkState", AutoSize = true, ForeColor = SecondaryText };
    private readonly Forms.Label agentSleepState = new() { Name = "agentSleepState", AutoSize = true, ForeColor = SecondaryText };
    private readonly Forms.Label agentMaintenanceState = new() { Name = "agentMaintenanceState", AutoSize = true, ForeColor = SecondaryText };
    private readonly Forms.CheckBox adminMaintenanceToggle = new Forms.CheckBox
    {
        Name = "adminMaintenanceToggle", AutoSize = true, ForeColor = PrimaryText,
        Margin = new Forms.Padding(8, 2, 0, 0)
    }.WithText(() => UiText.AdminMaintenanceEnabled);
    private readonly Forms.Label agentSessionNote = new WorkspaceLabel() { Name = "agentSessionNote", AutoSize = true, ForeColor = SecondaryText, MaximumSize = new Size(620, 0) };
    private readonly Forms.Panel setupNotice = new() { Name = "agentSetupNotice", AutoSize = true, Visible = false, Padding = new Forms.Padding(12), BackColor = WarningBack };
    private readonly Forms.Label setupNoticeText = new() { AutoSize = true, ForeColor = WarningText, MaximumSize = new Size(440, 0) };
    private readonly Forms.Button preparePlatform = Button(() => UiText.EnableOnThisPc, "preparePlatform", 148);
    private readonly Forms.Label agentFingerprint = new() { Name = "agentFingerprint", AutoSize = true, ForeColor = SecondaryText, MaximumSize = new Size(720, 0) };
    private readonly Forms.Label agentLog = new() { Name = "agentLog", AutoSize = true, ForeColor = SecondaryText, MaximumSize = new Size(720, 0) };

    // Connection screen.
    private readonly RememberedListView peers = new() { Name = "peers", Dock = Forms.DockStyle.Fill, View = Forms.View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, BorderStyle = Forms.BorderStyle.None, BackColor = Surface, LogicalRowHeight = 44 };
    private readonly Forms.TextBox host = TextBox("host");
    private readonly Forms.TextBox code = TextBox("pairCode");
    private readonly Forms.Button pairButton = Button(() => UiText.Connect, "pair", 110, primary: true);
    private readonly Forms.Button updateClientButton = Button(() => UiText.UpdateClient, "updateClient", 150);
    private readonly Forms.Button discoverButton = Button(() => UiText.Refresh, "discover", 92);
    private readonly Forms.Label connectionState = new() { Name = "connectionFormState", AutoSize = true, ForeColor = SecondaryText, MaximumSize = new Size(460, 0) };
    private readonly Forms.TableLayoutPanel updateProgressArea = new() { Dock = Forms.DockStyle.Top, Height = 50, ColumnCount = 1, RowCount = 2, Margin = Forms.Padding.Empty, Visible = false };
    private readonly Forms.Panel updateProgressTrack = new() { Name = "updateProgress", Dock = Forms.DockStyle.Fill, BackColor = Divider, Margin = new Forms.Padding(0, 0, 0, 4) };
    private readonly Forms.Panel updateProgressFill = new() { Dock = Forms.DockStyle.Left, BackColor = Teal, Width = 0 };
    private readonly Forms.Label updateProgressText = new() { Name = "updateProgressText", Dock = Forms.DockStyle.Fill, AutoSize = false, ForeColor = SecondaryText, Margin = Forms.Padding.Empty };
    private int updateTransferPercent;
    private readonly Forms.Label selectedPeerName = new WorkspaceLabel { AutoSize = true, Font = new Font("Segoe UI", 15, FontStyle.Bold), ForeColor = PrimaryText }.WithText(() => UiText.ManualConnection);
    private readonly Forms.Label selectedPeerAddress = new WorkspaceLabel { AutoSize = true, ForeColor = SecondaryText }.WithText(() => UiText.TargetAddress);
    private readonly Forms.Label discoveryState = new() { AutoSize = true, ForeColor = SecondaryText };
    private readonly List<Peer> discoveredPeers = [];
    private Peer? selectedPeer;
    private string selectedFingerprint = "";
    private bool renderingPeers;

    // Live screen.
    private readonly RemoteScreenView screen = new() { Name = "remoteScreen", Dock = Forms.DockStyle.Fill, SizeMode = Forms.PictureBoxSizeMode.Zoom, BackColor = Rail };
    private readonly Forms.Panel screenSurface = new() { Dock = Forms.DockStyle.Fill, BackColor = Rail, Padding = new Forms.Padding(0) };
    private readonly Forms.Label liveBadge = Badge(() => UiText.Live, "liveBadge");
    private readonly Forms.Label streamOverlay = new Forms.Label { Name = "streamOverlay", AutoSize = true, ForeColor = Color.White, BackColor = Color.FromArgb(190, 20, 38, 48), Padding = new Forms.Padding(10, 7, 10, 7), Visible = true }.WithText(() => UiText.WaitingFreshFrameEllipsis);
    private readonly Forms.Button pauseViewing = Button(() => UiText.Pause, "pauseViewing", 82);
    private readonly LocalizedComboBox monitor = new() { Name = "monitor", DropDownStyle = Forms.ComboBoxStyle.DropDownList, Width = 130 };
    private readonly Forms.CheckBox mouseEnabled = new Forms.CheckBox { Name = "mouseKeyboard", Checked = true, AutoSize = true, ForeColor = PrimaryText, Margin = new Forms.Padding(12, 10, 0, 0) }.WithText(() => UiText.MouseKeyboardControl);
    private readonly Forms.CheckBox relayEconomy = new Forms.CheckBox { Name = "relayEconomy", Checked = true, AutoSize = true, ForeColor = PrimaryText, Margin = new Forms.Padding(12, 10, 0, 0) }.WithText(() => UiText.RelayEconomy);
    private bool resumeViewingAfterMinimize;
    private readonly Forms.Label streamStatus = new() { Name = "streamStatus", AutoSize = false, Dock = Forms.DockStyle.Fill, Margin = Forms.Padding.Empty, ForeColor = SecondaryText, Font = new Font("Segoe UI", 9.5F), AutoEllipsis = true, TextAlign = ContentAlignment.MiddleRight };
    private readonly Forms.Label inputStatus = new() { Name = "inputStatus", AutoSize = false, Dock = Forms.DockStyle.Fill, Margin = Forms.Padding.Empty, ForeColor = SecondaryText, Font = new Font("Segoe UI", 9.5F), AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly RemoteInputState inputState = new();
    private readonly Forms.TextBox remoteText = TextBox("remoteText");
    private readonly Forms.Button typeText = Button(() => UiText.TypeText, "typeText", 76);
    private readonly Forms.Button enterKey = Button(() => UiText.EnterKey, "enterKey", 76);
    private DesktopGeometry? geometry;
    private bool liveFrameFresh;
    private CancellationTokenSource? liveStream;
    private DateTimeOffset? streamStartedUtc;
    private int streamFrames;
    private long streamBytes;
    private string streamCodec = "";

    // Processes and files.
    private readonly RememberedListView processList = new() { Name = "processList", Dock = Forms.DockStyle.Fill, View = Forms.View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, BorderStyle = Forms.BorderStyle.None, BackColor = Surface };
    private readonly Forms.Label cpuSummary = SummaryValue("cpuSummary");
    private readonly Forms.Label ramSummary = SummaryValue("ramSummary");
    private readonly Forms.Label processSummary = SummaryValue("processSummary");
    private readonly Forms.Label resourceMeasuredAt = SummaryValue("resourceMeasuredAt");
    private readonly Forms.Label resourceState = new() { Name = "resourceState", AutoSize = true, ForeColor = SecondaryText };
    private readonly Forms.Label volumeSummary = new() { Name = "volumeSummary", AutoSize = false, Dock = Forms.DockStyle.Fill, ForeColor = SecondaryText, Font = new Font("Segoe UI", 9.5F), AutoEllipsis = true };
    private SortState<ProcessSortColumn> processSort = new(ProcessSortColumn.CpuPercentTotalMachine, true);
    private readonly List<ProcessSortRow> processRows = [];
    private readonly Forms.TextBox fileDirectory = TextBox("remoteDirectory");
    private readonly Forms.TextBox remotePath = TextBox("remotePath");
    private readonly RememberedListView fileList = new() { Name = "remoteFiles", Dock = Forms.DockStyle.Fill, View = Forms.View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, BorderStyle = Forms.BorderStyle.None, BackColor = Surface };
    private readonly Forms.Label fileState = new() { Name = "fileState", AutoSize = true, ForeColor = SecondaryText };
    private SortState<FileSortColumn> fileSort = new(FileSortColumn.Name, false);
    private readonly List<FileSortRow> fileRows = [];
    private string currentDirectory = "";
    private string? selectedFilePath;
    private bool fileDirectoryLoaded;
    private Forms.Button openFolderButton = null!;
    private readonly Forms.ProgressBar fileTransferProgress = new() { Name = "fileTransferProgress", Dock = Forms.DockStyle.Top, Height = 12 };
    private readonly Forms.Label fileTransferStatus = new() { Name = "fileTransferStatus", AutoSize = false, AutoEllipsis = true, Height = 26, ForeColor = PrimaryText };
    private readonly Forms.Label fileTransferDetails = new() { Name = "fileTransferDetails", AutoSize = false, AutoEllipsis = true, Height = 26, Dock = Forms.DockStyle.Top, ForeColor = SecondaryText };

    // Diagnostics.
    private readonly Forms.ComboBox operations = new() { Name = "operation", DropDownStyle = Forms.ComboBoxStyle.DropDownList, Width = 190 };
    private readonly Forms.NumericUpDown pid = new() { Name = "targetPid", Maximum = int.MaxValue, Width = 90, Minimum = 0 };
    private readonly Forms.TextBox arguments = new() { Name = "arguments", Multiline = true, ScrollBars = Forms.ScrollBars.Vertical, Dock = Forms.DockStyle.Fill, Font = new Font("Consolas", 10), BackColor = Surface, BorderStyle = Forms.BorderStyle.FixedSingle };
    private readonly Forms.TextBox output = new() { Name = "output", Multiline = true, ReadOnly = true, ScrollBars = Forms.ScrollBars.Both, Dock = Forms.DockStyle.Fill, Font = new Font("Consolas", 9), WordWrap = false, BackColor = Surface, BorderStyle = Forms.BorderStyle.FixedSingle };
    private readonly Forms.Label diagnosticState = new() { Name = "diagnosticState", AutoSize = true, ForeColor = SecondaryText };
    private readonly Forms.Label technicalIdentity = new() { Name = "technicalIdentity", AutoSize = true, ForeColor = SecondaryText, MaximumSize = new Size(900, 0) };

    private readonly RemoteInputQueue<QueuedInput> inputQueue = new();
    private long lastMove;
    private readonly string root;
    private readonly bool loopbackOnly;
    private readonly bool startAgentOnLaunch;
    private readonly bool startInTray;
    private bool adminMaintenanceEnabled;
    private bool changingAdminMaintenance;
    private readonly Forms.Timer renderTimer = new() { Interval = 250 };
    private readonly Forms.Timer inputRecoveryTimer = new() { Interval = 750 };
    private Size statusPillRegionSize;
    private readonly Forms.NotifyIcon tray = new();
    private RemoteClient? client;
    private AgentServer? agent;
    private bool agentNetworkPrepared;
    private CancellationTokenSource? action;
    private CancellationTokenSource? fileTransferLifetime;
    private Forms.Button transferCancelButton = null!;
    private string? inputBlockMessage;
    private CancellationTokenSource? heartbeatLifetime;
    private CancellationTokenSource? discoveryLifetime;
    private CancellationTokenSource? pairingLifetime;
    private CancellationTokenSource? clientUpdateLifetime;
    private Task? heartbeatTask;
    private PowerHold? powerHold;
    private bool heartbeatHealthy;
    private bool supportSession;
    private bool pairingBusy;
    private bool clientUpdateBusy;
    private bool clientUpToDate;
    private bool synchronizingAgent;
    private bool terminating;
    private bool quitting;
    private bool shutdownStarted;
    private bool suppressTerminationEvent;
    private bool trayVisible;
    private bool trayNoticeShown;
    private Forms.FormWindowState trayWindowState;
    private bool resumeViewingOnRestore;
    private int operationGeneration;
    private int sessionGeneration;
    private string? operationId;
    private DateTimeOffset? lastMeasurementUtc;

    public string? CurrentPairingCode { get; private set; }
    public AgentServer? Agent => agent;
    public bool AgentNetworkReady => loopbackOnly || agentNetworkPrepared || (PrivateInternet && agent?.Internet?.Connected == true);
    public RemoteClient? Client => client;

    private readonly string? startupPreparationError;

    public MainForm(bool startAgent = true, string? dataRoot = null, bool loopbackOnly = false, string? startupPreparationError = null, string? languageOverride = null, bool enableSupport = false, bool startInTray = false)
    {
        // Build the entire 96-DPI layout before WinForms applies startup DPI.
        // Otherwise early layout can consume the scale factor while later
        // panels still contain their unscaled design-time dimensions.
        SuspendLayout();
        root = dataRoot ?? Vault.DefaultRoot;
        isUpdateAdmin = new UpdateAdminStore(root).IsAdmin;
        this.loopbackOnly = loopbackOnly;
        this.startupPreparationError = startupPreparationError;
        startAgentOnLaunch = startAgent;
        this.startInTray = startInTray;
        adminMaintenanceEnabled = AdminMaintenancePreference.Load(root);
        adminMaintenanceToggle.Checked = adminMaintenanceEnabled;
        privateSupportEnabled = enableSupport;

        Text = "Remote Debugger";
        Name = "RemoteDebuggerMain";
        Icon = LoadApplicationIcon();
        Width = 1280;
        Height = 860;
        MinimumSize = new Size(1060, 720);
        StartPosition = Forms.FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10.5F);
        BackColor = Canvas;
        AutoScaleMode = Forms.AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);

        BuildShell(languageOverride);
        BuildAgentPage();
        BuildControllerPages();
        BuildTray();
        WireEvents();
        InitializeWorkspaceState();
        LoadSavedConnection();
        SupportPlatform.ManagedRelaunchRequested += OnManagedRelaunchRequested;

        renderTimer.Tick += (_, _) => RefreshUiState();
        Load += (_, _) =>
        {
            RestoreWindowPlacement();
            if (startInTray)
            {
                trayWindowState = WindowState;
                ShowInTaskbar = false;
                WindowState = Forms.FormWindowState.Minimized;
            }
        };
        Shown += MainFormShown;
        FormClosing += MainFormClosing;
        FormClosed += (_, _) => DisposeResources();
        Resize += (_, _) => UpdateMinimizedViewing();
        ResumeLayout(true);
    }

    private void BuildShell(string? languageOverride)
    {
        shell.Padding = Forms.Padding.Empty;
        shell.BackColor = Canvas;
        shell.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Absolute, 216));
        shell.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
        shell.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 96));
        shell.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100));
        shell.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 40));
        shell.Controls.Add(rail, 0, 0);
        shell.SetRowSpan(rail, 3);
        shell.Controls.Add(header, 1, 0);
        shell.Controls.Add(rolePages, 1, 1);
        shell.Controls.Add(footer, 1, 2);
        Controls.Add(shell);

        BuildRail(languageOverride);
        BuildHeader();
        BuildFooter();
    }

    private void BuildRail(string? languageOverride)
    {
        var layout = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Forms.Padding(12, 16, 12, 8), BackColor = Rail };
        layout.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 76));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 112));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 72));

        var roles = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, FlowDirection = Forms.FlowDirection.TopDown, WrapContents = false, Margin = Forms.Padding.Empty, Padding = new Forms.Padding(0), BackColor = Rail };
        roles.Controls.Add(roleAgent); roles.Controls.Add(roleController);
        var brand = new WorkspaceLabel { Name = "appBrand", Text = "Remote\nDebugger", ForeColor = Color.White, Font = new Font("Segoe UI", 17, FontStyle.Bold), Dock = Forms.DockStyle.Fill, Padding = new Forms.Padding(14, 0, 0, 0) };
        layout.Controls.Add(brand, 0, 0);
        layout.Controls.Add(roles, 0, 1);

        var work = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, FlowDirection = Forms.FlowDirection.TopDown, WrapContents = false, Margin = Forms.Padding.Empty, Padding = new Forms.Padding(0, 14, 0, 0), BackColor = Rail };
        work.Controls.Add(controllerNavCaption);
        work.Controls.Add(navConnection); work.Controls.Add(navScreen); work.Controls.Add(navProcesses); work.Controls.Add(navFiles); work.Controls.Add(navDiagnostics);
        layout.Controls.Add(work, 0, 2);

        var local = new Forms.Panel { Dock = Forms.DockStyle.Fill };
        var machine = new Forms.Label { Text = Environment.MachineName, AutoSize = false, Width = 180, Height = 20, ForeColor = RailSecondary, Font = new Font("Segoe UI", 9.5F), Location = new Point(12, 8), AutoEllipsis = true };
        var version = new Forms.Label { Text = "Remote Debugger · " + (typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "0.2"), AutoSize = false, Width = 180, Height = 20, ForeColor = Color.FromArgb(116, 143, 154), Font = new Font("Segoe UI", 8.5F), Location = new Point(12, 32), AutoEllipsis = true };
        layout.Controls.Add(BuildLanguageSelector(languageOverride), 0, 3);
        local.Controls.Add(machine); local.Controls.Add(version); layout.Controls.Add(local, 0, 4);
        rail.Controls.Add(layout);
    }

    private void BuildHeader()
    {
        var layout = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Forms.Padding(28, 0, 28, 0), Margin = Forms.Padding.Empty };
        layout.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
        layout.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.AutoSize));
        var titles = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Forms.Padding(0, 14, 0, 0), Margin = Forms.Padding.Empty };
        titles.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize)); titles.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
        headerTitle.Font = new Font("Segoe UI", 22, FontStyle.Bold); headerSubtitle.Font = new Font("Segoe UI", 10.5F);
        titles.Controls.Add(headerTitle, 0, 0); titles.Controls.Add(headerSubtitle, 0, 1);
        var actions = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, FlowDirection = Forms.FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Padding = new Forms.Padding(0, 20, 0, 0) };
        statusPill.SizeChanged += (_, _) => RefreshStatusPillRegion();
        statusPill.Width = 176; statusPill.Height = 30; statusPill.Margin = new Forms.Padding(0, 3, 12, 0);
        terminateSession.Margin = Forms.Padding.Empty; terminateSession.Visible = false;
        statusDot.Location = new Point(12, 7); statusLabel.Location = new Point(28, 6); statusPill.Controls.Add(statusDot); statusPill.Controls.Add(statusLabel); RefreshStatusPillRegion();
        actions.Controls.Add(statusPill); actions.Controls.Add(terminateSession);
        layout.Controls.Add(titles, 0, 0); layout.Controls.Add(actions, 1, 0);
        header.Controls.Add(layout);
        var line = new Forms.Panel { Dock = Forms.DockStyle.Bottom, Height = 1, BackColor = Divider }; header.Controls.Add(line);
    }

    private void BuildFooter()
    {
        var line = new Forms.Panel { Dock = Forms.DockStyle.Top, Height = 1, BackColor = Divider }; footer.Controls.Add(line);
        footerContent.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 58)); footerContent.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 42));
        footerLeft.Margin = Forms.Padding.Empty; footerRight.Margin = Forms.Padding.Empty;
        SetScreenFooter(false);
        footer.Controls.Add(footerContent);
    }

    private bool screenFooterVisible;
    private void SetScreenFooter(bool visible)
    {
        if (screenFooterVisible == visible && footerContent.Controls.Count == 2) return;
        footerContent.Controls.Clear();
        footerContent.Controls.Add(visible ? inputStatus : footerLeft, 0, 0);
        footerContent.Controls.Add(visible ? streamStatus : footerRight, 1, 0);
        screenFooterVisible = visible;
    }

    private void BuildAgentPage()
    {
        var page = new PagePanel(() => UiText.GiveControl) { BackColor = Canvas, Padding = new Forms.Padding(0) };
        var content = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 1, RowCount = 1, Padding = new Forms.Padding(28, 36, 28, 20), Margin = Forms.Padding.Empty };
        content.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
        content.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100));
        var heroHost = new Forms.Panel { Name = "agentWorkspace", Dock = Forms.DockStyle.Fill, BackColor = Canvas, Margin = Forms.Padding.Empty, AutoScroll = true };
        var hero = BuildAgentContent(); hero.Dock = Forms.DockStyle.Top; hero.Width = 760; hero.Anchor = Forms.AnchorStyles.Top | Forms.AnchorStyles.Left;
        // Docked content is excluded from WinForms' automatic scroll extent.
        // Preserve access to the final explanation on short/high-DPI displays.
        hero.SizeChanged += (_, _) => UpdateAgentScrollExtent(heroHost, hero.Height);
        heroHost.SizeChanged += (_, _) => UpdateAgentScrollExtent(heroHost, hero.Height);
        heroHost.Controls.Add(hero); content.Controls.Add(heroHost, 0, 0);
        page.Controls.Add(content); rolePages.TabPages.Add(page);
    }

    private Forms.Control BuildAgentContent()
    {
        var panel = new Forms.Panel { Dock = Forms.DockStyle.Top, Width = 760, AutoSize = true, AutoSizeMode = Forms.AutoSizeMode.GrowAndShrink };
        var layout = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Top, Width = 760, AutoSize = true, AutoSizeMode = Forms.AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 11, Margin = Forms.Padding.Empty, Padding = Forms.Padding.Empty };
        layout.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 20));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 38));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 90));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 30));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));

        agentEyebrow.Dock = Forms.DockStyle.Fill; layout.Controls.Add(agentEyebrow, 0, 0);
        layout.Controls.Add(agentHeading, 0, 1);
        layout.Controls.Add(BuildInternetSection(), 0, 2);

        var codeRow = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Top, AutoSize = true, AutoSizeMode = Forms.AutoSizeMode.GrowAndShrink, WrapContents = false, FlowDirection = Forms.FlowDirection.LeftToRight, Padding = new Forms.Padding(0, 10, 0, 0), Margin = Forms.Padding.Empty };
        agentPairCode.Margin = new Forms.Padding(0, 0, 14, 0); copyAgentCode.Margin = new Forms.Padding(0, 4, 0, 0);
        if (PrivateInternet) codeRow.Controls.Add(enableSupport);
        else { codeRow.Controls.Add(agentPairCode); codeRow.Controls.Add(copyAgentCode); }
        restartAgent.Visible = false; codeRow.Controls.Add(restartAgent); layout.Controls.Add(codeRow, 0, 3);
        pairingCountdown.Width = 480; pairingCountdown.Height = 12; pairingCountdown.Margin = new Forms.Padding(0, 2, 0, 4);
        pairingCountdown.Visible = !PrivateInternet; layout.Controls.Add(pairingCountdown, 0, 4);
        pairingCountdownText.AutoSize = false; pairingCountdownText.Dock = Forms.DockStyle.Fill; pairingCountdownText.Margin = Forms.Padding.Empty; layout.Controls.Add(pairingCountdownText, 0, 5);

        var divider = new Forms.Panel { Dock = Forms.DockStyle.Top, Height = 1, BackColor = Divider, Margin = new Forms.Padding(0, 18, 0, 0) }; layout.Controls.Add(divider, 0, 6);
        var states = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, Height = 90, ColumnCount = 4, RowCount = 3, Margin = Forms.Padding.Empty };
        states.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Absolute, 22)); states.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 52)); states.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 28)); states.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.AutoSize));
        for (int row = 0; row < 3; row++) states.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 33.333F));
        AddAgentStateRow(states, 0, () => PrivateInternet ? UiText.InternetLabel : UiText.PrivateNetwork, agentNetworkState); AddAgentStateRow(states, 1, () => UiText.Sleep, agentSleepState); AddAgentStateRow(states, 2, () => UiText.AdminMaintenance, agentMaintenanceState); states.Controls.Add(adminMaintenanceToggle, 3, 2); layout.Controls.Add(states, 0, 7);

        setupNotice.AutoSize = false; setupNotice.Dock = Forms.DockStyle.Fill; setupNotice.Width = 760; setupNotice.Height = 66; setupNotice.Padding = new Forms.Padding(8); setupNotice.Controls.Clear();
        var noticeLayout = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Forms.Padding.Empty, Padding = Forms.Padding.Empty };
        noticeLayout.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100)); noticeLayout.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.AutoSize));
        setupNoticeText.AutoSize = false; setupNoticeText.Dock = Forms.DockStyle.Fill; setupNoticeText.MaximumSize = Size.Empty; setupNoticeText.Margin = Forms.Padding.Empty; setupNoticeText.TextAlign = ContentAlignment.MiddleLeft;
        preparePlatform.Dock = Forms.DockStyle.Top; preparePlatform.Margin = new Forms.Padding(12, 4, 0, 0);
        noticeLayout.Controls.Add(setupNoticeText, 0, 0); noticeLayout.Controls.Add(preparePlatform, 1, 0); setupNotice.Controls.Add(noticeLayout); layout.Controls.Add(setupNotice, 0, 8);

        agentState.AutoSize = false; agentState.Dock = Forms.DockStyle.Fill; agentState.Margin = Forms.Padding.Empty; agentState.TextAlign = ContentAlignment.MiddleLeft; layout.Controls.Add(agentState, 0, 9);
        agentSessionNote.Dock = Forms.DockStyle.Top; agentSessionNote.MaximumSize = new Size(760, 0); agentSessionNote.Margin = Forms.Padding.Empty; layout.Controls.Add(agentSessionNote, 0, 10);
        panel.Controls.Add(layout);
        return panel;
    }

    private static void AddAgentStateRow(Forms.TableLayoutPanel table, int row, Func<string> label, Forms.Label value)
    {
        var dot = new Forms.Label { AutoSize = true, Text = "●", ForeColor = SecondaryText, Margin = new Forms.Padding(0, 3, 0, 0) };
        var name = new Forms.Label { AutoSize = true, ForeColor = SecondaryText, Margin = new Forms.Padding(0, 3, 0, 0) }.WithText(label);
        value.Margin = new Forms.Padding(0, 3, 0, 0); value.Anchor = Forms.AnchorStyles.Left;
        table.Controls.Add(dot, 0, row); table.Controls.Add(name, 1, row); table.Controls.Add(value, 2, row);
    }

    private void BuildControllerPages()
    {
        var page = new PagePanel(() => UiText.TakeControl) { BackColor = Canvas };
        page.Controls.Add(controllerPages);
        controllerPages.TabPages.Add(BuildConnectionPage());
        controllerPages.TabPages.Add(BuildScreenPage());
        controllerPages.TabPages.Add(BuildProcessPage());
        controllerPages.TabPages.Add(BuildFilePage());
        controllerPages.TabPages.Add(BuildDiagnosticsPage());
        rolePages.TabPages.Add(page);
    }

    private PagePanel BuildConnectionPage()
    {
        var page = new PagePanel(() => UiText.Connection) { BackColor = Canvas, Padding = new Forms.Padding(28) };
        var columns = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        columns.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, PrivateInternet ? 62 : 45)); columns.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Absolute, 1)); columns.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, PrivateInternet ? 38 : 55));
        columns.Controls.Add(BuildPeerList(), 0, 0); columns.Controls.Add(new Forms.Panel { Dock = Forms.DockStyle.Fill, BackColor = Divider }, 1, 0); columns.Controls.Add(BuildConnectionForm(), 2, 0);
        page.Controls.Add(columns); return page;
    }

    private Forms.Control BuildPeerList()
    {
        var panel = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Forms.Padding(0, 4, 24, 0) };
        panel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 42)); panel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 54)); panel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 40)); panel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100));
        panel.Controls.Add(new WorkspaceLabel { AutoSize = true, Font = new Font("Segoe UI", 15, FontStyle.Bold), ForeColor = PrimaryText, Anchor = Forms.AnchorStyles.Left, Margin = new Forms.Padding(0, 0, 0, 8) }.WithText(() => UiText.AvailablePcs), 0, 0);
        discoverButton.Anchor = Forms.AnchorStyles.Left;
        panel.Controls.Add(PrivateInternet ? ControlRow(discoverButton, updateAllDevices) : discoverButton, 0, 1);
        discoveryState.Dock = Forms.DockStyle.Fill; discoveryState.Margin = new Forms.Padding(0, 0, 0, 8); discoveryState.TextAlign = ContentAlignment.MiddleLeft;
        panel.Controls.Add(discoveryState, 0, 2);
        peers.Columns.Add("", 110).WithText(() => UiText.Name);
        if (!PrivateInternet) peers.Columns.Add("", 125).WithText(() => UiText.Address);
        if (PrivateInternet) peers.Columns.Add("", 62).WithText(() => UiText.DeviceVersion);
        peers.Columns.Add("", PrivateInternet ? 150 : 90).WithText(() => UiText.State);
        if (PrivateInternet) peers.Columns.Add("", 180).WithText(() => UiText.DeviceProgress);
        InitializeFleet();
        peers.Margin = Forms.Padding.Empty;
        peers.RememberLayout(root, PrivateInternet ? ["name", "version", "state", "progress"] : ["name", "address", "state"]); panel.Controls.Add(peers, 0, 3);
        return panel;
    }

    private Forms.Control BuildConnectionForm()
    {
        if (PrivateInternet) { selectedPeerName.SetText(() => UiText.SelectComputer); selectedPeerAddress.SetText(""); }
        var panel = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 1, RowCount = 9, Padding = new Forms.Padding(24, 0, 0, 0) };
        panel.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
        selectedPeerName.AutoSize = selectedPeerAddress.AutoSize = connectionState.AutoSize = false;
        selectedPeerName.Dock = selectedPeerAddress.Dock = connectionState.Dock = Forms.DockStyle.Fill;
        selectedPeerName.AutoEllipsis = selectedPeerAddress.AutoEllipsis = connectionState.AutoEllipsis = true;
        connectionState.MaximumSize = Size.Empty;
        panel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 34)); panel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 30));
        for (int i = 2; i < 7; i++) panel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
        panel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 60)); panel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100));
        panel.Controls.Add(selectedPeerName, 0, 0); panel.Controls.Add(selectedPeerAddress, 0, 1);
        if (PrivateInternet) BuildWanAddressFields(panel);
        if (!PrivateInternet)
        {
            panel.Controls.Add(new WorkspaceLabel { AutoSize = true, ForeColor = SecondaryText, Margin = Forms.Padding.Empty, Dock = Forms.DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }.WithText(() => UiText.IpOrManual), 0, 2);
            host.Dock = Forms.DockStyle.Top; host.Margin = new Forms.Padding(0, 4, 0, 18); panel.Controls.Add(host, 0, 3);
        }
        if (!PrivateInternet) panel.Controls.Add(new WorkspaceLabel { AutoSize = true, ForeColor = SecondaryText, Margin = Forms.Padding.Empty, Dock = Forms.DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }.WithText(() => UiText.SixDigitCode), 0, 4);
        code.Width = 150; code.Font = new Font("Consolas", 20); code.MaxLength = 6; code.TextAlign = Forms.HorizontalAlignment.Center;
        updateClientButton.Visible = false;
        var codeRow = PrivateInternet ? ControlRow(pairButton, saveWanAddress) : ControlRow(code, pairButton); codeRow.Margin = new Forms.Padding(0, 4, 0, 0); panel.Controls.Add(codeRow, 0, 5);
        panel.Controls.Add(BuildWakeControls(), 0, 6);
        connectionState.Margin = new Forms.Padding(0, 7, 0, 0); panel.Controls.Add(connectionState, 0, 7);
        updateProgressArea.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 10));
        updateProgressArea.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100));
        updateProgressTrack.Controls.Add(updateProgressFill);
        updateProgressTrack.SizeChanged += (_, _) => updateProgressFill.Width = updateProgressTrack.ClientSize.Width * updateTransferPercent / 100;
        updateProgressArea.Controls.Add(updateProgressTrack, 0, 0); updateProgressArea.Controls.Add(updateProgressText, 0, 1);
        panel.Controls.Add(updateProgressArea, 0, 8); return panel;
    }

    private static string UpdateProgressDescription(AgentUpdateProgress progress) => progress.Stage switch
    {
        "preparing" => UiText.PreparingUpdate,
        "hashing" => UiText.PreparingUpdatePackage,
        "finalizing" => UiText.FinalizingUpdate,
        "transferring" => UiText.Format(UiText.TransferProgress, progress.TransferPercent, progress.TransferredBytes / 1048576d, progress.TotalBytes / 1048576d),
        "verifying" => UiText.TransferVerifying,
        "restarting" => UiText.TransferRestarting,
        "complete" => UiText.VersionSynchronized,
        _ => UiText.PreparingTransfer
    };

    private void ShowUpdateProgress(AgentUpdateProgress progress)
    {
        updateProgressArea.Visible = true;
        if (connectedUpdateProgress == null || progress.Stage == "idle")
            connectedUpdateProgress = CreateUpdateProgress(client?.Connection.Fingerprint ?? "connected");
        connectedUpdateReport = progress;
        connectedUpdateProgress.Report(progress.Stage == "idle" ? "preparing" : progress.Stage, progress.TransferredBytes, progress.TotalBytes);
        updateProgressFill.BackColor = Teal;
        updateProgressText.ForeColor = SecondaryText;
        RefreshUpdateProgress();
        if (progress.Stage == "complete") SaveUpdateTimings();
    }

    private PagePanel BuildScreenPage()
    {
        var page = new PagePanel(() => UiText.RemoteScreen) { BackColor = Canvas, Padding = new Forms.Padding(20, 8, 20, 12) };
        monitor.Items.Add(new MonitorChoice(0, () => UiText.PrimaryMonitor)); monitor.SelectedIndex = 0;
        var top = ControlRow(RowLabel(() => UiText.Monitor, "monitorLabel"), monitor, mouseEnabled, relayEconomy, new Forms.Panel { Size = new Size(1, 1) }, pauseViewing);
        top.Dock = Forms.DockStyle.Top; top.Padding = new Forms.Padding(0, 0, 0, 8);
        top.ColumnStyles[4] = new Forms.ColumnStyle(Forms.SizeType.Percent, 100);
        screenSurface.Controls.Add(screen); screenSurface.Controls.Add(liveBadge); screenSurface.Controls.Add(streamOverlay); liveBadge.BringToFront(); streamOverlay.BringToFront(); liveBadge.Location = new Point(16, 14); streamOverlay.Anchor = Forms.AnchorStyles.None; screenSurface.Resize += (_, _) => streamOverlay.Location = new Point(Math.Max(0, (screenSurface.Width - streamOverlay.Width) / 2), Math.Max(0, (screenSurface.Height - streamOverlay.Height) / 2));
        var view = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        // An automatic column can grow to the bitmap's preferred width when DPI
        // changes. Keep both the viewer and its input row inside the workspace.
        view.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
        view.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100)); view.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize)); view.Controls.Add(screenSurface, 0, 0);
        var bottom = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Forms.Padding(0, 6, 0, 0), Margin = Forms.Padding.Empty };
        bottom.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100)); bottom.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.AutoSize)); bottom.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.AutoSize));
        bottom.AutoSize = true; bottom.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
        remoteText.Anchor = Forms.AnchorStyles.Left | Forms.AnchorStyles.Right; remoteText.PlaceholderText = UiText.RemoteTextPlaceholder;
        typeText.Anchor = enterKey.Anchor = Forms.AnchorStyles.Left;
        bottom.Controls.Add(remoteText, 0, 0); bottom.Controls.Add(typeText, 1, 0); bottom.Controls.Add(enterKey, 2, 0);
        view.Controls.Add(bottom, 0, 1);
        page.Controls.Add(view); page.Controls.Add(top); return page;
    }

    private PagePanel BuildProcessPage()
    {
        var page = new PagePanel(() => UiText.Processes) { BackColor = Canvas, Padding = new Forms.Padding(28) };
        refreshResourcesButton = Button(() => UiText.Refresh, "refreshResources", 100, primary: true);
        var action = ControlRow(refreshResourcesButton, resourceState);
        var summary = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Top, AutoSize = true, ColumnCount = 4, RowCount = 1, Margin = Forms.Padding.Empty, Padding = new Forms.Padding(0, 16, 0, 10) };
        summary.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
        for (int i = 0; i < 4; i++) summary.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 25));
        AddSummary(summary, 0, () => "CPU", cpuSummary); AddSummary(summary, 1, () => "RAM", ramSummary); AddSummary(summary, 2, () => UiText.Processes, processSummary); AddSummary(summary, 3, () => UiText.MeasuredAt, resourceMeasuredAt);
        processList.Columns.Add("PID", 80, Forms.HorizontalAlignment.Right); processList.Columns.Add("", 240).WithText(() => UiText.Application); processList.Columns.Add("CPU %", 100, Forms.HorizontalAlignment.Right); processList.Columns.Add("", 110, Forms.HorizontalAlignment.Right).WithText(() => UiText.RamMib); processList.Columns.Add("", 100).WithText(() => UiText.Responding); processList.Columns.Add("", 300).WithText(() => UiText.Window);
        processList.RememberLayout(root, "pid", "name", "cpu", "ram", "responding", "window");
        var layout = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Forms.Padding.Empty };
        layout.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100));
        volumeSummary.AutoSize = true; volumeSummary.AutoEllipsis = false; volumeSummary.Margin = new Forms.Padding(0, 0, 0, 12);
        layout.Controls.Add(action, 0, 0); layout.Controls.Add(summary, 0, 1); layout.Controls.Add(volumeSummary, 0, 2); layout.Controls.Add(processList, 0, 3); page.Controls.Add(layout); return page;
    }

    private PagePanel BuildFilePage()
    {
        var page = new PagePanel(() => UiText.Files) { BackColor = Canvas, Padding = new Forms.Padding(28) };
        fileDirectory.ReadOnly = false; fileDirectory.PlaceholderText = UiText.RemoteFolderPlaceholder;
        uploadButton = Button(() => UiText.UploadFile, "upload", 150, primary: true); var parent = parentFolderButton = Button(() => UiText.ParentFolder, "parentFolder", 78); var refresh = browseFilesButton = Button(() => UiText.Refresh, "browseFiles", 92);
        openFolderButton = Button(() => UiText.OpenRemoteFolder, "openRemoteFolder", 64);
        var pathRow = ControlRow(fileDirectory, openFolderButton, parent, refresh);
        pathRow.ColumnStyles[0] = new Forms.ColumnStyle(Forms.SizeType.Percent, 100); fileDirectory.Anchor |= Forms.AnchorStyles.Right;
        remotePath.ReadOnly = true; remotePath.PlaceholderText = UiText.SelectFileInList;
        var selectionRow = ControlRow(RowLabel(() => UiText.SelectedFile, "selectionLabel"), remotePath);
        selectionRow.ColumnStyles[1] = new Forms.ColumnStyle(Forms.SizeType.Percent, 100); remotePath.Anchor |= Forms.AnchorStyles.Right;
        uploadFolderButton = Button(() => UiText.UploadFolder, "uploadFolder", 160); downloadButton = Button(() => UiText.Download, "download", 102);
        transferCancelButton = Button(() => UiText.Cancel, "cancelTransfer", 100); transferCancelButton.Enabled = false;
        transferCancelButton.Click += (_, _) => fileTransferLifetime?.Cancel();
        var fileButtons = ControlRow(uploadButton, uploadFolderButton, downloadButton);
        var transferHeader = ControlRow(fileTransferStatus, transferCancelButton);
        transferHeader.ColumnStyles[0] = new Forms.ColumnStyle(Forms.SizeType.Percent, 100); fileTransferStatus.Anchor |= Forms.AnchorStyles.Right;
        fileTransferStatus.SetText(() => UiText.FileTransfers); fileTransferDetails.SetText(() => UiText.FileTransferReady);
        var transferArea = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 3, Margin = new Forms.Padding(0, 12, 0, 0) };
        transferArea.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
        for (int i = 0; i < 3; i++) transferArea.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
        transferArea.Controls.Add(transferHeader, 0, 0); transferArea.Controls.Add(fileTransferProgress, 0, 1); transferArea.Controls.Add(fileTransferDetails, 0, 2);
        fileList.Columns.Add("", 330).WithText(() => UiText.Name); fileList.Columns.Add("", 100).WithText(() => UiText.FileType); fileList.Columns.Add("", 105, Forms.HorizontalAlignment.Right).WithText(() => UiText.Size); fileList.Columns.Add("", 220).WithText(() => UiText.Modified);
        fileList.RememberLayout(root, "name", "type", "size", "modified");
        var layout = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 1, RowCount = 7, Margin = Forms.Padding.Empty };
        layout.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
        for (int i = 0; i < 5; i++) layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
        fileState.Margin = new Forms.Padding(0, 4, 0, 12); fileState.Dock = Forms.DockStyle.Top;
        layout.Controls.Add(RowLabel(() => UiText.RemoteFolder, "remoteFolderLabel"), 0, 0); layout.Controls.Add(pathRow, 0, 1); layout.Controls.Add(fileButtons, 0, 2);
        layout.Controls.Add(selectionRow, 0, 3); layout.Controls.Add(fileState, 0, 4); layout.Controls.Add(fileList, 0, 5); layout.Controls.Add(transferArea, 0, 6); page.Controls.Add(layout);
        parent.Click += async (_, _) => await OpenRemoteFolderAsync(ParentPath(currentDirectory));
        refresh.Click += async (_, _) => await BrowseFilesAsync();
        openFolderButton.Click += async (_, _) => await OpenRemoteFolderAsync(fileDirectory.Text.Trim().Trim('"'));
        fileDirectory.KeyDown += async (_, e) => { if (e.KeyCode == Forms.Keys.Enter) { e.SuppressKeyPress = true; await OpenRemoteFolderAsync(fileDirectory.Text.Trim().Trim('"')); } };
        fileDirectory.TextChanged += (_, _) => RefreshControllerControls();
        return page;
    }

    private PagePanel BuildDiagnosticsPage()
    {
        var page = new PagePanel(() => UiText.Diagnostics) { BackColor = Canvas, Padding = new Forms.Padding(28) };
        executeButton = Button(() => UiText.Execute, "execute", 92, primary: true); cancelButton = Button(() => UiText.Cancel, "cancel", 82);
        var top = ControlRow(RowLabel(() => UiText.Action, "operationLabel"), operations, RowLabel("PID", "pidLabel"), pid, executeButton, cancelButton);
        technicalIdentity.AutoSize = false; technicalIdentity.Dock = Forms.DockStyle.Fill; technicalIdentity.AutoEllipsis = true; technicalIdentity.TextAlign = ContentAlignment.MiddleLeft;
        diagnosticState.AutoSize = false; diagnosticState.Dock = Forms.DockStyle.Fill; diagnosticState.AutoEllipsis = true; diagnosticState.TextAlign = ContentAlignment.MiddleLeft; diagnosticState.Margin = Forms.Padding.Empty;
        operations.Items.AddRange(Templates.Keys.Cast<object>().ToArray()); operations.SelectedIndex = 0;
        var layout = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 1, RowCount = 6, Margin = Forms.Padding.Empty };
        layout.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 32)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 148)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 32));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 12)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100));
        layout.Controls.Add(technicalIdentity, 0, 0); layout.Controls.Add(top, 0, 1);
        layout.Controls.Add(DiagnosticSection(() => UiText.JsonArguments, "argumentsLabel", arguments, () => UiText.AdjustArguments), 0, 2);
        layout.Controls.Add(diagnosticState, 0, 3); layout.Controls.Add(new Forms.Panel(), 0, 4);
        layout.Controls.Add(DiagnosticSection(() => UiText.Result, "resultLabel", output), 0, 5); page.Controls.Add(layout); return page;
    }

    private void BuildTray()
    {
        tray.Icon = Icon; tray.Text = "Remote Debugger"; tray.Visible = true;
        var menu = new Forms.ContextMenuStrip(); menu.Items.Add("", null, (_, _) => RestoreFromTray()).WithText(() => UiText.Open); menu.Items.Add("", null, async (_, _) => await TerminateSupportAsync()).WithText(() => UiText.EndSupport); menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("", null, (_, _) => RequestQuit()).WithText(() => UiText.Quit); tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void WireEvents()
    {
        roleAgent.Click += (_, _) => SelectRole(0);
        roleController.Click += (_, _) => SelectRole(1);
        navConnection.Click += (_, _) => SelectControllerPage(0); navScreen.Click += (_, _) => SelectControllerPage(1); navProcesses.Click += (_, _) => SelectControllerPage(2); navFiles.Click += (_, _) => SelectControllerPage(3); navDiagnostics.Click += (_, _) => SelectControllerPage(4);
        terminateSession.Click += async (_, _) => await TerminateSupportAsync();
        copyAgentCode.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(CurrentPairingCode)) Forms.Clipboard.SetText(CurrentPairingCode); SetFooterMessage(() => UiText.CodeCopied); RefreshFooter(); };
        preparePlatform.Click += async (_, _) => await ProvisionPlatformAsync();
        enableSupport.Click += async (_, _) => await EnablePrivateSupportAsync();
        adminMaintenanceToggle.CheckedChanged += async (_, _) => await ApplyAdminMaintenancePreferenceAsync();
        discoverButton.Click += async (_, _) => await DiscoverAsync(true);
        peers.SelectedIndexChanged += (_, _) => SelectPeerFromList();
        peers.MouseDoubleClick += async (_, e) => { if (peers.HitTest(e.Location).Item?.Tag is Peer) await PairSelectedAsync(); };
        host.TextChanged += (_, _) => { if (selectedPeer?.Host != host.Text.Trim()) { selectedPeer = null; selectedFingerprint = ""; selectedPeerName.SetText(() => UiText.EnterPc); selectedPeerAddress.SetText(() => UiText.IdentityBoundToCode); } };
        code.KeyPress += (_, e) => { if (!char.IsControl(e.KeyChar) && !char.IsAsciiDigit(e.KeyChar)) e.Handled = true; };
        code.KeyDown += async (_, e) => { if (e.KeyCode == Forms.Keys.Enter) { e.SuppressKeyPress = true; await PairSelectedAsync(); } };
        pairButton.Click += async (_, _) => await PairSelectedAsync();
        updateClientButton.Click += async (_, _) => await UpdateConnectedClientAsync();
        pauseViewing.Click += (_, _) => { if (liveStream == null) _ = StartStreamAsync(); else StopStream(() => UiText.ViewingPaused); };
        relayEconomy.CheckedChanged += (_, _) => { if (liveStream != null) { StopStream(() => UiText.ViewingSuspended); _ = StartStreamAsync(); } };
        monitor.SelectedIndexChanged += (_, _) => { if (!refreshingMonitorLabels && liveStream != null) { StopStream(() => UiText.MonitorChanged); _ = StartStreamAsync(); } };
        mouseEnabled.CheckedChanged += (_, _) => { inputState.Enabled = mouseEnabled.Checked; if (!mouseEnabled.Checked) ReleaseHeldInputForCurrentSession(); RefreshInputStatus(); };
        restartAgent.Click += (_, _) => { agent?.Dispose(); agent = null; agentIdle = false; StartAgent(); _ = PrepareAgentAsync(); };
        Deactivate += (_, _) => { ReleaseHeldInputForCurrentSession(); RefreshInputStatus(); };
        Activated += (_, _) => RefreshInputStatus();
        inputRecoveryTimer.Tick += (_, _) => RetryInputRecovery();
        screen.MouseDown += (_, e) => { screen.Focus(); RefreshInputStatus(); QueueMouse("down", e); }; screen.MouseUp += (_, e) => QueueMouse("up", e); screen.MouseMove += (_, e) => { long now = Environment.TickCount64; if (now - lastMove < 33) return; lastMove = now; QueueMouse("move", e); }; screen.MouseWheel += (_, e) => QueueMouse("wheel", e); screen.PreviewKeyDown += (_, e) => e.IsInputKey = true; screen.KeyDown += (_, e) => { if (!CanSendInput()) return; e.SuppressKeyPress = true; QueueInput(new { kind = "keyDown", virtualKey = (int)e.KeyCode }); }; screen.KeyUp += (_, e) => { if (!CanSendInput()) return; e.SuppressKeyPress = true; QueueInput(new { kind = "keyUp", virtualKey = (int)e.KeyCode }); }; screen.GotFocus += (_, _) => RefreshInputStatus(); screen.LostFocus += (_, _) => { ReleaseHeldInputForCurrentSession(); RefreshInputStatus(); };
        typeText.Click += (_, _) => QueueFocusedText(); enterKey.Click += async (_, _) => await ExecuteAsync("ui.key", new { pid = (int)pid.Value, key = "ENTER" });
        processList.ColumnClick += (_, e) => { processSort = processSort.Toggle(ProcessColumn(e.Column)); RenderProcesses(); }; processList.SelectedIndexChanged += (_, _) => { if (processList.SelectedItems.Count > 0 && processList.SelectedItems[0].Tag is ProcessSortRow row) { pid.Value = row.Pid; } };
        fileList.ColumnClick += (_, e) => { fileSort = fileSort.Toggle(FileColumn(e.Column)); RenderFiles(); }; fileList.SelectedIndexChanged += (_, _) => { selectedFilePath = fileList.SelectedItems.Count > 0 && fileList.SelectedItems[0].Tag is FileSortRow row && !row.IsDirectory ? row.Path : null; remotePath.SetText(selectedFilePath ?? ""); RefreshControllerControls(); }; fileList.DoubleClick += async (_, _) => { if (fileList.SelectedItems.Count > 0 && fileList.SelectedItems[0].Tag is FileSortRow { IsDirectory: true } row) await OpenRemoteFolderAsync(row.Path); };
        refreshResourcesButton.Click += async (_, _) => await RefreshResourcesAsync(); executeButton.Click += async (_, _) => await ExecuteSelectedAsync(); cancelButton.Click += async (_, _) => await CancelActionAsync(); uploadButton.Click += async (_, _) => await UploadFileAsync(); uploadFolderButton.Click += async (_, _) => await UploadFolderAsync(); downloadButton.Click += async (_, _) => await DownloadFileAsync();
    }

    // These named controls are created in page builders to keep event wiring readable.
    private Forms.Button refreshResourcesButton = null!;
    private Forms.Button executeButton = null!;
    private Forms.Button cancelButton = null!;
    private Forms.Button uploadButton = null!;
    private Forms.Button uploadFolderButton = null!;
    private Forms.Button downloadButton = null!;

    private void LoadSavedConnection()
    {
        // Keep the protected connection profile available for an authenticated
        // resume after a controller restart. Discovery still selects the online
        // computer for a new private-internet session when this resume is absent.
        try
        {
            client = RemoteClient.Load();
            if (!PrivateInternet)
            {
                host.SetText(client.Connection.Host);
                // Reuse the address as a convenience. A new displayed code starts
                // a new PAKE exchange that authenticates the current certificate;
                // an old saved pin must not block fresh pairing after reinstall.
                selectedFingerprint = "";
                selectedPeerName.SetText(() => UiText.SavedConnection);
                selectedPeerAddress.SetText(client.Connection.Host);
            }
        }
        catch { client = null; }
    }

    private async void MainFormShown(object? sender, EventArgs e)
    {
        if (startInTray)
        {
            trayVisible = true;
            Hide();
        }
        renderTimer.Start();
        _ = PumpInputAsync();
        if (!startInTray && File.Exists(new SecurityMigrationStore(root).PendingSetupPath)) ShowSecuritySetup();
        // A protected relay profile is already the user's authorization to keep
        // this managed computer available. Re-enable its listener on every
        // normal launch without requiring a second button click.
        privateSupportEnabled = WindowLifetime.EnableSupportAtStartup(
            PrivateInternet, HasConfiguredPrivateSupport(), privateSupportEnabled);
        SelectRole(startAgentOnLaunch ? 0 : 1);
        if (!startAgentOnLaunch && client != null) _ = ResumeSavedSupportAsync();
        await Task.Yield();
    }

    private async Task ResumeSavedSupportAsync()
    {
        RemoteClient? target = client;
        if (target == null || supportSession || quitting) return;
        int generation = ++operationGeneration;
        sessionGeneration++;
        var resumeCts = new CancellationTokenSource();
        pairingLifetime = resumeCts;
        pairingBusy = true;
        synchronizingAgent = false;
        UpdateHeader(); RefreshControllerControls(); RefreshFooter();
        try
        {
            JsonElement heartbeat = await target.HeartbeatAsync(resumeCts.Token);
            bool connected = heartbeat.TryGetProperty("session", out var session) &&
                session.TryGetProperty("connected", out var connectedValue) && connectedValue.GetBoolean();
            bool matched = heartbeat.TryGetProperty("binaryMatched", out var matchedValue) && matchedValue.GetBoolean();
            if (generation != operationGeneration || !ReferenceEquals(target, client) || !connected) return;
            clientUpToDate = matched;
            if (ShouldSynchronizeSavedSession(connected, matched))
            {
                synchronizingAgent = true;
                connectionState.SetText(() => UiText.AgentSynchronizing); SetFooterMessage(() => UiText.AgentSynchronizing); SetFooterDetail(() => UiText.TransferValidateVersion);
                ShowUpdateProgress(new AgentUpdateProgress("idle", 0, 0)); UpdateHeader(); RefreshControllerControls(); RefreshFooter();
                using var synchronization = CancellationTokenSource.CreateLinkedTokenSource(resumeCts.Token);
                synchronization.CancelAfter(TimeSpan.FromSeconds(SupportOperationTimeouts.ControllerSynchronizationSeconds));
                try { await SynchronizeClientAsync(target, generation, synchronization.Token); }
                catch (OperationCanceledException) when (!resumeCts.IsCancellationRequested) { throw new TimeoutException(UiText.SynchronizationTimedOut); }
            }
            if (generation != operationGeneration || !ReferenceEquals(target, client)) return;
            synchronizingAgent = false; updateProgressArea.Visible = false; supportSession = true; heartbeatHealthy = false; powerHold ??= PowerHold.Acquire();
            SelectRole(1); SelectControllerPage(1); code.SetText("");
            connectionState.SetText(() => UiText.SessionEstablished); SetFooterMessage(() => UiText.ActiveVersionsSynchronized); SetFooterDetail(() => UiText.LoadingMeasurements); RefreshFooter();
            StartHeartbeat(); _ = LoadInitialRemoteStateAsync(generation);
        }
        catch (Exception ex)
        {
            _ = await TryRetainFailedSynchronizationAsync(target, generation);
            if (generation == operationGeneration && ReferenceEquals(target, client))
            {
                connectionState.SetText(() => UiText.FailurePrefix + ex.Message);
                SetFooterMessage(() => UiText.ConnectionFailed); footerDetail = ex.Message; RefreshFooter();
            }
        }
        finally
        {
            bool ownsResume = ReferenceEquals(pairingLifetime, resumeCts);
            if (ownsResume)
            {
                if (updateProgressArea.Visible && !supportSession)
                {
                    updateProgressText.SetText(() => UiText.SynchronizationInterrupted);
                    updateProgressText.ForeColor = DestructiveText; updateProgressFill.BackColor = DestructiveText;
                }
                pairingLifetime = null;
                synchronizingAgent = false;
                resumeCts.Dispose();
            }
            if (ownsResume || generation == operationGeneration)
            {
                pairingBusy = false;
                UpdateHeader(); RefreshControllerControls(); RefreshFooter();
            }
        }
    }

    internal static bool ShouldSynchronizeSavedSession(bool connected, bool binaryMatched) => connected && !binaryMatched;

    private void OnManagedRelaunchRequested()
    {
        PostUi(() =>
        {
            if (quitting) return;
            quitting = true;
            shutdownStarted = true;
            _ = ShutdownAndCloseAsync();
        });
    }

    private async Task ShutdownAndCloseAsync()
    {
        if (await ShutdownAsync())
        {
            if (!IsDisposed) Close();
        }
        else
        {
            quitting = false;
            shutdownStarted = false;
        }
    }

    public void StartAgent()
    {
        if (agent != null) return;
        try
        {
            // Pairing can appear immediately without triggering the broad Windows
            // firewall consent dialog. LAN listening starts only after the
            // provisioned broker has verified the Private/LocalSubnet rules.
            agentIdle = false;
            var started = new AgentServer(root, loopbackOnly: loopbackOnly || !agentNetworkPrepared, enableInternet: !loopbackOnly,
                enableAdminMaintenance: adminMaintenanceEnabled);
            agent = started;
            started.Status += text => PostUi(() => { if (ReferenceEquals(agent, started)) { agentLog.SetText(text); RefreshFooter(); } });
            started.TerminationRequested += reason =>
            {
                if (suppressTerminationEvent || quitting) return;
                PostUi(() =>
                {
                    if (!ReferenceEquals(agent, started)) return;
                    if (WindowLifetime.ExitAfterAgentStop(reason)) { agent = null; RequestQuit(); }
                    else if (WindowLifetime.RestartAfterAgentStop(reason)) _ = RestartAgentAfterSupportEndAsync(started);
                });
            };
            agent.Start();
            agentFingerprint.SetText(agent.Fingerprint);
            SetFooterDetail(() => UiText.CloseToTray);
            RefreshUiState();
        }
        catch (Exception ex)
        {
            agent?.Dispose(); agent = null; agentState.SetText(() => UiText.PreparationFailedPrefix + ex.Message); SetFooterMessage(() => UiText.AgentUnavailable); RefreshFooter();
        }
    }

    public void StopAgent()
    {
        pairingLifetime?.Cancel();
        InvalidateInputSession();
        _ = StopAgentAsync();
    }

    private async Task<bool> StopAgentAsync()
    {
        AgentServer? local = agent;
        if (local == null) return true;
        suppressTerminationEvent = true;
        try { await local.TerminateAsync(CancellationToken.None); }
        catch (Exception ex) { SetFooterMessage(() => UiText.AgentStopIncomplete); footerDetail = ex.Message; RefreshFooter(); return false; }
        finally { suppressTerminationEvent = false; }
        local.Dispose();
        if (ReferenceEquals(agent, local)) agent = null;
        privateSupportEnabled = PrivateInternet && HasConfiguredPrivateSupport();
        powerHold?.Dispose(); powerHold = null; CurrentPairingCode = null; RefreshUiState();
        return true;
    }

    private async Task RestartAgentAfterSupportEndAsync(AgentServer ended)
    {
        if (quitting || !ReferenceEquals(agent, ended)) return;
        // A terminated AgentServer deliberately keeps its listener in a
        // revoked state. Dispose that instance before starting a fresh one so
        // a new pairing grant/code is created and the relay becomes reachable
        // again without user interaction.
        agent = null;
        agentIdle = false;
        CurrentPairingCode = null;
        try { ended.Dispose(); } catch { }
        powerHold?.Dispose(); powerHold = null;
        if (PrivateInternet)
            privateSupportEnabled = HasConfiguredPrivateSupport();
        if (PrivateInternet && !privateSupportEnabled) { RefreshUiState(); return; }
        StartAgent();
        await PrepareAgentAsync();
    }

    private async Task PrepareAgentAsync()
    {
        AgentServer? preparing = agent;
        if (preparing == null) return;
        if (loopbackOnly) { agentNetworkState.SetText(() => UiText.LocalOnly); return; }
        try
        {
            SupportPlatformStatus status = await SupportPlatform.PrepareAsync(requireFirewall: !loopbackOnly);
            if (!ReferenceEquals(agent, preparing) || quitting) return;
            if (status.Available && status.FirewallReady && !agentNetworkPrepared)
            {
                // Keep the displayed code, rate limits and any accepted local
                // connection while widening the prepared listener to the LAN.
                preparing.EnablePrivateNetwork();
                agentNetworkPrepared = true;
            }
            ApplyPlatformStatus(status);
            if (startupPreparationError != null)
                ShowSetupNotice(() => UiText.InstalledStartupFailedPrefix + startupPreparationError);
        }
        catch (Exception ex) { ShowSetupNotice(() => UiText.AutomaticPreparationFailedPrefix + ex.Message); }
    }

    private async Task ProvisionPlatformAsync()
    {
        preparePlatform.Enabled = false;
        try { ApplyPlatformStatus(await SupportPlatform.ProvisionAsync()); if (!quitting) await PrepareAgentAsync(); }
        catch (Exception ex) { ShowSetupNotice(() => UiText.ActivationFailedPrefix + ex.Message); }
        finally { preparePlatform.Enabled = true; }
    }

    private void ApplyPlatformStatus(SupportPlatformStatus status)
    {
        agentNetworkState.SetText(() => status.FirewallReady ? UiText.Ready : status.RequiresAdministratorConsent ? UiText.ActivationRequired : UiText.Unavailable);
        agentNetworkState.ForeColor = status.FirewallReady ? ConnectedText : WarningText;
        agentSleepState.SetText(() => powerHold != null ? UiText.Suspended : UiText.Active);
        agentSleepState.ForeColor = powerHold != null ? PrimaryText : SecondaryText;
        if (PrivateInternet) HideSetupNotice();
        else if (status.RequiresAdministratorConsent) ShowSetupNotice(() => UiText.EnableSupportNotice);
        else if (status.Available || status.Provisioned) HideSetupNotice();
        output.SetText(Pretty(status));
        SetFooterMessage(() => status.FirewallReady ? UiText.ReadyForConnection : UiText.EnablePrivateNetwork); RefreshFooter();
    }

    private async Task ApplyAdminMaintenancePreferenceAsync()
    {
        if (changingAdminMaintenance) return;
        bool requested = adminMaintenanceToggle.Checked;
        bool previous = adminMaintenanceEnabled;
        if (requested == previous) return;

        changingAdminMaintenance = true;
        adminMaintenanceToggle.Enabled = false;
        try
        {
            AdminMaintenancePreference.Save(root, requested);
            adminMaintenanceEnabled = requested;
            if (agent is { } current && !agentIdle)
                await current.SetAdminMaintenanceEnabledAsync(requested);
            SetFooterMessage(() => requested ? UiText.AdminMaintenanceEnabledMessage : UiText.AdminMaintenanceDisabledMessage);
            SetFooterDetail(() => UiText.CloseToTrayShort);
        }
        catch (Exception ex)
        {
            try { AdminMaintenancePreference.Save(root, previous); } catch { }
            adminMaintenanceEnabled = previous;
            adminMaintenanceToggle.Checked = previous;
            SetFooterMessage(() => UiText.AdminMaintenanceChangeFailedPrefix);
            footerDetail = ex.Message;
        }
        finally
        {
            changingAdminMaintenance = false;
            adminMaintenanceToggle.Enabled = true;
            RefreshUiState();
            RefreshFooter();
        }
    }

    private void ShowSetupNotice(Func<string> message)
    {
        setupNoticeText.SetText(() => string.IsNullOrWhiteSpace(message()) ? UiText.WindowsPermissionOnce : message());
        setupNotice.Visible = true;
    }

    private void HideSetupNotice() => setupNotice.Visible = false;

    private void RefreshUiState()
    {
        if (IsDisposed) return;
        RefreshPowerHold();
        UpdateAgentState(); if (PrivateInternet) UpdatePrivateAgentState(); UpdateInternetState(); UpdateHeader(); RefreshControllerControls(); RefreshFooter(); RefreshInputStatus();
        if (fleetRefreshing || fleet.Values.Any(device => NeedsFleetProgressAnimation(device.State))) peers.Invalidate();
        if (updateProgressArea.Visible && updateProgressFill.BackColor == Teal) RefreshUpdateProgress();
        if (!agentIdle && agent?.Operations.Maintenance is { } maintenance)
        {
            var state = Json.Element(maintenance.Status); bool active = state.TryGetProperty("active", out var a) && a.GetBoolean(); bool brokerAvailable = !state.TryGetProperty("brokerAvailable", out var broker) || broker.GetBoolean(); bool requiresProvisioning = state.TryGetProperty("requiresProvisioning", out var provisioning) && provisioning.GetBoolean(); bool paired = agent?.Session.HasPaired == true;
            if (!adminMaintenanceEnabled || !maintenance.Enabled)
            {
                agentMaintenanceState.SetText(() => UiText.AdminMaintenanceDisabled);
                agentMaintenanceState.ForeColor = SecondaryText;
            }
            else
            {
                agentMaintenanceState.SetText(() => active ? UiText.Active : !paired ? UiText.AfterConnection : requiresProvisioning ? UiText.ActivationRequired : !brokerAvailable ? UiText.Unavailable : UiText.Preparing);
                agentMaintenanceState.ForeColor = active ? ConnectedText : !paired ? SecondaryText : requiresProvisioning || !brokerAvailable ? WarningText : SecondaryText;
            }
        }
    }

    private void UpdateAgentState()
    {
        var transfer = agent?.Session.Connected == true ? agent.Operations.FileTransfer : null;
        bool updateOngoing = agent != null && !agentIdle && IsOngoingUpdate(agent.UpdateProgress);
        pairingCountdown.Visible = !PrivateInternet || !agentIdle && (updateOngoing || transfer != null);
        pairingCountdown.Height = updateOngoing || transfer != null ? 12 : 4;
        pairingCountdown.Style = transfer?.Stage == "preparing" && !updateOngoing ? Forms.ProgressBarStyle.Marquee : Forms.ProgressBarStyle.Continuous;
        pairingCountdown.AccessibleName = updateOngoing ? UiText.ClientUpdateProgress : transfer != null ? UiText.FileTransfers : UiText.PairingCodeCaption;
        agentSleepState.SetText(() => powerHold != null ? UiText.Suspended : UiText.Active);
        agentSleepState.ForeColor = powerHold != null ? PrimaryText : SecondaryText;
        bool paired = agent?.Session.HasPaired == true;
        if (PrivateInternet && agent != null && !agentIdle && !paired && !updateOngoing) return;
        agentEyebrow.SetText(() => paired ? UiText.SupportSessionCaption : UiText.PairingCodeCaption);
        agentHeading.SetText(() => agent?.Session.State == "reconnecting" ? UiText.ConnectionInterruptedHeading : paired ? UiText.PcBeingAssisted : UiText.ShareCode);
        agentSubtitle.SetText(() => paired ? UiText.ConnectionStaysVisible : internetConfigured && !loopbackOnly ? UiText.InternetInstructions : UiText.EnterCodeOnController);
        copyAgentCode.Visible = !paired && !agentIdle;
        restartAgent.Visible = agentIdle;
        float stateFontSize = paired ? 24 : 42;
        if (agentPairCode.Font.Size != stateFontSize)
        {
            var previousFont = agentPairCode.Font;
            agentPairCode.Font = new Font(paired ? "Segoe UI" : "Consolas", stateFontSize, FontStyle.Bold);
            previousFont.Dispose();
        }
        if (agent == null || agentIdle)
        {
            agentPairCode.SetText(agentIdle ? "" : "— — —"); CurrentPairingCode = null;
            agentHeading.SetText(() => agentIdle ? UiText.SupportEnded : UiText.ShareCode);
            agentSubtitle.SetText(() => agentIdle ? UiText.PcNoLongerAccessible : UiText.EnterCodeOnController);
            agentEyebrow.SetText(() => agentIdle ? UiText.SessionClosedCaption : UiText.PairingCodeCaption);
            agentState.SetText(() => agentIdle ? UiText.NoActiveConnection : UiText.AgentAwaitingPreparation);
            agentSessionNote.SetText(() => agentIdle ? UiText.RevokedAccessNote : UiText.PreparingSupport);
            pairingCountdown.Value = 0; pairingCountdownText.SetText("");
            agentNetworkState.SetText(() => agentIdle ? UiText.Waiting : UiText.Preparing); agentMaintenanceState.SetText(() => adminMaintenanceEnabled ? UiText.Inactive : UiText.AdminMaintenanceDisabled);
            setupNotice.Visible = false; return;
        }
        var session = agent.Session;
        var updateProgress = agent.UpdateProgress;
        if (paired && !terminating && !quitting)
            if (session.State is "connected" or "synchronizing" or "reconnecting")
                SetFooterMessage(() => session.State switch { "connected" => UiText.SupportActive, "synchronizing" => UiText.AgentSynchronizing, _ => UiText.WaitingForController });
        if (!agentIdle && IsOngoingUpdate(updateProgress))
        {
            CurrentPairingCode = null;
            pairingCountdown.Value = updateProgress.TransferPercent * 3;
            pairingCountdownText.SetText(() => UpdateProgressDescription(updateProgress));
            agentPairCode.SetText(() => UiText.Synchronizing);
            agentState.SetText(() => UiText.SessionPreparingAgent);
            agentSessionNote.SetText(() => UiText.CommandsDisabledUntilVersions);
            return;
        }
        if (session.Connected && session.BinaryMatched)
        {
            CurrentPairingCode = null; pairingCountdownText.SetText(() => UiText.CodeConsumed); if (transfer == null) pairingCountdown.Value = 0; string duration = session.StartedUtc is { } started ? FormatDuration(DateTimeOffset.UtcNow - started) : UiText.JustNow; agentPairCode.SetText(() => UiText.ControllerConnected); agentState.SetText(() => UiText.Format(UiText.ConnectedDuration, duration)); agentSessionNote.SetText(() => UiText.AuthenticatedControllerNote);
            if (transfer != null)
            {
                var metrics = FileTransferMetrics.Calculate(transfer.TransferredBytes, transfer.TotalBytes, transfer.BytesThisAttempt, transfer.Elapsed);
                pairingCountdown.Value = metrics.Percent * 3;
                agentState.SetText(() => UiText.Format(transfer.Receiving ? UiText.ReceivingFile : UiText.SendingFile, Path.GetFileName(transfer.Path)));
                pairingCountdownText.SetText(() => transfer.Stage switch
                {
                    "preparing" => UiText.PreparingFileTransfer,
                    "verifying" => UiText.VerifyingFileTransfer,
                    "complete" => transfer.Receiving ? UiText.FileReceived : UiText.FileSent,
                    "paused" => UiText.TransferPaused,
                    "failed" => transfer.Error ?? UiText.CannotReadFolder,
                    _ => UiText.Format(UiText.FileTransferNumbers, metrics.Percent, FormatBytes(transfer.TransferredBytes), FormatBytes(transfer.TotalBytes),
                        metrics.BytesPerSecond > 0 ? FormatBytes((long)metrics.BytesPerSecond) + "/s" : "—",
                        metrics.Remaining is { } eta ? FormatTransferEta(eta) : UiText.CalculatingTransferEta)
                });
            }
        }
        else if (session.Connected)
        {
            CurrentPairingCode = null; pairingCountdown.Value = updateProgress.TransferPercent * 3; pairingCountdownText.SetText(() => UpdateProgressDescription(updateProgress)); agentPairCode.SetText(() => UiText.Synchronizing); agentState.SetText(() => UiText.SessionPreparingAgent); agentSessionNote.SetText(() => UiText.CommandsDisabledUntilVersions);
        }
        else if (session.HasPaired && session.State == "reconnecting")
        {
            agentPairCode.SetText(() => UiText.SessionInterrupted); CurrentPairingCode = null; var remaining = session.DisconnectDeadlineUtc is { } d ? d - DateTimeOffset.UtcNow : TimeSpan.Zero; pairingCountdownText.SetText(() => remaining > TimeSpan.Zero ? UiText.Format(UiText.ReconnectionCountdown, remaining) : UiText.WaitingForReconnection); pairingCountdown.Value = 0; agentState.SetText(() => UiText.WaitingForReconnection); agentSessionNote.SetText(() => UiText.ReconnectionGraceNote);
        }
        else
        {
            string current = agent.Pairing.CurrentCode ?? ""; CurrentPairingCode = current.Length == 6 ? current : null; agentPairCode.SetText(FormatPairingCode(current)); var expires = agent.Pairing.ExpiresUtc; var remaining = expires - DateTimeOffset.UtcNow; int seconds = Math.Clamp((int)Math.Ceiling(remaining.TotalSeconds), 0, 300); pairingCountdown.Value = seconds; pairingCountdownText.SetText(() => seconds > 0 ? UiText.Format(UiText.NewCodeCountdown, TimeSpan.FromSeconds(seconds)) : UiText.PreparingNextCode); agentState.SetText(() => UiText.WaitingForConnection); agentSessionNote.SetText(() => UiText.CodeRotatesNote);
        }
    }

    private void UpdateHeader()
    {
        bool onAgent = rolePages.SelectedIndex == 0;
        bool onController = !onAgent;
        tray.Text = onAgent ? UiText.TrayAssistedPc : UiText.TrayController;
        if (onAgent)
        {
            headerTitle.SetText(() => UiText.GiveControl); headerSubtitle.SetText(() => Environment.MachineName + (internetConfigured && !loopbackOnly ? UiText.InternetSupportSuffix : UiText.PrivateSupportSuffix));
        }
        else
        {
            var presentation = ControllerPagePresentation(controllerPages.SelectedIndex);
            headerTitle.SetText(presentation.Title);
            if (PrivateInternet && selectedPeer != null)
                headerSubtitle.SetText(selectedPeer.Name);
            else if (supportSession && client != null)
                headerSubtitle.SetText(() => selectedPeer == null || selectedPeer.Name == client.Connection.Host
                    ? client.Connection.Host + UiText.SupportSessionSuffix
                    : selectedPeer.Name + " · " + client.Connection.Host);
            else if (selectedPeer != null)
                headerSubtitle.SetText(selectedPeer.Name + " · " + selectedPeer.Host);
            else
                headerSubtitle.SetText(PrivateInternet && controllerPages.SelectedIndex == 0 ? UiText.PrivateConnectInstructions : presentation.Subtitle);
        }

        bool updateOngoing = onAgent && agent != null && IsOngoingUpdate(agent.UpdateProgress);
        bool connected = onAgent ? agent?.Session is { Connected: true, BinaryMatched: true } && !updateOngoing : heartbeatHealthy && supportSession && !clientUpdateBusy;
        bool reconnecting = onAgent ? agent?.Session.State == "reconnecting" : supportSession && !heartbeatHealthy && !clientUpdateBusy;
        bool pairing = onController && pairingBusy && !synchronizingAgent;
        bool synchronizing = onAgent && (agent?.Session is { Connected: true, BinaryMatched: false } || updateOngoing) ||
            onController && ((pairingBusy && synchronizingAgent) || clientUpdateBusy);
        statusPill.BackColor = connected ? ConnectedBack : reconnecting ? Color.FromArgb(255, 244, 222) : Color.FromArgb(237, 241, 244);
        statusDot.ForeColor = connected ? Color.FromArgb(50, 137, 91) : reconnecting ? WarningText : SecondaryText;
        statusLabel.ForeColor = connected ? ConnectedText : reconnecting ? WarningText : Color.FromArgb(80, 103, 113);
        statusLabel.SetText(() => connected ? UiText.Connected : reconnecting ? UiText.Reconnecting : synchronizing ? UiText.SynchronizingEllipsis : pairing ? UiText.Pairing : agentIdle && onAgent ? UiText.SessionClosed : UiText.Waiting);
        statusPill.AccessibleName = statusLabel.Text; RefreshStatusPillRegion();
        bool showTerminateSession = ShouldShowTerminateSession(onAgent, agentIdle, agent?.Session.Connected == true,
            agent?.Session.State, supportSession, synchronizingAgent);
        if (terminateSession.Visible != showTerminateSession) terminateSession.Visible = showTerminateSession;
        roleAgent.BackColor = onAgent ? SelectedRail : Rail; roleController.BackColor = onController ? SelectedRail : Rail; navConnection.BackColor = onController && controllerPages.SelectedIndex == 0 ? SelectedRail : Rail; navScreen.BackColor = onController && controllerPages.SelectedIndex == 1 ? SelectedRail : Rail; navProcesses.BackColor = onController && controllerPages.SelectedIndex == 2 ? SelectedRail : Rail; navFiles.BackColor = onController && controllerPages.SelectedIndex == 3 ? SelectedRail : Rail; navDiagnostics.BackColor = onController && controllerPages.SelectedIndex == 4 ? SelectedRail : Rail;
    }

    private void RefreshFooter()
    {
        bool screenFooter = rolePages.SelectedIndex == 1 && !terminating && controllerPages.SelectedIndex == 1;
        SetScreenFooter(screenFooter);
        if (rolePages.SelectedIndex != 1 || terminating) { footerLeft.SetText(footerMessage); footerRight.SetText(footerDetail); return; }
        if (screenFooter) return;
        var text = WorkspacePresentation.Footer(controllerPages.SelectedIndex, connectionState.Text, streamStatus.Text,
            resourceState.Text, fileState.Text, diagnosticState.Text, PrivateInternet ? selectedPeer?.Name ?? "" : host.Text,
            lastMeasurementUtc is { } measured ? UiText.Format(UiText.ProcessMeasurement, processRows.Count, measured.ToLocalTime()) : UiText.NoMeasurement,
            currentDirectory, operations.SelectedItem?.ToString() ?? "");
        footerLeft.SetText(() => clientUpdateBusy ? UiText.Synchronizing : supportSession && !heartbeatHealthy ? UiText.ReconnectionInProgress : text.Status);
        footerRight.SetText(PrivateInternet && controllerPages.SelectedIndex == 0 && selectedPeer == null ? "" : text.Detail);
    }

    private void SelectRole(int index)
    {
        // Role navigation is presentation-only. Keep a local agent and a remote
        // controller session alive independently when the user changes views.
        bool roleChanged = index != rolePages.SelectedIndex;
        bool preserveSessions = ShouldPreserveActiveSessionsOnRoleSwitch(rolePages.SelectedIndex, index,
            agent != null, supportSession || pairingBusy);
        if (roleChanged)
        {
            pairingLifetime?.Cancel();
            if (preserveSessions && rolePages.SelectedIndex == 1 && liveStream != null)
                StopStream(() => UiText.ViewingSuspended);
            InvalidateInputSession();
        }
        rolePages.SelectedIndex = index;
        controllerNavCaption.Visible = index == 1; navConnection.Visible = index == 1; navScreen.Visible = index == 1; navProcesses.Visible = index == 1; navFiles.Visible = index == 1; navDiagnostics.Visible = index == 1;
        if (index == 0 && agent == null && !quitting && !agentIdle && (!PrivateInternet || privateSupportEnabled)) { StartAgent(); _ = PrepareAgentAsync(); }
        if (index == 1 && !supportSession)
        {
            SetFooterMessage(() => UiText.ChoosePc);
            SetFooterDetail(() => UiText.CloseToTrayShort);
            RefreshFooter();
        }
        if (index == 1 && supportSession && client != null && liveStream == null && controllerPages.SelectedIndex == 1)
            _ = StartStreamAsync();
        UpdateHeader();
    }

    private void SelectControllerPage(int index)
    {
        index = AvailableControllerPage(index, supportSession, terminating);
        if (liveStream != null && controllerPages.SelectedIndex == 1 && index != 1) StopStream(() => UiText.ViewingSuspended);
        controllerPages.SelectedIndex = index; UpdateHeader(); RefreshControllerControls(); RefreshFooter();
        if (index == 1 && supportSession && client != null && liveStream == null) _ = StartStreamAsync();
        if (index == 2 && processRows.Count == 0 && supportSession && heartbeatHealthy) _ = RefreshResourcesAsync();
        if (index == 3 && fileRows.Count == 0 && supportSession && heartbeatHealthy) _ = BrowseFilesAsync();
    }

    private async Task DiscoverAsync(bool explicitRefresh)
    {
        if (pairingBusy || supportSession || FleetBusy || fleetRefreshing || rolePages.SelectedIndex != 1 || quitting) return;
        discoveryState.SetText(() => explicitRefresh ? UiText.SearchingPcs : UiText.SearchingAtStartup); discoverButton.Enabled = false;
        discoveryLifetime?.Cancel(); discoveryLifetime = new CancellationTokenSource();
        try
        {
            var result = await PeerDiscovery.FindAsync(
                PrivateInternet,
                token => Discovery.FindAsync(2500, token),
                token => InternetSettings.Load(root)!.FindAsync(token),
                discoveryLifetime.Token);
            var found = result.Peers;
            if (pairingBusy || supportSession || rolePages.SelectedIndex != 1) return;
            discoveredPeers.Clear();
            string? localSupportId = PrivateInternet ? agent?.Internet?.SupportId : null;
            discoveredPeers.AddRange(PeerDiscovery.DistinctPeers(found.Where(p => !PeerDiscovery.IsLocalPeer(p, localSupportId) && (InternetSettings.IsSupportId(p.Host) || IsRemotePeer(p)))).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase));
            ObserveFleet(); SaveFleet();
            if (selectedPeer != null) selectedPeer = PeerDiscovery.Rebind(selectedPeer, discoveredPeers) ?? selectedPeer;
            if (selectedPeer != null)
            {
                selectedFingerprint = selectedPeer.Fingerprint; host.SetText(selectedPeer.Host); selectedPeerName.SetText(selectedPeer.Name);
            }
            else
            {
                selectedPeer = null; selectedFingerprint = ""; host.SetText(""); code.SetText("");
                selectedPeerName.SetText(() => UiText.SelectComputer); selectedPeerAddress.SetText("");
                connectionState.SetText(() => UiText.PrivateConnectInstructions);
            }
            RenderPeers();
            discoveryState.SetText(() => result.UsedLanFallback
                ? UiText.Format(UiText.LanFallbackActive, discoveredPeers.Count)
                : discoveredPeers.Count == 0
                    ? PrivateInternet ? UiText.NoInternetPcs : UiText.NoPcsFound
                    : UiText.Format(UiText.AvailablePcCount, discoveredPeers.Count));
            if (discoveredPeers.Count == 1 && selectedPeer == null && peers.Items.Count > 0) peers.Items[0].Selected = true;
            await RefreshFleetVersionsAsync(discoveryLifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (PrivateInternet)
            {
                foreach (var key in fleet.Keys.ToArray()) fleet[key] = fleet[key] with { Online = false, State = "offline", Detail = "" };
                RenderPeers();
            }
            discoveryState.SetText(() => UiText.SearchUnavailablePrefix + ex.Message);
        }
        finally { discoverButton.Enabled = true; }
    }

    private bool IsRemotePeer(Peer peer)
    {
        var localAddresses = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address);
        return PeerDiscoveryPolicy.IsRemoteAddress(peer.Host, localAddresses);
    }

    private void RenderPeers()
    {
        string? keep = selectedPeer?.Host ?? host.Text.Trim();
        renderingPeers = true;
        peers.BeginUpdate();
        try
        {
            peers.Items.Clear();
            foreach (var peer in fleet.Values.OrderBy(d => d.Peer.Name, StringComparer.CurrentCultureIgnoreCase).Select(d => d.Peer))
            {
                var item = new Forms.ListViewItem(peer.Name);
                if (!PrivateInternet) item.SubItems.Add(peer.Host);
                if (PrivateInternet && fleet.TryGetValue(DeviceKey(peer), out var device))
                {
                    item.SubItems.Add(device.Version.Length == 0 ? "—" : device.Version);
                    item.SubItems.Add(FleetState(device)); item.SubItems.Add(device.Detail); item.ToolTipText = device.Detail;
                }
                else item.SubItems.Add(fleet.TryGetValue(DeviceKey(peer), out var known) && !known.Online ? UiText.DeviceOffline : UiText.Available);
                item.Tag = peer; peers.Items.Add(item); if (peer.Host == keep) item.Selected = true;
            }
        }
        finally
        {
            peers.EndUpdate();
            renderingPeers = false;
        }
    }

    private void SelectPeerFromList()
    {
        // Headers remain interactive during support so their layout can be
        // adjusted; selecting a row must never replace an active target.
        if (renderingPeers || supportSession || pairingBusy || FleetBusy || terminating) return;
        if (peers.SelectedItems.Count == 0 || peers.SelectedItems[0].Tag is not Peer peer) return;
        InvalidateInputSession();
        selectedPeer = peer; selectedFingerprint = peer.Fingerprint; host.SetText(peer.Host); selectedPeerName.SetText(peer.Name);
        RefreshWanAddress();
        selectedPeerAddress.SetText(() => PrivateInternet
            ? InternetSettings.IsSupportId(peer.Host) ? UiText.InternetReady : UiText.LanFallbackReady
            : peer.Host);
        connectionState.SetText(() => PrivateInternet ? UiText.PrivateConnectInstructions : UiText.EnterDisplayedCode); code.SetText("");
        if (PrivateInternet) pairButton.Focus(); else code.Focus();
    }

    private async Task PairSelectedAsync()
    {
        if (supportSession) { connectionState.SetText(() => UiText.EndSupportBeforeNewCode); return; }
        if (pairingBusy || FleetBusy || fleetRefreshing || string.IsNullOrWhiteSpace(host.Text)) { connectionState.SetText(() => PrivateInternet ? UiText.SelectComputer : UiText.ChoosePcPeriod); return; }
        if (!PrivateInternet && (code.Text.Length != 6 || !code.Text.All(char.IsAsciiDigit))) { connectionState.SetText(() => UiText.CodeMustBeSixDigits); code.Focus(); return; }
        if (PrivateInternet && !SaveWanAddress()) return;
        pairingBusy = true; synchronizingAgent = false; pairButton.Enabled = false; discoverButton.Enabled = false; code.Enabled = false; host.Enabled = false; operationGeneration++; int generation = operationGeneration; sessionGeneration++; liveFrameFresh = false; var pairingCts = new CancellationTokenSource(); pairingLifetime = pairingCts;
        RemoteClient? pairedClient = null;
        updateProgressArea.Visible = false;
        try
        {
            // Ending support rotates the private invitation, including for a LAN peer.
            // Rebind before pairing so its authentication secret is never stale.
            if (PrivateInternet && selectedPeer is { } previous)
            {
                var fresh = await PeerDiscovery.FindAsync(PrivateInternet, token => Discovery.FindAsync(1500, token),
                    token => InternetSettings.Load(root)!.FindAsync(token), pairingCts.Token);
                selectedPeer = PeerDiscovery.Rebind(previous, fresh.Peers)
                    ?? throw new IOException("The selected computer is no longer available. Select its current entry after discovery refreshes.");
                selectedFingerprint = selectedPeer.Fingerprint;
                host.SetText(selectedPeer.Host);
            }
            string targetAddress = host.Text.Trim();
            int targetPort = selectedPeer?.Port is > 0 and < 65536 ? selectedPeer.Port : 45832;
            string privateSupportId = selectedPeer?.SupportId ?? "";
            if (PrivateInternet && privateSupportId.Length == 0 && InternetSettings.IsSupportId(targetAddress)) privateSupportId = targetAddress;
            if (PrivateInternet && privateSupportId.Length == 0) throw new InvalidOperationException(UiText.PrivateLanPeerNeedsUpdate);
            pairedClient = new RemoteClient(InternetSettings.Target(new Peer(selectedPeer?.Name ?? targetAddress, targetAddress, targetPort, selectedFingerprint, privateSupportId), root)) { AdminRoot = root }; client = pairedClient; connectionState.SetText(() => UiText.Pairing); SetFooterMessage(() => UiText.Pairing); UpdateHeader(); RefreshFooter();
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(pairingCts.Token))
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(SupportOperationTimeouts.PairingHandshakeSeconds));
                try { await pairedClient.PairAsync(PrivateInternet ? InternetSettings.Load(root)!.AuthenticationSecret(privateSupportId) : code.Text, handshake.Token); }
                catch (OperationCanceledException) when (!pairingCts.IsCancellationRequested) { throw new TimeoutException(UiText.PairingTimedOut); }
            }
            // Pairing already proved this LAN route; do not probe every adapter again.
            if (pairedClient.Connection.RelayUrl.Length > 0 && !RemoteClient.IsLanAddress(pairedClient.Connection.DirectHost))
            {
                using var directUpgrade = CancellationTokenSource.CreateLinkedTokenSource(pairingCts.Token);
                directUpgrade.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    var candidates = await pairedClient.GetDirectEndpointsAsync(directUpgrade.Token);
                    await pairedClient.TryPreferDirectAsync(candidates, directUpgrade.Token);
                }
                catch (OperationCanceledException) when (!pairingCts.IsCancellationRequested) { }
                catch (Exception) when (!pairingCts.IsCancellationRequested) { }
            }
            pairedClient.Save(); synchronizingAgent = true; connectionState.SetText(() => UiText.AgentSynchronizing); SetFooterMessage(() => UiText.AgentSynchronizing); SetFooterDetail(() => UiText.TransferValidateVersion); ShowUpdateProgress(new AgentUpdateProgress("idle", 0, 0)); UpdateHeader(); RefreshFooter();
            using (var synchronization = CancellationTokenSource.CreateLinkedTokenSource(pairingCts.Token))
            {
                synchronization.CancelAfter(TimeSpan.FromSeconds(SupportOperationTimeouts.ControllerSynchronizationSeconds));
                try { await SynchronizeClientAsync(pairedClient, generation, synchronization.Token); }
                catch (OperationCanceledException) when (!pairingCts.IsCancellationRequested) { throw new TimeoutException(UiText.SynchronizationTimedOut); }
            }
            if (generation != operationGeneration) return; synchronizingAgent = false; updateProgressArea.Visible = false; supportSession = true; heartbeatHealthy = false; powerHold ??= PowerHold.Acquire(); StartHeartbeat(); SelectRole(1); SelectControllerPage(1); code.SetText(""); connectionState.SetText(() => UiText.SessionEstablished); SetFooterMessage(() => UiText.ActiveVersionsSynchronized); SetFooterDetail(() => UiText.LoadingMeasurements); RefreshFooter(); _ = LoadInitialRemoteStateAsync(generation);
        }
        catch (OperationCanceledException) { if (pairedClient != null) _ = EndSessionBestEffortAsync(pairedClient); if (generation == operationGeneration) { connectionState.SetText(() => UiText.ConnectionCancelledPeriod); SetFooterMessage(() => UiText.ConnectionCancelled); SetFooterDetail(() => UiText.RetryDisplayedCode); } }
        catch (Exception ex)
        {
            bool retained = pairedClient != null && await TryRetainFailedSynchronizationAsync(pairedClient, generation);
            if (pairedClient != null && !retained) _ = EndSessionBestEffortAsync(pairedClient);
            if (generation == operationGeneration) { connectionState.SetText(() => UiText.FailurePrefix + ex.Message); SetFooterMessage(() => UiText.ConnectionFailed); footerDetail = ex.Message; RefreshFooter(); }
        }
        finally
        {
            bool ownsPairing = ReferenceEquals(pairingLifetime, pairingCts);
            if (ownsPairing)
            {
                if (updateProgressArea.Visible && !supportSession)
                {
                    updateProgressText.SetText(() => UiText.SynchronizationInterrupted);
                    updateProgressText.ForeColor = DestructiveText; updateProgressFill.BackColor = DestructiveText;
                }
                pairingLifetime = null;
                synchronizingAgent = false;
                pairingCts.Dispose();
            }
            if (ownsPairing || generation == operationGeneration) { pairingBusy = false; discoverButton.Enabled = true; UpdateHeader(); RefreshControllerControls(); RefreshFooter(); }
            if (!supportSession && !quitting) _ = DiscoverAsync(true);
        }
    }

    private async Task SynchronizeClientAsync(RemoteClient target, int generation, CancellationToken ct)
    {
        var heartbeat = await target.HeartbeatAsync(ct);
        if (heartbeat.TryGetProperty("binaryMatched", out var matched) && matched.ValueKind == JsonValueKind.True)
        {
            if (generation == operationGeneration && ReferenceEquals(target, client)) clientUpToDate = true;
            return;
        }
        var progress = new Progress<AgentUpdateProgress>(value =>
        {
            if (!IsDisposed && generation == operationGeneration && ReferenceEquals(target, client) && (pairingBusy || clientUpdateBusy))
                ShowUpdateProgress(value);
        });
        target.AdminRoot = root;
        await SupportPlatform.SynchronizeAgentAsync(target, ct, progress);
        if (generation == operationGeneration && ReferenceEquals(target, client)) clientUpToDate = true;
    }

    private async Task<bool> TryRetainFailedSynchronizationAsync(RemoteClient target, int generation)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await target.CloseHeartbeatChannelAsync();
            JsonElement heartbeat = await target.HeartbeatAsync(deadline.Token);
            if (!CanRetainFailedSynchronization(heartbeat) || generation != operationGeneration || !ReferenceEquals(target, client)) return false;
            clientUpToDate = heartbeat.GetProperty("binaryMatched").GetBoolean();
            supportSession = true; heartbeatHealthy = clientUpToDate; powerHold ??= PowerHold.Acquire();
            SelectRole(1); SelectControllerPage(0); code.SetText(""); StartHeartbeat();
            return true;
        }
        catch { return false; }
    }

    internal static bool CanRetainFailedSynchronization(JsonElement heartbeat) =>
        heartbeat.TryGetProperty("session", out var session) &&
        session.TryGetProperty("connected", out var connected) && connected.ValueKind == JsonValueKind.True &&
        heartbeat.TryGetProperty("binaryMatched", out var matched) && matched.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private async Task UpdateConnectedClientAsync()
    {
        if (client is not { } target || !CanUpdateClient(supportSession, hasClient: true, pairingBusy, clientUpdateBusy, terminating, clientUpToDate) || action != null)
            return;
        if (Forms.MessageBox.Show(this, UiText.UpdateClientConfirmation, UiText.UpdateClientConfirmationTitle,
            Forms.MessageBoxButtons.YesNo, Forms.MessageBoxIcon.Warning) != Forms.DialogResult.Yes)
            return;

        // Re-check the target after the confirmation dialog yielded to other UI
        // events, then run the same authenticated update protocol used by pairing.
        if (!CanUpdateClient(supportSession, hasClient: client != null, pairingBusy, clientUpdateBusy, terminating, clientUpToDate) || action != null || !ReferenceEquals(target, client))
            return;

        bool resumeStream = liveStream != null;
        clientUpdateBusy = true;
        clientUpdateLifetime = new CancellationTokenSource();
        int generation = ++operationGeneration;
        sessionGeneration++;
        heartbeatLifetime?.Cancel();
        if (resumeStream)
            StopStream(() => UiText.ViewingSuspended);
        else
        {
            liveFrameFresh = false;
            liveBadge.Visible = false;
            ReleaseHeldInputForCurrentSession();
            RefreshInputStatus();
        }
        heartbeatHealthy = false;
        connectionState.SetText(() => UiText.AgentSynchronizing);
        SetFooterMessage(() => UiText.AgentSynchronizing);
        SetFooterDetail(() => UiText.TransferValidateVersion);
        ShowUpdateProgress(new AgentUpdateProgress("idle", 0, 0));
        UpdateHeader(); RefreshControllerControls(); RefreshFooter();

        var lifetime = clientUpdateLifetime!;
        bool synchronizationSucceeded = false;
        try
        {
            using var synchronization = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            synchronization.CancelAfter(TimeSpan.FromSeconds(SupportOperationTimeouts.ControllerSynchronizationSeconds));
            await SynchronizeClientAsync(target, generation, synchronization.Token);
            synchronizationSucceeded = true;
            if (generation != operationGeneration || !ReferenceEquals(target, client)) return;

            heartbeatHealthy = await ConfirmHealthySessionAsync(target, synchronization.Token);
            if (generation != operationGeneration || !ReferenceEquals(target, client)) return;
            synchronization.Token.ThrowIfCancellationRequested();
            updateProgressArea.Visible = false;
            connectionState.SetText(() => UiText.SessionEstablished);
            SetFooterMessage(() => UiText.ActiveVersionsSynchronized);
            SetFooterDetail(() => heartbeatHealthy ? UiText.SessionEstablished : UiText.ReconnectionInProgress);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested || terminating)
        {
        }
        catch (OperationCanceledException)
        {
            if (generation == operationGeneration && ReferenceEquals(target, client) && !terminating)
                ShowSynchronizationFailure(UiText.SynchronizationTimedOut);
        }
        catch (Exception ex)
        {
            if (generation == operationGeneration && ReferenceEquals(target, client) && !terminating)
                ShowSynchronizationFailure(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(clientUpdateLifetime, lifetime)) clientUpdateLifetime = null;
            lifetime.Dispose();
            if (generation == operationGeneration && ReferenceEquals(target, client))
            {
                clientUpdateBusy = false;
                StartHeartbeat();
                UpdateHeader(); RefreshControllerControls(); RefreshInputStatus(); RefreshFooter();
                if (synchronizationSucceeded && resumeStream && rolePages.SelectedIndex == 1 && controllerPages.SelectedIndex == 1)
                    _ = StartStreamAsync();
            }
        }
    }

    private void ShowSynchronizationFailure(string detail)
    {
        updateProgressText.SetText(() => UiText.SynchronizationInterrupted);
        updateProgressText.ForeColor = DestructiveText;
        updateProgressFill.BackColor = DestructiveText;
        updateProgressTrack.AccessibleName = updateProgressText.Text;
        connectionState.SetText(() => UiText.FailurePrefix + detail);
        SetFooterMessage(() => UiText.ConnectionFailed);
        footerDetail = detail;
    }

    private static async Task<bool> ConfirmHealthySessionAsync(RemoteClient target, CancellationToken ct)
    {
        try
        {
            JsonElement heartbeat = await target.HeartbeatAsync(ct);
            bool connected = heartbeat.TryGetProperty("session", out var session) &&
                session.TryGetProperty("connected", out var connectedValue) && connectedValue.GetBoolean();
            bool matched = heartbeat.TryGetProperty("binaryMatched", out var matchedValue) && matchedValue.GetBoolean();
            return connected && matched;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task LoadInitialRemoteStateAsync(int generation)
    {
        try
        {
            if (client == null) return;
            _ = RememberConnectedWakeAdapterAsync(client);
            if (liveStream == null) _ = StartStreamAsync();
            var resourcesTask = RefreshResourcesAsync(); var monitorsTask = LoadMonitorsAsync(generation); currentDirectory = ""; fileDirectory.SetText(""); var filesTask = BrowseFilesAsync(); await Task.WhenAll(resourcesTask, monitorsTask, filesTask); if (generation != operationGeneration) return; SetFooterDetail(() => UiText.MeasurementsFilesLoaded); RefreshFooter();
        }
        catch (Exception ex) { SetFooterMessage(() => UiText.SessionPartiallyLoaded); footerDetail = ex.Message; RefreshFooter(); }
    }

    private async Task LoadMonitorsAsync(int generation)
    {
        try
        {
            if (client == null) return;
            var data = RemoteClient.Require(await client.CallAsync("monitors", seconds: 15));
            if (generation != operationGeneration || IsDisposed) return;
            var choices = data.EnumerateArray().Select((item, index) => new MonitorChoice(
                item.Int("index", index),
                () => item.TryGetProperty("primary", out var primary) && primary.GetBoolean() ? UiText.PrimaryMonitor : UiText.Format(UiText.MonitorNumber, index + 1))).ToArray();
            if (choices.Length == 0) return;
            monitor.BeginUpdate();
            monitor.Items.Clear();
            monitor.Items.AddRange(choices);
            monitor.SelectedIndex = 0;
            monitor.EndUpdate();
        }
        catch
        {
            // The default monitor remains usable when a peer cannot enumerate
            // its desktop layout yet; the screenshot call reports its own error.
        }
    }

    private void StartHeartbeat()
    {
        heartbeatLifetime?.Cancel(); heartbeatLifetime = new CancellationTokenSource(); heartbeatTask = HeartbeatLoopAsync(heartbeatLifetime.Token);
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            RemoteClient? target = client;
            if (target == null) break;
            try
            {
                var heartbeat = await target.HeartbeatAsync(ct);
                bool sessionConnected = heartbeat.TryGetProperty("session", out var session) && session.TryGetProperty("connected", out var connected) && connected.GetBoolean();
                bool binaryMatched = heartbeat.TryGetProperty("binaryMatched", out var matched) && matched.GetBoolean();
                if (ct.IsCancellationRequested || !ReferenceEquals(target, client)) break;
                heartbeatHealthy = sessionConnected && binaryMatched;
                clientUpToDate = binaryMatched;
                if (!heartbeatHealthy)
                {
                    liveFrameFresh = false;
                    ReleaseHeldInputForCurrentSession();
                }
                else
                {
                    if (!liveFrameFresh && liveStream != null) RemoteClient.Require(await target.CallAsync("screen.refresh", ct: ct, seconds: 5));
                    if (inputState.Suspended) BeginInputRecovery();
                }
                PostUi(() => { UpdateHeader(); RefreshInputStatus(); });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (RemoteOperationException ex) when (ex.Code is "access_denied" or "session_ended")
            {
                if (!ct.IsCancellationRequested && ReferenceEquals(target, client))
                    await TerminateControllerSessionAsync();
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested || !ReferenceEquals(target, client)) break;
                heartbeatHealthy = false; liveFrameFresh = false; ReleaseHeldInputForCurrentSession(); SetFooterMessage(() => UiText.Reconnecting); footerDetail = ex.Message; PostUi(() => { streamOverlay.SetText(() => UiText.SessionRetrying); streamOverlay.Visible = true; liveBadge.Visible = false; UpdateHeader(); RefreshInputStatus(); RefreshFooter(); });
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task RefreshScreenAsync()
    {
        RequireClient(); var watch = Stopwatch.StartNew(); var data = RemoteClient.Require(await client!.CallAsync("screenshot", new { monitor = MonitorValue() }, seconds: 30)); Present(data.Deserialize<ScreenFrame>(Json.Options)!); streamStatus.SetText(() => StreamStatusWithRoute(UiText.Format(UiText.FreshFrameTime, watch.Elapsed.TotalMilliseconds))); SetFooterDetail(() => streamStatus.Text); RefreshFooter();
    }

    private async Task StartStreamAsync()
    {
        if (liveStream != null || client == null || trayVisible || rolePages.SelectedIndex != 1) return;
        if (WindowState == Forms.FormWindowState.Minimized) { resumeViewingAfterMinimize = true; return; }
        try { RequireClient(); } catch (Exception ex) { streamStatus.SetText(ex.Message); return; }
        RemoteClient target = client;
        int generation = sessionGeneration;
        var lifetime = new CancellationTokenSource();
        liveStream = lifetime; streamStartedUtc = DateTimeOffset.UtcNow; streamFrames = 0; streamBytes = 0; streamCodec = ""; pauseViewing.SetText(() => UiText.Pause); streamOverlay.Visible = true; streamOverlay.SetText(() => UiText.ConnectingStream);
        try
        {
            while (!lifetime.IsCancellationRequested && generation == sessionGeneration && ReferenceEquals(target, client))
            {
                if (!heartbeatHealthy) { await Task.Delay(250, lifetime.Token); continue; }
                try
                {
                    await target.StreamAdaptiveAsync(frame =>
                    {
                        if (!lifetime.IsCancellationRequested && ReferenceEquals(liveStream, lifetime) && generation == sessionGeneration && ReferenceEquals(target, client) && supportSession) Present(frame);
                        return Task.CompletedTask;
                    }, (codec, reason) => SetStreamCodec(codec, reason), StreamPolicy.MaximumFps, MonitorValue(), 300, lifetime.Token, relayEconomy.Checked);
                }
                catch (Exception ex) when (!lifetime.IsCancellationRequested && ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException)
                {
                    // The five-minute transport boundary and transient link loss
                    // renew the stream inside the same authenticated session.
                    liveFrameFresh = false; liveBadge.Visible = false;
                    streamOverlay.SetText(() => UiText.ReconnectingStream); streamOverlay.Visible = true;
                    streamStatus.SetText(() => UiText.WaitingFreshFrame); RefreshInputStatus();
                    ReleaseHeldInputForCurrentSession();
                    await Task.Delay(500, lifetime.Token);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(liveStream, lifetime))
            {
                liveFrameFresh = false; liveBadge.Visible = false; streamOverlay.SetText(() => UiText.StreamInterruptedResume); streamOverlay.Visible = true; streamStatus.SetText(() => UiText.StreamInterruptedPrefix + ex.Message); RefreshInputStatus();
            }
        }
        finally
        {
            if (ReferenceEquals(liveStream, lifetime))
            {
                liveStream = null;
                monitor.Enabled = true;
                pauseViewing.SetText(() => UiText.Resume);
                liveFrameFresh = false;
                liveBadge.Visible = false;
                streamOverlay.Visible = true;
                streamOverlay.SetText(() => UiText.StreamStoppedResume);
                RefreshInputStatus();
            }
            lifetime.Dispose();
            if (generation == sessionGeneration && ReferenceEquals(target, client)) ReleaseHeldInputForCurrentSession();
        }
    }

    private void StopStream(Func<string> message)
    {
        var target = client;
        var running = liveStream;
        liveStream = null;
        running?.Cancel();
        pauseViewing.SetText(() => UiText.Resume); monitor.Enabled = true;
        liveFrameFresh = false; liveBadge.Visible = false; streamOverlay.Visible = true; streamOverlay.SetText(() => UiText.ViewingPausedResume); streamStatus.SetText(message); RefreshInputStatus();
        if (target != null) ReleaseHeldInputForCurrentSession();
    }

    private void Present(ScreenFrame frame)
    {
        if (InvokeRequired)
        {
            PostUi(() => Present(frame));
            return;
        }
        geometry = frame.Geometry; using var ms = new MemoryStream(Convert.FromBase64String(frame.Data)); using var image = Image.FromStream(ms); var previous = screen.Image; screen.Image = new Bitmap(image); previous?.Dispose(); screen.Refresh(); liveFrameFresh = true; liveBadge.Visible = true; streamOverlay.Visible = false; RefreshInputStatus();
        if (liveStream != null && streamStartedUtc is { } started)
        {
            streamFrames++; streamBytes += frame.Data.Length; double seconds = Math.Max(0.001, (DateTimeOffset.UtcNow - started).TotalSeconds); double fps = streamFrames / seconds; double mbps = streamBytes * 8 / seconds / 1_000_000d; streamStatus.SetText(() => StreamStatusWithRoute(UiText.Format(UiText.StreamMetrics, fps, mbps, frame.CaptureEncodeMs))); SetFooterDetail(() => streamStatus.Text); RefreshFooter();
        }
        else streamStatus.SetText(() => UiText.Format(UiText.CaptureMetrics, frame.CapturedUtc.ToLocalTime(), frame.CaptureEncodeMs));
    }

    private void SetStreamCodec(string codec, string? reason)
    {
        string label = codec.Equals("h264", StringComparison.OrdinalIgnoreCase) ? "H.264" : string.IsNullOrWhiteSpace(reason) ? "JPEG" : "JPEG fallback";
        PostUi(() =>
        {
            if (liveStream == null) return;
            streamCodec = label;
            if (streamFrames == 0) streamStatus.SetText(label);
            SetFooterDetail(() => streamStatus.Text); RefreshFooter();
        });
    }

    private void Present(DecodedStreamFrame frame)
    {
        if (InvokeRequired)
        {
            if (IsDisposed || !IsHandleCreated) { frame.Dispose(); return; }
            PostUi(() => Present(frame));
            return;
        }
        try
        {
            geometry = frame.Geometry;
            var image = frame.TakeImage(); var previous = screen.Image; screen.Image = image; previous?.Dispose(); screen.Refresh(); liveFrameFresh = true; liveBadge.Visible = true; streamOverlay.Visible = false; RefreshInputStatus();
            if (string.IsNullOrEmpty(streamCodec)) streamCodec = frame.Codec.Equals("h264", StringComparison.OrdinalIgnoreCase) ? "H.264" : "JPEG";
            if (liveStream != null && streamStartedUtc is { } started)
            {
                streamFrames++; streamBytes += frame.Bytes; double seconds = Math.Max(0.001, (DateTimeOffset.UtcNow - started).TotalSeconds); double fps = streamFrames / seconds; double mbps = streamBytes * 8 / seconds / 1_000_000d; streamStatus.SetText(() => StreamStatusWithRoute(UiText.Format(UiText.StreamMetrics, streamCodec, fps, mbps, frame.CaptureEncodeMs))); SetFooterDetail(() => streamStatus.Text); RefreshFooter();
            }
        }
        finally { frame.Dispose(); }
    }

    private void QueueMouse(string kind, Forms.MouseEventArgs e)
    {
        if (!CanSendInput() || geometry == null) return; var point = geometry.MapLetterbox(screen.Width, screen.Height, e.X, e.Y); if (point == null) { if (kind == "up") QueueInput(new { kind = "release" }); return; } QueueInput(new { kind, x = point.Value.X, y = point.Value.Y, layoutId = geometry.LayoutId, button = e.Button == Forms.MouseButtons.Right ? "right" : e.Button == Forms.MouseButtons.Middle ? "middle" : "left", delta = e.Delta });
    }

    private void RefreshPowerHold()
    {
        bool agentSessionRequiresHold = agent is { } activeAgent &&
            PowerHoldPolicy.ShouldHoldForAgent(activeAgent.Session);
        bool shouldHold = supportSession || agentSessionRequiresHold;
        if (!shouldHold)
        {
            powerHold?.Dispose();
            powerHold = null;
            return;
        }

        if (powerHold != null) return;
        try { powerHold = PowerHold.Acquire(); }
        catch (Exception ex)
        {
            // The session remains visible, but the Sleep row stays Active so
            // the UI does not claim a Windows power request that failed.
            agentLog.SetText("Unable to prevent system sleep: " + ex.Message);
        }
    }

    private void QueueFocusedText()
    {
        if (string.IsNullOrEmpty(remoteText.Text) || !CanSendFocusedInput()) return;
        QueueInput(new { kind = "text", text = remoteText.Text });
    }

    private bool CanSendFocusedInput() => rolePages.SelectedIndex == 1 && !clientUpdateBusy && supportSession && heartbeatHealthy && !trayVisible && liveFrameFresh;

    private bool CanSendInput() => rolePages.SelectedIndex == 1 && !clientUpdateBusy && inputState.CanSend(supportSession && heartbeatHealthy && !trayVisible, liveFrameFresh, screen.ContainsFocus && ContainsFocus);

    private void RefreshInputStatus()
    {
        inputStatus.SetText(() => !supportSession ? UiText.ConnectToControl : clientUpdateBusy ? UiText.Synchronizing : !inputState.Enabled ? UiText.ViewOnly : !heartbeatHealthy ? UiText.ControlAwaitingConnection : inputBlockMessage ?? (inputState.Suspended ? UiText.RestoringControl : !liveFrameFresh ? UiText.WaitingFreshFrame : screen.ContainsFocus && ContainsFocus ? UiText.MouseKeyboardActive : UiText.ClickScreenToControl));
    }

    private string StreamStatusWithRoute(string status) => client?.ActiveRoute is { Length: > 0 } route
        ? route + " · " + status
        : status;

    private void QueueInput(object value)
    {
        RemoteClient? target = client;
        if (target == null || !supportSession) return;
        string kind = Json.Element(value).Str("kind");
        if (clientUpdateBusy && kind != "release") return;
        if (kind == "release") { ReleaseHeldInputForCurrentSession(); return; }
        if (!inputQueue.TryWrite(kind, new QueuedInput(target, sessionGeneration, value)))
        {
            inputState.Suspend();
            inputStatus.SetText(() => UiText.InputQueueFull);
            BeginInputRecovery();
        }
    }

    /// <summary>
    /// Invalidates queued input before a role, target, or session change. Every
    /// queued item retains its original client and generation, so a delayed
    /// dequeue can never be sent to a newly selected target.
    /// </summary>
    private void InvalidateInputSession()
    {
        RemoteClient? oldClient = client;
        bool hadInputSession = supportSession || liveStream != null;
        sessionGeneration++;
        if (hadInputSession && oldClient != null) _ = ReleaseHeldInputAsync(oldClient);
    }

    private async Task ReleaseHeldInputAsync(RemoteClient target)
    {
        try
        {
            RemoteClient.Require(await target.SendInputAsync(new { kind = "release" }, seconds: 5));
        }
        catch
        {
            // Release is best effort when the peer has already disconnected;
            // AgentServer also releases native input during its grace period.
        }
    }

    private void ReleaseHeldInputForCurrentSession()
    {
        if (supportSession && client is { } target)
            inputQueue.Reset(new QueuedInput(target, sessionGeneration, new { kind = "release" }));
    }

    private void BeginInputRecovery()
    {
        if (!inputState.Suspended)
        {
            inputRecoveryTimer.Stop();
            return;
        }

        if (!supportSession || client == null || !heartbeatHealthy)
        {
            inputRecoveryTimer.Stop();
            return;
        }

        ReleaseHeldInputForCurrentSession();
        inputRecoveryTimer.Start();
    }

    private void RetryInputRecovery()
    {
        if (!supportSession || client == null || !inputState.Suspended || !heartbeatHealthy)
        {
            inputRecoveryTimer.Stop();
            return;
        }

        ReleaseHeldInputForCurrentSession();
    }

    private async Task EndSessionBestEffortAsync(RemoteClient target)
    {
        if (target.Connection.Token.Length == 0) return;
        try { await target.EndSessionAsync(CancellationToken.None); } catch { }
        if (ReferenceEquals(client, target))
        {
            client = null;
            powerHold?.Dispose(); powerHold = null;
        }
    }

    private async Task PumpInputAsync()
    {
        await foreach (QueuedInput item in inputQueue.ReadAllAsync())
        {
            RemoteClient target = item.Client;
            if (!supportSession || item.Generation != sessionGeneration || !ReferenceEquals(target, client)) continue;
            bool release = Json.Element(item.Payload).Str("kind") == "release";
            try
            {
                RemoteClient.Require(await target.SendInputAsync(item.Payload));
                if (!release) inputBlockMessage = null;
                if (release && item.Generation == sessionGeneration && ReferenceEquals(target, client))
                {
                    inputState.Released();
                    inputRecoveryTimer.Stop();
                    RefreshInputStatus();
                }
            }
            catch (Exception ex)
            {
                if (item.Generation != sessionGeneration || !ReferenceEquals(target, client)) continue;
                inputState.Suspend();
                if (ex is RemoteOperationException { Code: "input_blocked" }) inputBlockMessage = ex.Message;
                inputStatus.SetText(() => ex is RemoteOperationException { Code: "input_blocked" } ? ex.Message : UiText.RestoringControl);
                output.SetText(() => UiText.InputInterruptedPrefix + ex.Message);
                BeginInputRecovery();
            }
        }
    }

    private async Task RefreshResourcesAsync()
    {
        if (resourcesLoading) return;
        resourcesLoading = true; RefreshControllerControls();
        int generation = sessionGeneration; RemoteClient? target = client;
        try
        {
            RequireClient(); resourceState.SetText(() => UiText.Measuring); volumeSummary.SetText(() => UiText.VolumesMeasuring); int? selected = processList.SelectedItems.Count > 0 && processList.SelectedItems[0].Tag is ProcessSortRow row ? row.Pid : null; var data = RemoteClient.Require(await target!.CallAsync("processes", seconds: 30)); var system = RemoteClient.Require(await target.CallAsync("system", seconds: 30)); if (generation != sessionGeneration || !ReferenceEquals(target, client)) return; processRows.Clear(); foreach (var p in data.GetProperty("processes").EnumerateArray()) processRows.Add(new ProcessSortRow(p.Int("pid"), p.Str("name", UiText.Unavailable), NullableDouble(p, "cpuPercentTotalMachine"), NullableLong(p, "workingSetBytes"), NullableBool(p, "responding"), p.Str("window"), NullableDate(p, "startUtc"))); lastMeasurementUtc = NullableDate(data, "sampleEndUtc") ?? DateTimeOffset.UtcNow; RenderProcesses(selected); var cpu = NullableDouble(system, "cpuPercentTotalMachine"); cpuSummary.SetText(() => cpu is { } c ? $"{c:F1} %" : UiText.Unavailable); long? total = NullableLong(system, "physicalMemoryTotalBytes"); long? available = NullableLong(system, "physicalMemoryAvailableBytes"); ramSummary.SetText(() => total is > 0 && available is >= 0 ? FormatBytes(total.Value - available.Value) : UiText.Unavailable); processSummary.SetText(processRows.Count.ToString("N0")); resourceMeasuredAt.SetText(lastMeasurementUtc.Value.ToLocalTime().ToString("T")); volumeSummary.SetText(() => FormatVolumes(system)); resourceState.SetText(() => UiText.MeasurementComplete); SetFooterDetail(() => UiText.Format(UiText.MeasuredAtTime, resourceMeasuredAt.Text)); RefreshFooter();
        }
        catch (Exception ex) { if (generation != sessionGeneration) return; resourceState.SetText(() => UiText.MeasurementUnavailable); SetFooterMessage(() => UiText.ResourcesUnavailable); footerDetail = ex.Message; RefreshFooter(); }
        finally { resourcesLoading = false; RefreshControllerControls(); }
    }

    private void RenderProcesses(int? selectedPid = null)
    {
        SetSortIndicator(processList, (int)processSort.Column, processSort.Descending);
        int? keep = selectedPid ?? (processList.SelectedItems.Count > 0 && processList.SelectedItems[0].Tag is ProcessSortRow selectedRow ? selectedRow.Pid : null); var sorted = UiSorting.SortProcesses(processRows, processSort); processList.BeginUpdate(); processList.Items.Clear(); foreach (var row in sorted) { var item = new Forms.ListViewItem(row.Pid.ToString()); item.SubItems.Add(row.Name); item.SubItems.Add(row.CpuPercentTotalMachine is { } cpu ? cpu.ToString("F1") : "—"); item.SubItems.Add(row.WorkingSetBytes is { } bytes ? (bytes / 1048576d).ToString("F1") : "—"); item.SubItems.Add(row.Responding is null ? "—" : row.Responding.Value ? UiText.Yes : UiText.NotResponding); item.SubItems.Add(string.IsNullOrWhiteSpace(row.Window) ? "—" : row.Window); item.Tag = row; if (keep == row.Pid) item.Selected = true; processList.Items.Add(item); } processList.EndUpdate();
    }

    private async Task OpenRemoteFolderAsync(string path)
    {
        if (filesLoading || fileTransferLifetime != null) return;
        currentDirectory = path; fileDirectoryLoaded = false; selectedFilePath = null;
        remotePath.SetText(""); fileDirectory.SetText(path);
        await BrowseFilesAsync();
    }

    private async Task BrowseFilesAsync()
    {
        if (filesLoading) return;
        filesLoading = true; RefreshControllerControls();
        int generation = sessionGeneration; RemoteClient? target = client; string directory = currentDirectory;
        try
        {
            RequireClient(); fileState.SetText(() => UiText.Loading); string keep = selectedFilePath ?? "";
            string resolvedDirectory = directory;
            if (!Path.IsPathFullyQualified(directory))
            {
                var status = RemoteClient.Require(await target!.CallAsync("status", seconds: 15));
                resolvedDirectory = string.IsNullOrWhiteSpace(directory) ? status.Str("workspace") : Path.GetFullPath(Path.Combine(status.Str("workspace"), directory));
            }
            var data = RemoteClient.Require(await target!.CallAsync("files", new { path = directory }, seconds: 30));
            if (generation != sessionGeneration || !ReferenceEquals(target, client) || directory != currentDirectory) return;
            currentDirectory = resolvedDirectory; fileDirectoryLoaded = true; fileRows.Clear();
            foreach (var entry in data.EnumerateArray()) fileRows.Add(new FileSortRow(entry.Str("name"), entry.TryGetProperty("directory", out var d) && d.GetBoolean(), NullableLong(entry, "size"), NullableDate(entry, "modifiedUtc"), entry.Str("path")));
            RenderFiles(keep); fileState.SetText(() => fileRows.Count == 0 ? UiText.EmptyFolder : UiText.Format(UiText.ItemCount, fileRows.Count)); fileDirectory.SetText(currentDirectory); SetFooterDetail(() => UiText.Format(UiText.FilesItemCount, fileRows.Count)); RefreshFooter();
        }
        catch (Exception ex) { if (generation != sessionGeneration || directory != currentDirectory) return; fileDirectoryLoaded = false; fileRows.Clear(); fileList.Items.Clear(); selectedFilePath = null; remotePath.SetText(""); fileState.SetText(() => UiText.CannotReadCheckPath); SetFooterMessage(() => UiText.CannotReadFolder); footerDetail = ex.Message; RefreshFooter(); }
        finally { filesLoading = false; RefreshControllerControls(); }
    }

    private void RenderFiles(string? selectedPath = null)
    {
        SetSortIndicator(fileList, (int)fileSort.Column, fileSort.Descending);
        string keep = selectedPath ?? selectedFilePath ?? ""; var sorted = UiSorting.SortFiles(fileRows, fileSort); fileList.BeginUpdate(); fileList.Items.Clear(); foreach (var row in sorted) { var item = new Forms.ListViewItem(row.Name); item.SubItems.Add(row.IsDirectory ? UiText.Folder : UiText.File); item.SubItems.Add(row.SizeBytes is { } bytes ? FormatBytes(bytes) : "—"); item.SubItems.Add(row.ModifiedUtc is { } date ? date.ToLocalTime().ToString("g") : "—"); item.Tag = row; if (!row.IsDirectory && row.Path == keep) item.Selected = true; fileList.Items.Add(item); } fileList.EndUpdate(); remotePath.SetText(selectedFilePath ?? "");
    }

    private static void SetSortIndicator(Forms.ListView table, int selectedColumn, bool descending)
    {
        for (int index = 0; index < table.Columns.Count; index++)
        {
            var column = table.Columns[index];
            column.Text = LiveText.Caption(column) + (index == selectedColumn ? descending ? "  ▼" : "  ▲" : "");
        }
    }

    private async Task ExecuteSelectedAsync()
    {
        if (operations.SelectedItem is not string op) return;
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(arguments.Text);
            if (args.ValueKind != JsonValueKind.Object) throw new JsonException(UiText.ArgumentsMustBeObject);
            await ExecuteAsync(op, args);
        }
        catch (JsonException ex) { diagnosticState.SetText(() => UiText.InvalidJsonArguments); output.SetText(ex.Message); RefreshFooter(); }
    }

    public async Task ExecuteAsync(string op, object args) => await ExecuteAsync(op, Json.Element(args));

    private async Task ExecuteAsync(string op, JsonElement args)
    {
        if (action != null) { diagnosticState.SetText(() => UiText.ActionAlreadyRunning); return; }
        int generation = sessionGeneration;
        RemoteClient? target = client;
        try
        {
            RequireClient(); using var cts = new CancellationTokenSource(); action = cts; operationId = Guid.NewGuid().ToString(); diagnosticState.SetText(() => UiText.Running); output.SetText(""); RefreshControllerControls(); RefreshFooter();
            var reply = await target!.CallAsync(op, args, cts.Token, operationId, 120);
            if (generation != sessionGeneration || !ReferenceEquals(target, client)) return;
            output.SetText(Pretty(reply)); RemoteClient.Require(reply); diagnosticState.SetText(() => UiText.CompletedPrefix + DateTime.Now.ToString("T"));
        }
        catch (OperationCanceledException) { if (generation == sessionGeneration) { diagnosticState.SetText(() => UiText.ActionCancelled); output.SetText(() => UiText.ExecutionInterrupted); } }
        catch (Exception ex) { if (generation == sessionGeneration) { diagnosticState.SetText(() => UiText.ActionFailedSeeResult); output.SetText(Pretty(new { ok = false, message = ex.Message })); } }
        finally { action = null; operationId = null; RefreshControllerControls(); RefreshFooter(); }
    }

    private async Task CancelActionAsync()
    {
        action?.Cancel();
        string? id = operationId;
        if (client != null && !string.IsNullOrWhiteSpace(id))
            try { await client.CallAsync("cancel", new { id }, seconds: 5); } catch { }
    }

    private async Task UploadFileAsync()
    {
        if (fileTransferLifetime != null || !fileDirectoryLoaded) return;
        using var dialog = new Forms.OpenFileDialog(); if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        string path = Path.Combine(currentDirectory, Path.GetFileName(dialog.FileName));
        await TransferFileAsync(path, async (target, ct, progress) =>
        {
            var result = await target.UploadAsync(dialog.FileName, path, ct, progress);
            output.SetText(Pretty(result));
        });
    }

    private async Task UploadFolderAsync()
    {
        if (fileTransferLifetime != null || !fileDirectoryLoaded) return;
        using var dialog = new Forms.FolderBrowserDialog(); if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        string folderDestination = currentDirectory;
        await TransferFileAsync(folderDestination, async (target, ct, progress) =>
        {
            var files = await Task.Run(() => Directory.EnumerateFiles(dialog.SelectedPath, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Select(path => new FileInfo(path)).ToArray(), ct);
            long total = files.Sum(file => file.Length), completed = 0, sent = 0;
            foreach (var file in files)
            {
                long attempt = 0;
                var fileProgress = new ForwardFileProgress(value =>
                {
                    attempt = value.BytesThisAttempt;
                    progress.Report(new(completed + value.TransferredBytes, total, sent + attempt));
                });
                await target.UploadAsync(file.FullName, Path.Combine(folderDestination, Path.GetRelativePath(dialog.SelectedPath, file.FullName)), ct, fileProgress);
                completed += file.Length; sent += attempt;
                progress.Report(new(completed, total, sent));
            }
        });
    }

    private async Task DownloadFileAsync()
    {
        if (fileTransferLifetime != null) return;
        if (string.IsNullOrWhiteSpace(selectedFilePath)) { fileState.SetText(() => UiText.SelectFile); return; }
        string path = selectedFilePath;
        using var dialog = new Forms.SaveFileDialog { FileName = Path.GetFileName(path) }; if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        await TransferFileAsync(dialog.FileName, (target, ct, progress) => target.DownloadAsync(path, dialog.FileName, ct, progress), download: true);
    }

    private async Task TransferFileAsync(string name, Func<RemoteClient, CancellationToken, IProgress<FileTransferProgress>, Task> transfer, bool download = false)
    {
        if (fileTransferLifetime != null) return;
        using var lifetime = new CancellationTokenSource();
        int generation = sessionGeneration;
        bool receivingProgress = true, measuring = false;
        try
        {
            RequireClient(); var target = client!;
            fileTransferLifetime = lifetime; RefreshControllerControls();
            fileTransferProgress.Value = 0; fileTransferProgress.Style = Forms.ProgressBarStyle.Marquee;
            fileTransferStatus.SetText(() => UiText.Format(download ? UiText.DownloadingTo : UiText.UploadingTo, name));
            fileTransferDetails.SetText(() => UiText.PreparingFileTransfer);
            var watch = Stopwatch.StartNew();
            var progress = new Progress<FileTransferProgress>(value =>
            {
                if (!receivingProgress || generation != sessionGeneration || !ReferenceEquals(fileTransferLifetime, lifetime)) return;
                if (!measuring) { watch.Restart(); measuring = true; }
                var metrics = FileTransferMetrics.Calculate(value.TransferredBytes, value.TotalBytes, value.BytesThisAttempt, watch.Elapsed);
                fileTransferProgress.Style = Forms.ProgressBarStyle.Continuous; fileTransferProgress.Value = metrics.Percent;
                fileTransferDetails.SetText(() => value.TransferredBytes >= value.TotalBytes ? UiText.VerifyingFileTransfer :
                    UiText.Format(UiText.FileTransferNumbers, metrics.Percent, FormatBytes(value.TransferredBytes), FormatBytes(value.TotalBytes),
                        metrics.BytesPerSecond > 0 ? FormatBytes((long)metrics.BytesPerSecond) + "/s" : "—",
                        metrics.Remaining is { } eta ? FormatTransferEta(eta) : UiText.CalculatingTransferEta));
            });
            await transfer(target, lifetime.Token, progress);
            receivingProgress = false;
            if (generation == sessionGeneration)
            {
                fileTransferProgress.Style = Forms.ProgressBarStyle.Continuous; fileTransferProgress.Value = 100;
                fileTransferDetails.SetText(() => download ? UiText.DownloadVerified : UiText.UploadVerified);
                await BrowseFilesAsync();
            }
        }
        catch (OperationCanceledException) { if (generation == sessionGeneration) fileTransferDetails.SetText(() => UiText.TransferPaused); }
        catch (Exception ex) { if (generation == sessionGeneration) fileTransferDetails.SetText(ex.Message); }
        finally { receivingProgress = false; if (ReferenceEquals(fileTransferLifetime, lifetime)) { fileTransferLifetime = null; fileTransferProgress.Style = Forms.ProgressBarStyle.Continuous; RefreshControllerControls(); } }
    }

    private sealed class ForwardFileProgress(Action<FileTransferProgress> report) : IProgress<FileTransferProgress>
    {
        public void Report(FileTransferProgress value) => report(value);
    }

    private static string FormatTransferEta(TimeSpan eta) => eta.TotalHours >= 1
        ? $"{(int)eta.TotalHours}:{eta.Minutes:00}:{eta.Seconds:00}" : $"{eta.Minutes}:{eta.Seconds:00}";

    private async Task TerminateSupportAsync()
    {
        SessionTerminationTarget target = SelectTerminationTarget(rolePages.SelectedIndex == 0,
            agent != null, client != null && (supportSession || pairingBusy));
        if (target == SessionTerminationTarget.Agent)
        {
            await TerminateAgentSessionAsync();
            return;
        }
        if (target == SessionTerminationTarget.Controller)
            await TerminateControllerSessionAsync();
    }

    private async Task TerminateAgentSessionAsync()
    {
        if (terminating || agent is not { } oldAgent) return;
        terminating = true; terminateSession.Enabled = false; roleAgent.Enabled = roleController.Enabled = false;
        SetFooterMessage(() => UiText.EndingSupport); RefreshFooter();
        bool stopped = true;
        suppressTerminationEvent = true;
        try { await oldAgent.TerminateAsync(CancellationToken.None); }
        catch (Exception ex) { stopped = false; SetFooterDetail(() => UiText.TargetOfflineExpiration); output.SetText(Pretty(new { ok = false, message = ex.Message })); }
        finally { suppressTerminationEvent = false; }
        if (stopped && ReferenceEquals(agent, oldAgent))
            await RestartAgentAfterSupportEndAsync(oldAgent);
        roleAgent.Enabled = roleController.Enabled = true;
        terminateSession.Enabled = true; terminating = false;
        RefreshUiState();
        if (stopped)
        {
            SetFooterMessage(() => UiText.WaitingForController); SetFooterDetail(() => UiText.CloseToTray); RefreshFooter();
        }
    }

    private async Task TerminateControllerSessionAsync()
    {
        if (terminating || (client == null && !pairingBusy)) return;
        terminating = true; operationGeneration++; terminateSession.Enabled = false; roleAgent.Enabled = roleController.Enabled = false;
        SetFooterMessage(() => UiText.EndingSupport); RefreshFooter();
        pairingLifetime?.Cancel(); clientUpdateLifetime?.Cancel(); heartbeatLifetime?.Cancel(); action?.Cancel(); fileTransferLifetime?.Cancel(); resumeViewingOnRestore = false; resumeViewingAfterMinimize = false;
        StopStream(() => UiText.SupportEnded); QueueInput(new { kind = "release" });
        RemoteClient? oldClient = client;
        sessionGeneration++; supportSession = false; heartbeatHealthy = false;
        if (!quitting) { SelectRole(1); SelectControllerPage(0); }
        else RefreshControllerControls();
        try
        {
            if (oldClient != null)
            {
                await ReleaseHeldInputAsync(oldClient);
                await oldClient.EndSessionAsync(CancellationToken.None);
            }
        }
        catch (Exception ex) { SetFooterDetail(() => UiText.TargetOfflineExpiration); output.SetText(Pretty(new { ok = false, message = ex.Message })); }
        ClearControllerSession();
        roleAgent.Enabled = roleController.Enabled = true;
        terminateSession.Enabled = true; terminating = false;
        RefreshControllerControls();
        SetFooterMessage(() => UiText.SupportEnded); SetFooterDetail(() => UiText.SelectPcToRestart); RefreshFooter();
    }

    private void ClearControllerSession()
    {
        fileTransferLifetime?.Cancel(); fileTransferLifetime = null; fileDirectoryLoaded = false;
        fileTransferProgress.Style = Forms.ProgressBarStyle.Continuous; fileTransferProgress.Value = 0;
        fileTransferStatus.SetText(() => UiText.FileTransfers); fileTransferDetails.SetText(() => UiText.FileTransferReady);
        clientUpdateLifetime?.Cancel();
        clientUpdateBusy = false; clientUpToDate = false;
        updateProgressArea.Visible = false;
        // Retain the selected PC identity so reconnect refreshes its invitation.
        client = null; selectedFilePath = null;
        selectedPeerName.SetText(() => selectedPeer?.Name ?? UiText.NewConnection); selectedPeerAddress.SetText(() => PrivateInternet ? UiText.PrivateConnectInstructions : UiText.EnterRemoteCode);
        geometry = null; inputState.Released(); inputRecoveryTimer.Stop(); code.SetText("");
        processRows.Clear(); fileRows.Clear(); processList.Items.Clear(); fileList.Items.Clear();
        screen.Image?.Dispose(); screen.Image = null; currentDirectory = ""; fileDirectory.Clear(); remotePath.SetText("");
        cpuSummary.SetText(ramSummary.SetText(processSummary.SetText(resourceMeasuredAt.SetText("—"))));
        lastMeasurementUtc = null; powerHold?.Dispose(); powerHold = null;
        resourceState.SetText(() => ConnectToContinue); fileState.SetText(() => ConnectToContinue); diagnosticState.SetText(() => ConnectToContinue);
        volumeSummary.SetText(() => UiText.NoMeasurementsAvailable); output.SetText(""); remoteText.SetText(""); pid.Value = 0;
        streamStatus.SetText(() => UiText.NoActiveConnection);
        connectionState.SetText(() => PrivateInternet ? UiText.SupportEnded : UiText.SupportEndedNewCode);
        RefreshControllerControls(); RefreshPowerHold();
    }

    private void RequestQuit()
    {
        if (quitting) return; quitting = true; Close();
    }

    private async void MainFormClosing(object? sender, Forms.FormClosingEventArgs e)
    {
        SaveWindowPlacement();
        if (shutdownStarted) return;
        if (WindowLifetime.HideToTray(e.CloseReason, quitting))
        {
            e.Cancel = true;
            if (!trayVisible) HideToTray();
            return;
        }
        if (!shutdownStarted)
        {
            e.Cancel = true;
            quitting = true;
            shutdownStarted = true;
            if (await ShutdownAsync()) Close();
            else { quitting = false; shutdownStarted = false; }
            return;
        }
    }

    private void HideToTray()
    {
        trayWindowState = WindowState;
        resumeViewingOnRestore = liveStream != null;
        if (resumeViewingOnRestore) StopStream(() => UiText.MinimizedConnected);
        ReleaseHeldInputForCurrentSession();
        trayVisible = true; tray.Visible = true; Hide();
        if (!trayNoticeShown)
        {
            trayNoticeShown = true;
            tray.ShowBalloonTip(3000, "Remote Debugger", UiText.TrayNotice, Forms.ToolTipIcon.Info);
        }
    }

    private void RestoreFromTray()
    {
        trayVisible = false; ShowInTaskbar = true; Show(); WindowState = trayWindowState == Forms.FormWindowState.Maximized ? trayWindowState : Forms.FormWindowState.Normal; Activate();
        if (resumeViewingOnRestore && supportSession && controllerPages.SelectedIndex == 1) _ = StartStreamAsync();
        resumeViewingOnRestore = false;
    }

    internal void ActivateExistingWindow() => PostUi(() =>
    {
        if (quitting) return;
        if (trayVisible) RestoreFromTray();
        else
        {
            if (WindowState == Forms.FormWindowState.Minimized) WindowState = Forms.FormWindowState.Normal;
            Show(); Activate();
        }
    });

    private void RestoreWindowPlacement()
    {
        WindowPlacement? placement = WindowPlacementStore.Load(root, WindowPlacementStore.WorkingAreas());
        if (placement == null) return;
        StartPosition = Forms.FormStartPosition.Manual;
        Bounds = placement.Bounds;
        if (placement.Maximized) WindowState = Forms.FormWindowState.Maximized;
    }

    private void SaveWindowPlacement()
    {
        Rectangle bounds = WindowState == Forms.FormWindowState.Normal ? Bounds : RestoreBounds;
        WindowPlacementStore.Save(root, bounds, (trayVisible ? trayWindowState : WindowState) == Forms.FormWindowState.Maximized);
    }

    private async Task<bool> ShutdownAsync()
    {
        renderTimer.Stop(); inputRecoveryTimer.Stop(); discoveryLifetime?.Cancel(); heartbeatLifetime?.Cancel(); pairingLifetime?.Cancel(); clientUpdateLifetime?.Cancel(); fleetLifetime?.Cancel(); liveStream?.Cancel(); action?.Cancel(); fileTransferLifetime?.Cancel();
        // A saved connection only pre-fills the controller form. It is not an
        // active outbound session, and must never delay an agent replacement
        // while trying to contact an unrelated (possibly offline) old peer.
        RemoteClient? oldClient = supportSession || pairingBusy ? client : null;
        client = null;
        AgentServer? localAgent = agent;
        sessionGeneration++;
        supportSession = false;
        heartbeatHealthy = false;
        if (oldClient != null)
        {
            await ReleaseHeldInputAsync(oldClient);
            try { await oldClient.EndSessionAsync(CancellationToken.None); } catch { }
        }
        if (localAgent != null)
        {
            suppressTerminationEvent = true;
            try { await localAgent.TerminateAsync(CancellationToken.None); }
            catch (Exception ex)
            {
                suppressTerminationEvent = false;
                SetFooterMessage(() => UiText.AgentStopDelayed);
                footerDetail = ex.Message;
                renderTimer.Start();
                RefreshFooter();
                return false;
            }
            finally { suppressTerminationEvent = false; }
            localAgent.Dispose();
            if (ReferenceEquals(agent, localAgent)) agent = null;
        }
        inputQueue.Complete(); if (agent != null) agent.Dispose(); agent = null; powerHold?.Dispose(); powerHold = null; tray.Visible = false; screen.Image?.Dispose();
        return true;
    }

    private void DisposeResources()
    {
        SupportPlatform.ManagedRelaunchRequested -= OnManagedRelaunchRequested;
        renderTimer.Dispose(); inputRecoveryTimer.Dispose(); discoveryLifetime?.Dispose(); heartbeatLifetime?.Dispose(); pairingLifetime?.Dispose(); clientUpdateLifetime?.Dispose(); liveStream?.Dispose(); action?.Dispose(); powerHold?.Dispose(); tray.Dispose();
    }

    private void RequireClient() { if (client == null || !supportSession) throw new InvalidOperationException(UiText.ConnectBeforeAction); }
    private int MonitorValue() => monitor.SelectedItem is MonitorChoice choice ? choice.Index : 0;
    private static string FormatPairingCode(string code) => code.Length == 6 ? code[..3] + " " + code[3..] : "— — —";
    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1 ? $"{(int)duration.TotalHours} h {duration.Minutes:00}" : duration.TotalMinutes >= 1 ? $"{duration.Minutes} min" : UiText.JustNow;
    private static string ParentPath(string path) => string.IsNullOrWhiteSpace(path) ? "" : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? path;
    private static string FormatVolumes(JsonElement system)
    {
        if (!system.TryGetProperty("volumes", out var volumes) || volumes.ValueKind != JsonValueKind.Array) return UiText.VolumesUnavailable;
        var values = volumes.EnumerateArray().Select(volume =>
        {
            string name = volume.Str("name", "?").TrimEnd('\\', '/');
            if (!volume.TryGetProperty("ready", out var ready) || !ready.GetBoolean()) return UiText.Format(UiText.VolumeUnavailable, name);
            long? free = NullableLong(volume, "freeBytes");
            long? total = NullableLong(volume, "totalBytes");
            return free is { } freeBytes && total is { } totalBytes ? UiText.Format(UiText.VolumeSpace, name, FormatBytes(freeBytes), FormatBytes(totalBytes)) : UiText.Format(UiText.VolumeReady, name);
        }).ToArray();
        return values.Length == 0 ? UiText.VolumesUnavailable : UiText.VolumesPrefix + string.Join(" · ", values);
    }
    private static double? NullableDouble(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number ? item.GetDouble() : null;
    private static long? NullableLong(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number ? item.GetInt64() : null;
    private static bool? NullableBool(JsonElement value, string name) => value.TryGetProperty(name, out var item) ? item.ValueKind == JsonValueKind.Null ? null : item.GetBoolean() : null;
    private static DateTimeOffset? NullableDate(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(item.GetString(), out var date) ? date : null;
    private static string FormatBytes(long bytes) => bytes < 1024 * 1024 ? UiText.Format(UiText.Kibibytes, bytes / 1024d) : UiText.Format(UiText.Mebibytes, bytes / 1048576d);
    internal static (string Title, string Subtitle) ControllerPagePresentation(int index) => index switch
    {
        0 => (UiText.Connection, UiText.ConnectionSubtitle),
        1 => (UiText.RemoteScreen, UiText.ScreenSubtitle),
        2 => (UiText.Processes, UiText.ProcessesSubtitle),
        3 => (UiText.Files, UiText.FilesSubtitle),
        4 => (UiText.Diagnostics, UiText.DiagnosticsSubtitle),
        _ => (UiText.TakeControl, UiText.ConnectionSubtitle)
    };

    internal static bool ShouldShowTerminateSession(bool onAgent, bool agentIdle, bool agentConnected,
        string? agentState, bool supportSession, bool synchronizingAgent) => onAgent
            ? !agentIdle && (agentConnected || agentState == "reconnecting")
            : supportSession || synchronizingAgent;

    internal static bool IsOngoingUpdate(AgentUpdateProgress progress) => progress.Stage is not ("idle" or "complete");

    internal static bool CanUpdateClient(bool supportSession, bool hasClient, bool pairingBusy, bool clientUpdateBusy, bool terminating, bool clientUpToDate = false) =>
        supportSession && hasClient && !pairingBusy && !clientUpdateBusy && !terminating && !clientUpToDate;

    internal static bool CanUseControllerWorkspace(bool supportSession, bool terminating) => supportSession && !terminating;

    internal static int AvailableControllerPage(int requestedPage, bool supportSession, bool terminating) =>
        requestedPage == 0 || CanUseControllerWorkspace(supportSession, terminating) ? requestedPage : 0;

    internal static bool ShouldPreserveActiveSessionsOnRoleSwitch(
        int currentRole, int targetRole, bool agentRunning, bool controllerSessionActive) =>
        currentRole != targetRole && (agentRunning || controllerSessionActive);

    private enum SessionTerminationTarget { None, Agent, Controller }

    private static SessionTerminationTarget SelectTerminationTarget(bool onAgent, bool agentRunning, bool controllerSessionActive) =>
        onAgent ? agentRunning ? SessionTerminationTarget.Agent : SessionTerminationTarget.None :
        controllerSessionActive ? SessionTerminationTarget.Controller : SessionTerminationTarget.None;

    private static ProcessSortColumn ProcessColumn(int index) => index switch { 0 => ProcessSortColumn.Pid, 1 => ProcessSortColumn.Name, 2 => ProcessSortColumn.CpuPercentTotalMachine, 3 => ProcessSortColumn.WorkingSetBytes, 4 => ProcessSortColumn.Responding, _ => ProcessSortColumn.Window };
    private static FileSortColumn FileColumn(int index) => index switch { 0 => FileSortColumn.Name, 1 => FileSortColumn.Type, 2 => FileSortColumn.SizeBytes, _ => FileSortColumn.ModifiedUtc };
    private static string Pretty(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(Json.Options) { WriteIndented = true });
    private static void AddSummary(Forms.TableLayoutPanel table, int col, Func<string> title, Forms.Label value)
    {
        var stack = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2, Margin = new Forms.Padding(0, 0, 12, 0) };
        stack.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
        stack.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize)); stack.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
        stack.Controls.Add(new WorkspaceLabel { AutoSize = true, ForeColor = SecondaryText, Font = new Font("Segoe UI", 9.5F), Margin = new Forms.Padding(0, 0, 0, 4) }.WithText(title), 0, 0);
        value.Dock = Forms.DockStyle.Top; stack.Controls.Add(value, 0, 1); table.Controls.Add(stack, col, 0);
    }
    private static Forms.Label SummaryValue(string name) => new WorkspaceLabel { Name = name, Text = "—", AutoSize = true, ForeColor = PrimaryText, Font = new Font("Segoe UI", 13, FontStyle.Bold) };
    private static Forms.Label Eyebrow(string text) => new WorkspaceLabel() { Name = "agentEyebrow", Text = text, AutoSize = true, ForeColor = Teal, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold) };
    private static Forms.Label RailCaption(string text) => new() { Text = text, AutoSize = true, ForeColor = RailSecondary, Font = new Font("Segoe UI", 8.5F, FontStyle.Bold), Margin = new Forms.Padding(12, 0, 0, 8) };
    private static Forms.Button RailButton(string text, string name) => new WorkspaceButton() { Name = name, Text = text, AccessibleName = text, AutoSize = false, Width = 188, Height = 44, FlatStyle = Forms.FlatStyle.Flat, FlatAppearance = { BorderSize = 0 }, ForeColor = Color.White, BackColor = Rail, TextAlign = ContentAlignment.MiddleLeft, Padding = new Forms.Padding(16, 0, 0, 0), Margin = new Forms.Padding(0, 0, 0, 4), UseMnemonic = false };
    private static Forms.Button RailSubButton(string text, string name) => new WorkspaceButton() { Name = name, Text = text, AccessibleName = text, AutoSize = false, Width = 188, Height = 40, FlatStyle = Forms.FlatStyle.Flat, FlatAppearance = { BorderSize = 0 }, ForeColor = RailSecondary, BackColor = Rail, TextAlign = ContentAlignment.MiddleLeft, Padding = new Forms.Padding(16, 0, 0, 0), Margin = new Forms.Padding(0, 0, 0, 4), UseMnemonic = false };
    private static Forms.Button Button(string text, string name, int width = 0, bool primary = false, bool destructive = false) => new WorkspaceButton
    {
        Name = name, Text = text, AccessibleName = text, AutoSize = true, AutoSizeMode = Forms.AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(width > 0 ? width : 120, 38), Font = new Font("Segoe UI", 10), FlatStyle = Forms.FlatStyle.Flat,
        UseMnemonic = false, Padding = new Forms.Padding(12, 0, 12, 0),
        BackColor = destructive ? DestructiveBack : primary ? Teal : Surface,
        ForeColor = destructive ? DestructiveText : primary ? Color.White : PrimaryText,
        FlatAppearance = { BorderSize = primary ? 0 : 1, BorderColor = destructive ? Color.FromArgb(242, 219, 222) : Divider }
    };
    private static Forms.TextBox TextBox(string name) { var box = new Forms.TextBox { Name = name, AccessibleName = name, BorderStyle = Forms.BorderStyle.FixedSingle, BackColor = Surface, ForeColor = PrimaryText, Font = new Font("Segoe UI", 11) }; box.Enter += (_, _) => box.BackColor = Color.FromArgb(248, 253, 253); box.Leave += (_, _) => box.BackColor = Surface; return box; }
    private static Forms.Label Badge(string text, string name) => new() { Name = name, Text = "●  " + text, AutoSize = true, ForeColor = ConnectedText, BackColor = ConnectedBack, Font = new Font("Segoe UI", 8.5F, FontStyle.Bold), Padding = new Forms.Padding(8, 5, 8, 5), Visible = false };
    private void RefreshStatusPillRegion() => ControlRegions.ApplyRounded(statusPill, ref statusPillRegionSize, 16);
    internal static Size CalculateAgentScrollExtent(int viewportHeight, int contentHeight, int currentExtentHeight = 0)
    {
        if (viewportHeight <= 0) return Size.Empty;
        if (contentHeight > viewportHeight) return new Size(0, contentHeight);
        // Keep a visible scrollbar through a small DPI-rounding dead band so it cannot flash on and off.
        if (currentExtentHeight > 0 && contentHeight > viewportHeight - 8)
            return new Size(0, viewportHeight + 1);
        return Size.Empty;
    }

    private void UpdateMinimizedViewing()
    {
        if (WindowState == Forms.FormWindowState.Minimized)
        {
            if (liveStream != null) { resumeViewingAfterMinimize = true; StopStream(() => UiText.MinimizedConnected); }
        }
        else if (resumeViewingAfterMinimize && !trayVisible)
        {
            resumeViewingAfterMinimize = false;
            if (supportSession && controllerPages.SelectedIndex == 1) _ = StartStreamAsync();
        }
    }
    private static void UpdateAgentScrollExtent(Forms.Panel host, int height)
    {
        Size extent = CalculateAgentScrollExtent(host.ClientSize.Height, height, host.AutoScrollMinSize.Height);
        if (host.AutoScrollMinSize != extent) host.AutoScrollMinSize = extent;
    }
    private static Icon LoadApplicationIcon() { using Stream stream = typeof(MainForm).Assembly.GetManifestResourceStream("RemoteDebugger.Assets.RemoteDebugger.ico") ?? throw new InvalidOperationException("The application icon resource is missing."); using var icon = new Icon(stream); return (Icon)icon.Clone(); }
    private void PostUi(Action callback) { if (IsDisposed || !IsHandleCreated) return; try { BeginInvoke(callback); } catch (InvalidOperationException) { } }

    private sealed record QueuedInput(RemoteClient Client, int Generation, object Payload);
    private sealed record MonitorChoice(int Index, Func<string> Name) { public override string ToString() => Name(); }
    public static readonly Dictionary<string, string> Templates = new()
    {
        ["status"] = "{}", ["processes"] = "{}", ["process.info"] = "{\"pid\":1234}", ["start"] = "{\"path\":\"deployments/essai-1/MonApp.exe\",\"arguments\":[]}", ["stop"] = "{\"pid\":1234,\"mode\":\"graceful\"}", ["restart"] = "{\"pid\":1234,\"mode\":\"graceful\",\"arguments\":[]}",
        ["system"] = "{}", ["network"] = "{}", ["events"] = "{\"log\":\"Application\",\"count\":20}", ["services"] = "{}", ["windows"] = "{}", ["ui.inspect"] = "{\"pid\":1234}", ["ui.click"] = "{\"pid\":1234,\"automationId\":\"saveButton\"}", ["ui.key"] = "{\"pid\":1234,\"key\":\"CTRL+A\"}", ["debug.attach"] = "{\"pid\":1234,\"seconds\":3}", ["debug.dump"] = "{\"pid\":1234}",
        ["files"] = "{}", ["file.info"] = "{\"path\":\"deployments/essai-1/MonApp.exe\"}", ["command"] = "{\"file\":\"whoami.exe\",\"arguments\":[]}", ["maintenance.status"] = "{}", ["maintenance.session"] = "{\"file\":\"whoami.exe\",\"arguments\":[\"/groups\"]}", ["maintenance.elevated"] = "{\"file\":\"whoami.exe\",\"arguments\":[\"/groups\"]}", ["history"] = "{}"
    };
}

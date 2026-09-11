using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Threading.Channels;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

/// <summary>
/// The visible support workspace. The form deliberately keeps the safety-critical
/// session state in the header and footer while each page owns its working surface.
/// </summary>
public sealed class MainForm : Forms.Form
{
    private static readonly Color Canvas = Color.FromArgb(246, 248, 250);
    private static readonly Color Surface = Color.White;
    private static readonly Color Rail = Color.FromArgb(20, 38, 48);
    private static readonly Color RailSecondary = Color.FromArgb(168, 186, 194);
    private static readonly Color PrimaryText = Color.FromArgb(24, 48, 57);
    private static readonly Color SecondaryText = Color.FromArgb(99, 119, 128);
    private static readonly Color Divider = Color.FromArgb(223, 230, 234);
    private static readonly Color SelectedRail = Color.FromArgb(36, 68, 78);
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
    private readonly Forms.Label headerTitle = new() { AutoSize = true, ForeColor = PrimaryText };
    private readonly Forms.Label headerSubtitle = new() { AutoSize = true, ForeColor = SecondaryText };
    private readonly Forms.Panel statusPill = new() { Name = "connectionStatus", Height = 32, Width = 184 };
    private readonly Forms.Label statusDot = new() { AutoSize = true, Text = "●", Font = new Font("Segoe UI", 9), Margin = new Forms.Padding(10, 7, 4, 0) };
    private readonly Forms.Label statusLabel = new() { AutoSize = true, Font = new Font("Segoe UI", 9.5F), Margin = new Forms.Padding(0, 7, 8, 0) };
    private readonly Forms.Button terminateSession = Button("Terminer l’assistance", "terminateSession", destructive: true);
    private readonly Forms.Label footerLeft = new() { AutoSize = true, ForeColor = SecondaryText, Font = new Font("Segoe UI", 9.5F) };
    private readonly Forms.Label footerRight = new() { AutoSize = true, ForeColor = SecondaryText, Font = new Font("Segoe UI", 9.5F) };
    private readonly Forms.FlowLayoutPanel footerLeftFlow = new() { Dock = Forms.DockStyle.Fill, WrapContents = false, Padding = new Forms.Padding(24, 0, 0, 0), FlowDirection = Forms.FlowDirection.LeftToRight };
    private readonly Forms.FlowLayoutPanel footerRightFlow = new() { Dock = Forms.DockStyle.Fill, WrapContents = false, Padding = new Forms.Padding(0, 0, 24, 0), FlowDirection = Forms.FlowDirection.RightToLeft };

    private readonly Forms.Button roleAgent = RailButton("Donner le contrôle", "roleAgent");
    private readonly Forms.Button roleController = RailButton("Prendre le contrôle", "roleController");
    private readonly Forms.Label controllerNavCaption = RailCaption("ESPACE DE TRAVAIL");
    private readonly Forms.Button navConnection = RailSubButton("Connexion", "navConnection");
    private readonly Forms.Button navScreen = RailSubButton("Écran distant", "navScreen");
    private readonly Forms.Button navProcesses = RailSubButton("Processus", "navProcesses");
    private readonly Forms.Button navFiles = RailSubButton("Fichiers", "navFiles");
    private readonly Forms.Button navDiagnostics = RailSubButton("Diagnostics", "navDiagnostics");

    // Agent screen.
    private readonly Forms.Label agentPairCode = new() { Name = "agentPairCode", AutoSize = true, Text = "— — —", Font = new Font("Consolas", 42, FontStyle.Bold), ForeColor = PrimaryText };
    private readonly Forms.Button copyAgentCode = Button("Copier", "copyAgentCode", 86);
    private readonly Forms.ProgressBar pairingCountdown = new() { Name = "pairingCountdown", Minimum = 0, Maximum = 300, Value = 0, Height = 4, Style = Forms.ProgressBarStyle.Continuous };
    private readonly Forms.Label pairingCountdownText = new() { Name = "pairingCountdownText", AutoSize = true, ForeColor = SecondaryText };
    private readonly Forms.Label agentState = new() { Name = "agentState", AutoSize = true, ForeColor = PrimaryText };
    private readonly Forms.Label agentNetworkState = new() { Name = "agentNetworkState", AutoSize = true, ForeColor = SecondaryText };
    private readonly Forms.Label agentSleepState = new() { Name = "agentSleepState", AutoSize = true, ForeColor = SecondaryText };
    private readonly Forms.Label agentMaintenanceState = new() { Name = "agentMaintenanceState", AutoSize = true, ForeColor = SecondaryText };
    private readonly Forms.Label agentSessionNote = new() { AutoSize = true, ForeColor = SecondaryText, MaximumSize = new Size(620, 0) };
    private readonly Forms.Panel setupNotice = new() { Name = "agentSetupNotice", AutoSize = true, Visible = false, Padding = new Forms.Padding(12), BackColor = WarningBack };
    private readonly Forms.Label setupNoticeText = new() { AutoSize = true, ForeColor = WarningText, MaximumSize = new Size(440, 0) };
    private readonly Forms.Button preparePlatform = Button("Activer sur ce PC", "preparePlatform", 148);
    private readonly Forms.Label agentFingerprint = new() { Name = "agentFingerprint", AutoSize = true, ForeColor = SecondaryText, MaximumSize = new Size(720, 0) };
    private readonly Forms.Label agentLog = new() { Name = "agentLog", AutoSize = true, ForeColor = SecondaryText, MaximumSize = new Size(720, 0) };

    // Connection screen.
    private readonly Forms.ListView peers = new() { Name = "peers", Dock = Forms.DockStyle.Fill, View = Forms.View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, BorderStyle = Forms.BorderStyle.None, BackColor = Surface };
    private readonly Forms.TextBox host = TextBox("host");
    private readonly Forms.TextBox code = TextBox("pairCode");
    private readonly Forms.Button pairButton = Button("Connecter", "pair", 110, primary: true);
    private readonly Forms.Button discoverButton = Button("Actualiser", "discover", 92);
    private readonly Forms.Label connectionState = new() { Name = "connectionFormState", AutoSize = true, ForeColor = SecondaryText, MaximumSize = new Size(460, 0) };
    private readonly Forms.Label selectedPeerName = new() { AutoSize = true, Font = new Font("Segoe UI", 15, FontStyle.Bold), ForeColor = PrimaryText };
    private readonly Forms.Label selectedPeerAddress = new() { AutoSize = true, ForeColor = SecondaryText };
    private readonly Forms.Label discoveryState = new() { AutoSize = true, ForeColor = SecondaryText };
    private readonly List<Peer> discoveredPeers = [];
    private Peer? selectedPeer;
    private string selectedFingerprint = "";

    // Live screen.
    private readonly RemoteScreenView screen = new() { Name = "remoteScreen", Dock = Forms.DockStyle.Fill, SizeMode = Forms.PictureBoxSizeMode.Zoom, BackColor = Rail };
    private readonly Forms.Panel screenSurface = new() { Dock = Forms.DockStyle.Fill, BackColor = Rail, Padding = new Forms.Padding(0) };
    private readonly Forms.Label liveBadge = Badge("EN DIRECT", "liveBadge");
    private readonly Forms.Label streamOverlay = new() { Name = "streamOverlay", AutoSize = true, ForeColor = Color.White, BackColor = Color.FromArgb(190, 20, 38, 48), Padding = new Forms.Padding(10, 7, 10, 7), Text = "En attente d’une image fraîche…", Visible = true };
    private readonly Forms.Button pauseViewing = Button("Pause", "pauseViewing", 82);
    private readonly Forms.ComboBox monitor = new() { Name = "monitor", DropDownStyle = Forms.ComboBoxStyle.DropDownList, Width = 130 };
    private readonly Forms.CheckBox mouseEnabled = new() { Name = "mouseKeyboard", Text = "Contrôle souris et clavier", Checked = true, AutoSize = true, ForeColor = PrimaryText, Margin = new Forms.Padding(12, 10, 0, 0) };
    private readonly Forms.Label streamStatus = new() { Name = "streamStatus", AutoSize = true, ForeColor = SecondaryText };
    private readonly Forms.TextBox remoteText = TextBox("remoteText");
    private readonly Forms.Button typeText = Button("Saisir", "typeText", 76);
    private readonly Forms.Button enterKey = Button("Entrée", "enterKey", 76);
    private DesktopGeometry? geometry;
    private bool liveFrameFresh;
    private DateTimeOffset? lastFrameUtc;
    private CancellationTokenSource? liveStream;
    private DateTimeOffset? streamStartedUtc;
    private int streamFrames;
    private long streamBytes;

    // Processes and files.
    private readonly Forms.ListView processList = new() { Name = "processList", Dock = Forms.DockStyle.Fill, View = Forms.View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, BorderStyle = Forms.BorderStyle.None, BackColor = Surface };
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
    private readonly Forms.ListView fileList = new() { Name = "remoteFiles", Dock = Forms.DockStyle.Fill, View = Forms.View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, BorderStyle = Forms.BorderStyle.None, BackColor = Surface };
    private readonly Forms.Label fileState = new() { Name = "fileState", AutoSize = true, ForeColor = SecondaryText };
    private SortState<FileSortColumn> fileSort = new(FileSortColumn.Name, false);
    private readonly List<FileSortRow> fileRows = [];
    private string currentDirectory = "";
    private string? selectedFilePath;
    private readonly Forms.TextBox destination = TextBox("destination");

    // Diagnostics.
    private readonly Forms.ComboBox operations = new() { Name = "operation", DropDownStyle = Forms.ComboBoxStyle.DropDownList, Width = 190 };
    private readonly Forms.NumericUpDown pid = new() { Name = "targetPid", Maximum = int.MaxValue, Width = 90, Minimum = 0 };
    private readonly Forms.TextBox arguments = new() { Name = "arguments", Multiline = true, ScrollBars = Forms.ScrollBars.Vertical, Dock = Forms.DockStyle.Fill, Font = new Font("Consolas", 10), BackColor = Surface, BorderStyle = Forms.BorderStyle.FixedSingle };
    private readonly Forms.TextBox output = new() { Name = "output", Multiline = true, ReadOnly = true, ScrollBars = Forms.ScrollBars.Both, Dock = Forms.DockStyle.Fill, Font = new Font("Consolas", 9), WordWrap = false, BackColor = Surface, BorderStyle = Forms.BorderStyle.FixedSingle };
    private readonly Forms.Label diagnosticState = new() { Name = "diagnosticState", AutoSize = true, ForeColor = SecondaryText };
    private readonly Forms.Label technicalIdentity = new() { Name = "technicalIdentity", AutoSize = true, ForeColor = SecondaryText, MaximumSize = new Size(900, 0) };

    private readonly Channel<QueuedInput> inputQueue = Channel.CreateBounded<QueuedInput>(new BoundedChannelOptions(128) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private long lastMove;
    private readonly string root;
    private readonly bool loopbackOnly;
    private readonly bool startAgentOnLaunch;
    private readonly Forms.Timer renderTimer = new() { Interval = 250 };
    private readonly Forms.Timer discoveryTimer = new() { Interval = 30000 };
    private readonly Forms.NotifyIcon tray = new();
    private RemoteClient? client;
    private AgentServer? agent;
    private bool agentNetworkPrepared;
    private CancellationTokenSource? action;
    private CancellationTokenSource? heartbeatLifetime;
    private CancellationTokenSource? discoveryLifetime;
    private CancellationTokenSource? pairingLifetime;
    private Task? heartbeatTask;
    private PowerHold? powerHold;
    private bool heartbeatHealthy;
    private bool supportSession;
    private bool pairingBusy;
    private bool terminating;
    private bool quitting;
    private bool shutdownStarted;
    private bool suppressTerminationEvent;
    private bool trayVisible;
    private int operationGeneration;
    private int sessionGeneration;
    private string? operationId;
    private DateTimeOffset? lastMeasurementUtc;
    private string footerMessage = "Prêt à recevoir une connexion";
    private string footerDetail = "Fermer la fenêtre quitte l’agent";

    public string? CurrentPairingCode { get; private set; }
    public AgentServer? Agent => agent;
    public bool AgentNetworkReady => loopbackOnly || agentNetworkPrepared;
    public RemoteClient? Client => client;

    public MainForm(bool startAgent = true, string? dataRoot = null, bool loopbackOnly = false)
    {
        root = dataRoot ?? Vault.DefaultRoot;
        this.loopbackOnly = loopbackOnly;
        startAgentOnLaunch = startAgent;

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

        BuildShell();
        BuildAgentPage();
        BuildControllerPages();
        BuildTray();
        WireEvents();
        LoadSavedConnection();
        SupportPlatform.ManagedRelaunchRequested += OnManagedRelaunchRequested;

        renderTimer.Tick += (_, _) => RefreshUiState();
        discoveryTimer.Tick += async (_, _) => await DiscoverAsync(false);
        Shown += MainFormShown;
        FormClosing += MainFormClosing;
        FormClosed += (_, _) => DisposeResources();
    }

    private void BuildShell()
    {
        shell.Padding = Forms.Padding.Empty;
        shell.BackColor = Canvas;
        shell.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Absolute, 208));
        shell.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
        shell.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 84));
        shell.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100));
        shell.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 40));
        shell.Controls.Add(rail, 0, 0);
        shell.SetRowSpan(rail, 3);
        shell.Controls.Add(header, 1, 0);
        shell.Controls.Add(rolePages, 1, 1);
        shell.Controls.Add(footer, 1, 2);
        Controls.Add(shell);

        BuildRail();
        BuildHeader();
        BuildFooter();
    }

    private void BuildRail()
    {
        var layout = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Forms.Padding(12, 0, 12, 8), BackColor = Rail };
        layout.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 96));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 112));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 44));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 62));

        var brand = new Forms.Panel { Dock = Forms.DockStyle.Fill };
        var mark = new Forms.Label { Text = "RD", AutoSize = false, Width = 34, Height = 34, TextAlign = ContentAlignment.MiddleCenter, BackColor = Teal, ForeColor = Color.White, Font = new Font("Segoe UI", 10, FontStyle.Bold), Location = new Point(10, 25) };
        mark.Region = RoundedRegion(mark.Size, 6);
        var brandName = new Forms.Label { Text = "Remote\nDebugger", AutoSize = false, Width = 128, Height = 54, Location = new Point(54, 18), ForeColor = Color.White, Font = new Font("Segoe UI", 14, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
        brand.Controls.Add(mark); brand.Controls.Add(brandName);
        layout.Controls.Add(brand, 0, 0);

        var roles = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, FlowDirection = Forms.FlowDirection.TopDown, WrapContents = false, Padding = new Forms.Padding(0), BackColor = Rail };
        roles.Controls.Add(roleAgent); roles.Controls.Add(roleController);
        layout.Controls.Add(roles, 0, 1);

        var work = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, FlowDirection = Forms.FlowDirection.TopDown, WrapContents = false, Padding = new Forms.Padding(0, 14, 0, 0), BackColor = Rail };
        work.Controls.Add(controllerNavCaption);
        work.Controls.Add(navConnection); work.Controls.Add(navScreen); work.Controls.Add(navProcesses); work.Controls.Add(navFiles); work.Controls.Add(navDiagnostics);
        layout.Controls.Add(work, 0, 2);

        var local = new Forms.Panel { Dock = Forms.DockStyle.Fill };
        var machine = new Forms.Label { Text = Environment.MachineName, AutoSize = false, Width = 180, Height = 20, ForeColor = RailSecondary, Font = new Font("Segoe UI", 9.5F), Location = new Point(12, 8), AutoEllipsis = true };
        var version = new Forms.Label { Text = "Remote Debugger · " + (typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "0.2"), AutoSize = false, Width = 180, Height = 20, ForeColor = Color.FromArgb(116, 143, 154), Font = new Font("Segoe UI", 8.5F), Location = new Point(12, 32), AutoEllipsis = true };
        local.Controls.Add(machine); local.Controls.Add(version); layout.Controls.Add(local, 0, 4);
        rail.Controls.Add(layout);
    }

    private void BuildHeader()
    {
        var layout = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Forms.Padding(28, 0, 28, 0) };
        layout.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
        layout.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.AutoSize));
        var titles = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Forms.Padding(0, 14, 0, 0) };
        titles.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 34)); titles.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 24));
        headerTitle.Font = new Font("Segoe UI", 22, FontStyle.Bold); headerSubtitle.Font = new Font("Segoe UI", 10.5F);
        titles.Controls.Add(headerTitle, 0, 0); titles.Controls.Add(headerSubtitle, 0, 1);
        var actions = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, FlowDirection = Forms.FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Padding = new Forms.Padding(0, 20, 0, 0) };
        statusPill.Width = 200; statusPill.Height = 30; statusPill.Margin = new Forms.Padding(0, 3, 12, 0);
        terminateSession.Margin = Forms.Padding.Empty;
        statusDot.Location = new Point(12, 7); statusLabel.Location = new Point(28, 6); statusPill.Controls.Add(statusDot); statusPill.Controls.Add(statusLabel); statusPill.Region = RoundedRegion(statusPill.Size, 15);
        actions.Controls.Add(statusPill); actions.Controls.Add(terminateSession);
        layout.Controls.Add(titles, 0, 0); layout.Controls.Add(actions, 1, 0);
        header.Controls.Add(layout);
        var line = new Forms.Panel { Dock = Forms.DockStyle.Bottom, Height = 1, BackColor = Divider }; header.Controls.Add(line);
    }

    private void BuildFooter()
    {
        var line = new Forms.Panel { Dock = Forms.DockStyle.Top, Height = 1, BackColor = Divider }; footer.Controls.Add(line);
        var layout = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        layout.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 58)); layout.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 42));
        footerLeft.Margin = Forms.Padding.Empty; footerRight.Margin = Forms.Padding.Empty;
        footerLeftFlow.Padding = new Forms.Padding(24, 8, 0, 0); footerRightFlow.Padding = new Forms.Padding(0, 8, 24, 0);
        footerLeftFlow.Controls.Add(footerLeft); footerRightFlow.Controls.Add(footerRight); layout.Controls.Add(footerLeftFlow, 0, 0); layout.Controls.Add(footerRightFlow, 1, 0); footer.Controls.Add(layout);
    }

    private void BuildAgentPage()
    {
        var page = new PagePanel("Donner le contrôle") { BackColor = Canvas, Padding = new Forms.Padding(0) };
        var content = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 1, RowCount = 1, Padding = new Forms.Padding(28, 48, 28, 20) };
        content.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
        var heroHost = new Forms.Panel { Dock = Forms.DockStyle.Fill, BackColor = Canvas };
        var hero = BuildAgentContent(); hero.Dock = Forms.DockStyle.Top; hero.Width = 760; hero.Anchor = Forms.AnchorStyles.Top | Forms.AnchorStyles.Left;
        heroHost.Controls.Add(hero); content.Controls.Add(heroHost, 0, 0);
        page.Controls.Add(content); rolePages.TabPages.Add(page);
    }

    private Forms.Control BuildAgentContent()
    {
        var panel = new Forms.Panel { Dock = Forms.DockStyle.Top, Width = 760, AutoSize = true, AutoSizeMode = Forms.AutoSizeMode.GrowAndShrink };
        var layout = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Top, Width = 760, AutoSize = true, AutoSizeMode = Forms.AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 11, Margin = Forms.Padding.Empty, Padding = Forms.Padding.Empty };
        layout.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 20));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 50));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 26));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 78));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 8));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 24));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 38));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 90));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 66));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 30));
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));

        var eyebrow = Eyebrow("CODE DE CONNEXION"); eyebrow.Dock = Forms.DockStyle.Fill; layout.Controls.Add(eyebrow, 0, 0);
        var title = new Forms.Label { Text = "Partagez ce code", AutoSize = false, Dock = Forms.DockStyle.Fill, Font = new Font("Segoe UI", 32, FontStyle.Bold), ForeColor = PrimaryText, TextAlign = ContentAlignment.MiddleLeft };
        layout.Controls.Add(title, 0, 1);
        var subtitle = new Forms.Label { Text = "Saisissez-le sur le PC qui vous assiste.", AutoSize = false, Dock = Forms.DockStyle.Fill, Font = new Font("Segoe UI", 13), ForeColor = SecondaryText, TextAlign = ContentAlignment.MiddleLeft };
        layout.Controls.Add(subtitle, 0, 2);

        var codeRow = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, Height = 78, WrapContents = false, FlowDirection = Forms.FlowDirection.LeftToRight, Padding = new Forms.Padding(0, 10, 0, 0), Margin = Forms.Padding.Empty };
        agentPairCode.Margin = new Forms.Padding(0, 0, 14, 0); copyAgentCode.Margin = new Forms.Padding(0, 4, 0, 0); codeRow.Controls.Add(agentPairCode); codeRow.Controls.Add(copyAgentCode); layout.Controls.Add(codeRow, 0, 3);
        pairingCountdown.Width = 480; pairingCountdown.Height = 4; pairingCountdown.Margin = new Forms.Padding(0, 2, 0, 0); layout.Controls.Add(pairingCountdown, 0, 4);
        pairingCountdownText.AutoSize = false; pairingCountdownText.Dock = Forms.DockStyle.Fill; pairingCountdownText.Margin = Forms.Padding.Empty; layout.Controls.Add(pairingCountdownText, 0, 5);

        var divider = new Forms.Panel { Dock = Forms.DockStyle.Top, Height = 1, BackColor = Divider, Margin = new Forms.Padding(0, 18, 0, 0) }; layout.Controls.Add(divider, 0, 6);
        var states = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, Height = 90, ColumnCount = 3, RowCount = 3, Margin = Forms.Padding.Empty };
        states.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Absolute, 22)); states.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 65)); states.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 35));
        for (int row = 0; row < 3; row++) states.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 33.333F));
        AddAgentStateRow(states, 0, "Réseau privé", agentNetworkState); AddAgentStateRow(states, 1, "Mise en veille", agentSleepState); AddAgentStateRow(states, 2, "Maintenance admin", agentMaintenanceState); layout.Controls.Add(states, 0, 7);

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

    private static void AddAgentStateRow(Forms.TableLayoutPanel table, int row, string label, Forms.Label value)
    {
        var dot = new Forms.Label { AutoSize = true, Text = "●", ForeColor = Color.FromArgb(50, 137, 91), Margin = new Forms.Padding(0, 3, 0, 0) };
        var name = new Forms.Label { AutoSize = true, Text = label, ForeColor = SecondaryText, Margin = new Forms.Padding(0, 3, 0, 0) };
        value.Margin = new Forms.Padding(0, 3, 0, 0); value.Anchor = Forms.AnchorStyles.Left;
        table.Controls.Add(dot, 0, row); table.Controls.Add(name, 1, row); table.Controls.Add(value, 2, row);
    }

    private void BuildControllerPages()
    {
        var page = new PagePanel("Prendre le contrôle") { BackColor = Canvas };
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
        var page = new PagePanel("Connexion") { BackColor = Canvas, Padding = new Forms.Padding(28) };
        var title = new Forms.Label { Text = "Prendre le contrôle", AutoSize = true, Font = new Font("Segoe UI", 22, FontStyle.Bold), ForeColor = PrimaryText };
        var sub = new Forms.Label { Text = "Choisissez un PC puis saisissez son code.", AutoSize = true, ForeColor = SecondaryText, Margin = Forms.Padding.Empty };
        var columns = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        columns.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Absolute, 360)); columns.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Absolute, 1)); columns.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
        columns.Controls.Add(BuildPeerList(), 0, 0); columns.Controls.Add(new Forms.Panel { Dock = Forms.DockStyle.Fill, BackColor = Divider }, 1, 0); columns.Controls.Add(BuildConnectionForm(), 2, 0);
        var layout = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 1, RowCount = 3 }; layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 34)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 34)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100));
        layout.Controls.Add(title, 0, 0); layout.Controls.Add(sub, 0, 1); layout.Controls.Add(columns, 0, 2); page.Controls.Add(layout); return page;
    }

    private Forms.Control BuildPeerList()
    {
        var panel = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Forms.Padding(0, 0, 24, 0) };
        panel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 38)); panel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 28)); panel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100));
        var toolbar = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 2 }; toolbar.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100)); toolbar.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.AutoSize));
        toolbar.Controls.Add(new Forms.Label { Text = "PC disponibles", AutoSize = true, Font = new Font("Segoe UI", 15, FontStyle.Bold), ForeColor = PrimaryText, Anchor = Forms.AnchorStyles.Left }, 0, 0); toolbar.Controls.Add(discoverButton, 1, 0);
        panel.Controls.Add(toolbar, 0, 0); panel.Controls.Add(discoveryState, 0, 1);
        peers.Columns.Add("Nom", 140); peers.Columns.Add("Adresse", 110); peers.Columns.Add("État", 65); peers.Resize += (_, _) => peers.Columns[0].Width = Math.Max(80, peers.ClientSize.Width - Forms.SystemInformation.VerticalScrollBarWidth - 175); panel.Controls.Add(peers, 0, 2);
        return panel;
    }

    private Forms.Control BuildConnectionForm()
    {
        var panel = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Forms.Padding(32, 0, 0, 0) };
        panel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 30)); panel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 25)); panel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 25)); panel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 46)); panel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 25)); panel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 46)); panel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 50)); panel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100));
        panel.Controls.Add(selectedPeerName, 0, 0); panel.Controls.Add(selectedPeerAddress, 0, 1); panel.Controls.Add(new Forms.Label { Text = "Adresse IP (ou saisie manuelle)", AutoSize = true, ForeColor = SecondaryText, Margin = Forms.Padding.Empty, Dock = Forms.DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
        host.Height = 40; host.Dock = Forms.DockStyle.Top; panel.Controls.Add(host, 0, 3);
        panel.Controls.Add(new Forms.Label { Text = "Code à 6 chiffres", AutoSize = true, ForeColor = SecondaryText, Margin = Forms.Padding.Empty, Dock = Forms.DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 4);
        var codeRow = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, WrapContents = false, FlowDirection = Forms.FlowDirection.LeftToRight, Margin = new Forms.Padding(0) };
        code.Width = 160; code.Height = 40; code.Font = new Font("Consolas", 20); code.MaxLength = 6; code.TextAlign = Forms.HorizontalAlignment.Center; code.Margin = new Forms.Padding(0, 0, 12, 0); codeRow.Controls.Add(code); codeRow.Controls.Add(pairButton); panel.Controls.Add(codeRow, 0, 5);
        connectionState.Margin = new Forms.Padding(0, 7, 0, 0); panel.Controls.Add(connectionState, 0, 6); return panel;
    }

    private PagePanel BuildScreenPage()
    {
        var page = new PagePanel("Écran distant") { BackColor = Canvas, Padding = new Forms.Padding(28, 12, 28, 16) };
        var top = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Top, ColumnCount = 5, RowCount = 1, Height = 42 };
        top.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.AutoSize)); top.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.AutoSize)); top.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.AutoSize)); top.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100)); top.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.AutoSize));
        monitor.Items.Add(new MonitorChoice(0, "Principal")); monitor.SelectedIndex = 0;
        top.Controls.Add(new Forms.Label { Text = "Écran", AutoSize = true, ForeColor = SecondaryText, Anchor = Forms.AnchorStyles.Left, Margin = new Forms.Padding(0, 10, 12, 0) }, 0, 0); top.Controls.Add(monitor, 1, 0); top.Controls.Add(mouseEnabled, 2, 0); top.Controls.Add(new Forms.Label { Text = "", AutoSize = true }, 3, 0); top.Controls.Add(pauseViewing, 4, 0);
        screenSurface.Controls.Add(screen); screenSurface.Controls.Add(liveBadge); screenSurface.Controls.Add(streamOverlay); liveBadge.BringToFront(); streamOverlay.BringToFront(); liveBadge.Location = new Point(16, 14); streamOverlay.Anchor = Forms.AnchorStyles.None; screenSurface.Resize += (_, _) => streamOverlay.Location = new Point(Math.Max(0, (screenSurface.Width - streamOverlay.Width) / 2), Math.Max(0, (screenSurface.Height - streamOverlay.Height) / 2));
        var view = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 1, RowCount = 2 }; view.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100)); view.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 42)); view.Controls.Add(screenSurface, 0, 0);
        var bottom = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, FlowDirection = Forms.FlowDirection.LeftToRight, WrapContents = false, Padding = new Forms.Padding(0, 8, 0, 0) }; remoteText.Width = 390; remoteText.Height = 32; bottom.Controls.Add(remoteText); bottom.Controls.Add(typeText); bottom.Controls.Add(enterKey); bottom.Controls.Add(streamStatus); view.Controls.Add(bottom, 0, 1);
        page.Controls.Add(view); page.Controls.Add(top); return page;
    }

    private PagePanel BuildProcessPage()
    {
        var page = new PagePanel("Processus") { BackColor = Canvas, Padding = new Forms.Padding(28) };
        var title = SectionTitle("Processus", "Mesure CPU, mémoire et activité des applications distantes.");
        refreshResourcesButton = Button("Actualiser", "refreshResources", 100, primary: true);
        var action = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, FlowDirection = Forms.FlowDirection.LeftToRight, WrapContents = false }; action.Controls.Add(refreshResourcesButton); action.Controls.Add(resourceState);
        var summary = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 4, RowCount = 1, Height = 62, Padding = new Forms.Padding(0, 10, 0, 4) };
        for (int i = 0; i < 4; i++) summary.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 25));
        AddSummary(summary, 0, "CPU", cpuSummary); AddSummary(summary, 1, "RAM", ramSummary); AddSummary(summary, 2, "Processus", processSummary); AddSummary(summary, 3, "Mesuré à", resourceMeasuredAt);
        processList.Columns.Add("PID", 80, Forms.HorizontalAlignment.Right); processList.Columns.Add("Application", 240); processList.Columns.Add("CPU %", 100, Forms.HorizontalAlignment.Right); processList.Columns.Add("RAM Mio", 110, Forms.HorizontalAlignment.Right); processList.Columns.Add("Réponse", 100); processList.Columns.Add("Fenêtre", 300);
        var layout = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 1, RowCount = 5 }; layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 50)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 44)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 62)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 28)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100)); layout.Controls.Add(title, 0, 0); layout.Controls.Add(action, 0, 1); layout.Controls.Add(summary, 0, 2); layout.Controls.Add(volumeSummary, 0, 3); layout.Controls.Add(processList, 0, 4); page.Controls.Add(layout); return page;
    }

    private PagePanel BuildFilePage()
    {
        var page = new PagePanel("Fichiers") { BackColor = Canvas, Padding = new Forms.Padding(28) };
        var title = SectionTitle("Fichiers", "Parcourez l’espace de travail distant et vérifiez les transferts.");
        var pathRow = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 3, RowCount = 2, Height = 82 }; pathRow.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100)); pathRow.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.AutoSize)); pathRow.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.AutoSize));
        fileDirectory.ReadOnly = false; fileDirectory.Height = 38; fileDirectory.Dock = Forms.DockStyle.Top; pathRow.Controls.Add(fileDirectory, 0, 0); uploadButton = Button("Téléverser un fichier", "upload", 150); var parent = Button("Parent", "parentFolder", 78); var refresh = Button("Actualiser", "browseFiles", 92); pathRow.Controls.Add(parent, 1, 0); pathRow.Controls.Add(refresh, 2, 0); pathRow.Controls.Add(new Forms.Label { Text = "Fichier sélectionné", AutoSize = true, ForeColor = SecondaryText, Margin = new Forms.Padding(0, 9, 8, 0) }, 0, 1); remotePath.ReadOnly = true; remotePath.Dock = Forms.DockStyle.Fill; pathRow.Controls.Add(remotePath, 1, 1); pathRow.SetColumnSpan(remotePath, 2);
        destination.Text = "deployments/"; destination.Width = 240; destination.Height = 32; destination.Dock = Forms.DockStyle.None;
        uploadFolderButton = Button("Téléverser un dossier", "uploadFolder", 160); downloadButton = Button("Télécharger", "download", 102);
        var actions = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, FlowDirection = Forms.FlowDirection.LeftToRight, WrapContents = false, Height = 42 }; actions.Controls.Add(uploadButton); actions.Controls.Add(uploadFolderButton); actions.Controls.Add(downloadButton); actions.Controls.Add(new Forms.Label { Text = "Destination", AutoSize = true, ForeColor = SecondaryText, Margin = new Forms.Padding(16, 10, 4, 0) }); actions.Controls.Add(destination); actions.Controls.Add(fileState);
        fileList.Columns.Add("Nom", 330); fileList.Columns.Add("Type", 100); fileList.Columns.Add("Taille", 105, Forms.HorizontalAlignment.Right); fileList.Columns.Add("Modifié", 220);
        var layout = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 1, RowCount = 5 }; layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 50)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 82)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 48)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 12)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100)); layout.Controls.Add(title, 0, 0); layout.Controls.Add(pathRow, 0, 1); layout.Controls.Add(actions, 0, 2); layout.Controls.Add(new Forms.Panel { Dock = Forms.DockStyle.Fill }, 0, 3); layout.Controls.Add(fileList, 0, 4); page.Controls.Add(layout);
        parent.Click += async (_, _) => { currentDirectory = ParentPath(currentDirectory); selectedFilePath = null; remotePath.Clear(); fileDirectory.Text = currentDirectory; await BrowseFilesAsync(); }; refresh.Click += async (_, _) => await BrowseFilesAsync(); fileDirectory.KeyDown += async (_, e) => { if (e.KeyCode == Forms.Keys.Enter) { e.SuppressKeyPress = true; currentDirectory = fileDirectory.Text.Trim(); selectedFilePath = null; remotePath.Clear(); await BrowseFilesAsync(); } };
        return page;
    }

    private PagePanel BuildDiagnosticsPage()
    {
        var page = new PagePanel("Diagnostics") { BackColor = Canvas, Padding = new Forms.Padding(28) };
        var title = SectionTitle("Diagnostics", "Actions avancées, résultats JSON et preuves d’exécution.");
        executeButton = Button("Exécuter", "execute", 92, primary: true); cancelButton = Button("Annuler", "cancel", 82);
        var top = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, FlowDirection = Forms.FlowDirection.LeftToRight, WrapContents = false, Height = 44 }; top.Controls.Add(new Forms.Label { Text = "Action", AutoSize = true, ForeColor = SecondaryText, Margin = new Forms.Padding(0, 10, 5, 0) }); top.Controls.Add(operations); top.Controls.Add(new Forms.Label { Text = "PID", AutoSize = true, ForeColor = SecondaryText, Margin = new Forms.Padding(12, 10, 5, 0) }); top.Controls.Add(pid); top.Controls.Add(executeButton); top.Controls.Add(cancelButton); top.Controls.Add(diagnosticState);
        var identity = new Forms.Panel { Dock = Forms.DockStyle.Fill, Padding = new Forms.Padding(0, 8, 0, 0) }; technicalIdentity.Text = "Identité technique disponible après connexion."; identity.Controls.Add(technicalIdentity);
        operations.Items.AddRange(Templates.Keys.Cast<object>().ToArray()); operations.SelectedIndex = 0;
        var layout = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 1, RowCount = 5 }; layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 50)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 44)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 40)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 40)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 60)); layout.Controls.Add(title, 0, 0); layout.Controls.Add(top, 0, 1); layout.Controls.Add(arguments, 0, 2); layout.Controls.Add(identity, 0, 3); layout.Controls.Add(output, 0, 4); page.Controls.Add(layout); return page;
    }

    private void BuildTray()
    {
        tray.Icon = Icon; tray.Text = "Remote Debugger"; tray.Visible = false;
        var menu = new Forms.ContextMenuStrip(); menu.Items.Add("Ouvrir", null, (_, _) => RestoreFromTray()); menu.Items.Add("Terminer l’assistance", null, async (_, _) => await TerminateSupportAsync()); menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("Quitter", null, (_, _) => RequestQuit()); tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void WireEvents()
    {
        roleAgent.Click += (_, _) => SelectRole(0);
        roleController.Click += (_, _) => { SelectRole(1); _ = DiscoverAsync(false); };
        navConnection.Click += (_, _) => SelectControllerPage(0); navScreen.Click += (_, _) => SelectControllerPage(1); navProcesses.Click += (_, _) => SelectControllerPage(2); navFiles.Click += (_, _) => SelectControllerPage(3); navDiagnostics.Click += (_, _) => SelectControllerPage(4);
        terminateSession.Click += async (_, _) => await TerminateSupportAsync();
        copyAgentCode.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(CurrentPairingCode)) Forms.Clipboard.SetText(CurrentPairingCode); footerMessage = "Code copié"; RefreshFooter(); };
        preparePlatform.Click += async (_, _) => await ProvisionPlatformAsync();
        discoverButton.Click += async (_, _) => await DiscoverAsync(true);
        peers.SelectedIndexChanged += (_, _) => SelectPeerFromList();
        host.TextChanged += (_, _) => { if (selectedPeer?.Host != host.Text.Trim()) { selectedPeer = null; selectedFingerprint = ""; selectedPeerName.Text = "PC à saisir"; selectedPeerAddress.Text = "La vérification d’identité est liée au code de connexion."; } };
        code.KeyPress += (_, e) => { if (!char.IsControl(e.KeyChar) && !char.IsAsciiDigit(e.KeyChar)) e.Handled = true; };
        code.KeyDown += async (_, e) => { if (e.KeyCode == Forms.Keys.Enter) { e.SuppressKeyPress = true; await PairSelectedAsync(); } };
        pairButton.Click += async (_, _) => await PairSelectedAsync();
        pauseViewing.Click += (_, _) => { if (liveStream == null) _ = StartStreamAsync(); else StopStream("Vision en pause"); };
        monitor.SelectedIndexChanged += (_, _) => { if (liveStream != null) { StopStream("Moniteur modifié"); _ = StartStreamAsync(); } };
        mouseEnabled.CheckedChanged += (_, _) => { if (!mouseEnabled.Checked) QueueInput(new { kind = "release" }); };
        screen.MouseDown += (_, e) => { screen.Focus(); QueueMouse("down", e); }; screen.MouseUp += (_, e) => QueueMouse("up", e); screen.MouseMove += (_, e) => { long now = Environment.TickCount64; if (now - lastMove < 33) return; lastMove = now; QueueMouse("move", e); }; screen.MouseWheel += (_, e) => QueueMouse("wheel", e); screen.PreviewKeyDown += (_, e) => e.IsInputKey = true; screen.KeyDown += (_, e) => { if (!CanSendInput()) return; e.SuppressKeyPress = true; QueueInput(new { kind = "keyDown", virtualKey = (int)e.KeyCode }); }; screen.KeyUp += (_, e) => { if (!CanSendInput()) return; e.SuppressKeyPress = true; QueueInput(new { kind = "keyUp", virtualKey = (int)e.KeyCode }); }; screen.LostFocus += (_, _) => ReleaseHeldInputForCurrentSession();
        typeText.Click += async (_, _) => await ExecuteAsync("ui.text", new { pid = (int)pid.Value, text = remoteText.Text }); enterKey.Click += async (_, _) => await ExecuteAsync("ui.key", new { pid = (int)pid.Value, key = "ENTER" });
        processList.ColumnClick += (_, e) => { processSort = processSort.Toggle(ProcessColumn(e.Column)); RenderProcesses(); }; processList.SelectedIndexChanged += (_, _) => { if (processList.SelectedItems.Count > 0 && processList.SelectedItems[0].Tag is ProcessSortRow row) { pid.Value = row.Pid; } };
        fileList.ColumnClick += (_, e) => { fileSort = fileSort.Toggle(FileColumn(e.Column)); RenderFiles(); }; fileList.SelectedIndexChanged += (_, _) => { if (fileList.SelectedItems.Count > 0 && fileList.SelectedItems[0].Tag is FileSortRow row && !row.IsDirectory) { selectedFilePath = row.Path; remotePath.Text = row.Path; } }; fileList.DoubleClick += async (_, _) => { if (fileList.SelectedItems.Count == 0 || fileList.SelectedItems[0].Tag is not FileSortRow row) return; if (row.IsDirectory) { currentDirectory = row.Path; selectedFilePath = null; remotePath.Clear(); fileDirectory.Text = currentDirectory; await BrowseFilesAsync(); } };
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
        try
        {
            client = RemoteClient.Load();
            host.Text = client.Connection.Host;
            selectedFingerprint = client.Connection.Fingerprint;
            selectedPeerName.Text = "Connexion enregistrée";
            selectedPeerAddress.Text = client.Connection.Host;
        }
        catch { client = null; }
    }

    private async void MainFormShown(object? sender, EventArgs e)
    {
        renderTimer.Start();
        discoveryTimer.Start();
        _ = PumpInputAsync();
        SelectRole(startAgentOnLaunch ? 0 : 1);
        if (!startAgentOnLaunch) _ = DiscoverAsync(false);
        await Task.Yield();
    }

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
            agent = new AgentServer(root, loopbackOnly: loopbackOnly || !agentNetworkPrepared);
            agent.Status += text => PostUi(() => { agentLog.Text = text; RefreshFooter(); });
            agent.TerminationRequested += () => { if (!suppressTerminationEvent && !quitting) PostUi(TerminateAgentFromRemote); };
            agent.Start();
            powerHold ??= PowerHold.Acquire();
            technicalIdentity.Text = "Empreinte technique de cet agent : " + agent.Fingerprint;
            agentFingerprint.Text = agent.Fingerprint;
            footerDetail = "Fermer la fenêtre quitte l’agent";
            RefreshUiState();
        }
        catch (Exception ex)
        {
            agent?.Dispose(); agent = null; agentState.Text = "Préparation impossible : " + ex.Message; footerMessage = "Agent indisponible"; RefreshFooter();
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
        catch (Exception ex) { footerMessage = "Arrêt de l’agent incomplet"; footerDetail = ex.Message; RefreshFooter(); return false; }
        finally { suppressTerminationEvent = false; }
        if (ReferenceEquals(agent, local)) agent = null;
        powerHold?.Dispose(); powerHold = null; CurrentPairingCode = null; RefreshUiState();
        return true;
    }

    private async Task PrepareAgentAsync()
    {
        AgentServer? preparing = agent;
        if (preparing == null) return;
        if (loopbackOnly) { agentNetworkState.Text = "Local uniquement"; return; }
        try
        {
            if (await SupportPlatform.TryRelaunchManagedAgentAsync(Environment.GetCommandLineArgs().Skip(1).ToArray()))
            {
                OnManagedRelaunchRequested(); return;
            }
            SupportPlatformStatus status = await SupportPlatform.PrepareAsync(requireFirewall: true);
            if (!ReferenceEquals(agent, preparing) || quitting) return;
            if (status.Available && status.FirewallReady && !agentNetworkPrepared)
            {
                agentNetworkPrepared = true;
                // No external controller can have paired on the pending
                // loopback listener. Preserve a planned-update resume record.
                preparing.Dispose(); agent = null; StartAgent();
            }
            ApplyPlatformStatus(status);
        }
        catch (Exception ex) { ShowSetupNotice("Préparation automatique impossible : " + ex.Message); }
    }

    private async Task ProvisionPlatformAsync()
    {
        preparePlatform.Enabled = false;
        try { ApplyPlatformStatus(await SupportPlatform.ProvisionAsync()); if (!quitting) await PrepareAgentAsync(); }
        catch (Exception ex) { ShowSetupNotice("Activation impossible : " + ex.Message); }
        finally { preparePlatform.Enabled = true; }
    }

    private void ApplyPlatformStatus(SupportPlatformStatus status)
    {
        agentNetworkState.Text = status.FirewallReady ? "Prêt" : status.RequiresAdministratorConsent ? "Activation requise" : "Indisponible";
        agentNetworkState.ForeColor = status.FirewallReady ? ConnectedText : WarningText;
        agentSleepState.Text = powerHold != null ? "Suspendue" : "Active";
        agentSleepState.ForeColor = powerHold != null ? PrimaryText : SecondaryText;
        if (status.RequiresAdministratorConsent) ShowSetupNotice("Activez l’assistance sur ce PC. Une autorisation Windows est nécessaire une seule fois.");
        else if (status.Available || status.Provisioned) HideSetupNotice();
        output.Text = Pretty(status);
        footerMessage = status.FirewallReady ? "Prêt à recevoir une connexion" : "Activez ce PC pour autoriser le réseau privé"; RefreshFooter();
    }

    private void ShowSetupNotice(string message)
    {
        setupNoticeText.Text = string.IsNullOrWhiteSpace(message) ? "Une autorisation Windows est nécessaire une seule fois sur ce PC." : message;
        setupNotice.Visible = true;
    }

    private void HideSetupNotice() => setupNotice.Visible = false;

    private void RefreshUiState()
    {
        if (IsDisposed) return;
        UpdateAgentState(); UpdateHeader(); RefreshFooter();
        if (supportSession && liveStream != null && lastFrameUtc is { } presented && DateTimeOffset.UtcNow - presented > TimeSpan.FromSeconds(3))
        {
            // A frozen bitmap must never continue to look like a live view or
            // remain eligible for remote input after its freshness window.
            liveFrameFresh = false;
            liveBadge.Visible = false;
            streamOverlay.Text = "Image figée · en attente d’une capture fraîche…";
            streamOverlay.Visible = true;
        }
        if (agent?.Operations.Maintenance is { } maintenance)
        {
            var state = Json.Element(maintenance.Status); bool active = state.TryGetProperty("active", out var a) && a.GetBoolean(); bool brokerAvailable = !state.TryGetProperty("brokerAvailable", out var broker) || broker.GetBoolean(); bool requiresProvisioning = state.TryGetProperty("requiresProvisioning", out var provisioning) && provisioning.GetBoolean();
            agentMaintenanceState.Text = active ? "Active" : !brokerAvailable || requiresProvisioning ? "Indisponible" : agent?.Session.HasPaired == true ? "Préparation…" : "Après connexion";
            agentMaintenanceState.ForeColor = active ? ConnectedText : !brokerAvailable || requiresProvisioning ? WarningText : SecondaryText;
        }
    }

    private void UpdateAgentState()
    {
        if (agent == null) { agentPairCode.Text = "— — —"; CurrentPairingCode = null; agentState.Text = "Agent en attente de préparation"; agentSessionNote.Text = "L’agent démarrera avec cette application lorsqu’elle est lancée en mode assistance."; setupNotice.Visible = false; return; }
        var session = agent.Session;
        if (session.Connected && session.BinaryMatched)
        {
            CurrentPairingCode = null; pairingCountdownText.Text = "Le code est consommé."; pairingCountdown.Value = 0; string duration = session.StartedUtc is { } started ? FormatDuration(DateTimeOffset.UtcNow - started) : "à l’instant"; agentPairCode.Text = "Contrôleur connecté"; agentState.Text = $"Connecté · assistance depuis {duration}"; agentSessionNote.Text = "Le contrôleur authentifié peut maintenant assister ce PC. Terminer l’assistance coupe immédiatement l’accès et libère la mise en veille."; terminateSession.Visible = true;
        }
        else if (session.Connected)
        {
            CurrentPairingCode = null; pairingCountdown.Value = 0; pairingCountdownText.Text = "Synchronisation de l’agent…"; agentPairCode.Text = "Synchronisation en cours"; agentState.Text = "Session authentifiée · préparation de l’agent"; agentSessionNote.Text = "Les commandes restent désactivées jusqu’à la validation des versions."; terminateSession.Visible = true;
        }
        else if (session.HasPaired && session.State == "reconnecting")
        {
            agentPairCode.Text = "Session interrompue"; CurrentPairingCode = null; var remaining = session.DisconnectDeadlineUtc is { } d ? d - DateTimeOffset.UtcNow : TimeSpan.Zero; pairingCountdownText.Text = remaining > TimeSpan.Zero ? $"Reconnexion en attente · {remaining:mm\\:ss}" : "Reconnexion en attente"; pairingCountdown.Value = 0; agentState.Text = "Reconnexion en attente"; agentSessionNote.Text = "La session reste disponible pendant dix minutes. La mise en veille reste suspendue."; terminateSession.Visible = true;
        }
        else
        {
            string current = agent.Pairing.CurrentCode ?? ""; CurrentPairingCode = current.Length == 6 ? current : null; agentPairCode.Text = FormatPairingCode(current); var expires = agent.Pairing.ExpiresUtc; var remaining = expires - DateTimeOffset.UtcNow; int seconds = Math.Clamp((int)Math.Ceiling(remaining.TotalSeconds), 0, 300); pairingCountdown.Value = seconds; pairingCountdownText.Text = seconds > 0 ? $"Nouveau code dans {TimeSpan.FromSeconds(seconds):mm\\:ss}" : "Préparation du prochain code…"; agentState.Text = "En attente de connexion"; agentSessionNote.Text = "Le code change automatiquement toutes les cinq minutes."; terminateSession.Visible = false;
        }
    }

    private void UpdateHeader()
    {
        bool onAgent = rolePages.SelectedIndex == 0;
        bool onController = !onAgent;
        if (onAgent)
        {
            headerTitle.Text = "Donner le contrôle"; headerSubtitle.Text = Environment.MachineName + " · Assistance sur le réseau privé";
        }
        else if (selectedPeer != null)
        {
            headerTitle.Text = selectedPeer.Name; headerSubtitle.Text = selectedPeer.Host + " · Session d’assistance";
        }
        else { headerTitle.Text = "Prendre le contrôle"; headerSubtitle.Text = "Choisissez un PC puis saisissez son code"; }

        bool connected = onAgent ? agent?.Session is { Connected: true, BinaryMatched: true } : heartbeatHealthy && supportSession;
        statusPill.BackColor = connected ? ConnectedBack : Color.FromArgb(237, 241, 244); statusDot.ForeColor = connected ? Color.FromArgb(50, 137, 91) : SecondaryText; statusLabel.ForeColor = connected ? ConnectedText : Color.FromArgb(80, 103, 113); statusLabel.Text = connected ? "Connecté" : onController && supportSession ? "Reconnexion…" : "En attente de connexion"; statusPill.AccessibleName = statusLabel.Text; statusPill.Region?.Dispose(); statusPill.Region = RoundedRegion(statusPill.Size, 16);
        terminateSession.Visible = onAgent ? agent?.Session.Connected == true || agent?.Session.State == "reconnecting" : supportSession;
        roleAgent.BackColor = onAgent ? SelectedRail : Rail; roleController.BackColor = onController ? SelectedRail : Rail; navConnection.BackColor = onController && controllerPages.SelectedIndex == 0 ? SelectedRail : Rail; navScreen.BackColor = onController && controllerPages.SelectedIndex == 1 ? SelectedRail : Rail; navProcesses.BackColor = onController && controllerPages.SelectedIndex == 2 ? SelectedRail : Rail; navFiles.BackColor = onController && controllerPages.SelectedIndex == 3 ? SelectedRail : Rail; navDiagnostics.BackColor = onController && controllerPages.SelectedIndex == 4 ? SelectedRail : Rail;
    }

    private void RefreshFooter() { footerLeft.Text = footerMessage; footerRight.Text = footerDetail; }

    private void SelectRole(int index)
    {
        if (index == 0 && supportSession && agent == null)
        {
            // Ending a controller session is asynchronous. Switch only after
            // the remote grant and held input have been released.
            roleAgent.Enabled = false;
            _ = TerminateSupportThenSelectAgentAsync();
            return;
        }
        bool roleChanged = index != rolePages.SelectedIndex;
        if (roleChanged)
        {
            pairingLifetime?.Cancel();
            InvalidateInputSession();
        }
        if (index == 1 && agent != null)
        {
            // A machine being used as the visible agent must stop offering its
            // session before the user enters the controller workspace.
            _ = StopAgentThenSelectControllerAsync();
            return;
        }
        rolePages.SelectedIndex = index;
        controllerNavCaption.Visible = index == 1; navConnection.Visible = index == 1; navScreen.Visible = index == 1; navProcesses.Visible = index == 1; navFiles.Visible = index == 1; navDiagnostics.Visible = index == 1;
        if (index == 0 && agent == null && !quitting) { StartAgent(); _ = PrepareAgentAsync(); }
        if (index == 1 && !supportSession)
        {
            footerMessage = "Choisissez un PC ou saisissez son adresse IP";
            footerDetail = "Fermeture : zone de notification";
            RefreshFooter();
        }
        UpdateHeader();
    }

    private async Task TerminateSupportThenSelectAgentAsync()
    {
        await TerminateSupportAsync(selectControllerAfter: false);
        roleAgent.Enabled = true;
        if (!quitting) SelectRole(0);
    }

    private async Task StopAgentThenSelectControllerAsync()
    {
        roleController.Enabled = false;
        bool stopped = await StopAgentAsync();
        roleController.Enabled = true;
        if (stopped && !quitting) SelectRole(1);
    }

    private void SelectControllerPage(int index)
    {
        if (liveStream != null && controllerPages.SelectedIndex == 1 && index != 1) StopStream("Vision suspendue");
        controllerPages.SelectedIndex = index; UpdateHeader();
        if (index == 1 && supportSession && client != null && liveStream == null) _ = StartStreamAsync();
        if (index == 2 && processRows.Count == 0 && client != null) _ = RefreshResourcesAsync();
        if (index == 3 && fileRows.Count == 0 && client != null) _ = BrowseFilesAsync();
    }

    private async Task DiscoverAsync(bool explicitRefresh)
    {
        if (pairingBusy) return;
        discoveryState.Text = explicitRefresh ? "Recherche des PC disponibles…" : "Recherche au démarrage…"; discoverButton.Enabled = false;
        discoveryLifetime?.Cancel(); discoveryLifetime = new CancellationTokenSource();
        try
        {
            var found = await Discovery.FindAsync(2500, discoveryLifetime.Token); discoveredPeers.Clear(); discoveredPeers.AddRange(found.Where(IsRemotePeer).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)); RenderPeers(); discoveryState.Text = discoveredPeers.Count == 0 ? "Aucun PC trouvé · saisissez une adresse IP." : $"{discoveredPeers.Count} PC disponible(s)"; if (discoveredPeers.Count == 1 && selectedPeer == null && peers.Items.Count > 0) peers.Items[0].Selected = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { discoveryState.Text = "Recherche indisponible : " + ex.Message; }
        finally { discoverButton.Enabled = true; }
    }

    private bool IsRemotePeer(Peer peer)
    {
        if (string.Equals(peer.Name, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return false;
        return !string.Equals(peer.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) || !loopbackOnly;
    }

    private void RenderPeers()
    {
        string? keep = selectedPeer?.Host ?? host.Text.Trim(); peers.BeginUpdate(); peers.Items.Clear(); foreach (var peer in discoveredPeers) { var item = new Forms.ListViewItem(peer.Name); item.SubItems.Add(peer.Host); item.SubItems.Add("Disponible"); item.Tag = peer; peers.Items.Add(item); if (peer.Host == keep) item.Selected = true; } peers.EndUpdate();
    }

    private void SelectPeerFromList()
    {
        if (peers.SelectedItems.Count == 0 || peers.SelectedItems[0].Tag is not Peer peer) return;
        if (pairingBusy) { pairingLifetime?.Cancel(); action?.Cancel(); operationGeneration++; }
        if (supportSession && client != null)
        {
            RemoteClient oldClient = client;
            supportSession = false;
            heartbeatHealthy = false;
            heartbeatLifetime?.Cancel();
            StopStream("Cible modifiée");
            sessionGeneration++;
            _ = EndSessionBestEffortAsync(oldClient);
        }
        InvalidateInputSession();
        selectedPeer = peer; selectedFingerprint = peer.Fingerprint; host.Text = peer.Host; selectedPeerName.Text = peer.Name; selectedPeerAddress.Text = peer.Host; connectionState.Text = "Saisissez le code affiché sur ce PC."; code.Clear(); code.Focus();
    }

    private async Task PairSelectedAsync()
    {
        if (pairingBusy || string.IsNullOrWhiteSpace(host.Text)) { connectionState.Text = "Choisissez un PC ou saisissez son adresse IP."; return; }
        if (code.Text.Length != 6 || !code.Text.All(char.IsAsciiDigit)) { connectionState.Text = "Le code doit comporter exactement six chiffres."; code.Focus(); return; }
        pairingBusy = true; pairButton.Enabled = false; discoverButton.Enabled = false; code.Enabled = false; host.Enabled = false; operationGeneration++; int generation = operationGeneration; sessionGeneration++; lastFrameUtc = null; liveFrameFresh = false; var pairingCts = new CancellationTokenSource(TimeSpan.FromMinutes(2)); pairingLifetime = pairingCts; CancellationToken ct = pairingCts.Token;
        RemoteClient? pairedClient = null;
        try
        {
            connectionState.Text = "Appairage…"; footerMessage = "Appairage…"; RefreshFooter(); pairedClient = new RemoteClient(new Connection(host.Text.Trim(), 45832, selectedFingerprint, "")); client = pairedClient; await pairedClient.PairAsync(code.Text, ct); pairedClient.Save(); connectionState.Text = "Synchronisation de l’agent…"; await SupportPlatform.SynchronizeAgentAsync(pairedClient, ct); if (generation != operationGeneration) return; supportSession = true; heartbeatHealthy = false; powerHold ??= PowerHold.Acquire(); StartHeartbeat(); SelectRole(1); SelectControllerPage(1); connectionState.Text = "Session établie."; footerMessage = "Session active · versions synchronisées"; footerDetail = "Chargement des mesures…"; RefreshFooter(); _ = LoadInitialRemoteStateAsync(generation);
        }
        catch (OperationCanceledException) { if (pairedClient != null) _ = EndSessionBestEffortAsync(pairedClient); if (generation == operationGeneration) connectionState.Text = "Connexion annulée."; }
        catch (Exception ex) { if (pairedClient != null) _ = EndSessionBestEffortAsync(pairedClient); if (generation == operationGeneration) { connectionState.Text = "Échec : " + ex.Message; footerMessage = "Connexion impossible"; footerDetail = ex.Message; RefreshFooter(); } }
        finally
        {
            bool ownsPairing = ReferenceEquals(pairingLifetime, pairingCts);
            if (ownsPairing)
            {
                pairingLifetime = null;
                pairingCts.Dispose();
            }
            if (ownsPairing || generation == operationGeneration) { pairingBusy = false; pairButton.Enabled = true; discoverButton.Enabled = true; code.Enabled = true; host.Enabled = true; }
        }
    }

    private async Task LoadInitialRemoteStateAsync(int generation)
    {
        try
        {
            if (client == null) return;
            if (liveStream == null) _ = StartStreamAsync();
            var resourcesTask = RefreshResourcesAsync(); var monitorsTask = LoadMonitorsAsync(generation); currentDirectory = ""; fileDirectory.Text = ""; var filesTask = BrowseFilesAsync(); await Task.WhenAll(resourcesTask, monitorsTask, filesTask); if (generation != operationGeneration) return; footerDetail = "Mesures et fichiers chargés"; RefreshFooter();
        }
        catch (Exception ex) { footerMessage = "Session active · chargement partiel"; footerDetail = ex.Message; RefreshFooter(); }
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
                item.TryGetProperty("primary", out var primary) && primary.GetBoolean() ? "Principal" : $"Écran {index + 1}")).ToArray();
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
                if (!ReferenceEquals(target, client)) continue;
                heartbeatHealthy = sessionConnected && binaryMatched;
                if (!heartbeatHealthy)
                {
                    liveFrameFresh = false;
                    _ = ReleaseHeldInputAsync(target);
                }
                PostUi(UpdateHeader);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (!ReferenceEquals(target, client)) continue;
                heartbeatHealthy = false; liveFrameFresh = false; _ = ReleaseHeldInputAsync(target); footerMessage = "Reconnexion…"; footerDetail = ex.Message; PostUi(() => { streamOverlay.Text = "Session interrompue · nouvelle tentative…"; streamOverlay.Visible = true; liveBadge.Visible = false; UpdateHeader(); RefreshFooter(); });
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task RefreshScreenAsync()
    {
        RequireClient(); var watch = Stopwatch.StartNew(); var data = RemoteClient.Require(await client!.CallAsync("screenshot", new { monitor = MonitorValue() }, seconds: 30)); Present(data.Deserialize<ScreenFrame>(Json.Options)!); streamStatus.Text = $"Image fraîche · {watch.Elapsed.TotalMilliseconds:F0} ms"; footerDetail = streamStatus.Text; RefreshFooter();
    }

    private async Task StartStreamAsync()
    {
        if (liveStream != null || client == null) return;
        try { RequireClient(); } catch (Exception ex) { streamStatus.Text = ex.Message; return; }
        RemoteClient target = client;
        int generation = sessionGeneration;
        var lifetime = new CancellationTokenSource();
        liveStream = lifetime; streamStartedUtc = DateTimeOffset.UtcNow; streamFrames = 0; streamBytes = 0; pauseViewing.Text = "Pause"; streamOverlay.Visible = true; streamOverlay.Text = "Connexion au flux…";
        try
        {
            while (!lifetime.IsCancellationRequested && generation == sessionGeneration && ReferenceEquals(target, client))
            {
                if (!heartbeatHealthy) { await Task.Delay(250, lifetime.Token); continue; }
                try
                {
                    await target.StreamAsync(frame =>
                    {
                        if (generation == sessionGeneration && ReferenceEquals(target, client) && supportSession) Present(frame);
                        return Task.CompletedTask;
                    }, StreamPolicy.MaximumFps, MonitorValue(), 300, lifetime.Token);
                }
                catch (Exception ex) when (!lifetime.IsCancellationRequested && ex is IOException or System.Net.Sockets.SocketException)
                {
                    // The five-minute transport boundary and transient link loss
                    // renew the stream inside the same authenticated session.
                    liveFrameFresh = false; liveBadge.Visible = false;
                    streamOverlay.Text = "Reconnexion au flux…"; streamOverlay.Visible = true;
                    streamStatus.Text = "En attente d’une image fraîche";
                    await ReleaseHeldInputAsync(target);
                    await Task.Delay(500, lifetime.Token);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(liveStream, lifetime))
            {
                liveFrameFresh = false; liveBadge.Visible = false; streamOverlay.Text = "Flux interrompu · appuyez sur Reprendre"; streamOverlay.Visible = true; streamStatus.Text = "Flux interrompu : " + ex.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(liveStream, lifetime))
            {
                liveStream = null;
                monitor.Enabled = true;
                pauseViewing.Text = "Reprendre";
                liveFrameFresh = false;
                liveBadge.Visible = false;
                streamOverlay.Visible = true;
                streamOverlay.Text = "Flux arrêté · appuyez sur Reprendre";
            }
            lifetime.Dispose();
            _ = ReleaseHeldInputAsync(target);
        }
    }

    private void StopStream(string message)
    {
        var target = client;
        var running = liveStream;
        liveStream = null;
        running?.Cancel();
        pauseViewing.Text = "Reprendre"; monitor.Enabled = true;
        liveFrameFresh = false; liveBadge.Visible = false; streamOverlay.Visible = true; streamOverlay.Text = "Vision en pause · appuyez sur Reprendre"; streamStatus.Text = message;
        if (target != null) _ = ReleaseHeldInputAsync(target);
    }

    private void Present(ScreenFrame frame)
    {
        if (InvokeRequired)
        {
            PostUi(() => Present(frame));
            return;
        }
        geometry = frame.Geometry; using var ms = new MemoryStream(Convert.FromBase64String(frame.Data)); using var image = Image.FromStream(ms); var previous = screen.Image; screen.Image = new Bitmap(image); previous?.Dispose(); screen.Refresh(); liveFrameFresh = true; lastFrameUtc = DateTimeOffset.UtcNow; liveBadge.Visible = true; streamOverlay.Visible = false;
        if (liveStream != null && streamStartedUtc is { } started)
        {
            streamFrames++; streamBytes += frame.Data.Length; double seconds = Math.Max(0.001, (DateTimeOffset.UtcNow - started).TotalSeconds); double fps = streamFrames / seconds; double mbps = streamBytes * 8 / seconds / 1_000_000d; streamStatus.Text = $"{fps:F1} i/s · {mbps:F1} Mbit/s · capture {frame.CaptureEncodeMs:F0} ms"; footerDetail = streamStatus.Text; RefreshFooter();
        }
        else streamStatus.Text = $"{frame.CapturedUtc.ToLocalTime():HH:mm:ss} · capture {frame.CaptureEncodeMs:F0} ms";
    }

    private void QueueMouse(string kind, Forms.MouseEventArgs e)
    {
        if (!CanSendInput() || geometry == null) return; var point = geometry.MapLetterbox(screen.Width, screen.Height, e.X, e.Y); if (point == null) { if (kind == "up") QueueInput(new { kind = "release" }); return; } QueueInput(new { kind, x = point.Value.X, y = point.Value.Y, layoutId = geometry.LayoutId, button = e.Button == Forms.MouseButtons.Right ? "right" : e.Button == Forms.MouseButtons.Middle ? "middle" : "left", delta = e.Delta });
    }

    private bool CanSendInput() => supportSession && heartbeatHealthy && liveFrameFresh && mouseEnabled.Checked && screen.ContainsFocus;

    private void QueueInput(object value)
    {
        RemoteClient? target = client;
        if (target == null || !supportSession) return;
        if (!inputQueue.Writer.TryWrite(new QueuedInput(target, sessionGeneration, value)))
        {
            mouseEnabled.Checked = false;
            streamStatus.Text = "Contrôle suspendu : file d’entrée saturée.";
            _ = ReleaseHeldInputAsync(target);
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
            RemoteClient.Require(await target.CallAsync("ui.input", new { kind = "release" }, seconds: 5));
        }
        catch
        {
            // Release is best effort when the peer has already disconnected;
            // AgentServer also releases native input during its grace period.
        }
    }

    private void ReleaseHeldInputForCurrentSession()
    {
        if (supportSession && client is { } target) _ = ReleaseHeldInputAsync(target);
    }

    private async Task EndSessionBestEffortAsync(RemoteClient target)
    {
        try { await target.EndSessionAsync(CancellationToken.None); } catch { }
        if (ReferenceEquals(client, target))
        {
            client = null;
            powerHold?.Dispose(); powerHold = null;
        }
    }

    private async Task PumpInputAsync()
    {
        await foreach (QueuedInput item in inputQueue.Reader.ReadAllAsync())
        {
            RemoteClient target = item.Client;
            if (!supportSession || item.Generation != sessionGeneration || !ReferenceEquals(target, client)) continue;
            try { RemoteClient.Require(await target.CallAsync("ui.input", item.Payload, seconds: 5)); }
            catch (Exception ex) { PostUi(() => { mouseEnabled.Checked = false; streamStatus.Text = "Entrée interrompue : " + ex.Message; }); }
        }
    }

    private async Task RefreshResourcesAsync()
    {
        int generation = sessionGeneration; RemoteClient? target = client;
        try
        {
            RequireClient(); resourceState.Text = "Mesure en cours…"; volumeSummary.Text = "Volumes · mesure en cours…"; int? selected = processList.SelectedItems.Count > 0 && processList.SelectedItems[0].Tag is ProcessSortRow row ? row.Pid : null; var data = RemoteClient.Require(await target!.CallAsync("processes", seconds: 30)); var system = RemoteClient.Require(await target.CallAsync("system", seconds: 30)); if (generation != sessionGeneration || !ReferenceEquals(target, client)) return; processRows.Clear(); foreach (var p in data.GetProperty("processes").EnumerateArray()) processRows.Add(new ProcessSortRow(p.Int("pid"), p.Str("name", "Indisponible"), NullableDouble(p, "cpuPercentTotalMachine"), NullableLong(p, "workingSetBytes"), NullableBool(p, "responding"), p.Str("window"), NullableDate(p, "startUtc"))); lastMeasurementUtc = NullableDate(data, "sampleEndUtc") ?? DateTimeOffset.UtcNow; RenderProcesses(selected); var cpu = NullableDouble(system, "cpuPercentTotalMachine"); cpuSummary.Text = cpu is { } c ? $"{c:F1} %" : "Indisponible"; long? total = NullableLong(system, "physicalMemoryTotalBytes"); long? available = NullableLong(system, "physicalMemoryAvailableBytes"); ramSummary.Text = total is > 0 && available is >= 0 ? FormatBytes(total.Value - available.Value) : "Indisponible"; processSummary.Text = processRows.Count.ToString("N0"); resourceMeasuredAt.Text = lastMeasurementUtc.Value.ToLocalTime().ToString("HH:mm:ss"); volumeSummary.Text = FormatVolumes(system); resourceState.Text = "Mesure terminée"; footerDetail = $"Mesuré à {resourceMeasuredAt.Text}"; RefreshFooter();
        }
        catch (Exception ex) { if (generation != sessionGeneration) return; resourceState.Text = "Mesure indisponible"; footerMessage = "Ressources indisponibles"; footerDetail = ex.Message; RefreshFooter(); }
    }

    private void RenderProcesses(int? selectedPid = null)
    {
        int? keep = selectedPid ?? (processList.SelectedItems.Count > 0 && processList.SelectedItems[0].Tag is ProcessSortRow selectedRow ? selectedRow.Pid : null); var sorted = UiSorting.SortProcesses(processRows, processSort); processList.BeginUpdate(); processList.Items.Clear(); foreach (var row in sorted) { var item = new Forms.ListViewItem(row.Pid.ToString()); item.SubItems.Add(row.Name); item.SubItems.Add(row.CpuPercentTotalMachine is { } cpu ? cpu.ToString("F1") : "—"); item.SubItems.Add(row.WorkingSetBytes is { } bytes ? (bytes / 1048576d).ToString("F1") : "—"); item.SubItems.Add(row.Responding is null ? "—" : row.Responding.Value ? "Oui" : "Bloqué"); item.SubItems.Add(string.IsNullOrWhiteSpace(row.Window) ? "—" : row.Window); item.Tag = row; if (keep == row.Pid) item.Selected = true; processList.Items.Add(item); } processList.EndUpdate();
    }

    private async Task BrowseFilesAsync()
    {
        int generation = sessionGeneration; RemoteClient? target = client; string directory = currentDirectory;
        try
        {
            RequireClient(); fileState.Text = "Chargement…"; string keep = selectedFilePath ?? ""; var data = RemoteClient.Require(await target!.CallAsync("files", new { path = directory }, seconds: 30)); if (generation != sessionGeneration || !ReferenceEquals(target, client) || directory != currentDirectory) return; fileRows.Clear(); foreach (var entry in data.EnumerateArray()) fileRows.Add(new FileSortRow(entry.Str("name"), entry.TryGetProperty("directory", out var d) && d.GetBoolean(), NullableLong(entry, "size"), NullableDate(entry, "modifiedUtc"), entry.Str("path"))); RenderFiles(keep); fileState.Text = fileRows.Count == 0 ? "Dossier vide" : $"{fileRows.Count} élément(s)"; fileDirectory.Text = currentDirectory; footerDetail = $"Fichiers · {fileRows.Count} élément(s)"; RefreshFooter();
        }
        catch (Exception ex) { if (generation != sessionGeneration || directory != currentDirectory) return; fileState.Text = "Fichiers indisponibles"; footerMessage = "Lecture du dossier impossible"; footerDetail = ex.Message; RefreshFooter(); }
    }

    private void RenderFiles(string? selectedPath = null)
    {
        string keep = selectedPath ?? selectedFilePath ?? ""; var sorted = UiSorting.SortFiles(fileRows, fileSort); fileList.BeginUpdate(); fileList.Items.Clear(); foreach (var row in sorted) { var item = new Forms.ListViewItem(row.Name); item.SubItems.Add(row.IsDirectory ? "Dossier" : "Fichier"); item.SubItems.Add(row.SizeBytes is { } bytes ? FormatBytes(bytes) : "—"); item.SubItems.Add(row.ModifiedUtc is { } date ? date.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—"); item.Tag = row; if (!row.IsDirectory && row.Path == keep) item.Selected = true; fileList.Items.Add(item); } fileList.EndUpdate(); remotePath.Text = selectedFilePath ?? "";
    }

    private async Task ExecuteSelectedAsync()
    {
        if (operations.SelectedItem is not string op) return; try { var args = JsonSerializer.Deserialize<JsonElement>(arguments.Text); await ExecuteAsync(op, args); } catch (Exception ex) { diagnosticState.Text = "Arguments JSON invalides : " + ex.Message; }
    }

    public async Task ExecuteAsync(string op, object args) => await ExecuteAsync(op, Json.Element(args));

    private async Task ExecuteAsync(string op, JsonElement args)
    {
        if (action != null) { diagnosticState.Text = "Une action est déjà en cours."; return; }
        try
        {
            RequireClient(); using var cts = new CancellationTokenSource(); action = cts; operationId = Guid.NewGuid().ToString(); diagnosticState.Text = "En cours…"; var reply = await client!.CallAsync(op, args, cts.Token, operationId, 120); output.Text = Pretty(reply); RemoteClient.Require(reply); diagnosticState.Text = "Terminé · " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex) { diagnosticState.Text = "Échec : " + ex.Message; output.Text = Pretty(new { ok = false, message = ex.Message }); }
        finally { action = null; operationId = null; }
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
        using var dialog = new Forms.OpenFileDialog(); if (dialog.ShowDialog() != Forms.DialogResult.OK) return; try { RequireClient(); fileState.Text = "Téléversement…"; var result = await client!.UploadAsync(dialog.FileName, destination.Text.TrimEnd('/', '\\') + "/" + Path.GetFileName(dialog.FileName)); output.Text = Pretty(result); fileState.Text = "Téléversement vérifié"; await BrowseFilesAsync(); } catch (Exception ex) { fileState.Text = "Téléversement impossible : " + ex.Message; }
    }

    private async Task UploadFolderAsync()
    {
        using var dialog = new Forms.FolderBrowserDialog(); if (dialog.ShowDialog() != Forms.DialogResult.OK) return; try { RequireClient(); var target = client!; int generation = sessionGeneration; string folderDestination = destination.Text.TrimEnd('/', '\\'); foreach (string file in Directory.EnumerateFiles(dialog.SelectedPath, "*", SearchOption.AllDirectories)) { if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Les points de reparse ne sont pas téléversés."); if (generation != sessionGeneration) throw new OperationCanceledException("La cible a changé."); await target.UploadAsync(file, folderDestination + "/" + Path.GetRelativePath(dialog.SelectedPath, file)); } fileState.Text = "Dossier téléversé et vérifié"; await BrowseFilesAsync(); } catch (Exception ex) { fileState.Text = "Téléversement impossible : " + ex.Message; }
    }

    private async Task DownloadFileAsync()
    {
        if (string.IsNullOrWhiteSpace(selectedFilePath)) { fileState.Text = "Sélectionnez un fichier."; return; } using var dialog = new Forms.SaveFileDialog { FileName = Path.GetFileName(selectedFilePath) }; if (dialog.ShowDialog() != Forms.DialogResult.OK) return; try { RequireClient(); await client!.DownloadAsync(selectedFilePath, dialog.FileName); fileState.Text = "Téléchargement vérifié"; output.Text = "Fichier enregistré : " + dialog.FileName; } catch (Exception ex) { fileState.Text = "Téléchargement impossible : " + ex.Message; }
    }

    private async Task TerminateSupportAsync(bool selectControllerAfter = true)
    {
        if (terminating) return; terminating = true; terminateSession.Enabled = false; footerMessage = "Fin de l’assistance…"; RefreshFooter(); pairingLifetime?.Cancel(); heartbeatLifetime?.Cancel(); StopStream("Assistance terminée"); QueueInput(new { kind = "release" });
        RemoteClient? oldClient = client;
        sessionGeneration++;
        supportSession = false;
        heartbeatHealthy = false;
        bool localAgentStopped = true;
        try
        {
            if (agent != null)
            {
                localAgentStopped = await StopAgentAsync();
            }
            else if (oldClient != null)
            {
                await ReleaseHeldInputAsync(oldClient);
                await oldClient.EndSessionAsync(CancellationToken.None);
            }
        }
        catch (Exception ex) { footerDetail = "Cible hors ligne · expiration automatique active"; output.Text = Pretty(new { ok = false, message = ex.Message }); }
        if (!localAgentStopped)
        {
            terminateSession.Enabled = true;
            terminating = false;
            return;
        }
        client = null; selectedPeer = null; selectedFingerprint = ""; selectedFilePath = null; processRows.Clear(); fileRows.Clear(); powerHold?.Dispose(); powerHold = null; terminateSession.Enabled = true; terminating = false;
        if (agent == null && rolePages.SelectedIndex == 0)
        {
            quitting = true;
            shutdownStarted = true;
            if (await ShutdownAsync()) Close();
            else { quitting = false; shutdownStarted = false; }
            return;
        }
        if (selectControllerAfter)
        {
            SelectRole(1); SelectControllerPage(0); _ = DiscoverAsync(false);
        }
        footerMessage = "Assistance terminée"; footerDetail = "Sélectionnez un PC pour recommencer"; RefreshFooter();
    }

    private void TerminateAgentFromRemote()
    {
        if (quitting) return; footerMessage = "L’assistance a été terminée"; agent?.Dispose(); agent = null; powerHold?.Dispose(); powerHold = null; quitting = true; Close();
    }

    private void RequestQuit()
    {
        if (quitting) return; quitting = true; Close();
    }

    private async void MainFormClosing(object? sender, Forms.FormClosingEventArgs e)
    {
        if (shutdownStarted) return;
        if (quitting || agent != null)
        {
            e.Cancel = true;
            quitting = true;
            shutdownStarted = true;
            if (await ShutdownAsync()) Close();
            else { quitting = false; shutdownStarted = false; }
            return;
        }
        if (!trayVisible) { e.Cancel = true; HideToTray(); }
    }

    private void HideToTray() { trayVisible = true; Hide(); tray.Visible = true; footerMessage = "Assistance active dans la zone de notification"; footerDetail = "Ouvrir pour reprendre"; RefreshFooter(); }
    private void RestoreFromTray() { tray.Visible = false; trayVisible = false; Show(); WindowState = Forms.FormWindowState.Normal; Activate(); }

    private async Task<bool> ShutdownAsync()
    {
        renderTimer.Stop(); discoveryTimer.Stop(); discoveryLifetime?.Cancel(); heartbeatLifetime?.Cancel(); pairingLifetime?.Cancel(); liveStream?.Cancel(); action?.Cancel();
        RemoteClient? oldClient = client;
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
                footerMessage = "Arrêt de l’agent différé";
                footerDetail = ex.Message;
                renderTimer.Start();
                RefreshFooter();
                return false;
            }
            finally { suppressTerminationEvent = false; }
            if (ReferenceEquals(agent, localAgent)) agent = null;
        }
        inputQueue.Writer.TryComplete(); if (agent != null) agent.Dispose(); agent = null; powerHold?.Dispose(); powerHold = null; tray.Visible = false; screen.Image?.Dispose();
        return true;
    }

    private void DisposeResources()
    {
        SupportPlatform.ManagedRelaunchRequested -= OnManagedRelaunchRequested;
        renderTimer.Dispose(); discoveryTimer.Dispose(); discoveryLifetime?.Dispose(); heartbeatLifetime?.Dispose(); pairingLifetime?.Dispose(); liveStream?.Dispose(); action?.Dispose(); powerHold?.Dispose(); tray.Dispose();
    }

    private void RequireClient() { if (client == null || !supportSession) throw new InvalidOperationException("Connectez-vous à un PC distant avant cette action."); }
    private int MonitorValue() => monitor.SelectedItem is MonitorChoice choice ? choice.Index : 0;
    private static string FormatPairingCode(string code) => code.Length == 6 ? code[..3] + " " + code[3..] : "— — —";
    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1 ? $"{(int)duration.TotalHours} h {duration.Minutes:00}" : duration.TotalMinutes >= 1 ? $"{duration.Minutes} min" : "à l’instant";
    private static string ParentPath(string path) => string.IsNullOrWhiteSpace(path) ? "" : Path.GetDirectoryName(path) ?? "";
    private static string FormatVolumes(JsonElement system)
    {
        if (!system.TryGetProperty("volumes", out var volumes) || volumes.ValueKind != JsonValueKind.Array) return "Volumes · indisponibles";
        var values = volumes.EnumerateArray().Select(volume =>
        {
            string name = volume.Str("name", "?").TrimEnd('\\', '/');
            if (!volume.TryGetProperty("ready", out var ready) || !ready.GetBoolean()) return $"{name} indisponible";
            long? free = NullableLong(volume, "freeBytes");
            long? total = NullableLong(volume, "totalBytes");
            return free is { } freeBytes && total is { } totalBytes ? $"{name} {FormatBytes(freeBytes)} libres / {FormatBytes(totalBytes)}" : $"{name} prêt";
        }).ToArray();
        return values.Length == 0 ? "Volumes · indisponibles" : "Volumes · " + string.Join(" · ", values);
    }
    private static double? NullableDouble(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number ? item.GetDouble() : null;
    private static long? NullableLong(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number ? item.GetInt64() : null;
    private static bool? NullableBool(JsonElement value, string name) => value.TryGetProperty(name, out var item) ? item.ValueKind == JsonValueKind.Null ? null : item.GetBoolean() : null;
    private static DateTimeOffset? NullableDate(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(item.GetString(), out var date) ? date : null;
    private static string FormatBytes(long bytes) => bytes < 1024 * 1024 ? $"{bytes / 1024d:F0} Kio" : $"{bytes / 1048576d:F1} Mio";
    private static ProcessSortColumn ProcessColumn(int index) => index switch { 0 => ProcessSortColumn.Pid, 1 => ProcessSortColumn.Name, 2 => ProcessSortColumn.CpuPercentTotalMachine, 3 => ProcessSortColumn.WorkingSetBytes, 4 => ProcessSortColumn.Responding, _ => ProcessSortColumn.Window };
    private static FileSortColumn FileColumn(int index) => index switch { 0 => FileSortColumn.Name, 1 => FileSortColumn.Type, 2 => FileSortColumn.SizeBytes, _ => FileSortColumn.ModifiedUtc };
    private static string Pretty(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(Json.Options) { WriteIndented = true });
    private static void AddSummary(Forms.TableLayoutPanel table, int col, string title, Forms.Label value) { var flow = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, FlowDirection = Forms.FlowDirection.TopDown, WrapContents = false }; flow.Controls.Add(new Forms.Label { Text = title, AutoSize = true, ForeColor = SecondaryText, Font = new Font("Segoe UI", 9.5F) }); flow.Controls.Add(value); table.Controls.Add(flow, col, 0); }
    private static Forms.Label SummaryValue(string name) => new() { Name = name, Text = "—", AutoSize = true, ForeColor = PrimaryText, Font = new Font("Segoe UI", 13, FontStyle.Bold) };
    private static Forms.Label Eyebrow(string text) => new() { Text = text, AutoSize = true, ForeColor = Teal, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), Margin = new Forms.Padding(0) };
    private static Forms.Control SectionTitle(string title, string subtitle)
    {
        var panel = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Forms.Padding(0) };
        panel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 25));
        panel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100));
        panel.Controls.Add(new Forms.Label { Text = title, AutoSize = true, Dock = Forms.DockStyle.Fill, Font = new Font("Segoe UI", 15, FontStyle.Bold), ForeColor = PrimaryText }, 0, 0);
        panel.Controls.Add(new Forms.Label { Text = subtitle, AutoSize = true, Dock = Forms.DockStyle.Fill, Font = new Font("Segoe UI", 10.5F), ForeColor = SecondaryText }, 0, 1);
        return panel;
    }
    private static Forms.Label RailCaption(string text) => new() { Text = text, AutoSize = true, ForeColor = RailSecondary, Font = new Font("Segoe UI", 8.5F, FontStyle.Bold), Margin = new Forms.Padding(12, 0, 0, 8) };
    private static Forms.Button RailButton(string text, string name) => new() { Name = name, Text = text, AccessibleName = text, AutoSize = false, Width = 184, Height = 48, FlatStyle = Forms.FlatStyle.Flat, FlatAppearance = { BorderSize = 0 }, ForeColor = Color.White, BackColor = Rail, TextAlign = ContentAlignment.MiddleLeft, Padding = new Forms.Padding(16, 0, 0, 0), Margin = new Forms.Padding(0, 0, 0, 4), UseMnemonic = false };
    private static Forms.Button RailSubButton(string text, string name) => new() { Name = name, Text = text, AccessibleName = text, AutoSize = false, Width = 168, Height = 42, FlatStyle = Forms.FlatStyle.Flat, FlatAppearance = { BorderSize = 0 }, ForeColor = RailSecondary, BackColor = Rail, TextAlign = ContentAlignment.MiddleLeft, Padding = new Forms.Padding(16, 0, 0, 0), Margin = new Forms.Padding(8, 0, 0, 5), UseMnemonic = false };
    private static Forms.Button Button(string text, string name, int width = 0, bool primary = false, bool destructive = false) { var button = new Forms.Button { Name = name, Text = text, AccessibleName = text, AutoSize = true, AutoSizeMode = Forms.AutoSizeMode.GrowAndShrink, MinimumSize = new Size(width > 0 ? width : 120, 38), Font = new Font("Segoe UI", 10), FlatStyle = Forms.FlatStyle.Flat, UseMnemonic = false, Padding = new Forms.Padding(10, 0, 10, 0), BackColor = destructive ? DestructiveBack : primary ? Teal : Surface, ForeColor = destructive ? DestructiveText : primary ? Color.White : PrimaryText, FlatAppearance = { BorderSize = 1, BorderColor = destructive ? Color.FromArgb(250, 210, 214) : primary ? Teal : Color.FromArgb(203, 215, 221) } }; button.Region = RoundedRegion(button.Size, 6); button.Resize += (_, _) => { button.Region?.Dispose(); button.Region = RoundedRegion(button.Size, 6); }; button.GotFocus += (_, _) => button.FlatAppearance.BorderColor = Teal; button.LostFocus += (_, _) => button.FlatAppearance.BorderColor = destructive ? Color.FromArgb(250, 210, 214) : primary ? Teal : Color.FromArgb(203, 215, 221); button.MouseEnter += (_, _) => { if (primary) button.BackColor = TealHover; }; button.MouseLeave += (_, _) => { if (primary) button.BackColor = Teal; }; return button; }
    private static Forms.TextBox TextBox(string name) { var box = new Forms.TextBox { Name = name, AccessibleName = name, BorderStyle = Forms.BorderStyle.FixedSingle, BackColor = Surface, ForeColor = PrimaryText, Font = new Font("Segoe UI", 11) }; box.Enter += (_, _) => box.BackColor = Color.FromArgb(248, 253, 253); box.Leave += (_, _) => box.BackColor = Surface; return box; }
    private static Forms.Label Badge(string text, string name) => new() { Name = name, Text = "●  " + text, AutoSize = true, ForeColor = ConnectedText, BackColor = ConnectedBack, Font = new Font("Segoe UI", 8.5F, FontStyle.Bold), Padding = new Forms.Padding(8, 5, 8, 5), Visible = false };
    private static Region RoundedRegion(Size size, int radius) { var path = new GraphicsPath(); path.AddArc(0, 0, radius * 2, radius * 2, 180, 90); path.AddArc(size.Width - radius * 2, 0, radius * 2, radius * 2, 270, 90); path.AddArc(size.Width - radius * 2, size.Height - radius * 2, radius * 2, radius * 2, 0, 90); path.AddArc(0, size.Height - radius * 2, radius * 2, radius * 2, 90, 90); path.CloseFigure(); return new Region(path); }
    private static Icon LoadApplicationIcon() { using Stream stream = typeof(MainForm).Assembly.GetManifestResourceStream("RemoteDebugger.Assets.RemoteDebugger.ico") ?? throw new InvalidOperationException("The application icon resource is missing."); using var icon = new Icon(stream); return (Icon)icon.Clone(); }
    private void PostUi(Action callback) { if (IsDisposed || !IsHandleCreated) return; try { BeginInvoke(callback); } catch (InvalidOperationException) { } }

    private sealed record QueuedInput(RemoteClient Client, int Generation, object Payload);
    private sealed record MonitorChoice(int Index, string Name) { public override string ToString() => Name; }
    public static readonly Dictionary<string, string> Templates = new()
    {
        ["status"] = "{}", ["processes"] = "{}", ["process.info"] = "{\"pid\":1234}", ["start"] = "{\"path\":\"deployments/essai-1/MonApp.exe\",\"arguments\":[]}", ["stop"] = "{\"pid\":1234,\"mode\":\"graceful\"}", ["restart"] = "{\"pid\":1234,\"mode\":\"graceful\",\"arguments\":[]}",
        ["system"] = "{}", ["network"] = "{}", ["events"] = "{\"log\":\"Application\",\"count\":20}", ["services"] = "{}", ["windows"] = "{}", ["ui.inspect"] = "{\"pid\":1234}", ["ui.click"] = "{\"pid\":1234,\"automationId\":\"saveButton\"}", ["ui.key"] = "{\"pid\":1234,\"key\":\"CTRL+A\"}", ["debug.attach"] = "{\"pid\":1234,\"seconds\":3}", ["debug.dump"] = "{\"pid\":1234}",
        ["files"] = "{}", ["file.info"] = "{\"path\":\"deployments/essai-1/MonApp.exe\"}", ["command"] = "{\"file\":\"whoami.exe\",\"arguments\":[]}", ["maintenance.status"] = "{}", ["maintenance.session"] = "{\"file\":\"whoami.exe\",\"arguments\":[\"/groups\"]}", ["maintenance.elevated"] = "{\"file\":\"whoami.exe\",\"arguments\":[\"/groups\"]}", ["history"] = "{}"
    };
}

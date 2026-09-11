using System.Drawing;
using System.Text.Json;
using System.Diagnostics;
using System.Threading.Channels;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

public sealed class MainForm : Forms.Form
{
    private readonly PageSwitcher tabs = new() { Dock = Forms.DockStyle.Fill };
    private readonly PageSwitcher controllerTabs = new() { Dock = Forms.DockStyle.Fill };
    private readonly Forms.TextBox agentLog = new() { Multiline = true, ReadOnly = true, ScrollBars = Forms.ScrollBars.Vertical, Dock = Forms.DockStyle.Fill };
    private readonly Forms.TextBox identity = new() { ReadOnly = true, Multiline = true, Dock = Forms.DockStyle.Fill, Font = new Font("Consolas", 10) };
    private readonly Forms.Label agentState = new() { Text = "Agent arrêté", AutoSize = true };
    private readonly Forms.Label pairing = new() { Text = "Appairage fermé", AutoSize = true, Font = new Font("Segoe UI", 15, FontStyle.Bold), ForeColor = Color.DarkSlateBlue };
    private readonly Forms.Button agentToggle = Button("Démarrer l’agent", "agentStart");
    private readonly Forms.TextBox host = Box("host", ""), fingerprint = Box("fingerprint", ""), code = Box("pairCode", "");
    private readonly Forms.CheckBox verified = new() { Text = "J’ai comparé l’empreinte avec celle affichée sur l’agent", AutoSize = true, Name = "fingerprintVerified" };
    private readonly Forms.ListBox peers = new() { Name = "peers", Dock = Forms.DockStyle.Fill, DisplayMember = "Name" };
    private readonly Forms.ComboBox operations = new() { Name = "operation", DropDownStyle = Forms.ComboBoxStyle.DropDownList, Width = 180 };
    private readonly Forms.TextBox arguments = new() { Name = "arguments", Multiline = true, Dock = Forms.DockStyle.Fill, ScrollBars = Forms.ScrollBars.Vertical, Font = new Font("Consolas", 10) };
    private readonly Forms.TextBox output = new() { Name = "output", Multiline = true, ReadOnly = true, Dock = Forms.DockStyle.Fill, ScrollBars = Forms.ScrollBars.Both, Font = new Font("Consolas", 9), WordWrap = false };
    private readonly Forms.Label controllerState = new() { Text = "Choisis un agent ou saisis son IP.", AutoSize = true };
    private readonly Forms.NumericUpDown pid = new() { Name = "targetPid", Maximum = int.MaxValue, Width = 90 };
    private readonly Forms.TextBox input = Box("remoteText", ""), destination = Box("destination", "deployments/essai-1/"), remotePath = Box("remotePath", "");
    private readonly Forms.PictureBox screen = new() { Dock = Forms.DockStyle.Fill, SizeMode = Forms.PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(25, 30, 40), Name = "remoteScreen" };
    private readonly Forms.CheckBox mouseEnabled = new() { Text = "Clics distants actifs (PID requis)", AutoSize = true };
    private RemoteClient? client;
    private AgentServer? agent;
    private CancellationTokenSource? action;
    private string? operationId;
    private DesktopGeometry? geometry;
    private CancellationTokenSource? liveStream;
    private readonly Forms.Button liveButton = Button("Démarrer le direct · 5 i/s", "liveStream");
    private readonly Forms.Label streamStatus = new() { Text = "Direct arrêté", AutoSize = true, Name = "streamStatus" };
    private readonly Forms.NumericUpDown monitor = new() { Minimum = -1, Maximum = 15, Value = 0, Width = 55, Name = "monitor" };
    private readonly Channel<object> inputQueue = Channel.CreateBounded<object>(new BoundedChannelOptions(128) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private long lastMove;
    private readonly string root;
    private readonly bool loopbackOnly;
    public string? CurrentPairingCode { get; private set; }
    public AgentServer? Agent => agent;
    public RemoteClient? Client => client;
    public MainForm(bool startAgent = false, string? dataRoot = null, bool loopbackOnly = false)
    {
        root = dataRoot ?? Vault.DefaultRoot; this.loopbackOnly = loopbackOnly;
        Text = "Remote Debugger — 0.1.0"; Name = "RemoteDebuggerMain"; Icon = LoadApplicationIcon(); Width = 1180; Height = 820; MinimumSize = new Size(980, 680); StartPosition = Forms.FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10); BackColor = Color.WhiteSmoke;
        var header = new Forms.Label { Text = "Remote Debugger     /     Piloter, tester et dépanner un autre PC", Height = 54, Dock = Forms.DockStyle.Top, Padding = new Forms.Padding(18, 12, 0, 0), ForeColor = Color.White, BackColor = Color.FromArgb(27, 42, 61), Font = new Font("Segoe UI", 14, FontStyle.Bold) };
        Controls.Add(tabs); Controls.Add(header); var take = new PagePanel("Prendre le contrôle"); take.Controls.Add(controllerTabs); tabs.TabPages.Add(take); BuildController(); BuildScreen(); controllerTabs.TabPages.Add(BuildResources()); if (pendingFiles != null) controllerTabs.TabPages.Add(pendingFiles); BuildAgent();
        try { client = RemoteClient.Load(); host.Text = client.Connection.Host; fingerprint.Text = client.Connection.Fingerprint; controllerState.Text = "Connexion enregistrée. Exécute État pour la vérifier."; } catch (Exception) { }
        Shown += (_, _) => { _ = PumpInputAsync(); if (startAgent) { tabs.SelectedIndex = 1; StartAgent(); } };
        FormClosed += (_, _) => { action?.Cancel(); liveStream?.Cancel(); inputQueue.Writer.TryComplete(); agent?.Dispose(); screen.Image?.Dispose(); };
    }
    private static Icon LoadApplicationIcon()
    {
        using Stream stream = typeof(MainForm).Assembly.GetManifestResourceStream("RemoteDebugger.Assets.RemoteDebugger.ico")
            ?? throw new InvalidOperationException("The application icon resource is missing.");
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }
    private static Forms.Button Button(string text, string name) => new() { Text = text, Name = name, AutoSize = true, Padding = new Forms.Padding(5), Margin = new Forms.Padding(4) };
    private static Forms.TextBox Box(string name, string value) => new() { Name = name, Text = value, Dock = Forms.DockStyle.Fill };
    private static Forms.FlowLayoutPanel Row(params Forms.Control[] controls) { var row = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, AutoSize = true, WrapContents = true }; row.Controls.AddRange(controls); return row; }
    private static Forms.Label Label(string text) => new() { Text = text, AutoSize = true, Padding = new Forms.Padding(3, 7, 3, 0) };
    private static new Forms.TableLayoutPanel Layout(int rows)
    {
        var p = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, Padding = new Forms.Padding(14), ColumnCount = 1, RowCount = rows };
        p.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100)); return p;
    }
    private void BuildAgent()
    {
        var page = new PagePanel("Donner le contrôle"); var layout = Layout(7);
        layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 58)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 62)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 105)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 55)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 45)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 58)); layout.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100));
        layout.Controls.Add(Label("Laisse cette fenêtre ouverte sur le portable. Le pilote appairé pourra utiliser ta session Windows, transférer des fichiers et exécuter des commandes."), 0, 0);
        var open = Button("Ouvrir l’appairage (3 min)", "openPairing"); var revoke = Button("Révoquer l’accès", "revokeAccess"); var firewall = Button("Autoriser le réseau privé…", "configureFirewall");
        firewall.Click += async (_, _) => { firewall.Enabled = false; agentState.Text = "Accepte UAC sur ce PC pour autoriser uniquement le sous-réseau privé."; try { var result = Json.Element(await FirewallSetup.ConfigurePrivateAsync(root, CancellationToken.None)); if (result.Int("exitCode") != 0) throw new InvalidOperationException(result.Str("stderr")); agentState.Text = "Pare-feu configuré : TCP 45832 / UDP 45833, réseau Privé, sous-réseau local."; } catch (Exception ex) { agentState.Text = "Configuration impossible : " + ex.Message; } finally { firewall.Enabled = true; } };
        agentToggle.Click += (_, _) => { if (agent == null) StartAgent(); else StopAgent(); };
        open.Click += (_, _) => OpenPairing(); revoke.Click += (_, _) => { agent?.Revoke(); pairing.Text = "Accès révoqué"; CurrentPairingCode = null; };
        var admin = Button("Maintenance admin (1 h)…", "enableMaintenance");
        admin.Click += async (_, _) => { if (agent == null) { agentState.Text = "Démarre d’abord l’agent."; return; } admin.Enabled = false; try { await agent.Operations.Maintenance.StartAsync(CancellationToken.None); agentState.Text = "Maintenance administrateur autorisée pendant une heure. L’auxiliaire visible permet de la couper."; } catch (Exception ex) { agentState.Text = "Maintenance non autorisée : " + ex.Message; } finally { admin.Enabled = true; } };
        layout.Controls.Add(Row(firewall, agentToggle, open, revoke, admin), 0, 1); layout.Controls.Add(identity, 0, 2); layout.Controls.Add(pairing, 0, 3); layout.Controls.Add(agentState, 0, 4);
        layout.Controls.Add(Label("Pare-feu Windows : autorise RemoteDebugger sur le profil Privé uniquement. TCP 45832 · UDP 45833. Fermer cette fenêtre coupe l’accès ; les applications lancées restent ouvertes."), 0, 5);
        layout.Controls.Add(agentLog, 0, 6); page.Controls.Add(layout); tabs.TabPages.Add(page);
    }
    public void StartAgent()
    {
        try
        {
            agent = new AgentServer(root, loopbackOnly: loopbackOnly); agent.Status += text => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { agentLog.AppendText(text + Environment.NewLine); agentState.Text = agent?.Paired == true ? "CONNECTÉ / appairé — contrôle distant autorisé" : "EN ÉCOUTE — aucun pilote appairé"; if (agent?.Paired == true) { pairing.Text = "Appairage terminé"; CurrentPairingCode = null; } }); };
            agent.Start(); identity.Text = $"Machine : {Environment.MachineName}\r\nEmpreinte TLS SHA-256 à comparer sur le pilote :\r\n{agent.Fingerprint}";
            agentToggle.Text = "Couper l’agent"; agentState.Text = "EN ÉCOUTE — accès visible";
        }
        catch (Exception ex) { agent?.Dispose(); agent = null; agentState.Text = "Démarrage impossible : " + ex.Message; }
    }
    public void StopAgent() { agent?.Dispose(); agent = null; agentToggle.Text = "Démarrer l’agent"; agentState.Text = "ARRÊTÉ — accès coupé"; pairing.Text = "Appairage fermé"; CurrentPairingCode = null; }
    public void OpenPairing() { if (agent == null) { agentState.Text = "Démarre d’abord l’agent."; return; } if (agent.Paired) agent.Revoke(); CurrentPairingCode = agent.Pairing.Open(); pairing.Text = "Code temporaire : " + CurrentPairingCode + "  ·  3 minutes"; }
    private void BuildController()
    {
        var page = new PagePanel("Actions et diagnostics"); var p = Layout(8);
        foreach (float h in new[] { 52f, 100f, 38f, 52f, 54f, 100f, 42f }) p.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, h)); p.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100));
        var discover = Button("Découvrir les PC", "discover"); discover.Click += async (_, _) => await DoAsync(async ct => { var found = await Discovery.FindAsync(2500, ct); peers.DataSource = found; controllerState.Text = $"{found.Count} agent(s) trouvé(s). Compare l’empreinte sur le portable."; });
        var state = Button("État", "status"); state.Click += async (_, _) => await ExecuteAsync("status", new { });
        p.Controls.Add(Row(discover, state, controllerState), 0, 0);
        var connection = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
        connection.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Absolute, 230)); connection.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Absolute, 105)); connection.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
        connection.Controls.Add(peers, 0, 0); connection.SetRowSpan(peers, 2); connection.Controls.Add(Label("Adresse IP"), 1, 0); connection.Controls.Add(host, 2, 0); connection.Controls.Add(Label("Empreinte"), 1, 1); connection.Controls.Add(fingerprint, 2, 1);
        peers.SelectedIndexChanged += (_, _) => { if (peers.SelectedItem is Peer peer) { host.Text = peer.Host; fingerprint.Text = peer.Fingerprint; verified.Checked = false; } }; p.Controls.Add(connection, 0, 1);
        code.Width = 110; code.Dock = Forms.DockStyle.None; code.UseSystemPasswordChar = true; var pairButton = Button("Appairer", "pair");
        pairButton.Click += async (_, _) => await DoAsync(async ct => { if (!verified.Checked) throw new InvalidOperationException("Compare l’empreinte avec l’écran de l’agent et coche la case."); client = new RemoteClient(new Connection(host.Text.Trim(), 45832, fingerprint.Text.Trim(), "")); await client.PairAsync(code.Text.Trim(), ct); client.Save(); code.Clear(); controllerState.Text = "Appairé. Contrôle de la session distant autorisé."; });
        p.Controls.Add(verified, 0, 2); p.Controls.Add(Row(Label("Code agent"), code, pairButton), 0, 3);
        var execute = Button("Exécuter", "execute"); var cancel = Button("Annuler", "cancel");
        operations.Items.AddRange(Templates.Keys.Cast<object>().ToArray()); operations.SelectedIndexChanged += (_, _) => arguments.Text = Templates[(string)operations.SelectedItem!].Replace("1234", ((int)pid.Value).ToString()); operations.SelectedIndex = 0;
        execute.Click += async (_, _) => await DoAsync(async ct => { var a = JsonSerializer.Deserialize<JsonElement>(arguments.Text); await SendAsync((string)operations.SelectedItem!, a, ct); });
        cancel.Click += async (_, _) => { action?.Cancel(); if (client != null && operationId != null) try { await client.CallAsync("cancel", new { id = operationId }, seconds: 5); } catch (Exception ex) { controllerState.Text = ex.Message; } };
        p.Controls.Add(Row(Label("Action"), operations, Label("PID cible"), pid, execute, cancel), 0, 4); p.Controls.Add(arguments, 0, 5);
        var upload = Button("Déployer un fichier…", "upload"); var folder = Button("Déployer un dossier…", "uploadFolder"); var download = Button("Récupérer un fichier…", "download");
        upload.Click += async (_, _) => { using var dialog = new Forms.OpenFileDialog(); if (dialog.ShowDialog() == Forms.DialogResult.OK) await DoAsync(async ct => { RequireClient(); output.Text = Pretty(await client!.UploadAsync(dialog.FileName, destination.Text.TrimEnd('/', '\\') + "/" + Path.GetFileName(dialog.FileName), ct)); }); };
        folder.Click += async (_, _) => { using var dialog = new Forms.FolderBrowserDialog(); if (dialog.ShowDialog() == Forms.DialogResult.OK) await DoAsync(async ct => { RequireClient(); var results = new List<JsonElement>(); foreach (string file in Directory.EnumerateFiles(dialog.SelectedPath, "*", SearchOption.AllDirectories)) { if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Reparse points are not deployed."); results.Add(await client!.UploadAsync(file, destination.Text.TrimEnd('/', '\\') + "/" + Path.GetRelativePath(dialog.SelectedPath, file), ct)); } output.Text = Pretty(results); }); };
        download.Click += async (_, _) => { using var dialog = new Forms.SaveFileDialog { FileName = Path.GetFileName(remotePath.Text) }; if (dialog.ShowDialog() == Forms.DialogResult.OK) await DoAsync(async ct => { RequireClient(); await client!.DownloadAsync(remotePath.Text, dialog.FileName, ct); output.Text = "Fichier vérifié et enregistré : " + dialog.FileName; }); };
        var files = new PagePanel("Fichiers"); var fp = Layout(6); fp.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 65)); fp.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 40)); fp.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 65)); fp.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 40)); fp.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 60)); fp.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100));
        var fileList = new Forms.ListView { Dock = Forms.DockStyle.Fill, View = Forms.View.Details, FullRowSelect = true, Name = "remoteFiles" }; fileList.Columns.Add("Nom", 380); fileList.Columns.Add("Type", 90); fileList.Columns.Add("Modifié (UTC)", 240);
        var browse = Button("Explorer le dossier", "browseFiles"); var parent = Button("Dossier parent", "parentFolder");
        async Task BrowseFiles(CancellationToken ct) { RequireClient(); var data = RemoteClient.Require(await client!.CallAsync("files", new { path = remotePath.Text }, ct)); fileList.Items.Clear(); foreach (var entry in data.EnumerateArray()) { var item = new Forms.ListViewItem(entry.Str("name")) { Tag = entry }; item.SubItems.Add(entry.GetProperty("directory").GetBoolean() ? "Dossier" : "Fichier"); item.SubItems.Add(entry.Str("modifiedUtc")); fileList.Items.Add(item); } if (remotePath.Text.Length == 0) remotePath.Text = RemoteClient.Require(await client.CallAsync("status", ct: ct)).Str("workspace"); }
        browse.Click += async (_, _) => await DoAsync(BrowseFiles); parent.Click += async (_, _) => { remotePath.Text = Path.GetDirectoryName(remotePath.Text) ?? remotePath.Text; await DoAsync(BrowseFiles); };
        fileList.SelectedIndexChanged += (_, _) => { if (fileList.SelectedItems.Count > 0 && fileList.SelectedItems[0].Tag is JsonElement entry && !entry.GetProperty("directory").GetBoolean()) remotePath.Text = entry.Str("path"); };
        fileList.DoubleClick += async (_, _) => { if (fileList.SelectedItems.Count == 0 || fileList.SelectedItems[0].Tag is not JsonElement entry) return; remotePath.Text = entry.Str("path"); if (entry.GetProperty("directory").GetBoolean()) await DoAsync(BrowseFiles); };
        fp.Controls.Add(Label("Déployer sous le dossier de travail de l’agent. Utilise un nouveau dossier pour chaque version (ex. deployments/CloudNav/1.2.3)."), 0, 0); fp.Controls.Add(destination, 0, 1); fp.Controls.Add(Row(upload, folder), 0, 2); fp.Controls.Add(remotePath, 0, 3); fp.Controls.Add(Row(browse, parent, download), 0, 4); fp.Controls.Add(fileList, 0, 5); files.Controls.Add(fp);
        p.Controls.Add(Label("Résultat JSON · arguments éditables ci-dessus · les commandes agissent avec les droits de l’utilisateur distant"), 0, 6); p.Controls.Add(output, 0, 7); page.Controls.Add(p); controllerTabs.TabPages.Add(page); pendingFiles = files;
    }
    private PagePanel? pendingFiles;
    private void BuildScreen()
    {
        var page = new PagePanel("Écran distant"); var p = Layout(3); p.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 60)); p.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100)); p.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 60));
        var capture = Button("Actualiser l’écran", "screenshot"); capture.Click += async (_, _) => await DoAsync(RefreshScreenAsync);
        liveButton.Click += async (_, _) => { if (liveStream != null) liveStream.Cancel(); else await StartStreamAsync(); };
        p.Controls.Add(Row(liveButton, capture, Label("Écran"), monitor, mouseEnabled, streamStatus), 0, 0); p.Controls.Add(screen, 0, 1);
        input.Width = 420; input.Dock = Forms.DockStyle.None; var type = Button("Saisir", "typeText"); var enter = Button("Entrée", "enterKey");
        type.Click += async (_, _) => await ExecuteAsync("ui.text", new { pid = (int)pid.Value, text = input.Text }); enter.Click += async (_, _) => await ExecuteAsync("ui.key", new { pid = (int)pid.Value, key = "ENTER" });
        mouseEnabled.Text = "Souris et clavier distants"; screen.TabStop = true;
        screen.MouseDown += (_, e) => { screen.Focus(); QueueMouse("down", e); };
        screen.MouseUp += (_, e) => QueueMouse("up", e);
        screen.MouseMove += (_, e) => { long now = Environment.TickCount64; if (now - lastMove < 33) return; lastMove = now; QueueMouse("move", e); };
        screen.MouseWheel += (_, e) => QueueMouse("wheel", e);
        screen.PreviewKeyDown += (_, e) => e.IsInputKey = true;
        screen.KeyDown += (_, e) => { if (!mouseEnabled.Checked) return; e.SuppressKeyPress = true; QueueInput(new { kind = "keyDown", virtualKey = (int)e.KeyCode }); };
        screen.KeyUp += (_, e) => { if (!mouseEnabled.Checked) return; e.SuppressKeyPress = true; QueueInput(new { kind = "keyUp", virtualKey = (int)e.KeyCode }); };
        screen.LostFocus += (_, _) => QueueInput(new { kind = "release" });
        mouseEnabled.CheckedChanged += (_, _) => { if (!mouseEnabled.Checked) QueueInput(new { kind = "release" }); };
        p.Controls.Add(Row(input, type, enter), 0, 2); page.Controls.Add(p); controllerTabs.TabPages.Add(page);
    }
    private PagePanel BuildResources()
    {
        var page = new PagePanel("Processus & système"); var p = Layout(3); p.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 60)); p.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 80)); p.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100));
        var refresh = Button("Mesurer CPU, RAM et processus", "refreshResources"); var summary = new Forms.TextBox { Multiline = true, ReadOnly = true, Dock = Forms.DockStyle.Fill }; var list = new Forms.ListView { View = Forms.View.Details, FullRowSelect = true, Dock = Forms.DockStyle.Fill, Name = "processList" };
        foreach (var (name, width) in new[] { ("PID", 90), ("Application", 260), ("CPU total %", 110), ("RAM Mio", 110), ("Réponse", 110), ("Fenêtre", 350) }) list.Columns.Add(name, width);
        refresh.Click += async (_, _) => await DoAsync(async ct =>
        {
            RequireClient(); var data = RemoteClient.Require(await client!.CallAsync("processes", ct: ct)); list.Items.Clear();
            foreach (var process in data.GetProperty("processes").EnumerateArray())
            {
                string Number(string field, double scale = 1) => process.GetProperty(field).ValueKind == JsonValueKind.Number ? (process.GetProperty(field).GetDouble() / scale).ToString("F1") : "Indisponible";
                var item = new Forms.ListViewItem(process.Int("pid").ToString()) { Tag = process.Int("pid") }; item.SubItems.Add(process.Str("name")); item.SubItems.Add(Number("cpuPercentTotalMachine")); item.SubItems.Add(Number("workingSetBytes", 1024 * 1024)); item.SubItems.Add(process.GetProperty("responding").ValueKind == JsonValueKind.Null ? "—" : process.GetProperty("responding").GetBoolean() ? "Oui" : "Bloqué"); item.SubItems.Add(process.Str("window")); list.Items.Add(item);
            }
            var system = RemoteClient.Require(await client.CallAsync("system", ct: ct));
            string cpu = system.GetProperty("cpuPercentTotalMachine").ValueKind == JsonValueKind.Number ? system.GetProperty("cpuPercentTotalMachine").GetDouble().ToString("F1") + " %" : "indisponible";
            summary.Text = $"Mesuré à {data.Str("sampleEndUtc")} · CPU total {cpu} · fenêtre {data.GetProperty("intervalMs").GetDouble():F0} ms\r\n" + string.Join("   ", system.GetProperty("volumes").EnumerateArray().Where(v => v.GetProperty("freeBytes").ValueKind == JsonValueKind.Number).Select(v => $"{v.Str("name")} {v.Long("freeBytes") / 1073741824.0:F1} Gio libres"));
        });
        list.SelectedIndexChanged += (_, _) => { if (list.SelectedItems.Count > 0) { pid.Value = (int)list.SelectedItems[0].Tag!; arguments.Text = "{\"pid\":" + pid.Value + "}"; } };
        p.Controls.Add(Row(refresh, Label("Choisir une ligne définit le PID cible pour les actions.")), 0, 0); p.Controls.Add(summary, 0, 1); p.Controls.Add(list, 0, 2); page.Controls.Add(p); return page;
    }
    private void RequireClient() { if (client == null) throw new InvalidOperationException("Appaire d’abord un agent."); if (client.Connection.Host != host.Text.Trim() || client.Connection.Fingerprint != fingerprint.Text.Trim()) throw new InvalidOperationException("La cible a changé. Appaire-la avant d’envoyer une action."); }
    public async Task ExecuteAsync(string op, object args) => await DoAsync(ct => SendAsync(op, args, ct));
    private async Task SendAsync(string op, object args, CancellationToken ct)
    {
        RequireClient(); operationId = Guid.NewGuid().ToString(); var reply = await client!.CallAsync(op, args, ct, operationId, 120); output.Text = Pretty(reply);
        var data = RemoteClient.Require(reply); if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("pid", out var id)) pid.Value = id.GetInt32();
    }
    private async Task RefreshScreenAsync(CancellationToken ct)
    {
        RequireClient(); var sw = Stopwatch.StartNew(); var data = RemoteClient.Require(await client!.CallAsync("screenshot", new { monitor = (int)monitor.Value }, ct: ct));
        Present(data.Deserialize<ScreenFrame>(Json.Options)!); streamStatus.Text = $"Capture fraîche · {sw.Elapsed.TotalMilliseconds:F0} ms aller-retour";
    }
    private void Present(ScreenFrame frame)
    {
        geometry = frame.Geometry;
        using var ms = new MemoryStream(Convert.FromBase64String(frame.Data)); using var image = Image.FromStream(ms); var previous = screen.Image; screen.Image = new Bitmap(image); previous?.Dispose();
        screen.Refresh(); // Complete the paint before the transport acknowledges presentation.
    }
    private async Task StartStreamAsync()
    {
        try { RequireClient(); } catch (Exception ex) { streamStatus.Text = ex.Message; return; }
        liveStream = new CancellationTokenSource(); liveButton.Text = "Arrêter le direct"; monitor.Enabled = false;
        var elapsed = Stopwatch.StartNew(); int frames = 0; long bytes = 0;
        try
        {
            while (!liveStream.IsCancellationRequested)
            {
                try
                {
                    await client!.StreamAsync(frame => { Present(frame); frames++; bytes += frame.Data.Length + 400; if (frames % StreamPolicy.MaximumFps == 0) streamStatus.Text = $"{frames / elapsed.Elapsed.TotalSeconds:F1} i/s · {bytes * 8 / elapsed.Elapsed.TotalSeconds / 1e6:F1} Mbit/s · enc. {frame.CaptureEncodeMs:F0} ms"; return Task.CompletedTask; }, monitor: (int)monitor.Value, ct: liveStream.Token);
                }
                catch (EndOfStreamException) when (!liveStream.IsCancellationRequested) { streamStatus.Text = "Reconnexion du direct…"; await Task.Delay(1000, liveStream.Token); }
            }
        }
        catch (OperationCanceledException) { streamStatus.Text = "Direct arrêté"; }
        catch (Exception ex) { streamStatus.Text = "Direct interrompu : " + ex.Message; }
        finally { liveStream.Dispose(); liveStream = null; liveButton.Text = "Démarrer le direct · 5 i/s"; monitor.Enabled = true; QueueInput(new { kind = "release" }); }
    }
    private void QueueMouse(string kind, Forms.MouseEventArgs e)
    {
        if (!mouseEnabled.Checked || geometry == null) return;
        var point = geometry.MapLetterbox(screen.Width, screen.Height, e.X, e.Y); if (point == null) { if (kind == "up") QueueInput(new { kind = "release" }); return; }
        QueueInput(new { kind, x = point.Value.X, y = point.Value.Y, layoutId = geometry.LayoutId, button = e.Button == Forms.MouseButtons.Right ? "right" : e.Button == Forms.MouseButtons.Middle ? "middle" : "left", delta = e.Delta });
    }
    private void QueueInput(object value)
    {
        if (client == null) return;
        if (!inputQueue.Writer.TryWrite(value)) { mouseEnabled.Checked = false; streamStatus.Text = "File d’entrée saturée. Contrôle suspendu ; relâchement automatique sous 3 s."; }
    }
    private async Task PumpInputAsync()
    {
        await foreach (var args in inputQueue.Reader.ReadAllAsync())
        {
            try { if (client != null) RemoteClient.Require(await client.CallAsync("ui.input", args, seconds: 5)); }
            catch (Exception ex) { if (!IsDisposed) { mouseEnabled.Checked = false; streamStatus.Text = "Entrée interrompue : " + ex.Message; } }
        }
    }
    private async Task DoAsync(Func<CancellationToken, Task> work)
    {
        if (action != null) { controllerState.Text = "Une action est en cours ; annule-la ou attends son résultat."; return; }
        action = new CancellationTokenSource(); controllerState.Text = "En cours…";
        try { await work(action.Token); controllerState.Text = "Terminé · " + DateTime.Now.ToString("HH:mm:ss"); }
        catch (Exception ex) { controllerState.Text = "Échec : " + ex.Message; output.Text = Pretty(new { ok = false, message = ex.Message }); }
        finally { action.Dispose(); action = null; operationId = null; }
    }
    public static string Pretty(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(Json.Options) { WriteIndented = true });
    public static readonly Dictionary<string, string> Templates = new()
    {
        ["status"] = "{}", ["processes"] = "{}", ["process.info"] = "{\"pid\":1234}", ["start"] = "{\"path\":\"deployments/essai-1/MonApp.exe\",\"arguments\":[]}", ["stop"] = "{\"pid\":1234,\"mode\":\"graceful\"}", ["restart"] = "{\"pid\":1234,\"mode\":\"graceful\",\"arguments\":[]}",
        ["system"] = "{}", ["network"] = "{}", ["events"] = "{\"log\":\"Application\",\"count\":20}", ["services"] = "{}", ["windows"] = "{}", ["ui.inspect"] = "{\"pid\":1234}", ["ui.click"] = "{\"pid\":1234,\"automationId\":\"saveButton\"}", ["ui.key"] = "{\"pid\":1234,\"key\":\"CTRL+A\"}", ["debug.attach"] = "{\"pid\":1234,\"seconds\":3}", ["debug.dump"] = "{\"pid\":1234}",
        ["files"] = "{}", ["file.info"] = "{\"path\":\"deployments/essai-1/MonApp.exe\"}", ["command"] = "{\"file\":\"whoami.exe\",\"arguments\":[]}", ["maintenance.status"] = "{}", ["maintenance.session"] = "{\"file\":\"whoami.exe\",\"arguments\":[\"/groups\"]}", ["maintenance.elevated"] = "{\"file\":\"whoami.exe\",\"arguments\":[\"/groups\"]}", ["history"] = "{}"
    };
}

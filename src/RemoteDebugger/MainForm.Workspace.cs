using System.Text.Json;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

public sealed partial class MainForm
{
    // Auto-sized rows follow the native preferred heights at every DPI. Left
    // anchoring vertically centers labels, edits, selectors and buttons alike.
    private static Forms.TableLayoutPanel ControlRow(params Forms.Control[] controls)
    {
        var row = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Top, AutoSize = true, AutoSizeMode = Forms.AutoSizeMode.GrowAndShrink,
            ColumnCount = controls.Length, RowCount = 1, Margin = Forms.Padding.Empty };
        row.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));
        for (int i = 0; i < controls.Length; i++)
        {
            row.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.AutoSize));
            controls[i].Anchor = Forms.AnchorStyles.Left;
            controls[i].Margin = new Forms.Padding(0, 4, i == controls.Length - 1 ? 0 : 12, 4);
            row.Controls.Add(controls[i], i, 0);
        }
        return row;
    }

    private static Forms.Label RowLabel(string text, string name) => new WorkspaceLabel { Text = text, Name = name, WrapText = false, AutoSize = true, ForeColor = SecondaryText };

    private const string ConnectToContinue = "Connectez-vous depuis l’onglet Connexion.";
    private Forms.Button parentFolderButton = null!;
    private Forms.Button browseFilesButton = null!;
    private bool resourcesLoading, filesLoading;

    private void InitializeWorkspaceState()
    {
        arguments.AccessibleName = "Arguments JSON";
        output.AccessibleName = "Résultat de l’action";
        output.PlaceholderText = "Le résultat de l’action apparaîtra ici.";
        resourceState.Text = fileState.Text = diagnosticState.Text = ConnectToContinue;
        connectionState.Text = "Choisissez un PC ou saisissez son adresse IP et son code.";
        streamStatus.Text = "Aucune connexion active";
        operations.SelectedIndexChanged += (_, _) => LoadDiagnosticTemplate();
        pid.ValueChanged += (_, _) =>
        {
            try { arguments.Text = WorkspacePresentation.ArgumentsFor(arguments.Text, (int)pid.Value); }
            catch (JsonException) { /* Keep invalid JSON intact so the user can correct it. */ }
        };
        LoadDiagnosticTemplate();
        DpiChanged += (_, _) => ClampMinimumSizeToDisplay();
        Shown += (_, _) => ClampMinimumSizeToDisplay();
    }

    private void ClampMinimumSizeToDisplay()
    {
        var working = Forms.Screen.FromControl(this).WorkingArea;
        float scale = DeviceDpi / 96f;
        MinimumSize = new Size(Math.Min((int)(1060 * scale), working.Width), Math.Min((int)(720 * scale), working.Height));
    }

    private void LoadDiagnosticTemplate()
    {
        if (operations.SelectedItem is not string operation) return;
        arguments.Text = WorkspacePresentation.ArgumentsFor(Templates[operation], (int)pid.Value);
        diagnosticState.Text = supportSession ? "Prêt à exécuter." : ConnectToContinue;
        output.Clear();
        RefreshControllerControls();
    }

    private void RefreshControllerControls()
    {
        if (executeButton == null) return;
        var state = WorkspaceAvailability.For(supportSession, heartbeatHealthy, pairingBusy, terminating, action != null, selectedFilePath != null);
        pairButton.Enabled = host.Enabled = code.Enabled = state.CanPair;
        peers.Enabled = !pairingBusy && !terminating;
        pairButton.Text = pairingBusy ? "Connexion…" : supportSession ? "Connecté" : "Connecter";
        refreshResourcesButton.Enabled = state.CanOperate && !resourcesLoading;
        browseFilesButton.Enabled = fileDirectory.Enabled = state.CanOperate && !filesLoading;
        parentFolderButton.Enabled = state.CanOperate && !filesLoading && !string.IsNullOrEmpty(currentDirectory);
        uploadButton.Enabled = uploadFolderButton.Enabled = destination.Enabled = state.CanOperate;
        downloadButton.Enabled = state.CanDownload;
        executeButton.Enabled = state.CanOperate && action == null;
        if (state.CanOperate && diagnosticState.Text == ConnectToContinue) diagnosticState.Text = "Prêt à exécuter.";
        cancelButton.Enabled = state.CanCancel;
        operations.Enabled = arguments.Enabled = state.CanOperate && action == null;
        pid.Enabled = state.CanOperate && action == null && operations.SelectedItem is string operation && WorkspacePresentation.UsesPid(Templates[operation]);
        pauseViewing.Enabled = supportSession && !terminating;
        monitor.Enabled = state.CanOperate;
        typeText.Enabled = enterKey.Enabled = remoteText.Enabled = state.CanOperate && liveFrameFresh;
        technicalIdentity.Text = WorkspacePresentation.Identity(supportSession && client != null, heartbeatHealthy,
            client?.Connection.Host ?? "", client?.Connection.Fingerprint ?? "");
        if (supportSession && client != null)
        {
            selectedPeerName.Text = selectedPeer?.Name ?? client.Connection.Host;
            selectedPeerAddress.Text = heartbeatHealthy ? "Session authentifiée" : "Reconnexion en cours…";
        }
        if (!supportSession && !pairingBusy)
        {
            streamOverlay.Text = ConnectToContinue;
            streamOverlay.Visible = true;
        }
    }

    private static Forms.Control DiagnosticSection(string title, string name, Forms.Control editor, string? help = null)
    {
        var section = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 1, RowCount = help == null ? 2 : 3, Margin = Forms.Padding.Empty };
        section.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
        section.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 26));
        if (help != null) section.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 24));
        section.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100));
        section.Controls.Add(new WorkspaceLabel { Name = name, Text = title, Dock = Forms.DockStyle.Fill, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), ForeColor = PrimaryText }, 0, 0);
        if (help != null) section.Controls.Add(new WorkspaceLabel { Text = help, Dock = Forms.DockStyle.Fill, AutoEllipsis = true, ForeColor = SecondaryText }, 0, 1);
        editor.Margin = Forms.Padding.Empty;
        section.Controls.Add(editor, 0, help == null ? 1 : 2);
        return section;
    }
}

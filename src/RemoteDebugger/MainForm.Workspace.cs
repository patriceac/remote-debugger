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

    private static string ConnectToContinue => UiText.ConnectToContinue;
    private Forms.Button parentFolderButton = null!;
    private Forms.Button browseFilesButton = null!;
    private bool resourcesLoading, filesLoading;

    private void InitializeWorkspaceState()
    {
        RefreshLocalizedDescriptions();
        resourceState.SetText(() => ConnectToContinue); fileState.SetText(() => ConnectToContinue); diagnosticState.SetText(() => ConnectToContinue);
        connectionState.SetText(() => PrivateInternet ? UiText.PrivateConnectInstructions : UiText.ChoosePcAndCode);
        streamStatus.SetText(() => UiText.NoActiveConnection);
        operations.SelectedIndexChanged += (_, _) => LoadDiagnosticTemplate();
        pid.ValueChanged += (_, _) =>
        {
            try { arguments.SetText(WorkspacePresentation.ArgumentsFor(arguments.Text, (int)pid.Value)); }
            catch (JsonException) { /* Keep invalid JSON intact so the user can correct it. */ }
        };
        LoadDiagnosticTemplate();
        DpiChanged += (_, _) => ClampMinimumSizeToDisplay();
        Shown += (_, _) => ClampMinimumSizeToDisplay();
    }

    private void RefreshLocalizedDescriptions()
    {
        host.AccessibleName = UiText.Address;
        code.AccessibleName = UiText.SixDigitCode;
        remoteText.AccessibleName = UiText.RemoteTextPlaceholder;
        fileDirectory.AccessibleName = UiText.RemoteFolder;
        remotePath.AccessibleName = UiText.SelectedFile;
        fileTransferProgress.AccessibleName = UiText.FileTransfers;
        monitor.AccessibleName = UiText.Monitor;
        operations.AccessibleName = UiText.Action;
        pid.AccessibleName = "PID";
        arguments.AccessibleName = UiText.JsonArguments;
        output.AccessibleName = UiText.ActionResult;
        output.PlaceholderText = UiText.ResultPlaceholder;
        remoteText.PlaceholderText = UiText.RemoteTextPlaceholder;
        fileDirectory.PlaceholderText = UiText.RemoteFolderPlaceholder;
        remotePath.PlaceholderText = UiText.SelectFileInList;
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
        arguments.SetText(WorkspacePresentation.ArgumentsFor(Templates[operation], (int)pid.Value));
        diagnosticState.SetText(() => supportSession ? UiText.ReadyToRun : ConnectToContinue);
        output.SetText("");
        RefreshControllerControls();
    }

    private void RefreshControllerControls()
    {
        RefreshViewerControls();
        RefreshWanAddress();
        RefreshWakeControls();
        RefreshPowerControls();
        navScreen.Enabled = navProcesses.Enabled = navFiles.Enabled = navDiagnostics.Enabled = CanUseControllerWorkspace(supportSession, terminating);
        if (executeButton == null) return;
        var state = WorkspaceAvailability.For(supportSession, heartbeatHealthy, pairingBusy || clientUpdateBusy || FleetBusy || powerBusy, terminating, action != null, selectedFilePath != null);
        pairButton.Enabled = host.Enabled = code.Enabled = state.CanPair;
        peers.Enabled = !pairingBusy && !clientUpdateBusy && !terminating && !wakeBusy;
        pairButton.SetText(() => pairingBusy ? UiText.Connecting : supportSession ? UiText.Connected : UiText.Connect);
        updateClientButton.Visible = supportSession || clientUpdateBusy;
        updateClientButton.Enabled = isUpdateAdmin && !NewerDeviceKnown && CanUpdateClient(supportSession, client != null, pairingBusy, clientUpdateBusy, terminating, clientUpToDate) && action == null;
        updateClientButton.SetText(() => clientUpdateBusy ? UiText.Synchronizing : clientUpToDate ? UiText.ClientUpToDate : UiText.UpdateClient);
        updateAllDevices.Enabled = FleetBusy || isUpdateAdmin && !NewerDeviceKnown && !fleetRefreshing && !pairingBusy && !clientUpdateBusy && !terminating && action == null && fleet.Values.Any(d => d.Online && d.State is "available" or "legacy" or "failed");
        updateAllDevices.SetText(() => fleetRefreshing ? UiText.CheckingVersion : FleetBusy ? UiText.StopUpdates : UiText.UpdateAllDevices);
        if (NewerDeviceKnown) discoveryState.SetText(() => UiText.UpdateControllerFirst);
        discoverButton.Enabled = !pairingBusy && !clientUpdateBusy && !FleetBusy && !fleetRefreshing && !terminating && !supportSession;
        if (FleetBusy || fleetRefreshing || wakeBusy || powerBusy) { pairButton.Enabled = false; discoverButton.Enabled = false; updateClientButton.Enabled = false; updateAllDevices.Enabled = false; }
        refreshResourcesButton.Enabled = state.CanOperate && !resourcesLoading;
        bool filesAvailable = state.CanOperate && !filesLoading && fileTransferLifetime == null;
        browseFilesButton.Enabled = fileDirectory.Enabled = openFolderButton.Enabled = filesAvailable;
        parentFolderButton.Enabled = filesAvailable && !string.IsNullOrEmpty(currentDirectory) && ParentPath(currentDirectory) != currentDirectory;
        bool folderReady = filesAvailable && fileDirectoryLoaded && string.Equals(fileDirectory.Text.Trim(), currentDirectory, StringComparison.OrdinalIgnoreCase);
        uploadButton.Enabled = uploadFolderButton.Enabled = folderReady;
        downloadButton.Enabled = folderReady && state.CanDownload;
        fileList.Enabled = state.CanOperate && !filesLoading;
        transferCancelButton.Enabled = fileTransferLifetime != null && !fileTransferLifetime.IsCancellationRequested;
        executeButton.Enabled = state.CanOperate && action == null;
        if (state.CanOperate && diagnosticState.Text == ConnectToContinue) diagnosticState.SetText(() => UiText.ReadyToRun);
        cancelButton.Enabled = state.CanCancel;
        operations.Enabled = arguments.Enabled = state.CanOperate && action == null;
        pid.Enabled = state.CanOperate && action == null && operations.SelectedItem is string operation && WorkspacePresentation.UsesPid(Templates[operation]);
        pauseViewing.Enabled = supportSession && !clientUpdateBusy && !terminating;
        monitor.Enabled = state.CanOperate;
        typeText.Enabled = enterKey.Enabled = remoteText.Enabled = state.CanOperate && liveFrameFresh;
        technicalIdentity.SetText(WorkspacePresentation.Identity(supportSession && client != null, heartbeatHealthy,
            client?.Connection.Host ?? "", client?.Connection.Fingerprint ?? ""));
        if (supportSession && client != null)
        {
            selectedPeerName.SetText(selectedPeer?.Name ?? client.Connection.Host);
            selectedPeerAddress.SetText(() => heartbeatHealthy ? UiText.AuthenticatedSession : UiText.ReconnectionInProgress);
        }
        if (!supportSession && !pairingBusy)
        {
            streamOverlay.SetText(() => ConnectToContinue);
            streamOverlay.Visible = true;
        }
    }

    private static Forms.Control DiagnosticSection(Func<string> title, string name, Forms.Control editor, Func<string>? help = null)
    {
        var section = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 1, RowCount = help == null ? 2 : 3, Margin = Forms.Padding.Empty };
        section.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
        section.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 26));
        if (help != null) section.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 24));
        section.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100));
        section.Controls.Add(new WorkspaceLabel { Name = name, Dock = Forms.DockStyle.Fill, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), ForeColor = PrimaryText }.WithText(title), 0, 0);
        if (help != null) section.Controls.Add(new WorkspaceLabel { Dock = Forms.DockStyle.Fill, AutoEllipsis = true, ForeColor = SecondaryText }.WithText(help), 0, 1);
        editor.Margin = Forms.Padding.Empty;
        section.Controls.Add(editor, 0, help == null ? 1 : 2);
        return section;
    }
}

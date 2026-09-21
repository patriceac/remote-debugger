using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

public sealed partial class MainForm
{
    private Dictionary<string, Dictionary<string, double>> updateTimings = new();
    private UpdateProgressTracker? connectedUpdateProgress;
    private AgentUpdateProgress? connectedUpdateReport;

    private UpdateProgressTracker CreateUpdateProgress(string device)
    {
        if (!updateTimings.TryGetValue(device, out var timings) || timings == null)
            updateTimings[device] = timings = new();
        return new(timings);
    }

    private void LoadUpdateTimings()
    {
        try
        {
            string path = Path.Combine(root, "update-timings.dpapi");
            if (File.Exists(path)) updateTimings = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(Vault.Read(path), Json.Options) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or JsonException) { }
    }

    private void SaveUpdateTimings()
    {
        try { Vault.Save(Path.Combine(root, "update-timings.dpapi"), JsonSerializer.SerializeToUtf8Bytes(updateTimings, Json.Options)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { }
    }

    internal static string UpdateStepNumbers(UpdateStepProgress progress) => progress.Estimated
        ? UiText.Format(UiText.EstimatedUpdateProgress, progress.Percent, FormatTransferEta(progress.Remaining!.Value))
        : UiText.Format(UiText.MeasuredUpdateProgress, progress.Percent,
            progress.Remaining is { } eta ? FormatTransferEta(eta) : UiText.CalculatingTransferEta);

    private void RefreshUpdateProgress()
    {
        if (connectedUpdateProgress == null || connectedUpdateReport == null) return;
        var progress = connectedUpdateProgress.Snapshot();
        updateTransferPercent = progress.Percent;
        updateProgressFill.Width = updateProgressTrack.ClientSize.Width * updateTransferPercent / 100;
        string stage = connectedUpdateReport.Stage == "transferring" ? UiText.UpdatingDevice : UpdateProgressDescription(connectedUpdateReport);
        updateProgressText.SetText(stage + " · " + UpdateStepNumbers(progress));
        updateProgressTrack.AccessibleName = updateProgressText.Text;
    }
}

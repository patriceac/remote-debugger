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

    internal static string UpdateStepNumbers(UpdateStepProgress progress)
    {
        if (progress.Percent is { } percent)
            return UiText.Format(UiText.MeasuredUpdateProgress, percent,
                progress.Remaining is { } eta ? "≈ " + FormatTransferEta(eta) : UiText.CalculatingTransferEta);
        string elapsed = UiText.Format(UiText.ElapsedTime, FormatTransferEta(progress.Elapsed));
        return progress.Remaining is { } estimate
            ? elapsed + " · " + UiText.Format(UiText.EstimatedUpdateProgress, FormatTransferEta(estimate)) : elapsed;
    }

    private static Rectangle UpdateProgressBounds(Rectangle track, UpdateStepProgress progress)
    {
        if (progress.Percent is { } percent) return track with { Width = track.Width * percent / 100 };
        int width = Math.Max(1, track.Width / 4);
        double phase = (Environment.TickCount64 % 2000) / 2000d;
        return new(track.X + (int)((track.Width - width) * phase), track.Y, width, track.Height);
    }

    private void RefreshUpdateProgress()
    {
        if (connectedUpdateProgress == null || connectedUpdateReport == null) return;
        var progress = connectedUpdateProgress.Snapshot();
        updateProgressFill.Dock = System.Windows.Forms.DockStyle.None;
        updateProgressFill.Bounds = UpdateProgressBounds(updateProgressTrack.ClientRectangle, progress);
        string stage = connectedUpdateReport.Stage == "transferring" ? UiText.UpdatingDevice : UpdateProgressDescription(connectedUpdateReport);
        string finished = string.Join(" · ", connectedUpdateProgress.CompletedStages.Select(completed => "✓ " + (completed == "transferring" ? UiText.FileSent : UpdateProgressDescription(new AgentUpdateProgress(completed, 0, 0)))));
        updateProgressText.SetText(stage + " · " + UpdateStepNumbers(progress) + (finished.Length == 0 ? "" : Environment.NewLine + finished));
        updateProgressTrack.AccessibleName = updateProgressText.Text;
    }
}

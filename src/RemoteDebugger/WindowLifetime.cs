using System.Windows.Forms;

namespace RemoteDebugger;

public enum AgentStopReason { SupportEnded, UpdateReplacement }

internal static class WindowLifetime
{
    public static bool StartInTray(IReadOnlyCollection<string> arguments) =>
        arguments.Count == 0 || arguments.Contains("--startup") || arguments.Contains("--resume-update");
    public static bool ExitAfterAgentStop(AgentStopReason reason) => reason == AgentStopReason.UpdateReplacement;
    public static bool RestartAfterAgentStop(AgentStopReason reason) => reason == AgentStopReason.SupportEnded;
    public static bool EnableSupportAtStartup(bool privateInternet, bool protectedProfileAvailable, bool explicitRequest) =>
        !privateInternet || protectedProfileAvailable || explicitRequest;
    public static bool HideToTray(CloseReason reason, bool explicitlyQuitting) =>
        !explicitlyQuitting && reason is CloseReason.UserClosing or CloseReason.TaskManagerClosing or CloseReason.None;
}

using System.Windows.Forms;

namespace RemoteDebugger;

public enum AgentStopReason { SupportEnded, UpdateReplacement }

internal static class WindowLifetime
{
    public static bool ExitAfterAgentStop(AgentStopReason reason) => reason == AgentStopReason.UpdateReplacement;
    public static bool HideToTray(CloseReason reason, bool explicitlyQuitting) =>
        !explicitlyQuitting && reason is CloseReason.UserClosing or CloseReason.TaskManagerClosing or CloseReason.None;
}

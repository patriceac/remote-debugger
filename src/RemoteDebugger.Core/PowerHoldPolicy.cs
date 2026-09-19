namespace RemoteDebugger.Core;

/// <summary>Decides when an agent's authenticated session needs the system awake.</summary>
public static class PowerHoldPolicy
{
    /// <summary>
    /// An unpaired or still-synchronizing listener may sleep. A matched session
    /// keeps the machine awake while connected and during its reconnect grace.
    /// </summary>
    public static bool ShouldHoldForAgent(SupportSessionSnapshot session) =>
        session.HasPaired && (session.State is "connected" or "synchronizing" or "reconnecting");
}

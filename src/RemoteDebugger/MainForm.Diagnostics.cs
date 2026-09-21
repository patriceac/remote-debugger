using RemoteDebugger.Core;

namespace RemoteDebugger;

public sealed partial class MainForm
{
    private IncidentLog diagnostics = null!;
    private DateTimeOffset lastDiagnosticSample, lastPresentedAt;
    private bool diagnosticRunStarted;

    private SupportDiagnosticContext DiagnosticContext() => new(
        client?.Connection.Host ?? Environment.MachineName, supportSession ? "controller" : "agent",
        typeof(MainForm).Assembly.GetName().Version?.ToString() ?? "", sessionGeneration.ToString(), client?.ActiveRoute ?? "",
        supportSession && heartbeatHealthy, liveFrameFresh, inputState.Enabled, inputState.Suspended,
        screen.ContainsFocus && ContainsFocus,
        lastPresentedAt == default ? null : Math.Max(0, (DateTimeOffset.UtcNow - lastPresentedAt).TotalMilliseconds));

    private void RecordIncident(string name, Exception exception)
    {
        if (terminating || quitting || (clientUpdateBusy || powerBusy) && name is "connection_lost" or "stream_interrupted" or "input_failed" or "clipboard_interrupted") return;
        diagnostics.Record(name, DiagnosticContext(), (exception as RemoteOperationException)?.Code, exception, incident: true);
    }

    private void SampleDiagnostics()
    {
        if (!diagnosticRunStarted || DateTimeOffset.UtcNow - lastDiagnosticSample < TimeSpan.FromSeconds(5)) return;
        lastDiagnosticSample = DateTimeOffset.UtcNow;
        diagnostics.Record("support_state", DiagnosticContext());
    }
}

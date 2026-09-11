using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using RemoteDebugger;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("The Lab needs a role and an output directory.");
        Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
        Forms.Application.EnableVisualStyles();
        Forms.Application.Run(new LabForm(args[0], args[1], args.Length > 2 ? args[2] : "runtime", args.Length > 3 ? args[3] : "none"));
    }
}

internal sealed partial class LabForm : Forms.Form
{
    private const int CoordinationPort = 45835;
    private static readonly Regex SixDigits = new("^[0-9]{6}$", RegexOptions.CultureInvariant);
    private static readonly Regex TimeText = new("(?:^|\\s)(?:[0-5]?\\d):[0-5]\\d(?:$|\\s)", RegexOptions.CultureInvariant);
    private readonly string role;
    private readonly string output;
    private readonly string scope;
    private readonly string updateVariant;
    private string application;
    private readonly string productData;
    private readonly Forms.TextBox log = new() { Dock = Forms.DockStyle.Fill, Multiline = true, ScrollBars = Forms.ScrollBars.Vertical, ReadOnly = true };
    private readonly CancellationTokenSource stop = new(TimeSpan.FromMinutes(22));
    private readonly List<CheckRecord> checks = [];
    private readonly object checkLock = new();
    private readonly JsonElement contract;
    private Process? product;
    private int serial;
    private bool sawPairing;
    private bool sawTermination;
    private string? pairingCountdownAtOpen;
    private string? peerHost;
    private string? peerFingerprint;
    private string? workspace;
    private bool guestElevated;
    private string? rollbackCandidateHash;
    private bool retainCoordinationAfterProductExit;
    private bool rollbackCandidateKilled;
    private bool managedRelaunched;
    private bool sleepRequestObserved;
    private bool IsAgent => role is "agent" or "agent-local";
    private bool IsLoopback => role is "local" or "ui-local";
    private bool IsUpdateVariant => updateVariant is not ("none" or "");
    private bool RequiresProvisioning => scope is "full" or "provisioned";
    private string ContractId(string key) => contract.GetProperty("uiAutomationIds").GetProperty(key).GetString()!;
    private int PairingSeconds(string key, int fallback) => contract.GetProperty("pairing").TryGetProperty(key, out var value) && value.TryGetInt32(out int seconds) ? seconds : fallback;

    private sealed record CheckRecord(string Id, string Requirement, string Status, bool Required, DateTimeOffset Utc, object? Evidence);

    public LabForm(string role, string output, string scope, string updateVariant)
    {
        this.role = role.Trim().ToLowerInvariant();
        this.output = Path.GetFullPath(output);
        this.scope = scope.Trim().ToLowerInvariant();
        this.updateVariant = updateVariant.Trim().ToLowerInvariant();
        application = ResolveApplicationPath(this.role, this.updateVariant);
        productData = Path.Combine(this.output, "product-data");
        contract = LoadContract();
        Directory.CreateDirectory(this.output);
        Text = "Remote Debugger acceptance lab — " + this.role;
        Width = 820;
        Height = 480;
        Controls.Add(log);
        Shown += async (_, _) =>
        {
            try
            {
                if (role is "loopback" or "loopback-smoke") await LoopbackSmokeAsync();
                else if (role is "loopback-lifetime") await LoopbackLifetimeAsync();
                else if (IsAgent) await AgentAsync();
                else await ControllerAsync();
            }
            catch (Exception ex)
            {
                Record("lab.fatal", "The acceptance Lab completed without hiding an exception", "fail", true, new { error = ex.ToString() });
                await FinishAsync(ex.ToString());
            }
        };
        FormClosed += (_, _) => stop.Cancel();
    }

    private static string ResolveApplicationPath(string role, string variant)
    {
        string relative = variant switch
        {
            "upgrade" or "rollback" => role is "agent" or "agent-local" ? "../update-fixtures/older/RemoteDebugger.exe" : "../update-fixtures/newer/RemoteDebugger.exe",
            "downgrade" => role is "agent" or "agent-local" ? "../update-fixtures/newer/RemoteDebugger.exe" : "../update-fixtures/older/RemoteDebugger.exe",
            "sameversion" or "same-version" => role is "agent" or "agent-local" ? "../update-fixtures/same-version/RemoteDebugger.exe" : "../release/RemoteDebugger.exe",
            "none" or "" => "../release/RemoteDebugger.exe",
            _ => throw new ArgumentException("Unknown update variant: " + variant)
        };
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relative));
    }

    private JsonElement LoadContract()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "acceptance-contract.json");
        if (!File.Exists(path)) throw new FileNotFoundException("Acceptance contract was not published with the Lab.", path);
        var value = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));
        if (value.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Unsupported acceptance contract version.");
        return value;
    }

    private void Record(string id, string requirement, string status, bool required, object? evidence = null)
    {
        if (status is not ("pass" or "fail" or "blocked")) throw new ArgumentException("Invalid check status.");
        lock (checkLock) checks.Add(new CheckRecord(id, requirement, status, required, DateTimeOffset.UtcNow, evidence));
        if (InvokeRequired) BeginInvoke(() => log.AppendText(id + ": " + status.ToUpperInvariant() + Environment.NewLine));
        else log.AppendText(id + ": " + status.ToUpperInvariant() + Environment.NewLine);
        WriteProgress();
    }

    private void Log(string message)
    {
        if (InvokeRequired) BeginInvoke(() => log.AppendText(message + Environment.NewLine));
        else log.AppendText(message + Environment.NewLine);
    }

    private void Pass(string id, string requirement, object? evidence = null, bool required = true) => Record(id, requirement, "pass", required, evidence);
    private void Fail(string id, string requirement, object? evidence = null, bool required = true) => Record(id, requirement, "fail", required, evidence);
    private void Block(string id, string requirement, string capability, object? evidence = null, bool? required = null)
        => Record(id, requirement, "blocked", required ?? RequiresProvisioning, new { capability, evidence });

    private void WriteProgress()
    {
        CheckRecord[] snapshot;
        lock (checkLock) snapshot = checks.ToArray();
        File.WriteAllText(Path.Combine(output, "progress.json"), Json.Text(new { schemaVersion = 2, role, scope, updateVariant, checks = snapshot }));
    }

    private async Task FinishAsync(string? fatal = null)
    {
        CheckRecord[] snapshot;
        lock (checkLock) snapshot = checks.ToArray();
        bool passed = fatal == null && snapshot.All(x => !x.Required || x.Status == "pass");
        bool complete = snapshot.All(x => x.Status == "pass");
        string releaseHash = "";
        if (File.Exists(application)) releaseHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(application, stop.Token)));
        var result = new
        {
            schemaVersion = 2,
            passed,
            testEvaluated = true,
            coverageComplete = complete,
            role,
            scope,
            updateVariant,
            machine = Environment.MachineName,
            release = new { path = application, sha256 = releaseHash },
            checks = snapshot,
            fatal
        };
        string marker = Path.Combine(output, "lab-result.json");
        string temporary = marker + ".tmp";
        await File.WriteAllTextAsync(temporary, Json.Text(result), stop.Token);
        File.Move(temporary, marker, true);
    }

    private Process LaunchProduct(bool agent)
    {
        var psi = new ProcessStartInfo(application) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(application)! };
        // The agent is intentionally launched without a role flag: this is the
        // acceptance proof that the shipped default opens the agent view and
        // starts its listener. --controller is the only explicit alternate role.
        if (!agent) psi.ArgumentList.Add("--controller");
        psi.ArgumentList.Add("--data-root");
        psi.ArgumentList.Add(productData);
        return Process.Start(psi) ?? throw new IOException("Release process did not start.");
    }

    private async Task WaitUiAsync()
    {
        if (product == null) throw new InvalidOperationException("Release process was not launched.");
        for (int n = 0; n < 180; n++)
        {
            product.Refresh();
            if (product.HasExited) throw new IOException("Release process exited during startup: " + product.ExitCode);
            if (product.MainWindowHandle != IntPtr.Zero) { await Task.Delay(500, stop.Token); return; }
            await Task.Delay(250, stop.Token);
        }
        throw new TimeoutException("The Release window did not appear.");
    }

    private AutomationElement Root()
    {
        product?.Refresh();
        if (product == null || product.HasExited || product.MainWindowHandle == IntPtr.Zero) throw new InvalidOperationException("Release window is unavailable.");
        return AutomationElement.FromHandle(product.MainWindowHandle);
    }

    private AutomationElement? Find(string id, int milliseconds = 1200)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < milliseconds)
        {
            try
            {
                var found = Root().FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
                if (found != null) return found;
            }
            catch (ElementNotAvailableException) { }
            Thread.Sleep(100);
        }
        return null;
    }

    private AutomationElement Element(string key)
    {
        string id = ContractId(key);
        var found = Find(id, 5000);
        if (found != null) return found;
        var all = Root().FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
            .Select(x => new { id = Safe(() => x.Current.AutomationId), name = Safe(() => x.Current.Name), type = Safe(() => x.Current.ControlType.ProgrammaticName) })
            .Take(160).ToArray();
        File.WriteAllText(Path.Combine(output, "ui-inventory.json"), Json.Text(all));
        throw new InvalidOperationException("Missing stable UI Automation id " + id);
    }

    private AutomationElement? FindVisibleId(string id)
    {
        var found = Find(id, 100);
        if (found == null) return null;
        try { return found.Current.IsOffscreen ? null : found; } catch (ElementNotAvailableException) { return null; }
    }

    private static string Safe(Func<string> read)
    {
        try { return read(); } catch (ElementNotAvailableException) { return "<unavailable>"; }
    }

    private string Value(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && value is ValuePattern pattern) return pattern.Current.Value;
            return element.Current.Name;
        }
        catch (ElementNotAvailableException) { return ""; }
    }

    private string UiValue(string key)
    {
        var element = Element(key);
        string value = Value(element);
        if (!string.Equals(key, "connectionStatus", StringComparison.Ordinal)) return value;

        // WinForms exposes the status pill as a Panel.  New builds publish an
        // AccessibleName on that panel, while older builds expose the child
        // status label only.  Read both so the acceptance check follows the
        // actual accessible state instead of depending on one provider shape.
        try
        {
            var children = element.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                .Select(child =>
                {
                    try { return Value(child); }
                    catch (ElementNotAvailableException) { return ""; }
                })
                .Where(text => !string.IsNullOrWhiteSpace(text));
            return string.Join(" ", new[] { value }.Concat(children));
        }
        catch (ElementNotAvailableException) { return value; }
    }

    private string[] UiTexts()
    {
        try
        {
            return Root().FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                .Select(x =>
                {
                    try
                    {
                        if (x.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && value is ValuePattern pattern && !x.Current.IsPassword) return pattern.Current.Value;
                        return x.Current.Name;
                    }
                    catch (ElementNotAvailableException) { return ""; }
                })
                .Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        }
        catch (Exception) { return []; }
    }

    private AutomationElement? FindNameContaining(string fragment)
    {
        try
        {
            return Root().FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().FirstOrDefault(e =>
            {
                try { return e.Current.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase); } catch (ElementNotAvailableException) { return false; }
            });
        }
        catch (ElementNotAvailableException) { return null; }
    }

    private new void Click(string key)
    {
        var element = Element(key);
        InvokeElement(element);
    }

    private void Set(string key, string value)
    {
        var element = Element(key);
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) || pattern is not ValuePattern valuePattern) throw new InvalidOperationException("Control is not editable: " + ContractId(key));
        valuePattern.SetValue(value);
    }

    private void FocusAndEnter(string key)
    {
        Element(key).SetFocus();
        Native.FocusWindow(product!.Id);
        Native.Key(product.Id, "ENTER");
    }

    private async Task WaitForUiAsync(Func<bool> ready, int seconds = 45)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (ready()) return;
            await Task.Delay(250, stop.Token);
        }
        throw new TimeoutException("Expected UI state did not appear: " + string.Join(" | ", UiTexts().Take(40)));
    }

    private static bool IsConnected(string text)
    {
        string value = text.ToLowerInvariant();
        return (value.Contains("connect") || value.Contains("connecté") || value.Contains("connected"))
            && !value.Contains("déconnect") && !value.Contains("disconnected") && !value.Contains("reconnexion");
    }

    private static bool IsLiveBadge(string text)
    {
        string value = text.ToLowerInvariant();
        return value.Contains("live") || value.Contains("direct") || value.Contains("en direct");
    }

    private static bool IsLiveTelemetry(string text)
    {
        string value = text.ToLowerInvariant();
        return Regex.IsMatch(value, @"(?<!\d)\d+(?:[.,]\d+)?\s*i/s", RegexOptions.CultureInvariant)
            && value.Contains("capture", StringComparison.Ordinal);
    }

    private static bool IsPairingAttempt(string text)
    {
        string value = text.ToLowerInvariant();
        return IsConnected(value) || value.Contains("appair", StringComparison.Ordinal)
            || value.Contains("pair", StringComparison.Ordinal)
            || value.Contains("synchron", StringComparison.Ordinal);
    }

    private sealed record LiveEvidence(bool BadgeVisible, string BadgeText, bool TelemetryVisible, string TelemetryText, double WaitedSeconds);

    private async Task<LiveEvidence> WaitForLiveEvidenceAsync(int seconds)
    {
        var elapsed = Stopwatch.StartNew();
        string badgeText = "";
        string telemetryText = "";
        bool badgeVisible = false;
        bool telemetryVisible = false;
        while (elapsed.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            var badge = FindVisibleId(ContractId("liveBadge"));
            badgeVisible = badge != null;
            badgeText = badge == null ? "" : Value(badge);
            var telemetry = FindVisibleId(ContractId("streamStatus"));
            telemetryText = telemetry == null ? "" : Value(telemetry);
            telemetryVisible = IsLiveTelemetry(telemetryText);
            if (badgeVisible && IsLiveBadge(badgeText) && telemetryVisible)
                return new(true, badgeText, true, telemetryText, elapsed.Elapsed.TotalSeconds);
            await Task.Delay(250, stop.Token);
        }
        return new(badgeVisible && IsLiveBadge(badgeText), badgeText, telemetryVisible, telemetryText, elapsed.Elapsed.TotalSeconds);
    }

    private async Task AgentAsync()
    {
        product = LaunchProduct(true);
        await WaitUiAsync();
        WindowState = Forms.FormWindowState.Minimized;
        CaptureDesktop("agent-pairing.png");

        string agentState = TryValue("agentState");
        string code = await WaitPairingCodeAsync();
        pairingCountdownAtOpen = TryValue("pairingCountdown");
        guestElevated = Native.IsElevated();
        Pass("agent.elevation_context", "The Lab records the actual Windows elevation context supplied to the guest", new { elevated = guestElevated, user = Environment.UserName }, required: false);
        await TrySupportedProvisioningAsync();
        if (managedRelaunched)
        {
            // Provisioning installs a protected copy and deliberately starts a
            // fresh process. Pairing state is process-local, so only the fresh
            // managed process's code and countdown may be used below.
            agentState = TryValue("agentState");
            code = await WaitPairingCodeAsync();
            pairingCountdownAtOpen = TryValue("pairingCountdown");
            CaptureDesktop("agent-managed-pairing.png");
        }
        bool roleReady = IsAgentReadyState(agentState) && UiTexts().Any(x => x.Contains("Donner le contrôle", StringComparison.OrdinalIgnoreCase) || x.Contains("Donner le controle", StringComparison.OrdinalIgnoreCase));
        if (roleReady) Pass("agent.default_role", "Opening the Release starts the agent and shows the agent role first", new { launchArguments = "--data-root only", state = agentState, code = CodeEvidence(code), managedRelaunched });
        else Fail("agent.default_role", "Opening the Release starts the agent and shows the agent role first", new { launchArguments = "--data-root only", state = agentState, code = CodeEvidence(code), managedRelaunched, visibleTexts = UiTexts().Take(30).ToArray() });
        Pass("agent.pairing_code_format", "The first useful agent view exposes six ASCII pairing digits", new { code = CodeEvidence(code) });
        string countdown = pairingCountdownAtOpen;
        if (TimeText.IsMatch(countdown)) Pass("agent.pairing_countdown_visible", "The pairing expiry is visible in the agent view", new { countdown });
        else Fail("agent.pairing_countdown_visible", "The pairing expiry is visible in the agent view", new { countdown });

        await ProbeSleepRequestAsync();

        await ProbeFirewallAndProvisioningAsync();
        await AgentCoordinationAsync(code);
    }

    private async Task<string> WaitPairingCodeAsync()
    {
        string id = ContractId("agentPairCode");
        for (int n = 0; n < 80; n++)
        {
            var control = Find(id, 400);
            string value = control == null ? "" : NormalizePairingCode(Value(control));
            if (SixDigits.IsMatch(value)) return value;
            await Task.Delay(250, stop.Token);
        }
        throw new TimeoutException("No six-digit agent pairing code appeared.");
    }

    private static string NormalizePairingCode(string value)
    {
        string compact = Regex.Replace(value.Trim(), @"\s+", "");
        return compact.Length > 0 && compact.All(c => c is >= '0' and <= '9') ? compact : "";
    }

    private static bool IsAgentReadyState(string value)
    {
        string text = value.ToLowerInvariant();
        return text.Contains("écout", StringComparison.Ordinal) || text.Contains("ecout", StringComparison.Ordinal)
            || text.Contains("listen", StringComparison.Ordinal) || text.Contains("pair", StringComparison.Ordinal)
            || text.Contains("appair", StringComparison.Ordinal) || text.Contains("prépar", StringComparison.Ordinal)
            || text.Contains("prepar", StringComparison.Ordinal) || text.Contains("ready", StringComparison.Ordinal)
            || text.Contains("attente", StringComparison.Ordinal) || text.Contains("connexion", StringComparison.Ordinal);
    }

    private async Task AgentCoordinationAsync(string initialCode)
    {
        using var udp = new UdpClient(new IPEndPoint(IsLoopback ? IPAddress.Loopback : IPAddress.Any, CoordinationPort));
        string? currentCode = initialCode;
        while (!stop.IsCancellationRequested)
        {
            RefreshTrackedProduct();
            if (product?.HasExited == true && !retainCoordinationAfterProductExit)
            {
                if (sawPairing) Pass("agent.termination", "Terminating support exits the agent and releases its local session", new { exitCode = product.ExitCode });
                await ProbeSleepReleasedAsync();
                await FinishAsync();
                return;
            }

            UdpReceiveResult received;
            try { received = await udp.ReceiveAsync(stop.Token); }
            catch (OperationCanceledException) { break; }
            string request = Encoding.UTF8.GetString(received.Buffer);
            object response;
            if (request == "RD_LAB_BOOTSTRAP")
            {
                currentCode = TryPairingCode();
                pairingCountdownAtOpen ??= TryValue("pairingCountdown");
                response = new { machine = Environment.MachineName, code = currentCode, state = TryValue("agentState"), countdown = pairingCountdownAtOpen, binarySha256 = await HashFileAsync(application), updateVariant };
            }
            else if (request == "RD_LAB_ROTATE")
            {
                response = await WaitForRotationAsync(currentCode ?? initialCode, pairingCountdownAtOpen ?? TryValue("pairingCountdown"));
                currentCode = ((JsonElement)Json.Element(response)).Str("code");
            }
            else if (request == "RD_LAB_RESTART")
            {
                int oldPid = product!.Id;
                await RestartProductAsync(true);
                response = new { restarted = true, oldPid, newPid = product!.Id, paired = IsConnected(string.Join(" ", UiTexts())) };
            }
            else if (request.StartsWith("RD_LAB_ARM_ROLLBACK:", StringComparison.Ordinal))
            {
                string candidateHash = request["RD_LAB_ARM_ROLLBACK:".Length..].Trim();
                bool valid = candidateHash.Length == 64 && candidateHash.All(Uri.IsHexDigit);
                if (valid)
                {
                    rollbackCandidateHash = candidateHash.ToUpperInvariant();
                    retainCoordinationAfterProductExit = true;
                    _ = MonitorAndKillRollbackCandidateAsync(product?.Id ?? 0);
                }
                response = new { armed = valid, baselinePid = product?.Id ?? 0 };
            }
            else if (request == "RD_LAB_STATUS")
            {
                RefreshTrackedProduct();
                string state = TryValue("agentState");
                bool paired = IsConnected(string.Join(" ", UiTexts())) || state.Contains("appair", StringComparison.OrdinalIgnoreCase) || state.Contains("pair", StringComparison.OrdinalIgnoreCase);
                if (paired && !sawPairing)
                {
                    sawPairing = true;
                    await ProbeMaintenanceAfterPairingAsync();
                }
                response = new { alive = product != null && !product.HasExited, paired, machine = Environment.MachineName, state, coordinationAlive = true, rollbackCandidateKilled };
            }
            else if (request == "RD_LAB_DONE")
            {
                response = new { completed = true, alive = product != null && !product.HasExited };
                await SendUdpAsync(udp, response, received.RemoteEndPoint);
                retainCoordinationAfterProductExit = false;
                await WaitForProductExitAsync(TimeSpan.FromSeconds(20));
                if (rollbackCandidateHash != null && !rollbackCandidateKilled) Fail("agent.rollback_candidate_killed", "The rollback run kills the verified replacement before its startup health acknowledgement", new { candidateHash = rollbackCandidateHash, killed = false });
                if (sawPairing && !sawTermination && product != null && !product.HasExited) Fail("agent.termination", "Terminating support exits the agent and releases its local session", new { productStillRunning = true });
                await ProbeSleepReleasedAsync();
                await FinishAsync();
                return;
            }
            else continue;
            await SendUdpAsync(udp, response, received.RemoteEndPoint);
        }
    }

    private string TryPairingCode()
    {
        try
        {
            string value = NormalizePairingCode(TryValue("agentPairCode"));
            return SixDigits.IsMatch(value) ? value : "";
        }
        catch (Exception) { return ""; }
    }

    private async Task<object> WaitForRotationAsync(string oldCode, string initialCountdown)
    {
        var elapsed = Stopwatch.StartNew();
        string current = oldCode;
        int? initialRemaining = ParseCountdownSeconds(initialCountdown);
        double earliestAllowed = initialRemaining.HasValue ? Math.Max(0, initialRemaining.Value - PairingSeconds("rotationEarlyToleranceSeconds", 2)) : double.PositiveInfinity;
        double latestAllowed = initialRemaining.HasValue ? initialRemaining.Value + PairingSeconds("rotationLateToleranceSeconds", 15) : 0;
        int unchangedSamples = 0;
        bool changedBeforeDeadline = false;
        string lastCountdown = initialCountdown;
        while (elapsed.Elapsed < TimeSpan.FromSeconds(330))
        {
            current = TryPairingCode();
            lastCountdown = TryValue("pairingCountdown");
            double waited = elapsed.Elapsed.TotalSeconds;
            if (initialRemaining.HasValue && waited < earliestAllowed)
            {
                if (SixDigits.IsMatch(current) && current != oldCode) changedBeforeDeadline = true;
                else if (current == oldCode) unchangedSamples++;
            }
            if (SixDigits.IsMatch(current) && current != oldCode)
            {
                bool timing = initialRemaining.HasValue && !changedBeforeDeadline && unchangedSamples >= 2 && waited >= earliestAllowed && waited <= latestAllowed;
                var evidence = new { oldCode = CodeEvidence(oldCode), newCode = CodeEvidence(current), initialCountdown, lastCountdown, initialRemainingSeconds = initialRemaining, waitedSeconds = waited, unchangedSamples, changedBeforeDeadline, timing };
                if (timing) Pass("agent.pairing_code_rotation", "The pairing code stays valid until its five-minute expiry and then changes", evidence);
                else Fail("agent.pairing_code_rotation", "The pairing code stays valid until its five-minute expiry and then changes", evidence);
                return new { machine = Environment.MachineName, code = current, oldCode, rotated = true, waitedSeconds = waited, initialCountdown, rotatedCountdown = lastCountdown, unchangedSamples, changedBeforeDeadline, timing };
            }
            await Task.Delay(1000, stop.Token);
        }
        var timeoutEvidence = new { oldCode = CodeEvidence(oldCode), current = CodeEvidence(current), initialCountdown, lastCountdown, initialRemainingSeconds = initialRemaining, waitedSeconds = elapsed.Elapsed.TotalSeconds, unchangedSamples, changedBeforeDeadline };
        Fail("agent.pairing_code_rotation", "The pairing code stays valid until its five-minute expiry and then changes", timeoutEvidence);
        return new { machine = Environment.MachineName, code = current, oldCode, rotated = false, waitedSeconds = elapsed.Elapsed.TotalSeconds, initialCountdown, rotatedCountdown = lastCountdown, unchangedSamples, changedBeforeDeadline, timing = false };
    }

    private static int? ParseCountdownSeconds(string value)
    {
        var match = Regex.Match(value, @"(?<minutes>[0-5]?\d):(?<seconds>[0-5]\d)", RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups["minutes"].Value, out int minutes) || !int.TryParse(match.Groups["seconds"].Value, out int seconds)) return null;
        return minutes * 60 + seconds;
    }

    private static object CodeEvidence(string code) => new { length = code.Length, digitsOnly = SixDigits.IsMatch(code), sha256 = Safety.Hash(code) };

    private async Task RestartProductAsync(bool agent)
    {
        if (product == null) throw new InvalidOperationException("Release process is missing.");
        int oldPid = product.Id;
        try { product.CloseMainWindow(); } catch (InvalidOperationException) { }
        await WaitForProcessExitAsync(product, TimeSpan.FromSeconds(15));
        product.Dispose();
        product = LaunchProduct(agent);
        await WaitUiAsync();
        Pass("agent.release_restart", "The agent can restart as a fresh Release process", new { oldPid, newPid = product.Id });
    }

    private void RefreshTrackedProduct()
    {
        try { product?.Refresh(); } catch (InvalidOperationException) { }
        if (product != null && !product.HasExited) return;
        int previousPid = product?.Id ?? 0;
        foreach (Process candidate in Process.GetProcessesByName("RemoteDebugger"))
        {
            try
            {
                if (candidate.Id == previousPid || candidate.Id == Environment.ProcessId || candidate.HasExited) { candidate.Dispose(); continue; }
                string path = candidate.MainModule?.FileName ?? "";
                if (!path.EndsWith("RemoteDebugger.exe", StringComparison.OrdinalIgnoreCase)) { candidate.Dispose(); continue; }
                product?.Dispose();
                product = candidate;
                return;
            }
            catch (Exception) { candidate.Dispose(); }
        }
    }

    private async Task MonitorAndKillRollbackCandidateAsync(int baselinePid)
    {
        string expectedHash = rollbackCandidateHash ?? "";
        var deadline = DateTimeOffset.UtcNow.AddSeconds(120);
        while (!stop.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
        {
            foreach (Process candidate in Process.GetProcessesByName("RemoteDebugger"))
            {
                try
                {
                    if (candidate.Id == baselinePid || candidate.Id == Environment.ProcessId || candidate.HasExited) { candidate.Dispose(); continue; }
                    string path = candidate.MainModule?.FileName ?? "";
                    if (!path.EndsWith("RemoteDebugger.exe", StringComparison.OrdinalIgnoreCase)) { candidate.Dispose(); continue; }
                    string hash = await HashFileAsync(path);
                    if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) { candidate.Dispose(); continue; }
                    int pid = candidate.Id;
                    candidate.Kill(entireProcessTree: true);
                    await candidate.WaitForExitAsync(CancellationToken.None);
                    candidate.Dispose();
                    rollbackCandidateKilled = true;
                    Pass("agent.rollback_candidate_killed", "The rollback run kills the verified replacement before its startup health acknowledgement", new { pid, candidateHash = expectedHash, path });
                    return;
                }
                catch (Exception) { candidate.Dispose(); }
            }
            await Task.Delay(100, stop.Token);
        }
        Fail("agent.rollback_candidate_killed", "The rollback run kills the verified replacement before its startup health acknowledgement", new { candidateHash = expectedHash, killed = rollbackCandidateKilled });
    }

    private static async Task WaitForProcessExitAsync(Process process, TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(timeoutCts.Token); } catch (OperationCanceledException) { }
        process.Refresh();
        if (!process.HasExited) throw new TimeoutException("Release process did not exit in time.");
    }

    private async Task WaitForProductExitAsync(TimeSpan timeout)
    {
        if (product == null) return;
        using var timeoutCts = new CancellationTokenSource(timeout);
        try { await product.WaitForExitAsync(timeoutCts.Token); } catch (OperationCanceledException) { }
        product.Refresh();
    }

    private async Task ProbeMaintenanceAfterPairingAsync()
    {
        string[] texts = UiTexts();
        bool visibleActive = texts.Any(x => x.Contains("maintenance", StringComparison.OrdinalIgnoreCase) && (x.Contains("active", StringComparison.OrdinalIgnoreCase) || x.Contains("actif", StringComparison.OrdinalIgnoreCase) || x.Contains("autor", StringComparison.OrdinalIgnoreCase)));
        var service = await ReadRemoteDebuggerServicesAsync();
        if (visibleActive && service.Any(x => x.Contains("RemoteDebugger", StringComparison.OrdinalIgnoreCase)))
            Pass("agent.maintenance_after_pairing", "Administrator maintenance is active after pairing through the installed broker", new { visible = texts.Where(x => x.Contains("maintenance", StringComparison.OrdinalIgnoreCase)).Take(5).ToArray(), service = service.Take(8).ToArray() });
        else if (RequiresProvisioning)
            Block("agent.maintenance_after_pairing", "Administrator maintenance is active after pairing through the installed broker", "The guest lacks a verifiable installed broker/service or the secure-desktop consent was unavailable.", new { visible = texts.Where(x => x.Contains("maintenance", StringComparison.OrdinalIgnoreCase)).Take(5).ToArray(), service = service.Take(8).ToArray() });
        else
            Block("agent.maintenance_after_pairing", "Administrator maintenance is active after pairing through the installed broker", "Provisioned service evidence is outside the Runtime scope.", new { visible = texts.Where(x => x.Contains("maintenance", StringComparison.OrdinalIgnoreCase)).Take(5).ToArray(), service = service.Take(8).ToArray() }, required: false);
    }

    private async Task ProbeFirewallAndProvisioningAsync()
    {
        var firewall = await ReadFirewallAsync();
        for (int attempt = 0; attempt < 20 && !HasPrivateFirewallRule(firewall.Stdout); attempt++)
        {
            await Task.Delay(500, stop.Token);
            firewall = await ReadFirewallAsync();
        }
        bool privateRule = firewall.ExitCode == 0 && HasPrivateFirewallRule(firewall.Stdout);
        if (privateRule) Pass("agent.private_firewall", "Private/local-subnet firewall access is prepared automatically", new { stdout = firewall.Stdout });
        else if (RequiresProvisioning) Block("agent.private_firewall", "Private/local-subnet firewall access is prepared automatically", guestElevated ? "The guest is elevated, but no supported product provisioning operation has been provided to this Lab." : "The executable-testing contract does not provide secure-desktop UAC interaction for first provisioning.", new { elevated = guestElevated, exitCode = firewall.ExitCode, stdout = firewall.Stdout, stderr = firewall.Stderr });
        else Block("agent.private_firewall", "Private/local-subnet firewall access is prepared automatically", "Provisioned firewall evidence is outside the Runtime scope.", new { exitCode = firewall.ExitCode, stdout = firewall.Stdout, stderr = firewall.Stderr }, required: false);

        var service = await ReadRemoteDebuggerServicesAsync();
        if (service.Length == 0 && RequiresProvisioning) Block("agent.provisioning_receipt", "The guest has a one-time administrator broker installation", guestElevated ? "The guest is elevated, but no supported product provisioning operation has been provided to this Lab." : "No RemoteDebugger service was visible and the Hyper-V contract cannot drive the UAC secure desktop.", new { elevated = guestElevated, firewall = firewall.Stdout });
        else if (service.Length > 0) Pass("agent.provisioning_receipt", "The guest has a one-time administrator broker installation", new { service = service.Take(8).ToArray() }, required: RequiresProvisioning);
        else Block("agent.provisioning_receipt", "The guest has a one-time administrator broker installation", "The Runtime guest was not pre-provisioned.", new { service }, required: false);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> ReadFirewallAsync()
        => await RunGuestPowerShellAsync("Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like '*Remote Debugger*' -or $_.Name -like '*RemoteDebugger*' } | ForEach-Object { $p = $_ | Get-NetFirewallPortFilter; $a = $_ | Get-NetFirewallAddressFilter; [pscustomobject]@{ Name=$_.Name; DisplayName=$_.DisplayName; Enabled=$_.Enabled; Profile=$_.Profile; Direction=$_.Direction; Action=$_.Action; Protocol=$p.Protocol; LocalPort=$p.LocalPort; RemoteAddress=$a.RemoteAddress } } | ConvertTo-Json -Compress");

    private static bool HasPrivateFirewallRule(string value)
        => value.Contains("Private", StringComparison.OrdinalIgnoreCase) && value.Contains("LocalSubnet", StringComparison.OrdinalIgnoreCase);

    private async Task TrySupportedProvisioningAsync()
    {
        JsonElement? beforeStatus = await TryPlatformStatusAsync();
        if (beforeStatus.HasValue)
        {
            string availability = beforeStatus.Value.TryGetProperty("status", out var before) ? before.Str("availability") : "";
            Pass("agent.platform_status_before", "The product reports its pre-provisioning platform state through its own CLI", new { availability, status = beforeStatus.Value }, required: false);
        }
        else
            Block("agent.platform_status_before", "The product reports its pre-provisioning platform state through its own CLI", "The Release CLI did not expose platform-status in this artifact.", required: false);

        if (!guestElevated)
        {
            Block("agent.provisioning_command", "An elevated guest can invoke the supported one-time broker provisioner", "The Lab process is not elevated, so it must not simulate a secure-desktop UAC approval.", new { elevated = false }, required: RequiresProvisioning);
            return;
        }

        string sid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        string releaseHash = File.Exists(application)
            ? Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(application, stop.Token)))
            : "";
        if (sid.Length == 0 || releaseHash.Length != 64)
        {
            Fail("agent.provisioning_command", "An elevated guest can invoke the supported one-time broker provisioner", new { elevated = true, sidAvailable = sid.Length > 0, releaseHash });
            return;
        }

        int requestingProcessId = product?.Id ?? 0;
        long requestingProcessStartTicks = product == null ? 0 : product.StartTime.ToUniversalTime().Ticks;
        string request = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            expectedExecutableSha256 = releaseHash,
            registeredUserSid = sid,
            requestingProcessId,
            requestingProcessStartTicks
        }, Json.Options));
        var psi = new ProcessStartInfo(application)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(application)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(contract.GetProperty("provisioning").GetProperty("supportedVerb").GetString()!);
        psi.ArgumentList.Add(request);
        using var provisioner = Process.Start(psi);
        if (provisioner == null)
        {
            Fail("agent.provisioning_command", "An elevated guest can invoke the supported one-time broker provisioner", new { elevated = true, started = false });
            return;
        }
        string stdout = await provisioner.StandardOutput.ReadToEndAsync();
        string stderr = await provisioner.StandardError.ReadToEndAsync();
        await provisioner.WaitForExitAsync(stop.Token);
        var evidence = new { elevated = true, exitCode = provisioner.ExitCode, stdout, stderr, executable = application, releaseHash, registeredUserSid = sid, requestingProcessId, requestingProcessStartTicks };
        if (provisioner.ExitCode == 0)
        {
            Pass("agent.provisioning_command", "An elevated guest can invoke the supported one-time broker provisioner", evidence, required: RequiresProvisioning);
            string? managedPath = TryReadManagedApplicationPath();
            if (managedPath == null || !File.Exists(managedPath))
            {
                var receiptEvidence = new { receipt = ProvisioningReceiptPath(), managedPath, executable = application };
                if (RequiresProvisioning) Fail("agent.managed_relaunch", "Provisioning records a protected managed application path that can be relaunched for the session", receiptEvidence);
                else Block("agent.managed_relaunch", "Provisioning records a protected managed application path that can be relaunched for the session", "The supported provisioner exited successfully but did not publish a readable managed application receipt.", receiptEvidence, required: false);
                return;
            }

            string previous = application;
            if (PathsEqual(previous, managedPath))
            {
                managedRelaunched = true;
                Pass("agent.managed_relaunch", "Provisioning records a protected managed application path that can be relaunched for the session", new { previous, managedPath, relaunched = false, receipt = ProvisioningReceiptPath() }, required: RequiresProvisioning);
            }
            else
            {
                await RelaunchManagedProductAsync(managedPath);
                Pass("agent.managed_relaunch", "Provisioning records a protected managed application path that can be relaunched for the session", new { previous, managedPath, relaunched = true, receipt = ProvisioningReceiptPath() }, required: RequiresProvisioning);
            }
            JsonElement? afterStatus = await TryPlatformStatusAsync();
            bool ready = afterStatus.HasValue && afterStatus.Value.TryGetProperty("status", out var after) && after.Str("availability").Equals("Ready", StringComparison.OrdinalIgnoreCase) && after.TryGetProperty("available", out var available) && available.ValueKind == JsonValueKind.True && available.GetBoolean();
            if (ready) Pass("agent.platform_status_ready", "The protected managed application sees the LocalSystem support broker without another consent flow", new { status = afterStatus });
            else if (RequiresProvisioning) Fail("agent.platform_status_ready", "The protected managed application sees the LocalSystem support broker without another consent flow", new { status = afterStatus, managedPath });
            else Block("agent.platform_status_ready", "The protected managed application sees the LocalSystem support broker without another consent flow", "The post-provisioning platform status was not available in this Runtime-scoped run.", new { status = afterStatus, managedPath }, required: false);
        }
        else if (RequiresProvisioning) Fail("agent.provisioning_command", "An elevated guest can invoke the supported one-time broker provisioner", evidence);
        else Block("agent.provisioning_command", "An elevated guest can invoke the supported one-time broker provisioner", "The supported provisioner was present but did not complete in this Runtime-scoped run.", evidence, required: false);
    }

    private static string ProvisioningReceiptPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteDebugger", "Support", "provisioning-receipt.json");

    private static string? TryReadManagedApplicationPath()
    {
        string receipt = ProvisioningReceiptPath();
        try
        {
            if (!File.Exists(receipt)) return null;
            var value = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(receipt));
            string path = value.Str("managedApplicationPath");
            return path.Length == 0 ? null : Path.GetFullPath(path);
        }
        catch (Exception) { return null; }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private async Task RelaunchManagedProductAsync(string managedPath)
    {
        if (product == null) throw new InvalidOperationException("The product process is missing during managed relaunch.");
        int oldPid = product.Id;
        try { product.CloseMainWindow(); } catch (InvalidOperationException) { }
        await WaitForProcessExitAsync(product, TimeSpan.FromSeconds(20));
        product.Dispose();
        application = managedPath;
        product = LaunchProduct(true);
        await WaitUiAsync();
        managedRelaunched = true;
        Log("Relaunched managed product " + oldPid + " -> " + product.Id + " at " + managedPath);
    }

    private async Task<JsonElement?> TryPlatformStatusAsync()
    {
        try { return await CliAsync(["platform-status"], requireSuccess: false); }
        catch (Exception) { return null; }
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunGuestPowerShellAsync(string command)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell/v1.0/powershell.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        psi.ArgumentList.Add("-NoLogo"); psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-NonInteractive"); psi.ArgumentList.Add("-Command"); psi.ArgumentList.Add(command);
        using var p = Process.Start(psi) ?? throw new IOException("Could not start read-only guest probe.");
        string stdout = await p.StandardOutput.ReadToEndAsync(); string stderr = await p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync(stop.Token);
        return (p.ExitCode, stdout, stderr);
    }

    private async Task<string[]> ReadRemoteDebuggerServicesAsync()
    {
        var result = await RunGuestPowerShellAsync("Get-CimInstance Win32_Service -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'RemoteDebugger' -or $_.DisplayName -match 'Remote Debugger' } | ForEach-Object { \"$($_.Name)|$($_.State)|$($_.StartMode)|$($_.PathName)\" }");
        return result.Stdout.Split(["`r`n", "`n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool ContainsSleepHeld(string value)
        => value.Contains("mise en veille", StringComparison.OrdinalIgnoreCase) && (value.Contains("suspend", StringComparison.OrdinalIgnoreCase) || value.Contains("bloqu", StringComparison.OrdinalIgnoreCase) || value.Contains("prevent", StringComparison.OrdinalIgnoreCase));

    private async Task ProbeSleepRequestAsync()
    {
        string[] texts = UiTexts();
        var request = await RunGuestPowerShellAsync("powercfg /requests");
        bool osHeld = request.ExitCode == 0 && (request.Stdout.Contains("RemoteDebugger", StringComparison.OrdinalIgnoreCase) || request.Stdout.Contains("Remote Debugger", StringComparison.OrdinalIgnoreCase));
        bool visible = texts.Any(ContainsSleepHeld);
        sleepRequestObserved = osHeld;
        if (osHeld)
            Pass("agent.sleep_request", "The agent holds a Windows system power request while it is running", new { osEvidence = request.Stdout, visible = texts.Where(ContainsSleepHeld).Take(4).ToArray() });
        else
            Block("agent.sleep_request", "The agent holds a Windows system power request while it is running", "No product-owned power request was visible in the read-only powercfg output.", new { commandExitCode = request.ExitCode, osEvidence = request.Stdout, stderr = request.Stderr, visible }, required: false);
    }

    private async Task ProbeSleepReleasedAsync()
    {
        if (!sleepRequestObserved)
        {
            Block("agent.sleep_release", "The Windows power request is released after the agent exits", "The pre-exit power request was not observable, so release cannot be claimed.", required: false);
            return;
        }
        var request = await RunGuestPowerShellAsync("powercfg /requests");
        bool released = request.ExitCode == 0 && !request.Stdout.Contains("RemoteDebugger", StringComparison.OrdinalIgnoreCase) && !request.Stdout.Contains("Remote Debugger", StringComparison.OrdinalIgnoreCase);
        if (released) Pass("agent.sleep_release", "The Windows power request is released after the agent exits", new { osEvidence = request.Stdout });
        else Fail("agent.sleep_release", "The Windows power request is released after the agent exits", new { commandExitCode = request.ExitCode, osEvidence = request.Stdout, stderr = request.Stderr });
    }

    private async Task ControllerAsync()
    {
        product = LaunchProduct(false);
        await WaitUiAsync();
        WindowState = Forms.FormWindowState.Minimized;
        string controllerHash = await HashFileAsync(application);

        var peersControl = Element("peers");
        bool peerVisible = await WaitForRowsAsync(peersControl, 1, 40);
        var discovered = await DiscoverAsync();
        string localMachine = Environment.MachineName;
        var peer = discovered.FirstOrDefault(x => !x.Name.Equals(localMachine, StringComparison.OrdinalIgnoreCase));
        if (peer == null) throw new TimeoutException("Controller discovery did not find a remote agent.");
        peerHost = peer.Host; peerFingerprint = peer.Fingerprint;
        string selectedHost = TryValue("host");
        bool autoSelected = !string.IsNullOrWhiteSpace(selectedHost) && selectedHost == peerHost;
        if (peerVisible && autoSelected) Pass("controller.discovery_on_launch", "Controller discovery starts on launch and excludes the local machine", new { uiRows = peerVisible, selectedHost, peer = new { peer.Name, peer.Host }, localMachine });
        else Fail("controller.discovery_on_launch", "Controller discovery starts on launch and excludes the local machine", new { uiRows = peerVisible, selectedHost, peer = new { peer.Name, peer.Host }, localMachine });
        if (!autoSelected) Set("host", peerHost);

        JsonElement bootstrap = await LabMessageAsync(peerHost, "RD_LAB_BOOTSTRAP");
        string initialCode = bootstrap.Str("code");
        if (!SixDigits.IsMatch(initialCode)) throw new InvalidDataException("Agent coordination did not return a six-digit code.");
        string initialCountdown = bootstrap.Str("countdown");
        string oldCode = initialCode;
        string expectedPreviousHash = bootstrap.Str("binarySha256");
        if (updateVariant == "rollback")
        {
            JsonElement armed = await LabMessageAsync(peerHost, "RD_LAB_ARM_ROLLBACK:" + controllerHash, retry: false);
            if (armed.TryGetProperty("armed", out var armedValue) && armedValue.GetBoolean()) Pass("controller.rollback_failure_arm", "The rollback run arms a bounded candidate-startup failure against the controller fixture", new { controllerHash, expectedPreviousHash });
            else Fail("controller.rollback_failure_arm", "The rollback run arms a bounded candidate-startup failure against the controller fixture", armed);
        }
        // Wait for the real five-minute rotation before pairing. The agent
        // remains unpaired during this wait, so the old value can be rejected
        // over the actual TLS pairing operation below.
        JsonElement rotated = await LabMessageAsync(peerHost, "RD_LAB_ROTATE", retry: false);
        string code = rotated.Str("code");
        bool rotatedAtBoundary = rotated.TryGetProperty("rotated", out var rotatedFlag) && rotatedFlag.GetBoolean()
            && rotated.TryGetProperty("timing", out var timingFlag) && timingFlag.GetBoolean()
            && SixDigits.IsMatch(code);
        if (rotatedAtBoundary) Pass("controller.rotation_observed", "Controller receives the currently valid code after the agent stays on the old code until its five-minute expiry", new { initialCountdown, oldCode = CodeEvidence(oldCode), code = CodeEvidence(code), waitedSeconds = rotated.TryGetProperty("waitedSeconds", out var waited) ? waited.GetDouble() : 0, unchangedSamples = rotated.Int("unchangedSamples"), timing = true });
        else Fail("controller.rotation_observed", "Controller receives the currently valid code after the agent stays on the old code until its five-minute expiry", new { initialCountdown, oldCode = CodeEvidence(oldCode), code = CodeEvidence(code), rotated });
        if (!SixDigits.IsMatch(code)) throw new InvalidDataException("The rotated code is not six digits.");

        var oldPairConnection = Path.Combine(output, "expired-pair.connection");
        JsonElement oldPair;
        try
        {
            oldPair = await CliAsync(["pair", "--host", peerHost, "--port", "45832", "--fingerprint", peerFingerprint!, "--connection", oldPairConnection], stdin: oldCode, requireSuccess: false);
        }
        finally { try { File.Delete(oldPairConnection); } catch (IOException) { } }
        bool oldPairSucceeded = oldPair.TryGetProperty("ok", out var oldPairOk) && oldPairOk.ValueKind == JsonValueKind.True;
        bool oldPairDenied = !oldPairSucceeded && (oldPair.Str("message").Contains("incorrect", StringComparison.OrdinalIgnoreCase)
            || oldPair.Str("message").Contains("expired", StringComparison.OrdinalIgnoreCase)
            || oldPair.Str("message").Contains("unavailable", StringComparison.OrdinalIgnoreCase)
            || oldPair.Str("error").Contains("transport", StringComparison.OrdinalIgnoreCase));
        if (oldPairDenied) Pass("controller.expired_code_denied", "The previous pairing code is rejected after the five-minute rotation", new { code = CodeEvidence(oldCode), response = oldPair });
        else Fail("controller.expired_code_denied", "The previous pairing code is rejected after the five-minute rotation", new { code = CodeEvidence(oldCode), response = oldPair });

        Set("pairCode", code);
        bool hasFingerprintGate = FindVisibleId("fingerprintVerified") != null || FindVisibleId("fingerprint") != null;
        if (hasFingerprintGate) Fail("controller.no_fingerprint_gate", "Normal pairing has no fingerprint field or checkbox", new { fingerprintGateVisible = true });
        else Pass("controller.no_fingerprint_gate", "Normal pairing has no fingerprint field or checkbox", new { fingerprintGateVisible = false });
        FocusAndEnter("pairCode");
        string pairText = TryValue("connectionStatus");
        bool connected;
        if (updateVariant == "rollback")
        {
            // Rollback intentionally interrupts the synchronization handoff
            // after PAKE pairing.  Observe the visible pairing/synchronization
            // transition as proof that Enter was handled, then validate the
            // restored transaction through the saved authenticated token.
            bool pairingAttempted = await WaitForTextAsync("connectionStatus", IsPairingAttempt, 20);
            string outcomeText = TryValue("connectionStatus");
            connected = IsConnected(outcomeText);
            if (pairingAttempted)
                Pass("controller.code_enter_pairing", "Entering the six-digit code and pressing Enter starts the authenticated pairing flow before the rollback handoff", new { pairText, outcomeText, codeLength = code.Length, connected, rollbackHandoff = true });
            else
                Fail("controller.code_enter_pairing", "Entering the six-digit code and pressing Enter starts the authenticated pairing flow before the rollback handoff", new { pairText, outcomeText, codeLength = code.Length, connected, visible = UiTexts().Take(35).ToArray(), rollbackHandoff = true });
        }
        else
        {
            connected = await WaitForTextAsync("connectionStatus", IsConnected, 60);
            if (connected) sawPairing = true;
            if (connected) Pass("controller.code_enter_pairing", "Entering the six-digit code and pressing Enter pairs the selected PC", new { pairText, codeLength = code.Length });
            else Fail("controller.code_enter_pairing", "Entering the six-digit code and pressing Enter pairs the selected PC", new { pairText, visible = UiTexts().Take(35).ToArray() });
            if (!connected)
            {
                // Keep diagnostics useful for a failed keyboard route while
                // retaining the failed Enter assertion above.
                try { Click("pair"); connected = await WaitForTextAsync("connectionStatus", IsConnected, 30); } catch (Exception) { }
            }
            if (!connected) throw new InvalidOperationException("Controller did not connect after code entry.");
        }

        await ProbeUnauthorizedProfilesAsync();

        if (updateVariant == "rollback")
        {
            Block("controller.sync_before_live", "The remote agent hash equals the controller Release hash before live viewing", "The rollback fixture intentionally kills the replacement before startup health, so the normal pre-live hash match is not applicable to this run; rollback state and the restored previous hash are checked separately.", new { controllerHash, pairingStatus = TryValue("connectionStatus") }, required: false);
            await ProbeRollbackOutcomeAsync(controllerHash, expectedPreviousHash);
            bool rollbackAgentExited = await ProbeRollbackTerminateSavedTokenAsync();
            // The rollback Lab keeps its coordination socket alive while the
            // managed service restores the previous agent. Release that hold
            // explicitly after the controller has observed the rollback.
            if (!rollbackAgentExited) await LabMessageAsync(peerHost, "RD_LAB_DONE");
            await FinishAsync();
            return;
        }

        var statusReply = await CallAsync("status"); var status = Data(statusReply); workspace = status.Str("workspace");
        var heartbeat = await WaitForBinaryMatchAsync(controllerHash, IsUpdateVariant ? 180 : 10);
        string remoteHash = FindString(heartbeat, "agentBinarySha256", "binarySha256", "releaseSha256", "executableSha256", "sha256");
        string reportedControllerHash = FindString(heartbeat, "controllerBinarySha256");
        bool hashesMatch = remoteHash.Length == 64 && remoteHash.Equals(controllerHash, StringComparison.OrdinalIgnoreCase) && (reportedControllerHash.Length == 0 || reportedControllerHash.Equals(controllerHash, StringComparison.OrdinalIgnoreCase)) && (!heartbeat.TryGetProperty("binaryMatched", out var matched) || matched.GetBoolean());
        if (hashesMatch) Pass("controller.sync_before_live", "The remote agent hash equals the controller Release hash before live viewing", new { controllerHash, remoteHash, reportedControllerHash, heartbeat });
        else Fail("controller.sync_before_live", "The remote agent hash equals the controller Release hash before live viewing", new { controllerHash, remoteHash, reportedControllerHash, heartbeat });

        LiveEvidence liveEvidence = await WaitForLiveEvidenceAsync(60);
        var firstFrame = await CliAsync(["screenshot", "--file", Path.Combine(output, "connected-screen.jpg")]);
        bool freshFrame = firstFrame.TryGetProperty("ok", out var firstFrameOk) && firstFrameOk.ValueKind == JsonValueKind.True && firstFrame.TryGetProperty("roundTripMs", out var roundTrip) && roundTrip.ValueKind == JsonValueKind.Number && roundTrip.GetDouble() > 0;
        bool live = liveEvidence.BadgeVisible && liveEvidence.TelemetryVisible && freshFrame;
        if (live) Pass("controller.live_auto_start", "Live viewing starts only after a fresh frame arrives", new { liveBadge = new { visible = liveEvidence.BadgeVisible, text = liveEvidence.BadgeText }, streamStatus = liveEvidence.TelemetryText, waitedSeconds = liveEvidence.WaitedSeconds, frame = firstFrame });
        else Fail("controller.live_auto_start", "Live viewing starts only after a fresh frame arrives", new { live, liveBadge = new { visible = liveEvidence.BadgeVisible, text = liveEvidence.BadgeText }, streamStatus = liveEvidence.TelemetryText, waitedSeconds = liveEvidence.WaitedSeconds, freshFrame, frame = firstFrame });
        CaptureDesktop("controller-live.png");

        if (IsUpdateVariant)
        {
            await ProbeVariantReconnectAsync(controllerHash);
            bool updateAgentExited = await ProbeTrayAndTerminateAsync();
            if (!updateAgentExited) await LabMessageAsync(peerHost, "RD_LAB_DONE");
            await FinishAsync();
            return;
        }

        ProbeDefaultInput();
        await ProbeAutoDataAsync();
        await RunRegressionScenarioAsync(status);
        await ProbeReconnectAndUpdateAsync(controllerHash);
        bool agentExited = await ProbeTrayAndTerminateAsync();
        // The agent Lab finalizes as soon as the real product exits. Do not
        // send a coordination message to a disposed socket after termination.
        if (!agentExited) await LabMessageAsync(peerHost, "RD_LAB_DONE");
        await FinishAsync();
    }

    private async Task ProbeVariantReconnectAsync(string controllerHash)
    {
        var disconnected = await CallAsync("session.disconnect", requireSuccess: false, seconds: 10);
        bool disconnectAccepted = disconnected.TryGetProperty("ok", out var disconnectOk) && disconnectOk.ValueKind == JsonValueKind.True;
        await Task.Delay(1000, stop.Token);
        var heartbeat = await WaitForBinaryMatchAsync(controllerHash, 30);
        string remoteHash = FindString(heartbeat, "agentBinarySha256", "binarySha256", "releaseSha256", "executableSha256", "sha256");
        bool codePrompt = !string.IsNullOrWhiteSpace(TryValue("pairCode")) && SixDigits.IsMatch(TryValue("pairCode"));
        bool resumed = disconnectAccepted && remoteHash.Equals(controllerHash, StringComparison.OrdinalIgnoreCase) && !codePrompt;
        if (resumed) Pass("controller.sync_reconnect", "A synchronized agent reconnects through the saved session without requesting a new pairing code", new { updateVariant, disconnected = true, remoteHash, codePrompt = false, heartbeat });
        else Fail("controller.sync_reconnect", "A synchronized agent reconnects through the saved session without requesting a new pairing code", new { updateVariant, disconnectAccepted, remoteHash, codePrompt, heartbeat });
    }

    private string TryValue(string key)
    {
        try { return UiValue(key); } catch (Exception) { return ""; }
    }

    private async Task<bool> WaitForTextAsync(string key, Func<string, bool> predicate, int seconds)
    {
        try { await WaitForUiAsync(() => predicate(TryValue(key)), seconds); return true; } catch (TimeoutException) { return false; }
    }

    private async Task<List<Peer>> DiscoverAsync()
    {
        for (int n = 0; n < 20; n++)
        {
            try
            {
                var reply = await CliAsync(["discover"]);
                if (reply.TryGetProperty("peers", out var peers))
                {
                    var found = peers.Deserialize<List<Peer>>(Json.Options) ?? [];
                    if (found.Count > 0) return found;
                }
            }
            catch (Exception) { }
            await Task.Delay(1500, stop.Token);
        }
        return [];
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private async Task<JsonElement> WaitForBinaryMatchAsync(string expectedHash, int seconds)
    {
        JsonElement latest = Json.Element(new { unavailable = true });
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            try
            {
                var reply = await CallAsync("session.heartbeat", requireSuccess: false, seconds: 5);
                if (reply.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                {
                    latest = Data(reply);
                    string remote = FindString(latest, "agentBinarySha256", "binarySha256", "releaseSha256", "executableSha256", "sha256");
                    bool matched = !latest.TryGetProperty("binaryMatched", out var flag) || flag.GetBoolean();
                    if (matched && remote.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) return latest;
                }
            }
            catch (Exception) { }
            await Task.Delay(1000, stop.Token);
        }
        return latest;
    }

    private async Task ProbeRollbackOutcomeAsync(string controllerHash, string expectedPreviousHash)
    {
        string olderFixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../update-fixtures/older/RemoteDebugger.exe"));
        if (!File.Exists(olderFixture))
        {
            Block("controller.sync_rollback", "An interrupted mismatched update rolls back and reconnects", "The signed older update fixture is absent from the canonical Lab payload.", new { expectedFixture = olderFixture });
            return;
        }
        JsonElement latest = Json.Element(new { unavailable = true });
        string state = "";
        string actualHash = "";
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(120))
        {
            try
            {
                var reply = await CallAsync("update.snapshot", requireSuccess: false, seconds: 15);
                if (reply.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                {
                    latest = Data(reply);
                    if (latest.TryGetProperty("transaction", out var transaction) && transaction.ValueKind == JsonValueKind.Object)
                        state = transaction.Str("state");
                    if (latest.TryGetProperty("agent", out var agent) && agent.ValueKind == JsonValueKind.Object)
                        actualHash = agent.Str("sha256");
                    if (state.Equals("RolledBack", StringComparison.OrdinalIgnoreCase) || state.Equals("rolled_back", StringComparison.OrdinalIgnoreCase)) break;
                }
            }
            catch (Exception) { }
            await Task.Delay(1000, stop.Token);
        }
        bool previousKnown = expectedPreviousHash.Length == 64;
        bool hashRestored = previousKnown && actualHash.Equals(expectedPreviousHash, StringComparison.OrdinalIgnoreCase);
        bool rollback = state.Equals("RolledBack", StringComparison.OrdinalIgnoreCase) || state.Equals("rolled_back", StringComparison.OrdinalIgnoreCase);
        var evidence = new { controllerHash, expectedPreviousHash, actualHash, state, elapsedSeconds = elapsed.Elapsed.TotalSeconds, snapshot = latest };
        if (rollback && hashRestored) Pass("controller.sync_rollback", "An interrupted mismatched update restores the previous signed agent and reconnects", evidence);
        else if (latest.TryGetProperty("unavailable", out _)) Block("controller.sync_rollback", "An interrupted mismatched update restores the previous signed agent and reconnects", "The integrated product does not expose an authenticated update snapshot while the rollback is being exercised.", evidence);
        else Fail("controller.sync_rollback", "An interrupted mismatched update restores the previous signed agent and reconnects", evidence);
    }

    private async Task ProbeUnauthorizedProfilesAsync()
    {
        if (peerHost == null || peerFingerprint == null) return;
        string invalid = Path.Combine(output, "invalid.connection");
        string request = Path.Combine(output, "unauthorized.json");
        try
        {
            new RemoteClient(new Connection(peerHost, 45832, peerFingerprint, "invalid-fixture-token")).Save(invalid);
            await File.WriteAllTextAsync(request, Json.Text(new { operation = "status", args = new { } }), stop.Token);
            var denied = await CliAsync(["call", "--request", request, "--connection", invalid], requireSuccess: false);
            bool deniedResult = !denied.TryGetProperty("ok", out var deniedOk) || deniedOk.ValueKind != JsonValueKind.True;
            if (deniedResult) Pass("controller.invalid_token_denied", "An invalid saved token cannot authorize a controller request", denied);
            else Fail("controller.invalid_token_denied", "An invalid saved token cannot authorize a controller request", denied);

            new RemoteClient(new Connection(peerHost, 45832, new string('0', 64), "invalid-fixture-token")).Save(invalid);
            var pin = await CliAsync(["call", "--request", request, "--connection", invalid], requireSuccess: false);
            bool pinDenied = !pin.TryGetProperty("ok", out var pinOk) || pinOk.ValueKind != JsonValueKind.True;
            if (pinDenied) Pass("controller.wrong_certificate_pin_denied", "A mismatched certificate pin cannot establish the controller TLS channel", pin);
            else Fail("controller.wrong_certificate_pin_denied", "A mismatched certificate pin cannot establish the controller TLS channel", pin);
        }
        finally
        {
            try { File.Delete(invalid); } catch (IOException) { }
            try { File.Delete(request); } catch (IOException) { }
        }
    }

    private async Task<JsonElement> LabMessageAsync(string host, string message, bool retry = true)
    {
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        overall.CancelAfter(TimeSpan.FromSeconds(370));
        while (true)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            attempt.CancelAfter(retry ? TimeSpan.FromSeconds(12) : TimeSpan.FromSeconds(370));
            try
            {
                using var udp = new UdpClient { EnableBroadcast = true };
                await udp.SendAsync(Encoding.UTF8.GetBytes(message), new IPEndPoint(IPAddress.Parse(host), CoordinationPort), attempt.Token);
                var response = await udp.ReceiveAsync(attempt.Token);
                return JsonSerializer.Deserialize<JsonElement>(response.Buffer);
            }
            catch (OperationCanceledException) when (!overall.IsCancellationRequested && retry)
            {
                await Task.Delay(250, overall.Token);
            }
            catch (SocketException) when (!overall.IsCancellationRequested && retry)
            {
                await Task.Delay(250, overall.Token);
            }
        }
    }

    private static async Task SendUdpAsync(UdpClient udp, object value, IPEndPoint endpoint)
        => await udp.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, Json.Options), endpoint);

    private async Task<bool> WaitForRowsAsync(AutomationElement table, int minimum, int seconds)
    {
        try { await WaitForUiAsync(() => TableRows(table).Count >= minimum, seconds); return true; } catch (TimeoutException) { return false; }
    }

    private static List<string[]> TableRows(AutomationElement table)
    {
        var rowCondition = new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        return table.FindAll(TreeScope.Descendants, rowCondition).Cast<AutomationElement>().Select(row =>
        {
            var cells = row.FindAll(TreeScope.Descendants, new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)))
                .Cast<AutomationElement>().Select(e =>
                {
                    try
                    {
                        if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var p) && p is ValuePattern value) return value.Current.Value;
                        return e.Current.Name;
                    }
                    catch (ElementNotAvailableException) { return ""; }
                }).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            if (cells.Length == 0)
            {
                try { cells = [row.Current.Name]; } catch (ElementNotAvailableException) { cells = []; }
            }
            return cells;
        }).ToList();
    }

    private async Task ProbeAutoDataAsync()
    {
        NavigateControllerPage("navProcesses");
        var processes = Element("processList");
        bool processRows = await WaitForRowsAsync(processes, 2, 45);
        string[] texts = UiTexts();
        bool resourceTimestamp = texts.Any(x => x.Contains("CPU", StringComparison.OrdinalIgnoreCase) || x.Contains("RAM", StringComparison.OrdinalIgnoreCase)) && texts.Any(x => x.Contains(":", StringComparison.Ordinal));

        bool processSorted = ClickAndCheckSort(processes, "PID", numeric: true);
        if (processSorted) Pass("controller.process_sort", "Process headers sort rows with numeric ordering", new { rows = TableRows(processes).Take(12).ToArray() });
        else Fail("controller.process_sort", "Process headers sort rows with numeric ordering", new { rows = TableRows(processes).Take(12).ToArray() });

        CaptureDesktop("controller-processes-auto.png");
        await CreateFileSortFixturesAsync();
        NavigateControllerPage("navFiles");
        var files = Element("remoteFiles");
        bool fileRows = await WaitForRowsAsync(files, 2, 45);
        if (processRows && fileRows && resourceTimestamp) Pass("controller.resources_on_connect", "CPU, RAM, processes and the remote workspace load automatically after connection", new { processRows = TableRows(processes).Count, fileRows = TableRows(files).Count, resourceTimestamp });
        else Fail("controller.resources_on_connect", "CPU, RAM, processes and the remote workspace load automatically after connection", new { processRows, fileRows, resourceTimestamp, texts = texts.Take(60).ToArray(), files = TableRows(files).Take(12).ToArray() });
        bool fileSorted = ClickAndCheckSort(files, "Nom", numeric: false);
        if (fileSorted) Pass("controller.file_sort", "File headers sort rows with typed ordering", new { rows = TableRows(files).Take(12).ToArray() });
        else Fail("controller.file_sort", "File headers sort rows with typed ordering", new { rows = TableRows(files).Take(12).ToArray() });
        CaptureDesktop("controller-files-auto.png");
        try { Click("refreshResources"); } catch (Exception) { }
        try { Click("browseFiles"); } catch (Exception) { }
    }

    private void NavigateControllerPage(string key)
    {
        AutomationElement navigation = Element(key);
        InvokeElement(navigation);
        Thread.Sleep(350);
    }

    private async Task CreateFileSortFixturesAsync()
    {
        string root = Path.Combine(output, "file-sort-fixtures");
        Directory.CreateDirectory(root);
        foreach (string name in new[] { "acceptance-sort-02.txt", "acceptance-sort-10.txt" })
        {
            string local = Path.Combine(root, name);
            await File.WriteAllTextAsync(local, "acceptance sorting fixture " + name, stop.Token);
            await CliAsync(["upload", "--file", local, "--path", name]);
        }
    }

    private bool ClickAndCheckSort(AutomationElement table, string headerName, bool numeric)
    {
        var before = TableRows(table).Select(x => x.FirstOrDefault() ?? "").ToArray();
        AutomationElement? header = null;
        try
        {
            header = table.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.HeaderItem)).Cast<AutomationElement>().FirstOrDefault(e => e.Current.Name.Contains(headerName, StringComparison.OrdinalIgnoreCase));
        }
        catch (ElementNotAvailableException) { }
        if (header == null) return false;
        try
        {
            InvokeElement(header);
            Thread.Sleep(500);
            var after = TableRows(table).Select(x => x.FirstOrDefault() ?? "").Where(x => x.Length > 0).ToArray();
            if (after.Length < 2) return false;
            if (numeric)
            {
                var numbers = after.Select(x => int.TryParse(Regex.Match(x, "-?\\d+").Value, out var value) ? (int?)value : null).ToArray();
                var available = numbers.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
                return available.Length >= 2 && (available.SequenceEqual(available.OrderBy(x => x)) || available.SequenceEqual(available.OrderByDescending(x => x))) && !after.SequenceEqual(before);
            }
            return after.SequenceEqual(after.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) || after.SequenceEqual(after.OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception) { return false; }
    }

    private void ProbeDefaultInput()
    {
        var toggle = FindNameContaining("souris et clavier") ?? FindNameContaining("mouse and keyboard");
        if (toggle == null) { Fail("controller.input_default", "Mouse and keyboard forwarding is enabled by default", new { control = "missing" }); return; }
        try
        {
            if (!toggle.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern) || pattern is not TogglePattern togglePattern) throw new InvalidOperationException("Input control has no TogglePattern.");
            bool enabled = togglePattern.Current.ToggleState == ToggleState.On;
            if (enabled) Pass("controller.input_default", "Mouse and keyboard forwarding is enabled by default", new { toggle = toggle.Current.Name, state = togglePattern.Current.ToggleState.ToString() });
            else Fail("controller.input_default", "Mouse and keyboard forwarding is enabled by default", new { toggle = toggle.Current.Name, state = togglePattern.Current.ToggleState.ToString() });
        }
        catch (Exception ex) { Fail("controller.input_default", "Mouse and keyboard forwarding is enabled by default", new { error = ex.Message }); }
    }

    private async Task RunRegressionScenarioAsync(JsonElement status)
    {
        if (workspace == null) throw new InvalidOperationException("Status did not return the remote workspace.");
        var versionHashes = new List<string>(); int target = 0;
        for (int version = 1; version <= 2; version++)
        {
            string source = Path.Combine(AppContext.BaseDirectory, "fixtures", "v" + version, "Fixture.exe");
            string relative = "deployments/fixture/v" + version + "/Fixture.exe";
            if (!File.Exists(source)) { Fail("deploy.fixture.v" + version, "The Release Lab carries both fixture binaries", new { source }); continue; }
            var deployment = Data(await CliAsync(["upload", "--file", source, "--path", relative])); versionHashes.Add(deployment.Str("sha256"));
            var launch = Data(await CallAsync("start", new { path = relative, arguments = new[] { Path.Combine(workspace, "fixture-output-v" + version) } })); target = launch.Int("pid"); await Task.Delay(1600, stop.Token);
            var actual = Data(await CallAsync("process.info", new { pid = target }));
            bool binaryMatch = actual.GetProperty("binary").Str("sha256") == deployment.Str("sha256") && actual.GetProperty("binary").Str("fileVersion") == version + ".0.0.0";
            if (binaryMatch) Pass("deploy.running_identity.v" + version, "The running fixture has the uploaded hash and expected file version", new { pid = target, binary = actual.GetProperty("binary") });
            else Fail("deploy.running_identity.v" + version, "The running fixture has the uploaded hash and expected file version", new { pid = target, binary = actual.GetProperty("binary"), deployment });
            var tree = Data(await CallAsync("ui.inspect", new { pid = target }));
            if (tree.EnumerateArray().Any(e => e.Str("automationId") == "saveButton")) Pass("deploy.fixture_controls.v" + version, "The deployed fixture exposes its real UI Automation controls");
            else Fail("deploy.fixture_controls.v" + version, "The deployed fixture exposes its real UI Automation controls", tree);
            await CallAsync("ui.text", new { pid = target, text = "Remote round trip v" + version }); await CallAsync("ui.click", new { pid = target, automationId = "saveButton" }); await Task.Delay(400, stop.Token);
            string result = Path.Combine(output, "fixture-result-v" + version + ".json"); await CliAsync(["download", "--path", Path.Combine(workspace, "fixture-output-v" + version, "result.json"), "--file", result]);
            var saved = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(result));
            if (saved.Str("text") == "Remote round trip v" + version && saved.Str("version") == version + ".0.0.0") Pass("interaction.fixture_result.v" + version, "Real remote UI text and save produce the expected result file", saved);
            else Fail("interaction.fixture_result.v" + version, "Real remote UI text and save produce the expected result file", saved);
            string logPath = Path.Combine(output, "fixture-v" + version + ".log"); await CliAsync(["download", "--path", Path.Combine(workspace, "fixture-output-v" + version, "fixture.log"), "--file", logPath]);
            if ((await File.ReadAllTextAsync(logPath)).Contains("saved " + version + ".0.0")) Pass("interaction.fixture_log.v" + version, "The fixture log is downloaded from the remote workspace");
            else Fail("interaction.fixture_log.v" + version, "The fixture log is downloaded from the remote workspace");
            var screenshot = await CliAsync(["screenshot", "--file", Path.Combine(output, "remote-v" + version + ".jpg")]);
            if (screenshot.GetProperty("roundTripMs").GetDouble() > 0) Pass("interaction.fresh_capture.v" + version, "The screenshot is a fresh remote capture", screenshot);
            else Fail("interaction.fresh_capture.v" + version, "The screenshot is a fresh remote capture", screenshot);
            if (version == 1) await CallAsync("stop", new { pid = target });
        }
        if (versionHashes.Distinct().Count() == 2) Pass("deploy.distinct_binaries", "Fixture v1 and v2 are different uploaded binaries", new { hashes = versionHashes });
        else Fail("deploy.distinct_binaries", "Fixture v1 and v2 are different uploaded binaries", new { hashes = versionHashes });

        var debug = Data(await CallAsync("debug.attach", new { pid = target, seconds = 3 }));
        if (debug.GetProperty("attached").GetBoolean() && debug.GetProperty("breakpointObserved").GetBoolean() && debug.GetProperty("detached").GetBoolean()) Pass("debug.native_attach", "Native debugger attach, breakpoint and detach are reported by the product", debug);
        else Fail("debug.native_attach", "Native debugger attach, breakpoint and detach are reported by the product", debug);
        var dump = Data(await CallAsync("debug.dump", new { pid = target }));
        if (dump.Long("size") > 1024) Pass("debug.minidump", "The product writes a non-empty minidump", new { size = dump.Long("size"), path = dump.Str("path") });
        else Fail("debug.minidump", "The product writes a non-empty minidump", dump);
        var processesReply = Data(await CallAsync("processes")); var sample = processesReply.GetProperty("processes").EnumerateArray().First(p => p.Int("pid") == target);
        if (sample.GetProperty("cpuPercentTotalMachine").ValueKind == JsonValueKind.Number && sample.Long("workingSetBytes") > 0) Pass("diagnostics.process_cpu_memory", "Process diagnostics include sampled CPU and memory values", sample);
        else Fail("diagnostics.process_cpu_memory", "Process diagnostics include sampled CPU and memory values", sample);
        var system = Data(await CallAsync("system"));
        if (system.GetProperty("cpuPercentTotalMachine").ValueKind == JsonValueKind.Number && system.GetProperty("volumes").GetArrayLength() > 0) Pass("diagnostics.system_cpu_ram_volumes", "System diagnostics include CPU, RAM and volumes", system);
        else Fail("diagnostics.system_cpu_ram_volumes", "System diagnostics include CPU, RAM and volumes", system);
        foreach (string operation in new[] { "network", "services", "events" })
        {
            var value = Data(await CallAsync(operation));
            if (value.Int("exitCode") == 0) Pass("diagnostics." + operation, "The " + operation + " diagnostic command returns its result", new { stdoutCharacters = value.Str("stdout").Length });
            else Fail("diagnostics." + operation, "The " + operation + " diagnostic command returns its result", value);
        }
        var files = Data(await CallAsync("files", new { path = "deployments/fixture" }));
        if (files.GetArrayLength() >= 2) Pass("files.remote_browse", "Remote file listing contains both deployed fixture directories", files);
        else Fail("files.remote_browse", "Remote file listing contains both deployed fixture directories", files);
        var command = Data(await CallAsync("command", new { file = "cmd.exe", arguments = new[] { "/d", "/c", "echo maintenance-fixture-ok" } }));
        if (command.Int("exitCode") == 0 && command.Str("stdout").Contains("maintenance-fixture-ok")) Pass("commands.bounded_standard_user", "A bounded explicit command returns its stdout", command);
        else Fail("commands.bounded_standard_user", "A bounded explicit command returns its stdout", command);
        var streaming = CliAsync(["stream", "--seconds", "12", "--fps", "5", "--report", Path.Combine(output, "stream-metrics.json"), "--last-frame", Path.Combine(output, "stream-last.jpg")]);
        await Task.Delay(2000, stop.Token); await CallAsync("ui.key", new { pid = target, key = "TAB" }); await CallAsync("ui.key", new { pid = target, key = "TAB" });
        var fresh = Data(await CallAsync("screenshot")); string layout = fresh.GetProperty("geometry").Str("layoutId");
        var stale = await CallAsync("ui.input", new { kind = "move", x = 1, y = 1, layoutId = "stale" }, false);
        if (!stale.GetProperty("ok").GetBoolean()) Pass("input.stale_geometry_rejected", "Input with stale display geometry is rejected", stale);
        else Fail("input.stale_geometry_rejected", "Input with stale display geometry is rejected", stale);
        await CallAsync("ui.input", new { kind = "move", x = fresh.GetProperty("geometry").Int("x") + 100, y = fresh.GetProperty("geometry").Int("y") + 100, layoutId = layout });
        var stream = await streaming;
        bool streamGood = stream.Int("frames") >= 10 && stream.Int("distinctFrames") >= 3 && stream.GetProperty("receivedFps").GetDouble() <= 5.2;
        if (streamGood) Pass("stream.tls_continuous_input", "The continuous TLS stream varies frames and respects the five fps cap while input is accepted", stream);
        else Fail("stream.tls_continuous_input", "The continuous TLS stream varies frames and respects the five fps cap while input is accepted", stream);
        CaptureDesktop("controller-processes.png");

        var timeout = await CallAsync("command", new { file = "ping.exe", arguments = new[] { "-n", "20", "127.0.0.1" } }, false, 1);
        if (timeout.Str("error") == "cancelled_or_timeout") Pass("commands.deadline", "A command deadline cancels the remote process tree", timeout);
        else Fail("commands.deadline", "A command deadline cancels the remote process tree", timeout);
        string cancelId = Guid.NewGuid().ToString(); var cancellable = CallAsync("command", new { file = "ping.exe", arguments = new[] { "-n", "20", "127.0.0.1" } }, false, 30, cancelId); await Task.Delay(800, stop.Token); await CallAsync("cancel", new { id = cancelId }); var cancelled = await cancellable;
        if (cancelled.Str("error") == "cancelled_or_timeout") Pass("commands.explicit_cancel", "An explicitly cancelled command reports cancellation", cancelled);
        else Fail("commands.explicit_cancel", "An explicitly cancelled command reports cancellation", cancelled);

        string transfer = Guid.NewGuid().ToString("N"); byte[] resumedData = Encoding.UTF8.GetBytes("first block / second block"); string resumeHash = Convert.ToHexString(SHA256.HashData(resumedData));
        await CallAsync("upload.begin", new { transfer, path = "resume-proof.txt", size = resumedData.Length, sha256 = resumeHash }); await CallAsync("upload.chunk", new { transfer, offset = 0, data = Convert.ToBase64String(resumedData[..10]) });
        await LabMessageAsync(peerHost!, "RD_LAB_RESTART"); await Task.Delay(1200, stop.Token);
        var afterRestart = Data(await CallAsync("status"));
        if (afterRestart.Str("machine").Length > 0) Pass("session.reconnect_without_repair", "Agent restart reconnects with the saved pairing", new { machine = afterRestart.Str("machine") });
        else Fail("session.reconnect_without_repair", "Agent restart reconnects with the saved pairing", afterRestart);
        var offset = Data(await CallAsync("upload.status", new { transfer }));
        if (offset.Long("offset") == 10) Pass("transfer.resume_offset", "Upload offset survives an agent restart", offset);
        else Fail("transfer.resume_offset", "Upload offset survives an agent restart", offset);
        await CallAsync("upload.chunk", new { transfer, offset = 10, data = Convert.ToBase64String(resumedData[10..]) }); var resumed = Data(await CallAsync("upload.commit", new { transfer }));
        if (resumed.Str("sha256") == resumeHash) Pass("transfer.resume_hash", "Resumed upload commits with the expected SHA-256", resumed);
        else Fail("transfer.resume_hash", "Resumed upload commits with the expected SHA-256", resumed);
        var traversal = await CallAsync("upload.begin", new { transfer = Guid.NewGuid().ToString("N"), path = "../escape.txt", size = 0, sha256 = new string('0', 64) }, false);
        if (!traversal.GetProperty("ok").GetBoolean()) Pass("transfer.traversal_denied", "Remote upload path traversal is rejected", traversal);
        else Fail("transfer.traversal_denied", "Remote upload path traversal is rejected", traversal);

        string restartId = Guid.NewGuid().ToString(); var replay1 = Data(await CallAsync("start", new { path = "deployments/fixture/v1/Fixture.exe", arguments = new[] { Path.Combine(workspace!, "replay-output") } }, id: restartId)); var replay2 = Data(await CallAsync("start", new { path = "deployments/fixture/v1/Fixture.exe", arguments = new[] { Path.Combine(workspace!, "replay-output") } }, id: restartId));
        if (replay1.Int("pid") == replay2.Int("pid")) Pass("operations.idempotent_replay", "Repeating a mutation id does not duplicate a launch", new { first = replay1.Int("pid"), second = replay2.Int("pid") });
        else Fail("operations.idempotent_replay", "Repeating a mutation id does not duplicate a launch", new { first = replay1.Int("pid"), second = replay2.Int("pid") });
        var restarted = Data(await CallAsync("restart", new { pid = replay1.Int("pid"), arguments = new[] { Path.Combine(workspace!, "replay-output") } }));
        if (restarted.Int("pid") != replay1.Int("pid")) Pass("operations.restart_new_pid", "Application restart returns a new PID", restarted);
        else Fail("operations.restart_new_pid", "Application restart returns a new PID", restarted);
        await CallAsync("stop", new { pid = restarted.Int("pid") });

        var hang = CallAsync("ui.click", new { pid = target, automationId = "hangButton" }, false, 25); await Task.Delay(6000, stop.Token); var hung = Data(await CallAsync("process.info", new { pid = target }));
        if (!hung.GetProperty("responding").GetBoolean()) Pass("failure.hang_detected", "A deliberately hung application is reported as unresponsive", hung);
        else Fail("failure.hang_detected", "A deliberately hung application is reported as unresponsive", hung);
        await hang; await Task.Delay(16000, stop.Token); var recovered = Data(await CallAsync("process.info", new { pid = target }));
        if (recovered.GetProperty("responding").GetBoolean()) Pass("failure.hang_recovered", "The fixture recovers after its deliberate hang", recovered);
        else Fail("failure.hang_recovered", "The fixture recovers after its deliberate hang", recovered);
        int crashedPid = target; await CallAsync("ui.click", new { pid = target, automationId = "crashButton" }, false, 20); await Task.Delay(1500, stop.Token); var afterCrash = Data(await CallAsync("processes"));
        if (!afterCrash.GetProperty("processes").EnumerateArray().Any(p => p.Int("pid") == crashedPid)) Pass("failure.crash_detected", "A deliberately crashed application disappears from process inventory", new { crashedPid });
        else Fail("failure.crash_detected", "A deliberately crashed application disappears from process inventory", new { crashedPid });
        target = Data(await CallAsync("start", new { path = "deployments/fixture/v2/Fixture.exe", arguments = new[] { Path.Combine(workspace!, "fixture-output-v2") } })).Int("pid"); await Task.Delay(1200, stop.Token);
        if (target != crashedPid && Data(await CallAsync("process.info", new { pid = target })).GetProperty("responding").GetBoolean()) Pass("failure.crash_relaunch", "The application can be relaunched after a crash", new { target });
        else Fail("failure.crash_relaunch", "The application can be relaunched after a crash", new { target, crashedPid });
        var history = Data(await CallAsync("history"));
        if (history.GetArrayLength() > 10 && !history.GetRawText().Contains("Remote round trip") && !history.GetRawText().Contains("token")) Pass("audit.intervention_history", "History records interventions without input text or tokens", new { count = history.GetArrayLength() });
        else Fail("audit.intervention_history", "History records interventions without input text or tokens", history);
        await CallAsync("stop", new { pid = target });
    }

    private async Task ProbeReconnectAndUpdateAsync(string controllerHash)
    {
        string rollback = Path.Combine(AppContext.BaseDirectory, "fixtures", "rollback", "RemoteDebugger.previous.exe");
        if (!File.Exists(rollback)) Block("controller.sync_rollback", "An interrupted mismatched update rolls back and reconnects", "The build did not publish a deterministic older signed Release fixture.", new { expectedFixture = rollback }, required: false);
        else Block("controller.sync_rollback", "An interrupted mismatched update rolls back and reconnects", "The update fixture exists, but no product RPC contract for an induced interrupted update is exposed yet.", new { fixture = rollback, controllerHash }, required: false);
        if (peerHost == null) return;
        JsonElement status = Data(await CallAsync("session.heartbeat")); string remoteHash = FindString(status, "agentBinarySha256", "binarySha256", "releaseSha256", "executableSha256", "sha256");
        if (remoteHash.Equals(controllerHash, StringComparison.OrdinalIgnoreCase) && (!status.TryGetProperty("binaryMatched", out var matched) || matched.GetBoolean())) Pass("controller.sync_after_restart", "The reconnected agent still reports the controller Release hash", new { controllerHash, remoteHash, status });
        else Fail("controller.sync_after_restart", "The reconnected agent still reports the controller Release hash", new { controllerHash, remoteHash, status });
    }

    private async Task<bool> ProbeTrayAndTerminateAsync()
    {
        if (product == null) throw new InvalidOperationException("Controller process is missing.");
        Native.FocusWindow(product.Id);
        product.CloseMainWindow();
        await Task.Delay(1400, stop.Token);
        product.Refresh();
        bool trayAlive = !product.HasExited && product.MainWindowHandle == IntPtr.Zero;
        JsonElement? heartbeat = null;
        try { heartbeat = Data(await CallAsync("status")); } catch (Exception) { }
        bool heartbeatAlive = heartbeat.HasValue && heartbeat.Value.ValueKind == JsonValueKind.Object;
        if (trayAlive && heartbeatAlive) Pass("controller.close_to_tray", "Closing the controller hides it in the tray while its session heartbeat remains alive", new { pid = product.Id, heartbeat = heartbeat!.Value });
        else Fail("controller.close_to_tray", "Closing the controller hides the controller in the tray while its session heartbeat remains alive", new { trayAlive, heartbeatAlive, pid = product.Id, exited = product.HasExited });
        var open = FindDesktopMenuItem("Ouvrir");
        if (open != null) { InvokeElement(open); await WaitForUiAsync(() => product.MainWindowHandle != IntPtr.Zero, 15); }
        else Fail("controller.tray_restore", "The tray Open action restores the controller", new { menu = "Ouvrir missing" });

        var terminate = FindVisibleId(ContractId("terminateSession"));
        if (terminate == null) { Fail("controller.terminate", "The controller exposes a clear terminate support action", new { id = ContractId("terminateSession") }); return false; }
        InvokeElement(terminate);
        bool disconnected = await WaitForTextAsync("connectionStatus", x => !IsConnected(x), 30);
        JsonElement? stopped = null;
        for (int n = 0; n < 18; n++)
        {
            try { stopped = await LabMessageAsync(peerHost!, "RD_LAB_STATUS"); if (!stopped.Value.GetProperty("alive").GetBoolean()) break; } catch (Exception) { break; }
            await Task.Delay(1000, stop.Token);
        }
        bool agentExited = stopped.HasValue && !stopped.Value.GetProperty("alive").GetBoolean();
        if (disconnected && agentExited) { sawTermination = true; Pass("controller.terminate", "Terminate support ends the connection and exits the agent", new { disconnected, stopped }); }
        else Fail("controller.terminate", "Terminate support ends the connection and exits the agent", new { disconnected, agentExited, stopped });
        var deniedAfterTermination = await CallAsync("status", requireSuccess: false);
        bool accessClosed = !deniedAfterTermination.TryGetProperty("ok", out var accessOk) || accessOk.ValueKind != JsonValueKind.True;
        if (accessClosed) Pass("controller.terminated_access_denied", "The terminated support session no longer authorizes controller requests", deniedAfterTermination);
        else Fail("controller.terminated_access_denied", "The terminated support session no longer authorizes controller requests", deniedAfterTermination);
        CaptureDesktop("controller-terminated.png");
        return agentExited;
    }

    private async Task<bool> ProbeRollbackTerminateSavedTokenAsync()
    {
        // The rollback controller may still be waiting for the replacement's
        // startup-health response, so its normal green connection state and
        // terminate button are intentionally unavailable.  Exercise the same
        // saved DPAPI-protected token through the product CLI instead of
        // fabricating a second authorization path.
        var ended = await CallAsync("session.end", requireSuccess: false, seconds: 50);
        bool endAccepted = ended.TryGetProperty("ok", out var endedOk) && endedOk.ValueKind == JsonValueKind.True;
        JsonElement? stopped = null;
        for (int n = 0; n < 30; n++)
        {
            try
            {
                stopped = await LabMessageAsync(peerHost!, "RD_LAB_STATUS");
                if (!stopped.Value.GetProperty("alive").GetBoolean()) break;
            }
            catch (Exception) { break; }
            await Task.Delay(1000, stop.Token);
        }
        bool agentExited = stopped.HasValue && !stopped.Value.GetProperty("alive").GetBoolean();
        if (endAccepted && agentExited)
        {
            sawTermination = true;
            Pass("controller.rollback_terminate_saved_token", "The rollback session is terminated through the saved authenticated token and the restored agent exits", new { ended, stopped });
        }
        else
            Fail("controller.rollback_terminate_saved_token", "The rollback session is terminated through the saved authenticated token and the restored agent exits", new { ended, endAccepted, agentExited, stopped });

        var deniedAfterTermination = await CallAsync("status", requireSuccess: false, seconds: 15);
        bool accessClosed = !deniedAfterTermination.TryGetProperty("ok", out var accessOk) || accessOk.ValueKind != JsonValueKind.True;
        if (accessClosed) Pass("controller.rollback_saved_token_revoked", "The rollback token no longer authorizes requests after termination", deniedAfterTermination);
        else Fail("controller.rollback_saved_token_revoked", "The rollback token no longer authorizes requests after termination", deniedAfterTermination);
        CaptureDesktop("controller-rollback-terminated.png");
        return agentExited;
    }

    private AutomationElement? FindDesktopMenuItem(string name)
    {
        try
        {
            return AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>().SelectMany(window =>
            {
                try { return window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name)).Cast<AutomationElement>(); } catch (ElementNotAvailableException) { return []; }
            }).FirstOrDefault();
        }
        catch (ElementNotAvailableException) { return null; }
    }

    private void InvokeElement(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke) && invoke is InvokePattern pattern) pattern.Invoke();
        else { var p = element.GetClickablePoint(); Native.Mouse(0, (int)p.X, (int)p.Y); }
    }

    private async Task<JsonElement> CallAsync(string operation, object? args = null, bool requireSuccess = true, int seconds = 60, string? id = null)
    {
        string request = Path.Combine(output, "request-" + ++serial + ".json");
        await File.WriteAllTextAsync(request, Json.Text(new { operation, args = args ?? new { }, timeoutSeconds = seconds, id = id ?? Guid.NewGuid().ToString() }), stop.Token);
        try { return await CliAsync(["call", "--request", request], requireSuccess: requireSuccess); }
        finally { try { File.Delete(request); } catch (IOException) { } }
    }

    private async Task<JsonElement> CliAsync(string[] args, string? stdin = null, bool requireSuccess = true)
    {
        var psi = new ProcessStartInfo(application) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(application)! };
        psi.ArgumentList.Add("cli"); foreach (string arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi) ?? throw new IOException("CLI process did not start.");
        if (stdin != null) await p.StandardInput.WriteLineAsync(stdin); p.StandardInput.Close();
        Task<string> stdoutTask = p.StandardOutput.ReadToEndAsync(); Task<string> stderrTask = p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync(stop.Token);
        string stdout = await stdoutTask; string stderr = await stderrTask;
        if (stdout.Trim().Length == 0) throw new IOException("CLI produced no JSON: " + stderr);
        var result = JsonSerializer.Deserialize<JsonElement>(stdout);
        if (requireSuccess && (!result.TryGetProperty("ok", out var ok) || !ok.GetBoolean() || p.ExitCode != 0)) throw new IOException("CLI failed " + Json.Text(args) + ": " + stdout + stderr);
        return result;
    }

    private static JsonElement Data(JsonElement reply) => reply.GetProperty("data");

    private static string FindString(JsonElement value, params string[] keys)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (string key in keys)
            {
                if (value.TryGetProperty(key, out var found) && found.ValueKind == JsonValueKind.String && found.GetString() is string text && text.Length > 0) return text;
            }
            foreach (var child in value.EnumerateObject())
            {
                string nested = FindString(child.Value, keys);
                if (nested.Length > 0) return nested;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray())
            {
                string nested = FindString(child, keys);
                if (nested.Length > 0) return nested;
            }
        }
        return "";
    }

    private void CaptureDesktop(string name)
    {
        try
        {
            if (product == null || product.HasExited) return;
            Native.FocusWindow(product.Id);
            var capture = DesktopCapture.Capture(0, 1920, 85);
            var data = Json.Element(capture).GetProperty("data").GetString()!;
            File.WriteAllBytes(Path.Combine(output, name), Convert.FromBase64String(data));
        }
        catch (Exception ex) { File.AppendAllText(Path.Combine(output, "capture-warnings.txt"), name + ": " + ex.Message + Environment.NewLine); }
    }
}

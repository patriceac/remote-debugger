using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using RemoteDebugger;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private async Task LoopbackLifetimeAsync()
    {
        try
        {
            await LoopbackLifetimeCoreAsync();
        }
        finally
        {
            await CleanupLoopbackProcessesAsync();
        }
    }

    private async Task LoopbackLifetimeCoreAsync()
    {
        if (scope != "runtime" || IsUpdateVariant) throw new ArgumentException("Loopback lifetime only accepts the Runtime/None configuration.");

        string root = Path.Combine(output, "loopback-lifetime");
        string agentRoot = Path.Combine(root, "agent-data");
        string controllerRoot = Path.Combine(root, "controller-data");
        Directory.CreateDirectory(agentRoot);
        Directory.CreateDirectory(controllerRoot);

        // Seed the actual saved-profile path with an unreachable endpoint before
        // the agent starts. This catches startup/cleanup code that tries to
        // contact a stale controller profile while the agent is offline.
        string seededFingerprint = new string('A', 64);
        string seededToken = new string('B', 64);
        var seeded = new RemoteClient(new Connection("127.0.0.1", 45839, seededFingerprint, seededToken));
        seeded.Save(RemoteClient.DefaultPath);
        Pass("lifetime.offline_profile_seed", "The lifetime run starts with an unreachable saved controller profile", new { host = "127.0.0.1", port = 45839, fingerprintLength = seededFingerprint.Length, tokenLength = seededToken.Length });

        string releaseHash = await HashFileAsync(application);
        loopbackAgent = LaunchLoopbackProduct(true, agentRoot);
        product = loopbackAgent;
        await WaitUiAsync();
        WindowState = Forms.FormWindowState.Minimized;

        guestElevated = Native.IsElevated();
        Pass("lifetime.agent_elevation_context", "The lifetime Lab records the actual Windows elevation context supplied to the guest", new { elevated = guestElevated, user = Environment.UserName }, required: false);
        await ProbeSleepRequestAsync();
        string initialCode = await WaitPairingCodeAsync();
        string initialCountdown = TryValue("pairingCountdown");
        int? initialRemaining = ParseCountdownSeconds(initialCountdown);
        if (initialRemaining.HasValue)
            Pass("lifetime.pairing_countdown", "The lifetime run records the real pairing expiry countdown before waiting for rotation", new { initialCountdown, initialRemainingSeconds = initialRemaining.Value });
        else
        {
            Fail("lifetime.pairing_countdown", "The lifetime run records the real pairing expiry countdown before waiting for rotation", new { initialCountdown });
            await FinishAsync();
            return;
        }

        RotationObservation rotation = await WaitForLoopbackRotationAsync(initialCode, initialCountdown, initialRemaining.Value);
        if (!rotation.Rotated)
        {
            await FinishAsync();
            return;
        }

        string fingerprint = await FindLoopbackFingerprintAsync();
        var oldPairArgs = new List<string> { "pair", "--host", "127.0.0.1", "--port", "45832", "--connection", Path.Combine(output, "old-code.connection") };
        if (fingerprint.Length > 0)
        {
            oldPairArgs.Add("--fingerprint");
            oldPairArgs.Add(fingerprint);
        }
        var oldPair = await CliAsync(oldPairArgs.ToArray(), stdin: initialCode, requireSuccess: false);
        bool oldCodeDenied = IsPairingDenied(oldPair);
        if (oldCodeDenied)
            Pass("lifetime.expired_code_denied", "The previous pairing code is denied after the real five-minute rotation", new { code = CodeEvidence(initialCode), response = oldPair });
        else
            Fail("lifetime.expired_code_denied", "The previous pairing code is denied after the real five-minute rotation", new { code = CodeEvidence(initialCode), response = oldPair, fingerprintAvailable = fingerprint.Length > 0 });

        loopbackController = LaunchLoopbackProduct(false, controllerRoot);
        product = loopbackController;
        await WaitUiAsync();
        WindowState = Forms.FormWindowState.Minimized;
        Set("host", "127.0.0.1");
        Set("pairCode", rotation.NewCode);
        FocusAndEnter("pairCode");
        string pairStatusBeforeWait = TryValue("connectionStatus");
        bool connected = await WaitForTextAsync("connectionStatus", IsConnected, 60);
        string pairStatus = TryValue("connectionStatus");
        if (connected)
        {
            sawPairing = true;
            Pass("lifetime.code_enter_pairing", "The controller pairs with the newly rotated code through the normal code plus Enter flow", new { pairStatusBeforeWait, pairStatus, codeLength = rotation.NewCode.Length });
        }
        else
        {
            Fail("lifetime.code_enter_pairing", "The controller pairs with the newly rotated code through the normal code plus Enter flow", new { pairStatusBeforeWait, pairStatus });
            await FinishAsync();
            return;
        }

        var heartbeat = await WaitForBinaryMatchAsync(releaseHash, 30);
        string heartbeatHash = FindString(heartbeat, "agentBinarySha256", "binarySha256", "releaseSha256", "executableSha256", "sha256");
        if (heartbeatHash.Equals(releaseHash, StringComparison.OrdinalIgnoreCase))
            Pass("lifetime.heartbeat_before_disconnect", "A real paired heartbeat is established before the controller is disconnected", new { heartbeatHash, heartbeat });
        else
            Fail("lifetime.heartbeat_before_disconnect", "A real paired heartbeat is established before the controller is disconnected", new { heartbeatHash, releaseHash, heartbeat });

        CaptureDesktop("loopback-lifetime-before-disconnect.png");
        DateTimeOffset disconnectedUtc = DateTimeOffset.UtcNow;
        int controllerPid = loopbackController.Id;
        try
        {
            loopbackController.Kill(entireProcessTree: true);
            await WaitForProcessExitAsync(loopbackController, TimeSpan.FromSeconds(20));
            Pass("lifetime.controller_heartbeat_stopped", "The controller process is stopped to create a real session disconnect", new { controllerPid, disconnectedUtc });
        }
        catch (Exception ex)
        {
            Fail("lifetime.controller_heartbeat_stopped", "The controller process is stopped to create a real session disconnect", new { controllerPid, disconnectedUtc, error = ex.Message });
            await FinishAsync();
            return;
        }

        await WaitForLoopbackDisconnectExitAsync(disconnectedUtc);
        await ProbeSleepReleasedAsync();
        await FinishAsync();
    }

    private sealed record RotationObservation(bool Rotated, string NewCode, string LastCountdown, int UnchangedSamples, double WaitedSeconds, bool Timing);

    private async Task<RotationObservation> WaitForLoopbackRotationAsync(string oldCode, string initialCountdown, int initialRemaining)
    {
        var elapsed = Stopwatch.StartNew();
        int earlyTolerance = PairingSeconds("rotationEarlyToleranceSeconds", 2);
        int lateTolerance = PairingSeconds("rotationLateToleranceSeconds", 15);
        double earliestAllowed = Math.Max(0, initialRemaining - earlyTolerance);
        double latestAllowed = initialRemaining + lateTolerance;
        int unchangedSamples = 0;
        bool changedBeforeDeadline = false;
        string current = oldCode;
        string lastCountdown = initialCountdown;

        while (elapsed.Elapsed.TotalSeconds <= latestAllowed)
        {
            current = TryPairingCode();
            lastCountdown = TryValue("pairingCountdown");
            double waited = elapsed.Elapsed.TotalSeconds;
            if (waited < earliestAllowed)
            {
                if (SixDigits.IsMatch(current) && current != oldCode) changedBeforeDeadline = true;
                else if (current == oldCode) unchangedSamples++;
            }
            else if (SixDigits.IsMatch(current) && current != oldCode)
            {
                bool timing = !changedBeforeDeadline && unchangedSamples >= 2 && waited >= earliestAllowed && waited <= latestAllowed;
                var evidence = new
                {
                    oldCode = CodeEvidence(oldCode),
                    newCode = CodeEvidence(current),
                    initialCountdown,
                    lastCountdown,
                    initialRemainingSeconds = initialRemaining,
                    waitedSeconds = waited,
                    unchangedSamples,
                    changedBeforeDeadline,
                    earliestAllowedSeconds = earliestAllowed,
                    latestAllowedSeconds = latestAllowed,
                    timing
                };
                if (timing) Pass("lifetime.pairing_code_rotation", "The pairing code stays unchanged until its five-minute expiry and then rotates within scheduler tolerance", evidence);
                else Fail("lifetime.pairing_code_rotation", "The pairing code stays unchanged until its five-minute expiry and then rotates within scheduler tolerance", evidence);
                return new(true, current, lastCountdown, unchangedSamples, waited, timing);
            }
            await Task.Delay(1000, stop.Token);
        }

        var timeoutEvidence = new
        {
            oldCode = CodeEvidence(oldCode),
            current = CodeEvidence(current),
            initialCountdown,
            lastCountdown,
            initialRemainingSeconds = initialRemaining,
            waitedSeconds = elapsed.Elapsed.TotalSeconds,
            unchangedSamples,
            changedBeforeDeadline,
            earliestAllowedSeconds = earliestAllowed,
            latestAllowedSeconds = latestAllowed
        };
        Fail("lifetime.pairing_code_rotation", "The pairing code stays unchanged until its five-minute expiry and then rotates within scheduler tolerance", timeoutEvidence);
        return new(false, current, lastCountdown, unchangedSamples, elapsed.Elapsed.TotalSeconds, false);
    }

    private async Task<string> FindLoopbackFingerprintAsync()
    {
        try
        {
            var discovered = await CliAsync(["discover"], requireSuccess: false);
            string fingerprint = FindString(discovered, "fingerprint");
            if (fingerprint.Length == 64) return fingerprint;
        }
        catch (Exception) { }

        string visible = string.Join(" ", UiTexts());
        var match = Regex.Match(visible, "(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant);
        return match.Success ? match.Value : "";
    }

    private static bool IsPairingDenied(JsonElement reply)
    {
        if (reply.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True) return false;
        string value = reply.GetRawText().ToLowerInvariant();
        return value.Contains("pairing_denied", StringComparison.Ordinal)
            || value.Contains("incorrect", StringComparison.Ordinal)
            || value.Contains("expired", StringComparison.Ordinal)
            || value.Contains("unavailable", StringComparison.Ordinal);
    }

    private async Task WaitForLoopbackDisconnectExitAsync(DateTimeOffset disconnectedUtc)
    {
        if (loopbackAgent == null) throw new InvalidOperationException("Loopback agent process is missing.");
        TimeSpan grace = TimeSpan.FromSeconds(PairingSeconds("disconnectGraceSeconds", 600));
        TimeSpan earlyTolerance = TimeSpan.FromSeconds(5);
        TimeSpan lateTolerance = TimeSpan.FromSeconds(15);
        DateTimeOffset deadline = disconnectedUtc + grace + lateTolerance;
        DateTimeOffset earliest = disconnectedUtc + grace - earlyTolerance;
        DateTimeOffset? exitedUtc = null;

        while (DateTimeOffset.UtcNow <= deadline)
        {
            loopbackAgent.Refresh();
            if (loopbackAgent.HasExited)
            {
                exitedUtc = DateTimeOffset.UtcNow;
                break;
            }
            await Task.Delay(1000, stop.Token);
        }

        if (exitedUtc.HasValue)
        {
            double elapsedSeconds = (exitedUtc.Value - disconnectedUtc).TotalSeconds;
            bool timing = exitedUtc.Value >= earliest && exitedUtc.Value <= deadline;
            var evidence = new { disconnectedUtc, exitedUtc, elapsedSeconds, graceSeconds = grace.TotalSeconds, earliestAllowedUtc = earliest, latestAllowedUtc = deadline, timing };
            if (timing) Pass("lifetime.disconnect_grace_exit", "The agent exits after the real ten-minute disconnect grace period", evidence);
            else Fail("lifetime.disconnect_grace_exit", "The agent exits after the real ten-minute disconnect grace period", evidence);
        }
        else
            Fail("lifetime.disconnect_grace_exit", "The agent exits after the real ten-minute disconnect grace period", new { disconnectedUtc, graceSeconds = grace.TotalSeconds, deadline, agentExited = false });
    }
}

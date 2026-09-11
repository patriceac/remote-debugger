using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using RemoteDebugger;
using RemoteDebugger.Core;
using Forms = System.Windows.Forms;

namespace RemoteDebugger.Lab;

internal sealed partial class LabForm
{
    private Process? loopbackAgent;
    private Process? loopbackController;

    /// <summary>
    /// Runs a single-guest product smoke test. Both product processes are real
    /// Release binaries and bind only to loopback; this deliberately supplies
    /// no LAN-discovery, firewall, service, or UAC acceptance claim.
    /// </summary>
    private async Task LoopbackSmokeAsync()
    {
        try
        {
            await LoopbackSmokeCoreAsync();
        }
        finally
        {
            await CleanupLoopbackProcessesAsync();
        }
    }

    private async Task LoopbackTrayAsync()
    {
        try
        {
            await LoopbackSmokeCoreAsync(trayOnly: true);
        }
        finally
        {
            await CleanupLoopbackProcessesAsync();
        }
    }

    private async Task LoopbackSmokeCoreAsync(bool trayOnly = false)
    {
        if (scope != "runtime" || IsUpdateVariant) throw new ArgumentException("Loopback smoke only accepts the Runtime/None configuration.");

        string root = Path.Combine(output, "loopback");
        string agentRoot = Path.Combine(root, "agent-data");
        string controllerRoot = Path.Combine(root, "controller-data");
        Directory.CreateDirectory(agentRoot);
        Directory.CreateDirectory(controllerRoot);

        // A fresh Lab run must not inherit a token from a previous guest run.
        // Removing the encrypted saved connection is test isolation; pairing
        // below still obtains its token through the shipped PAKE flow.
        try { if (File.Exists(RemoteClient.DefaultPath)) File.Delete(RemoteClient.DefaultPath); } catch (IOException) { }

        string releaseHash = await HashFileAsync(application);
        loopbackAgent = LaunchLoopbackProduct(true, agentRoot);
        product = loopbackAgent;
        await WaitUiAsync();
        WindowState = Forms.FormWindowState.Minimized;

        string agentState = TryValue("agentState");
        string agentCode = await WaitPairingCodeAsync();
        string countdown = TryValue("pairingCountdown");
        guestElevated = Native.IsElevated();
        Pass("loopback.agent_elevation_context", "The loopback Lab records the actual Windows elevation context supplied to the guest", new { elevated = guestElevated, user = Environment.UserName }, required: false);
        if (IsAgentReadyState(agentState) && UiTexts().Any(x => x.Contains("Donner le contrôle", StringComparison.OrdinalIgnoreCase) || x.Contains("Donner le controle", StringComparison.OrdinalIgnoreCase)))
            Pass("loopback.agent_default_role", "The default Release launch opens the agent role and starts pairing", new { state = agentState, code = CodeEvidence(agentCode), launchArguments = new[] { "--loopback-only", "--data-root", "<agent-data>" } });
        else
            Fail("loopback.agent_default_role", "The default Release launch opens the agent role and starts pairing", new { state = agentState, code = CodeEvidence(agentCode), visible = UiTexts().Take(35).ToArray() });
        if (SixDigits.IsMatch(agentCode)) Pass("loopback.agent_pairing_code", "The loopback agent exposes six pairing digits", new { code = CodeEvidence(agentCode) });
        else Fail("loopback.agent_pairing_code", "The loopback agent exposes six pairing digits", new { code = CodeEvidence(agentCode) });
        if (TimeText.IsMatch(countdown)) Pass("loopback.agent_pairing_countdown", "The loopback agent exposes its pairing expiry countdown", new { countdown });
        else Fail("loopback.agent_pairing_countdown", "The loopback agent exposes its pairing expiry countdown", new { countdown });
        await ProbeSleepRequestAsync();
        var platformStatus = await TryPlatformStatusAsync();
        if (platformStatus.HasValue) Pass("loopback.platform_status", "The Release CLI exposes the local platform status without changing the loopback setup", platformStatus.Value, required: false);
        else Block("loopback.platform_status", "The Release CLI exposes the local platform status without changing the loopback setup", "The artifact did not return a local platform-status response.", required: false);
        Block("loopback.lan_scope", "LAN discovery, private firewall, installed broker, and UAC remain outside the loopback smoke claim", "Both product processes are intentionally bound to 127.0.0.1; the two-VM Provisioned/Full run owns LAN and broker evidence.", new { network = "loopback-only", discoveryClaimed = false, firewallClaimed = false, serviceClaimed = false }, required: false);
        CaptureDesktop("loopback-agent-pairing.png");
        if (trayOnly) await ProbeAgentTypographyAsync(paired: false);

        loopbackController = LaunchLoopbackProduct(false, controllerRoot);
        product = loopbackController;
        await WaitUiAsync();
        WindowState = Forms.FormWindowState.Minimized;

        bool fingerprintGateVisible = FindVisibleId("fingerprintVerified") != null || FindVisibleId("fingerprint") != null;
        if (fingerprintGateVisible)
            Fail("loopback.no_fingerprint_gate", "Loopback pairing has no visible fingerprint checkbox or fingerprint entry step", new { fingerprintGateVisible });
        else
            Pass("loopback.no_fingerprint_gate", "Loopback pairing has no visible fingerprint checkbox or fingerprint entry step", new { fingerprintGateVisible });
        Set("host", "127.0.0.1");
        Set("pairCode", agentCode);
        FocusAndEnter("pairCode");
        string pairStatusBeforeWait = TryValue("connectionStatus");
        bool connected = await WaitForTextAsync("connectionStatus", IsConnected, 60);
        string pairStatus = TryValue("connectionStatus");
        if (connected) sawPairing = true;
        if (connected) Pass("loopback.code_enter_pairing", "The controller pairs to the loopback agent with the six-digit code and Enter", new { host = "127.0.0.1", pairStatusBeforeWait, pairStatus, codeLength = agentCode.Length });
        else Fail("loopback.code_enter_pairing", "The controller pairs to the loopback agent with the six-digit code and Enter", new { host = "127.0.0.1", pairStatusBeforeWait, pairStatus, visible = UiTexts().Take(45).ToArray() });
        if (!connected) throw new InvalidOperationException("Loopback controller did not connect after code entry.");

        var statusReply = await CallAsync("status");
        var status = Data(statusReply);
        workspace = status.Str("workspace");
        string remoteHash = FindString(status, "agentBinarySha256", "binarySha256", "releaseSha256", "executableSha256", "sha256");
        var heartbeat = await WaitForBinaryMatchAsync(releaseHash, 30);
        string heartbeatHash = FindString(heartbeat, "agentBinarySha256", "binarySha256", "releaseSha256", "executableSha256", "sha256");
        bool matched = remoteHash.Equals(releaseHash, StringComparison.OrdinalIgnoreCase) && heartbeatHash.Equals(releaseHash, StringComparison.OrdinalIgnoreCase) && (!heartbeat.TryGetProperty("binaryMatched", out var binaryMatched) || binaryMatched.GetBoolean());
        if (matched) Pass("loopback.sync_exact_release", "The loopback agent reports the controller's exact Release bytes before live viewing", new { releaseHash, remoteHash, heartbeatHash, heartbeat });
        else Fail("loopback.sync_exact_release", "The loopback agent reports the controller's exact Release bytes before live viewing", new { releaseHash, remoteHash, heartbeatHash, heartbeat });

        LiveEvidence liveEvidence = await WaitForLiveEvidenceAsync(60);
        var firstFrame = await CliAsync(["screenshot", "--file", Path.Combine(output, "loopback-screen.jpg")]);
        bool freshFrame = firstFrame.TryGetProperty("ok", out var firstFrameOk) && firstFrameOk.ValueKind == JsonValueKind.True && firstFrame.TryGetProperty("roundTripMs", out var roundTrip) && roundTrip.ValueKind == JsonValueKind.Number && roundTrip.GetDouble() > 0;
        bool live = liveEvidence.BadgeVisible && liveEvidence.TelemetryVisible && freshFrame;
        if (live) Pass("loopback.live_auto_start", "The loopback controller starts live viewing after a fresh frame", new { liveBadge = new { visible = liveEvidence.BadgeVisible, text = liveEvidence.BadgeText }, streamStatus = liveEvidence.TelemetryText, waitedSeconds = liveEvidence.WaitedSeconds, frame = firstFrame });
        else Fail("loopback.live_auto_start", "The loopback controller starts live viewing after a fresh frame", new { live, liveBadge = new { visible = liveEvidence.BadgeVisible, text = liveEvidence.BadgeText }, streamStatus = liveEvidence.TelemetryText, waitedSeconds = liveEvidence.WaitedSeconds, freshFrame, frame = firstFrame });
        CaptureDesktop("loopback-controller-live.png");

        ProbeDefaultInput();
        ProbeLoopbackRemoteScreenInput();
        if (trayOnly)
        {
            product = loopbackAgent;
            try { await ProbeAgentTypographyAsync(paired: true); }
            finally { product = loopbackController; Native.FocusWindow(loopbackController.Id); }
            await ProbeLoopbackTrayAndTerminateAsync();
            await FinishAsync();
            return;
        }
        await ProbeAutoDataAsync();
        await CaptureLoopbackMinimumSizeAsync();
        await VerifyContinuousViewingAsync();
        await RunRegressionScenarioAsync(status);
        await ProbeLoopbackTrayAndTerminateAsync();
        await FinishAsync();
    }

    private async Task CleanupLoopbackProcessesAsync()
    {
        foreach (Process? process in new[] { loopbackController, loopbackAgent })
        {
            if (process == null) continue;
            try
            {
                process.Refresh();
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                    await WaitForProcessExitAsync(process, TimeSpan.FromSeconds(8));
                }
            }
            catch (Exception ex)
            {
                Record("loopback.cleanup", "Loopback product cleanup attempted graceful process shutdown", "blocked", false, new { pid = SafeProcessId(process), error = ex.Message });
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static int? SafeProcessId(Process process)
    {
        try { return process.Id; } catch (InvalidOperationException) { return null; }
    }

    private sealed record TrayContext(AutomationElement? OpenItem, string IconName, bool OverflowOpened);
    private sealed record TrayBounds(int Left, int Top, int Width, int Height);
    private sealed record TrayClickResult(bool Success, string Method, string Name, string Error, TrayBounds Bounds);

    private static TrayBounds ToTrayBounds(System.Windows.Rect bounds)
        => new(RoundTrayCoordinate(bounds.Left), RoundTrayCoordinate(bounds.Top), RoundTrayCoordinate(bounds.Width), RoundTrayCoordinate(bounds.Height));

    private static int RoundTrayCoordinate(double value)
        => value <= int.MinValue ? int.MinValue : value >= int.MaxValue ? int.MaxValue : (int)Math.Round(value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extraInfo);

    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;

    private static AutomationElement[] FindExplorerShellRoots()
    {
        try
        {
            // UIA's desktop-wide descendant walk is both expensive and prone
            // to finding the product window itself before it reaches the
            // shell notification area.  Restrict the search to top-level
            // windows owned by the Explorer processes that host the taskbar.
            var explorerPids = new HashSet<int>();
            foreach (var explorer in Process.GetProcessesByName("explorer"))
            {
                try { explorerPids.Add(explorer.Id); }
                catch (InvalidOperationException) { }
                finally { explorer.Dispose(); }
            }
            if (explorerPids.Count == 0) return [];

            return AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>()
                .Where(element =>
                {
                    try
                    {
                        var current = element.Current;
                        if (!explorerPids.Contains(current.ProcessId)) return false;

                        // Shell_TrayWnd and NotifyIconOverflowWindow are the
                        // usual Windows 10/11 providers.  Keep the taskbar
                        // bounds fallback for XAML popup providers whose
                        // class name is implementation-specific.
                        string className = current.ClassName.ToLowerInvariant();
                        string name = current.Name.ToLowerInvariant();
                        bool shellClass = className.Contains("tray", StringComparison.Ordinal)
                            || className.Contains("notify", StringComparison.Ordinal)
                            || className.Contains("overflow", StringComparison.Ordinal)
                            || className.Contains("popup", StringComparison.Ordinal)
                            || className.Contains("shell", StringComparison.Ordinal);
                        bool shellName = name.Contains("notification", StringComparison.Ordinal)
                            || name.Contains("hidden icon", StringComparison.Ordinal)
                            || name.Contains("icône", StringComparison.Ordinal)
                            || name.Contains("icones", StringComparison.Ordinal)
                            || name.Contains("icônes", StringComparison.Ordinal)
                            || name.Contains("masqu", StringComparison.Ordinal);
                        var bounds = current.BoundingRectangle;
                        var screen = Forms.Screen.PrimaryScreen?.Bounds;
                        bool nearTaskbar = screen.HasValue && !bounds.IsEmpty && bounds.Bottom >= screen.Value.Bottom - 260;
                        return shellClass || shellName || nearTaskbar;
                    }
                    catch (Exception) { return false; }
                })
                .ToArray();
        }
        catch (Exception) { return []; }
    }

    private static AutomationElement[] FindExplorerShellElements()
    {
        var elements = new List<AutomationElement>();
        foreach (var root in FindExplorerShellRoots())
        {
            elements.Add(root);
            try
            {
                elements.AddRange(root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>());
            }
            catch (Exception) { }
        }
        return elements.ToArray();
    }

    private static AutomationElement[] FindSystemTrayIcons()
    {
        try
        {
            // Materialize each candidate while its provider snapshot is still
            // valid.  A deferred LINQ sort can re-read an Explorer XAML
            // element after the overflow island refreshes and turn one stale
            // provider into an empty result for the whole query.
            var matches = new List<AutomationElement>();
            foreach (var element in FindExplorerShellElements())
            {
                try
                {
                    var current = element.Current;
                    // Windows 11 exposes overflow notification icons as
                    // Custom/Pane/Image providers, while older shells use
                    // Button/ListItem.  Keep the name anchored to the
                    // product and exclude a top-level window title.
                    if (!current.IsOffscreen
                        && current.ControlType.ProgrammaticName != "ControlType.Window"
                        && current.Name.Contains("Remote Debugger", StringComparison.OrdinalIgnoreCase))
                        matches.Add(element);
                }
                // Explorer's XAML island can invalidate one provider while it
                // is being materialized.  A single stale descendant must not
                // discard the other, already available tray elements.
                catch (Exception) { }
            }
            // Keep the eagerly materialized order.  Re-scoring UIA elements
            // after the XAML island refresh can invalidate a provider between
            // selection and the click even though the candidate was visible.
            return matches.ToArray();
        }
        catch (Exception) { return []; }
    }

    private static AutomationElement? FindSystemTrayIcon() => FindSystemTrayIcons().FirstOrDefault();

    private static AutomationElement[] FindTrayOverflowButtons()
    {
        try
        {
            var matches = new List<(AutomationElement Element, int Priority)>();
            foreach (var element in FindExplorerShellElements())
            {
                try
                {
                    var current = element.Current;
                    string name = current.Name.ToLowerInvariant();
                    string type = current.ControlType.ProgrammaticName;
                    string id = current.AutomationId.ToLowerInvariant();
                    string className = current.ClassName.ToLowerInvariant();
                    bool supportedType = type is "ControlType.Button" or "ControlType.SplitButton";
                    if (current.IsOffscreen || !supportedType) continue;
                    bool hiddenName = name.Contains("afficher les ic", StringComparison.Ordinal)
                        || name.Contains("show hidden", StringComparison.Ordinal)
                        || name.Contains("hidden icon", StringComparison.Ordinal)
                        || name.Contains("icônes cachées", StringComparison.Ordinal)
                        || name.Contains("icones cachees", StringComparison.Ordinal)
                        || name.Contains("masquer les ic", StringComparison.Ordinal);
                    bool chevronName = name.Contains("chevron", StringComparison.Ordinal);
                    bool legacyId = id.Equals("overflownotificationareabutton", StringComparison.Ordinal)
                        || id.Contains("overflownotificationareabutton", StringComparison.Ordinal);
                    bool shellClass = className.Contains("tray", StringComparison.Ordinal)
                        || className.Contains("notify", StringComparison.Ordinal)
                        || className.Contains("overflow", StringComparison.Ordinal);
                    if (!shellClass && !legacyId) continue;
                    if (!hiddenName && !chevronName && !legacyId) continue;
                    // Win11's SystemTrayIcon with the localized hidden-icons
                    // name is the exact overflow control.  Keep legacy IDs and
                    // explicit chevrons as lower-priority compatibility paths.
                    int priority = id.Equals("systemtrayicon", StringComparison.Ordinal)
                        && name.Contains("afficher les ic", StringComparison.Ordinal) ? 100
                        : legacyId ? 90
                        : chevronName ? 80
                        : 70;
                    matches.Add((element, priority));
                }
                catch (Exception) { }
            }
            return matches.OrderByDescending(match => match.Priority).Select(match => match.Element).ToArray();
        }
        catch (Exception) { return []; }
    }

    private static AutomationElement? FindTrayOverflowButton() => FindTrayOverflowButtons().FirstOrDefault();

    private static int TrayElementScore(AutomationElement element)
    {
        try
        {
            var current = element.Current;
            string type = current.ControlType.ProgrammaticName;
            string id = current.AutomationId.ToLowerInvariant();
            string className = current.ClassName.ToLowerInvariant();
            int score = type switch
            {
                "ControlType.Button" => 8,
                "ControlType.ListItem" => 7,
                "ControlType.Image" => 5,
                "ControlType.Custom" => 4,
                "ControlType.Pane" => 3,
                _ => 1
            };
            if (id.Contains("tray", StringComparison.Ordinal) || className.Contains("tray", StringComparison.Ordinal)) score += 4;
            if (className.Contains("notify", StringComparison.Ordinal) || className.Contains("overflow", StringComparison.Ordinal)) score += 3;
            return score;
        }
        catch (Exception) { return 0; }
    }

    private static object DescribeTrayElement(AutomationElement element)
    {
        try
        {
            var current = element.Current;
            var bounds = current.BoundingRectangle;
            return new
            {
                name = current.Name,
                automationId = current.AutomationId,
                className = current.ClassName,
                controlType = current.ControlType.ProgrammaticName,
                processId = current.ProcessId,
                offscreen = current.IsOffscreen,
                bounds = ToTrayBounds(bounds)
            };
        }
        catch (Exception ex) { return new { unavailable = true, error = ex.Message }; }
    }

    private static TrayClickResult RightClickTrayIcon(AutomationElement icon)
    {
        string name = "";
        TrayBounds bounds = new(0, 0, 0, 0);
        string clickablePointError = "";
        try
        {
            var current = icon.Current;
            name = current.Name;
            var rectangle = current.BoundingRectangle;
            bounds = ToTrayBounds(rectangle);
            if (current.IsOffscreen) return new(false, "none", name, "icon_is_offscreen", bounds);

            var point = icon.GetClickablePoint();
            if (double.IsNaN(point.X) || double.IsNaN(point.Y) || double.IsInfinity(point.X) || double.IsInfinity(point.Y))
                throw new InvalidOperationException("UIA returned a non-finite clickable point.");
            EmitRightClick(point.X, point.Y);
            return new(true, "GetClickablePoint", name, "", bounds);
        }
        catch (Exception ex)
        {
            clickablePointError = ex.Message;
        }

        // Windows 11's NotifyItemIcon often exposes a visible bounding box but
        // no UIA clickable point.  Use the freshly read box only as an input
        // fallback, and keep the original UIA error in the evidence.
        try
        {
            var current = icon.Current;
            name = current.Name;
            var rectangle = current.BoundingRectangle;
            bounds = ToTrayBounds(rectangle);
            if (current.IsOffscreen || rectangle.IsEmpty || rectangle.Width <= 0 || rectangle.Height <= 0)
                return new(false, "BoundingRectangleCenter", name, clickablePointError + "; invalid visible bounds", bounds);

            var virtualScreen = Forms.SystemInformation.VirtualScreen;
            if (virtualScreen.Width <= 0 || virtualScreen.Height <= 0)
                return new(false, "BoundingRectangleCenter", name, clickablePointError + "; virtual screen unavailable", bounds);
            double centerX = rectangle.Left + rectangle.Width / 2d;
            double centerY = rectangle.Top + rectangle.Height / 2d;
            int x = (int)Math.Round(Math.Clamp(centerX, virtualScreen.Left, virtualScreen.Right - 1));
            int y = (int)Math.Round(Math.Clamp(centerY, virtualScreen.Top, virtualScreen.Bottom - 1));
            EmitRightClick(x, y);
            return new(true, "BoundingRectangleCenter", name, clickablePointError, bounds);
        }
        catch (Exception ex)
        {
            string error = string.IsNullOrWhiteSpace(clickablePointError) ? ex.Message : clickablePointError + "; " + ex.Message;
            return new(false, "BoundingRectangleCenter", name, error, bounds);
        }
    }

    private static void EmitRightClick(double x, double y)
    {
        Forms.Cursor.Position = new System.Drawing.Point((int)Math.Round(x), (int)Math.Round(y));
        mouse_event(MouseEventRightDown, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MouseEventRightUp, 0, 0, 0, UIntPtr.Zero);
    }

    private void WriteTrayAttemptDiagnostics(string outcome, IReadOnlyCollection<object> attempts)
    {
        try
        {
            File.WriteAllText(Path.Combine(output, "tray-attempts.json"), Json.Text(new
            {
                schemaVersion = 1,
                outcome,
                capturedUtc = DateTimeOffset.UtcNow,
                attempts
            }));
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(output, "capture-warnings.txt"), "tray-attempts: " + ex.Message + Environment.NewLine); } catch (IOException) { }
        }
    }

    private async Task<TrayContext> OpenTrayContextAsync()
    {
        bool overflowOpened = false;
        string failure = "";
        int poll = 0;
        var attempts = new List<object>();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(12))
        {
            poll++;
            AutomationElement[] icons = FindSystemTrayIcons();
            attempts.Add(new
            {
                stage = "icon_search",
                poll,
                elapsedMs = deadline.ElapsedMilliseconds,
                overflowOpened,
                candidateCount = icons.Length,
                candidates = icons.Take(24).Select(DescribeTrayElement).ToArray()
            });
            if (icons.Length == 0 && !overflowOpened)
            {
                AutomationElement[] overflows = FindTrayOverflowButtons();
                attempts.Add(new
                {
                    stage = "overflow_search",
                    poll,
                    candidateCount = overflows.Length,
                    candidates = overflows.Take(24).Select(DescribeTrayElement).ToArray()
                });
                foreach (var overflow in overflows)
                {
                    try
                    {
                        InvokeElement(overflow);
                        overflowOpened = true;
                        attempts.Add(new { stage = "overflow_invoke", poll, success = true, candidate = DescribeTrayElement(overflow) });
                        await Task.Delay(350, stop.Token);
                        break;
                    }
                    catch (Exception ex)
                    {
                        attempts.Add(new { stage = "overflow_invoke", poll, success = false, error = ex.Message, candidate = DescribeTrayElement(overflow) });
                    }
                }
                if (!overflowOpened) failure = "tray_icon_and_overflow_not_found";
            }
            if (icons.Length == 0)
            {
                // Windows 11 creates the overflow XAML island and its
                // NotifyItemIcon asynchronously.  Refresh the Explorer
                // roots on each poll instead of treating one empty query as
                // proof that the tray icon is absent.
                await Task.Delay(250, stop.Token);
                continue;
            }
            foreach (var icon in icons)
            {
                TrayClickResult click = RightClickTrayIcon(icon);
                attempts.Add(new { stage = "tray_right_click", poll, click });
                if (!click.Success)
                {
                    failure = "tray_icon_right_click_failed";
                    continue;
                }
                while (deadline.Elapsed < TimeSpan.FromSeconds(12))
                {
                    var open = FindLoopbackTrayMenuItem("Ouvrir") ?? FindLoopbackTrayMenuItem("Open");
                    attempts.Add(new { stage = "tray_menu_search", poll, iconName = click.Name, menuVisible = open != null });
                    if (open != null)
                    {
                        WriteTrayAttemptDiagnostics("context_menu_found", attempts);
                        return new TrayContext(open, click.Name, overflowOpened);
                    }
                    await Task.Delay(250, stop.Token);
                }
                failure = "tray_context_menu_missing";
            }
            await Task.Delay(250, stop.Token);
        }
        WriteTrayAttemptDiagnostics(failure.Length == 0 ? "tray_lookup_failed" : failure, attempts);
        DumpTrayDiagnostics(failure.Length == 0 ? "tray_lookup_failed" : failure);
        return new TrayContext(null, "", overflowOpened);
    }

    private void DumpTrayDiagnostics(string reason)
    {
        try
        {
            var inventory = FindExplorerShellElements()
                .Select(element =>
                {
                    try
                    {
                        var current = element.Current;
                        string name = current.Name;
                        string id = current.AutomationId;
                        string className = current.ClassName;
                        string type = current.ControlType.ProgrammaticName;
                        string lower = (name + " " + id + " " + className + " " + type).ToLowerInvariant();
                        var bounds = current.BoundingRectangle;
                        bool nearTaskbar = !bounds.IsEmpty && bounds.Bottom >= Forms.Screen.PrimaryScreen!.Bounds.Bottom - 220;
                        bool trayHint = lower.Contains("remote debugger", StringComparison.Ordinal)
                            || lower.Contains("tray", StringComparison.Ordinal)
                            || lower.Contains("notify", StringComparison.Ordinal)
                            || lower.Contains("overflow", StringComparison.Ordinal)
                            || lower.Contains("hidden", StringComparison.Ordinal)
                            || lower.Contains("masqu", StringComparison.Ordinal)
                            || lower.Contains("icône", StringComparison.Ordinal)
                            || lower.Contains("icon", StringComparison.Ordinal)
                            || nearTaskbar;
                        return trayHint ? new { name, id, className, type, processId = current.ProcessId, offscreen = current.IsOffscreen, bounds = new { left = bounds.Left, top = bounds.Top, width = bounds.Width, height = bounds.Height } } : null;
                    }
                    catch (Exception) { return null; }
                })
                .Where(value => value != null)
                .Take(300)
                .ToArray();
            File.WriteAllText(Path.Combine(output, "tray-ui-inventory.json"), Json.Text(new { reason, capturedUtc = DateTimeOffset.UtcNow, elements = inventory }));
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(output, "capture-warnings.txt"), "tray-ui-inventory: " + ex.Message + Environment.NewLine); } catch (IOException) { }
        }

        // The tray icon can remain visible only in the shell overflow window
        // after the controller hides. Retain the desktop image beside the
        // bounded UIA inventory so a selector miss is reviewable.
        CaptureDesktop("loopback-tray-lookup.png");
    }

    private AutomationElement? FindLoopbackTrayMenuItem(string name)
    {
        try
        {
            int productPid = 0;
            try { productPid = product?.Id ?? loopbackController?.Id ?? 0; } catch (InvalidOperationException) { }
            foreach (var window in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>())
            {
                try
                {
                    var current = window.Current;
                    string className = current.ClassName;
                    string type = current.ControlType.ProgrammaticName;
                    bool nativeMenu = string.Equals(className, "#32768", StringComparison.Ordinal)
                        || type == "ControlType.Menu";
                    bool productMenu = productPid > 0
                        && current.ProcessId == productPid
                        && (type == "ControlType.Window" || type == "ControlType.Menu")
                        && (className.Contains("menu", StringComparison.OrdinalIgnoreCase)
                            || className.Contains("dropdown", StringComparison.OrdinalIgnoreCase)
                            || className.Contains("context", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(className, "#32768", StringComparison.Ordinal));
                    if (!nativeMenu && !productMenu) continue;
                    if (!current.IsOffscreen && string.Equals(current.Name, name, StringComparison.OrdinalIgnoreCase)) return window;
                    var item = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));
                    if (item != null && !item.Current.IsOffscreen) return item;
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        return null;
    }

    private bool IsControllerWindowVisible()
    {
        try { return FindVisibleId(ContractId("terminateSession")) != null; }
        catch (Exception) { return false; }
    }

    private Process LaunchLoopbackProduct(bool agent, string dataRoot)
    {
        var psi = new ProcessStartInfo(application)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(application)!,
            CreateNoWindow = false
        };
        if (!agent) psi.ArgumentList.Add("--controller");
        psi.ArgumentList.Add("--loopback-only");
        psi.ArgumentList.Add("--data-root");
        psi.ArgumentList.Add(dataRoot);
        return Process.Start(psi) ?? throw new IOException("Loopback Release process did not start.");
    }

    private async Task ProbeLoopbackTrayAndTerminateAsync()
    {
        if (product == null || loopbackAgent == null) throw new InvalidOperationException("Loopback product processes are missing.");
        Native.FocusWindow(product.Id);
        product.CloseMainWindow();
        await Task.Delay(1400, stop.Token);
        product.Refresh();
        TrayContext tray = await OpenTrayContextAsync();
        bool trayAlive = !product.HasExited && !IsControllerWindowVisible() && tray.OpenItem != null;
        JsonElement? heartbeat = null;
        try { heartbeat = Data(await CallAsync("status")); } catch (Exception) { }
        bool heartbeatAlive = heartbeat.HasValue && heartbeat.Value.ValueKind == JsonValueKind.Object;
        if (trayAlive && heartbeatAlive) Pass("loopback.close_to_tray", "Closing the loopback controller keeps its session alive in the tray", new { pid = product.Id, trayIcon = tray.IconName, overflowOpened = tray.OverflowOpened, heartbeat = heartbeat!.Value });
        else Fail("loopback.close_to_tray", "Closing the loopback controller keeps its session alive in the tray", new { trayAlive, heartbeatAlive, trayIcon = tray.IconName, overflowOpened = tray.OverflowOpened, pid = product.Id, exited = product.HasExited });

        var open = tray.OpenItem;
        if (open != null)
        {
            bool restored = false;
            string restoreError = "";
            try
            {
                InvokeElement(open);
                await WaitForUiAsync(IsControllerWindowVisible, 15);
                restored = true;
            }
            catch (Exception ex)
            {
                restoreError = ex.Message;
            }
            if (restored)
                Pass("loopback.tray_restore", "The loopback controller tray Open action restores its window", new { menuVisible = true, restored, trayIcon = tray.IconName, overflowOpened = tray.OverflowOpened });
            else
                Fail("loopback.tray_restore", "The loopback controller tray Open action restores its window", new { menuVisible = true, restored, trayIcon = tray.IconName, overflowOpened = tray.OverflowOpened, error = restoreError });
        }
        else Fail("loopback.tray_restore", "The loopback controller tray Open action restores its window", new { menu = "Ouvrir missing", trayIcon = tray.IconName, overflowOpened = tray.OverflowOpened });

        AutomationElement? terminate = null;
        try { terminate = FindVisibleId(ContractId("terminateSession")); }
        catch (InvalidOperationException) { }
        catch (ElementNotAvailableException) { }
        if (terminate == null)
        {
            Fail("loopback.terminate", "The controller terminate action ends the loopback session and exits the agent", new { id = ContractId("terminateSession") });
            await AttemptLoopbackSessionEndCleanupAsync("terminate_control_missing");
            return;
        }
        try { InvokeElement(terminate); }
        catch (Exception ex)
        {
            Fail("loopback.terminate", "The controller terminate action ends the loopback session and exits the agent", new { id = ContractId("terminateSession"), invokeError = ex.Message });
            await AttemptLoopbackSessionEndCleanupAsync("terminate_control_invoke_failed");
            return;
        }
        bool disconnected = await WaitForTextAsync("connectionStatus", text => !IsConnected(text), 30);
        bool agentExited = false;
        for (int n = 0; n < 30; n++)
        {
            loopbackAgent.Refresh();
            if (loopbackAgent.HasExited) { agentExited = true; break; }
            await Task.Delay(1000, stop.Token);
        }
        if (disconnected && agentExited)
        {
            sawTermination = true;
            Pass("loopback.terminate", "The controller terminate action ends the loopback session and exits the agent", new { disconnected, agentPid = loopbackAgent.Id, agentExited });
        }
        else Fail("loopback.terminate", "The controller terminate action ends the loopback session and exits the agent", new { disconnected, agentExited, agentPid = loopbackAgent.Id });
        if (!agentExited)
            await AttemptLoopbackSessionEndCleanupAsync("terminate_action_did_not_exit_agent", includeAccessDenied: false);
        JsonElement denied = Json.Element(new { ok = false, error = "transport_after_termination" });
        try { denied = await CallAsync("status", requireSuccess: false, seconds: 15); } catch (Exception ex) { denied = Json.Element(new { ok = false, error = ex.Message }); }
        bool accessClosed = !denied.TryGetProperty("ok", out var accessOk) || accessOk.ValueKind != JsonValueKind.True;
        if (accessClosed) Pass("loopback.terminated_access_denied", "The loopback support token no longer authorizes requests after termination", denied);
        else Fail("loopback.terminated_access_denied", "The loopback support token no longer authorizes requests after termination", denied);
        await ProbeSleepReleasedAsync();
        CaptureDesktop("loopback-controller-terminated.png");
    }

    private async Task AttemptLoopbackSessionEndCleanupAsync(string reason, bool includeAccessDenied = true)
    {
        JsonElement endReply;
        try { endReply = await CallAsync("session.end", requireSuccess: false, seconds: 50); }
        catch (Exception ex) { endReply = Json.Element(new { ok = false, error = ex.Message }); }

        bool endAccepted = endReply.TryGetProperty("ok", out var endOk) && endOk.ValueKind == JsonValueKind.True;
        bool agentExited = false;
        if (loopbackAgent != null)
        {
            for (int n = 0; n < 30; n++)
            {
                try
                {
                    loopbackAgent.Refresh();
                    if (loopbackAgent.HasExited) { agentExited = true; break; }
                }
                catch (InvalidOperationException) { agentExited = true; break; }
                await Task.Delay(1000, stop.Token);
            }
        }

        // This is cleanup evidence only. It never upgrades the GUI terminate
        // check: a missing or unusable terminate control remains a failure.
        if (endAccepted && agentExited)
            Pass("loopback.termination_cleanup", "The saved authenticated session.end operation is available as bounded cleanup and the real agent process exits", new { reason, endAccepted, agentExited }, required: false);
        else
            Fail("loopback.termination_cleanup", "The saved authenticated session.end operation is available as bounded cleanup and the real agent process exits", new { reason, endAccepted, agentExited, endReply }, required: false);

        if (includeAccessDenied)
        {
            JsonElement denied;
            try { denied = await CallAsync("status", requireSuccess: false, seconds: 15); }
            catch (Exception ex) { denied = Json.Element(new { ok = false, error = ex.Message }); }
            bool accessClosed = !denied.TryGetProperty("ok", out var accessOk) || accessOk.ValueKind != JsonValueKind.True;
            if (accessClosed) Pass("loopback.terminated_access_denied", "The loopback support token no longer authorizes requests after cleanup termination", denied, required: false);
            else Fail("loopback.terminated_access_denied", "The loopback support token no longer authorizes requests after cleanup termination", denied, required: false);
        }
        await ProbeSleepReleasedAsync();
    }

    private void ProbeLoopbackRemoteScreenInput()
    {
        AutomationElement? screen = FindVisibleId(ContractId("remoteScreen"));
        if (screen == null)
        {
            Fail("loopback.remote_screen_input", "The live remote screen is a focusable input surface", new { control = ContractId("remoteScreen"), visible = false });
            return;
        }

        try
        {
            bool keyboardFocusable = screen.Current.IsKeyboardFocusable;
            screen.SetFocus();
            bool hasFocus = screen.Current.HasKeyboardFocus;
            if (keyboardFocusable && hasFocus)
                Pass("loopback.remote_screen_input", "The live remote screen accepts focus for the enabled mouse and keyboard forwarding", new { control = ContractId("remoteScreen"), keyboardFocusable, hasFocus });
            else
                Fail("loopback.remote_screen_input", "The live remote screen accepts focus for the enabled mouse and keyboard forwarding", new { control = ContractId("remoteScreen"), keyboardFocusable, hasFocus });
        }
        catch (Exception ex)
        {
            Fail("loopback.remote_screen_input", "The live remote screen accepts focus for the enabled mouse and keyboard forwarding", new { control = ContractId("remoteScreen"), error = ex.Message });
        }
    }
}

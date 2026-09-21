using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using ServiceController = System.ServiceProcess.ServiceController;
using ServiceControllerStatus = System.ServiceProcess.ServiceControllerStatus;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<SupportPlatformAvailability>))]
public enum SupportPlatformAvailability
{
    NotProvisioned,
    Ready,
    ServiceStopped,
    IdentityRejected,
    Incompatible,
    Unavailable
}

public sealed record SupportPlatformStatus(
    SupportPlatformAvailability Availability,
    bool Provisioned,
    bool Available,
    bool IdentityVerified,
    bool FirewallReady,
    bool RequiresAdministratorConsent,
    string Message,
    string? RegisteredApplicationPath,
    string? PublisherThumbprint,
    string? ServiceVersion,
    bool InteractiveInputAvailable = false);

public sealed record AgentSynchronizationResult(
    bool AlreadyMatched,
    bool Updated,
    ExecutableSnapshot Controller,
    ExecutableSnapshot Agent,
    DateTimeOffset? PlannedDisconnectDeadlineUtc,
    string Message);

/// <summary>Immutable progress for the exact-binary agent synchronization.</summary>
public sealed record AgentUpdateProgress(string Stage, long TransferredBytes, long TotalBytes)
{
    public int TransferPercent => TotalBytes <= 0
        ? 0
        : (int)Math.Clamp(TransferredBytes / (double)TotalBytes * 100d, 0d, 100d);
}

public sealed record UpdateExitPlan(string TransactionId, DateTimeOffset DeadlineUtc);

public static class SupportPlatform
{
    private static readonly RpcConnectionPool brokerConnections = new(async ct => await OpenBrokerPipeAsync(ct), capacity: 1);
    public static event Action? ManagedRelaunchRequested;
    // Running agent identity. Candidate and installed bytes are verified again
    // at the privileged staging / installation boundary.
    private static readonly Lazy<Task<ExecutableSnapshot>> versionSnapshot = new(() =>
        Task.Run(() => CaptureCurrentExecutableAsync(CancellationToken.None)));
    internal static Task<ExecutableSnapshot> GetCurrentVersionAsync(CancellationToken ct) => versionSnapshot.Value.WaitAsync(ct);

    internal static bool RequiresAdministratorProvisioning(SupportPlatformStatus status) => !status.Provisioned;

    internal static bool RequiresServiceRefresh(SupportPlatformStatus status, string currentVersion)
    {
        if (!status.Available || !status.InteractiveInputAvailable) return true;
        try { return !UpdatePolicy.ReleaseVersion(status.ServiceVersion).Equals(UpdatePolicy.ReleaseVersion(currentVersion)); }
        catch (InvalidOperationException) { return true; }
    }

    /// <summary>
    /// Redirects a subsequently launched portable agent to the provisioned,
    /// protected Program Files copy. This path never elevates. Controller and
    /// CLI processes deliberately remain on their actual running executable,
    /// whose exact hash is authoritative for synchronization.
    /// </summary>
    public static async Task<bool> TryRelaunchManagedAgentAsync(
        IReadOnlyList<string> launchArguments,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(launchArguments);
        if (launchArguments.Count > 0 && string.Equals(launchArguments[0], "cli", StringComparison.OrdinalIgnoreCase)) return false;
        if (launchArguments.Any(argument => argument.Equals("--controller", StringComparison.OrdinalIgnoreCase) ||
                                            argument.Equals("--security", StringComparison.OrdinalIgnoreCase) ||
                                            argument.Equals("--platform-service", StringComparison.OrdinalIgnoreCase) ||
                                            argument.Equals("--support-provision", StringComparison.OrdinalIgnoreCase) ||
                                            argument.Equals("--elevated-job", StringComparison.OrdinalIgnoreCase) ||
                                            argument.Equals("--ui-job", StringComparison.OrdinalIgnoreCase) ||
                                            argument.Equals("--resume-update", StringComparison.OrdinalIgnoreCase) ||
                                            argument.Equals("--update-transaction", StringComparison.OrdinalIgnoreCase)))
            return false;

        string currentPath = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("Current executable path is unavailable."));
        if (PathsEqual(currentPath, SupportPlatformPaths.ApplicationExecutable)) return false;
        if (!File.Exists(SupportPlatformPaths.ConfigurationPath)) return false;
        if (Native.IsElevated())
            throw new InvalidOperationException("Launch the portable agent normally so the managed application remains unelevated in the interactive user session.");

        SupportConfiguration configuration;
        try
        {
            configuration = JsonSerializer.Deserialize<SupportConfiguration>(
                await File.ReadAllTextAsync(SupportPlatformPaths.ConfigurationPath, ct), Json.Options)
                ?? throw new InvalidDataException("The protected support configuration is empty.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidOperationException("The installed support configuration cannot be verified.", ex);
        }

        if (configuration.ProtocolVersion != SupportPlatformPaths.ProtocolVersion ||
            !PathsEqual(configuration.RegisteredApplicationPath, SupportPlatformPaths.ApplicationExecutable))
            throw new InvalidOperationException("The installed support configuration is incompatible or names an unexpected managed application.");
        string currentSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Current Windows user SID is unavailable.");
        if (!string.Equals(currentSid, configuration.RegisteredUserSid, StringComparison.OrdinalIgnoreCase) ||
            Process.GetCurrentProcess().SessionId <= 0)
            throw new UnauthorizedAccessException("Privileged support is registered to a different interactive Windows user.");
        if (!File.Exists(configuration.RegisteredApplicationPath) || !File.Exists(SupportPlatformPaths.ServiceExecutable))
            throw new FileNotFoundException("The provisioned managed application or its local support service is missing.");

        _ = PrivilegedPathSafety.RequireUnderNonReparseRoot(configuration.RegisteredApplicationPath, SupportPlatformPaths.ProductDirectory);
        _ = PrivilegedPathSafety.RequireUnderNonReparseRoot(SupportPlatformPaths.ServiceExecutable, SupportPlatformPaths.ProductDirectory);
        _ = AuthenticodeVerifier.VerifyPinnedTrusted(currentPath, configuration.PublisherThumbprint);
        ct.ThrowIfCancellationRequested();
        _ = AuthenticodeVerifier.VerifyPinnedTrusted(configuration.RegisteredApplicationPath, configuration.PublisherThumbprint);
        _ = AuthenticodeVerifier.VerifyPinnedTrusted(SupportPlatformPaths.ServiceExecutable, configuration.PublisherThumbprint);
        ct.ThrowIfCancellationRequested();

        var start = CreateManagedStartInfo(launchArguments);
        _ = Process.Start(start) ?? throw new IOException("The protected managed Remote Debugger application did not start.");
        return true;
    }

    public static async Task<SupportPlatformStatus> GetStatusAsync(CancellationToken ct = default)
    {
        bool provisioned = File.Exists(SupportPlatformPaths.ServiceExecutable) && File.Exists(SupportPlatformPaths.ConfigurationPath);
        if (!provisioned)
            return new(SupportPlatformAvailability.NotProvisioned, false, false, false, false, true,
                "Privileged local support is not provisioned. The first setup requires one explicit Windows administrator consent.",
                null, null, null);
        SupportConfiguration? localConfiguration = null;
        try { localConfiguration = JsonSerializer.Deserialize<SupportConfiguration>(await File.ReadAllTextAsync(SupportPlatformPaths.ConfigurationPath, ct), Json.Options); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(SupportPlatformAvailability.Unavailable, true, false, false, false, true,
                "The protected support configuration cannot be read. Reprovision local support. " + ex.Message, null, null, null);
        }
        if (localConfiguration == null ||
            !string.Equals(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(localConfiguration.RegisteredApplicationPath), StringComparison.OrdinalIgnoreCase))
            return new(SupportPlatformAvailability.IdentityRejected, true, false, false, false, false,
                "Privileged support is installed for the managed Remote Debugger application. Relaunch that signed Program Files copy.",
                localConfiguration?.RegisteredApplicationPath, localConfiguration?.PublisherThumbprint, localConfiguration?.ServiceVersion);
        try
        {
            JsonElement data = await BrokerCallAsync("platform.status", new { }, ct, SupportOperationTimeouts.PlatformStatusRoundTripSeconds);
            int protocol = data.Int("protocolVersion");
            if (protocol != SupportPlatformPaths.ProtocolVersion)
                return new(SupportPlatformAvailability.Incompatible, true, false, true, false, true,
                    $"Support service protocol {protocol} is incompatible with this application (expected {SupportPlatformPaths.ProtocolVersion}). Reprovision once as administrator.",
                    data.Str("registeredApplicationPath"), data.Str("publisherThumbprint"), data.Str("serviceVersion"));
            return new(SupportPlatformAvailability.Ready, true, true, true,
                data.TryGetProperty("firewallReady", out var firewall) && firewall.GetBoolean(), false,
                data.Str("message", "Privileged local support is ready."), data.Str("registeredApplicationPath"), data.Str("publisherThumbprint"), data.Str("serviceVersion"),
                data.TryGetProperty("interactiveInput", out var input) && input.ValueKind == JsonValueKind.True);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(SupportPlatformAvailability.IdentityRejected, provisioned, false, false, false, true,
                "The installed broker rejected this executable identity. Reprovision from this signed application. " + ex.Message,
                null, null, null);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or Win32Exception or InvalidOperationException)
        {
            return new(provisioned ? SupportPlatformAvailability.ServiceStopped : SupportPlatformAvailability.NotProvisioned,
                provisioned, false, false, false, true,
                provisioned
                    ? "The privileged local support service is installed but unavailable. Windows service status must be repaired or reprovisioned; no privileged action was performed. " + ex.Message
                    : "Privileged local support is not provisioned. The first setup requires one explicit Windows administrator consent.",
                null, null, null);
        }
    }

    public static async Task<SupportPlatformStatus> PrepareAsync(bool requireFirewall, CancellationToken ct = default)
    {
        var status = await GetStatusAsync(ct);
        if (!status.Available || !requireFirewall || status.FirewallReady) return status;
        _ = await BrokerCallAsync("firewall.ensure", new { }, ct, SupportOperationTimeouts.FirewallEnsureRoundTripSeconds);
        return await GetStatusAsync(ct);
    }

    internal static async Task<SupportPlatformStatus> EnsureCurrentServiceAsync(MaintenanceSession maintenance, CancellationToken ct)
    {
        string currentVersion = typeof(Program).Assembly.GetName().Version?.ToString()
            ?? throw new InvalidOperationException("Current application version is unavailable.");
        var status = await GetStatusAsync(ct);
        if (!status.Provisioned) return status;
        if (!RequiresServiceRefresh(status, currentVersion)) return status;
        if (!status.Available)
            throw new InvalidOperationException("The protected support service cannot be refreshed automatically: " + status.Message);

        string signalPath = await maintenance.QueueSupportRefreshAsync(ct);
        maintenance.End();
        await brokerConnections.ClearAsync();
        await File.WriteAllTextAsync(signalPath, "ready", CancellationToken.None);

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(SupportOperationTimeouts.ServiceRefreshSeconds);
        string resultPath = signalPath + ".result";
        while (!File.Exists(resultPath) && DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(100, ct);
        }
        if (!File.Exists(resultPath)) throw new TimeoutException("The protected support service refresh did not finish before its deadline.");
        string resultText = await File.ReadAllTextAsync(resultPath, ct);
        try { File.Delete(resultPath); } catch (IOException) { }
        var refresh = ParseServiceRefreshResult(resultText);
        if (refresh.ExitCode != 0)
            throw new InvalidOperationException("The protected support service refresh failed" +
                (string.IsNullOrWhiteSpace(refresh.Error) ? "." : ": " + refresh.Error));

        await brokerConnections.ClearAsync();
        status = await GetStatusAsync(ct);
        if (RequiresServiceRefresh(status, currentVersion))
            throw new InvalidOperationException("The protected support service refresh did not enable the current elevated-input broker: " + status.Message);
        await maintenance.StartAsync(ct);
        return status;
    }

    internal static SupportRefreshResult ParseServiceRefreshResult(string value)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<SupportRefreshResult>(value, Json.Options);
            if (parsed != null) return parsed;
        }
        catch (JsonException) { }
        if (int.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int legacyExitCode))
            return new(legacyExitCode, null);
        throw new InvalidDataException("The protected support service returned an invalid refresh result.");
    }

    public static async Task<SupportPlatformStatus> ProvisionAsync(CancellationToken ct = default, bool enableSupport = false)
    {
        // Provisioning is the only UAC boundary. Once the protected service and
        // configuration exist, recover through the broker without launching a
        // second runas process, even if the service is temporarily unavailable.
        var existing = await GetStatusAsync(ct);
        if (!RequiresAdministratorProvisioning(existing)) return existing;
        await SupportInstaller.ProvisionAsync(ct);
        if (!string.Equals(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(SupportPlatformPaths.ApplicationExecutable), StringComparison.OrdinalIgnoreCase))
        {
            var signature = AuthenticodeVerifier.InspectForEnrollment(SupportPlatformPaths.ApplicationExecutable);
            if (Environment.GetCommandLineArgs().Skip(1).FirstOrDefault() == "cli")
                return new(SupportPlatformAvailability.IdentityRejected, true, false, false, false, false,
                    "Privileged support was provisioned. Launch the protected managed application shown in RegisteredApplicationPath.",
                    SupportPlatformPaths.ApplicationExecutable, signature.SignerThumbprint,
                    typeof(Program).Assembly.GetName().Version?.ToString());
            var start = CreateManagedStartInfo(Environment.GetCommandLineArgs().Skip(1).ToArray());
            if (enableSupport && !start.ArgumentList.Contains("--enable-support")) start.ArgumentList.Add("--enable-support");
            _ = Process.Start(start) ?? throw new IOException("The managed Remote Debugger application did not relaunch.");
            ManagedRelaunchRequested?.Invoke();
            return new(SupportPlatformAvailability.Ready, true, true, true, false, false,
                "Privileged support was provisioned and the protected managed application was relaunched.",
                SupportPlatformPaths.ApplicationExecutable, signature.SignerThumbprint,
                typeof(Program).Assembly.GetName().Version?.ToString());
        }
        for (int attempt = 0; attempt < 30; attempt++)
        {
            var status = await GetStatusAsync(ct);
            if (status.Available) return status;
            await Task.Delay(250, ct);
        }
        return await GetStatusAsync(ct);
    }

    private static Task StartServiceAsync(CancellationToken ct) => Task.Run(async () =>
    {
        using var service = new ServiceController(SupportPlatformPaths.ServiceName);
        await EnsureServiceRunningAsync(() => { service.Refresh(); return service.Status; }, () => service.Start(), ct);
    }, ct);

    internal static async Task EnsureServiceRunningAsync(Func<ServiceControllerStatus> getStatus, Action start, CancellationToken ct)
    {
        bool startRequested = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var status = getStatus();
            if (status == ServiceControllerStatus.Running) return;
            if (status == ServiceControllerStatus.Stopped && !startRequested)
            {
                try { start(); }
                catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 1056 }) { }
                startRequested = true;
            }
            else if (status is not (ServiceControllerStatus.StartPending or ServiceControllerStatus.StopPending))
                throw new InvalidOperationException($"The support service entered {status} instead of Running.");
            await Task.Delay(100, ct);
        }
    }

    public static async Task<AgentSynchronizationResult> SynchronizeAgentAsync(
        RemoteClient client,
        CancellationToken ct = default,
        IProgress<AgentUpdateProgress>? progress = null)
    {
        progress?.Report(new("preparing", 0, 0));
        // Keep the verified payload unchanged until transfer completes.
        using var payload = File.Open(Environment.ProcessPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
        var controller = await Task.Run(() => CaptureCurrentExecutableAsync(ct), ct);
        var snapshot = RemoteClient.Require(await client.CallAsync("update.snapshot", ct: ct, seconds: 30));
        return await AgentUpdateClient.SynchronizeAgentAsync(client, controller, snapshot, ct, progress);
    }

    internal static async Task<JsonElement> BrokerCallAsync(string operation, object args, CancellationToken ct, int timeoutSeconds = 30)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            string id = Guid.NewGuid().ToString();
            var reply = await brokerConnections.CallAsync(new Request(id, "", operation, args is JsonElement element ? element : Json.Element(args)), timeout.Token);
            if (!reply.Ok)
            {
                if (reply.Error == "broker_identity_rejected") throw new UnauthorizedAccessException(reply.Message);
                throw new InvalidOperationException($"{reply.Error}: {reply.Message}");
            }
            return reply.Data;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The privileged local support operation '{operation}' did not complete within {timeoutSeconds} seconds.");
        }
    }

    internal static async Task<NamedPipeClientStream> OpenBrokerPipeAsync(CancellationToken ct)
    {
        await StartServiceAsync(ct);
        var pipe = new NamedPipeClientStream(".", SupportPlatformPaths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(ct);
            SupportPipeIdentity.VerifyServer(pipe);
            ct.ThrowIfCancellationRequested();
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }

    internal static async Task ReportStartupHealthyAsync(string transactionId, string ticket, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(transactionId, "N", out _)) return;
        _ = await BrokerCallAsync("update.startupHealthy", new { transactionId, ticket }, ct, 15);
    }

    internal static async Task<ExecutableSnapshot> CaptureCurrentExecutableAsync(CancellationToken ct)
    {
        string path = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("Current executable path is unavailable."));
        var signature = AuthenticodeVerifier.InspectForEnrollment(path);
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new(path, stream.Length, Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)),
            FileVersionInfo.GetVersionInfo(path).FileVersion, signature.SignerThumbprint);
    }

    private static ProcessStartInfo CreateManagedStartInfo(IReadOnlyList<string> launchArguments)
    {
        var start = new ProcessStartInfo(SupportPlatformPaths.ApplicationExecutable)
        {
            UseShellExecute = false,
            WorkingDirectory = SupportPlatformPaths.ProductDirectory
        };
        for (int index = 0; index < launchArguments.Count; index++)
        {
            if (launchArguments[index] is "--resume-update" or "--update-transaction") { index++; continue; }
            if (launchArguments[index] == "--wait-for-process-exit") { index += 2; continue; }
            start.ArgumentList.Add(launchArguments[index]);
        }
        using var current = Process.GetCurrentProcess();
        start.ArgumentList.Add("--wait-for-process-exit");
        start.ArgumentList.Add(current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return start;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}

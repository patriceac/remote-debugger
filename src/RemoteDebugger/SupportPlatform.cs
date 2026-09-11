using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

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
    string? ServiceVersion);

public sealed record AgentSynchronizationResult(
    bool AlreadyMatched,
    bool Updated,
    ExecutableSnapshot Controller,
    ExecutableSnapshot Agent,
    DateTimeOffset? PlannedDisconnectDeadlineUtc,
    string Message);

public sealed record UpdateExitPlan(string TransactionId, DateTimeOffset DeadlineUtc);

public static class SupportPlatform
{
    public static event Action? ManagedRelaunchRequested;

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
        _ = await TryStartServiceAsync(ct);
        try
        {
            JsonElement data = await BrokerCallAsync("platform.status", new { }, ct, 4);
            int protocol = data.Int("protocolVersion");
            if (protocol != SupportPlatformPaths.ProtocolVersion)
                return new(SupportPlatformAvailability.Incompatible, true, true, true, false, true,
                    $"Support service protocol {protocol} is incompatible with this application (expected {SupportPlatformPaths.ProtocolVersion}). Reprovision once as administrator.",
                    data.Str("registeredApplicationPath"), data.Str("publisherThumbprint"), data.Str("serviceVersion"));
            return new(SupportPlatformAvailability.Ready, true, true, true,
                data.TryGetProperty("firewallReady", out var firewall) && firewall.GetBoolean(), false,
                data.Str("message", "Privileged local support is ready."), data.Str("registeredApplicationPath"), data.Str("publisherThumbprint"), data.Str("serviceVersion"));
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
        _ = await BrokerCallAsync("firewall.ensure", new { }, ct);
        return await GetStatusAsync(ct);
    }

    public static async Task<SupportPlatformStatus> ProvisionAsync(CancellationToken ct = default)
    {
        await SupportInstaller.ProvisionAsync(ct);
        if (!string.Equals(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(SupportPlatformPaths.ApplicationExecutable), StringComparison.OrdinalIgnoreCase))
        {
            var signature = AuthenticodeVerifier.InspectForEnrollment(SupportPlatformPaths.ApplicationExecutable);
            if (Environment.GetCommandLineArgs().Skip(1).FirstOrDefault() == "cli")
                return new(SupportPlatformAvailability.IdentityRejected, true, false, false, false, false,
                    "Privileged support was provisioned. Launch the protected managed application shown in RegisteredApplicationPath.",
                    SupportPlatformPaths.ApplicationExecutable, signature.SignerThumbprint,
                    typeof(Program).Assembly.GetName().Version?.ToString());
            var start = new ProcessStartInfo(SupportPlatformPaths.ApplicationExecutable) { UseShellExecute = false, WorkingDirectory = SupportPlatformPaths.ProductDirectory };
            string[] launch = Environment.GetCommandLineArgs().Skip(1).ToArray();
            for (int index = 0; index < launch.Length; index++)
            {
                if (launch[index] is "--resume-update" or "--update-transaction" or "--wait-for-process-exit")
                {
                    index += launch[index] == "--wait-for-process-exit" ? 2 : 1;
                    continue;
                }
                start.ArgumentList.Add(launch[index]);
            }
            using var current = Process.GetCurrentProcess();
            start.ArgumentList.Add("--wait-for-process-exit");
            start.ArgumentList.Add(current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
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

    private static async Task<bool> TryStartServiceAsync(CancellationToken ct)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("start");
        start.ArgumentList.Add(SupportPlatformPaths.ServiceName);
        using var process = Process.Start(start) ?? throw new IOException("Windows Service Control Manager did not start.");
        await process.WaitForExitAsync(ct);
        return process.ExitCode == 0 || (await process.StandardOutput.ReadToEndAsync(ct)).Contains("1056", StringComparison.Ordinal);
    }

    public static async Task<AgentSynchronizationResult> SynchronizeAgentAsync(RemoteClient client, CancellationToken ct = default) =>
        await AgentUpdateClient.SynchronizeAgentAsync(client, ct);

    internal static async Task<JsonElement> BrokerCallAsync(string operation, object args, CancellationToken ct, int timeoutSeconds = 30)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var pipe = new NamedPipeClientStream(".", SupportPlatformPaths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("The privileged local support service did not answer."); }
        SupportPipeIdentity.VerifyServer(pipe);
        string id = Guid.NewGuid().ToString();
        await Wire.WriteAsync(pipe, new Request(id, "", operation, args is JsonElement element ? element : Json.Element(args)), timeout.Token);
        var reply = await Wire.ReadAsync<Reply>(pipe, timeout.Token);
        if (!reply.Ok)
        {
            if (reply.Error == "broker_identity_rejected") throw new UnauthorizedAccessException(reply.Message);
            throw new InvalidOperationException($"{reply.Error}: {reply.Message}");
        }
        if (!string.Equals(reply.Id, id, StringComparison.Ordinal)) throw new InvalidDataException("The privileged broker reply id did not match the request.");
        return reply.Data;
    }

    internal static async Task<NamedPipeClientStream> OpenMaintenancePipeAsync(CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", SupportPlatformPaths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(ct);
            SupportPipeIdentity.VerifyServer(pipe);
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
}

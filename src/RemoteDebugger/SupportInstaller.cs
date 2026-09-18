using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

internal sealed record SupportProvisionRequest(
    string ExpectedExecutableSha256,
    string RegisteredUserSid,
    int RequestingProcessId,
    long RequestingProcessStartTicks);

internal sealed record SupportUpgradeRequest(
    string ExpectedExecutableSha256,
    string RegisteredUserSid,
    int RequestingProcessId,
    long RequestingProcessStartTicks);

internal static class SupportInstaller
{
    public static async Task<int> UpgradeAsync(CancellationToken ct = default)
    {
        string source = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("Current executable path is unavailable."));
        var status = await SupportPlatform.GetStatusAsync(ct);
        if (!status.Provisioned || string.IsNullOrWhiteSpace(status.RegisteredApplicationPath)) return 0;
        if (PathsEqual(source, status.RegisteredApplicationPath)) return 0;
        if (status.PublisherThumbprint is not { Length: > 0 } publisher)
            throw new InvalidOperationException("The protected support publisher identity is unavailable.");
        _ = AuthenticodeVerifier.VerifyPinnedTrusted(source, publisher);
        string hash;
        await using (var stream = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        if (File.Exists(status.RegisteredApplicationPath))
        {
            await using var current = File.Open(status.RegisteredApplicationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            string currentHash = Convert.ToHexString(await SHA256.HashDataAsync(current, ct));
            if (UpdatePolicy.FixedHexEquals(hash, currentHash)) return 0;
        }
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Current Windows user SID is unavailable.");
        using var requester = Process.GetCurrentProcess();
        string encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new SupportUpgradeRequest(hash, sid, requester.Id, requester.StartTime.ToUniversalTime().Ticks), Json.Options));
        var start = new ProcessStartInfo(source)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add("--support-upgrade");
        start.ArgumentList.Add(encoded);
        using var process = Process.Start(start) ?? throw new IOException("Windows did not start the protected application upgrader.");
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Protected application upgrade failed (exit code {process.ExitCode}).");
        return 0;
    }

    public static async Task ProvisionAsync(CancellationToken ct)
    {
        string source = Environment.ProcessPath ?? throw new InvalidOperationException("Current executable path is unavailable.");
        var signature = AuthenticodeVerifier.InspectForEnrollment(source);
        if (!signature.CryptographicallyValid) throw new InvalidDataException("The current executable does not have a valid Authenticode signature.");
        string hash;
        await using (var stream = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Current Windows user SID is unavailable.");
        using var current = Process.GetCurrentProcess();
        string encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new SupportProvisionRequest(hash, sid, current.Id, current.StartTime.ToUniversalTime().Ticks), Json.Options));
        var start = new ProcessStartInfo(source)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add("--support-provision");
        start.ArgumentList.Add(encoded);
        using var process = Process.Start(start) ?? throw new IOException("Windows did not start the support provisioner.");
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Support service provisioning failed (exit code {process.ExitCode}). No privileged capability was reported as ready.");
    }

    public static int ExecuteElevated(string encodedRequest)
    {
        try
        {
            if (!Native.IsElevated()) return 3;
            var request = JsonSerializer.Deserialize<SupportProvisionRequest>(Convert.FromBase64String(encodedRequest), Json.Options)
                ?? throw new InvalidDataException("Missing provisioning request.");
            UpdatePolicy.ValidateSha256(request.ExpectedExecutableSha256, "provisioning executable SHA-256");
            _ = new SecurityIdentifier(request.RegisteredUserSid);

            string source = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("Provisioner executable path is unavailable."));
            var requester = ProcessIdentity.Capture(request.RequestingProcessId);
            if (requester.StartTicks != request.RequestingProcessStartTicks ||
                !string.Equals(requester.UserSid, request.RegisteredUserSid, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(requester.ExecutablePath), source, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Provisioning request does not match the signed interactive process that requested administrator consent.");
            string actualHash;
            using (var sourceStream = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                actualHash = Convert.ToHexString(SHA256.HashData(sourceStream));
            if (!UpdatePolicy.FixedHexEquals(actualHash, request.ExpectedExecutableSha256))
                throw new UnauthorizedAccessException("Provisioner bytes changed after administrator consent was requested.");

            var enrolled = AuthenticodeVerifier.InspectForEnrollment(source);
            _ = AuthenticodeVerifier.VerifyPinnedTrusted(source, enrolled.SignerThumbprint);

            StopExistingService();
            SecureDirectory(SupportPlatformPaths.StateDirectory, request.RegisteredUserSid, allowUserRead: true);
            SecureDirectory(SupportPlatformPaths.TransactionsDirectory, request.RegisteredUserSid, allowUserRead: false);
            PrivilegedPathSafety.RequireUnderNonReparseRoot(SupportPlatformPaths.ProductDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), includeLeaf: false);
            SecureDirectory(SupportPlatformPaths.ProductDirectory, request.RegisteredUserSid, allowUserRead: true);
            SecureDirectory(SupportPlatformPaths.InstallDirectory, request.RegisteredUserSid, allowUserRead: true);

            if (!string.Equals(source, SupportPlatformPaths.ApplicationExecutable, StringComparison.OrdinalIgnoreCase))
            {
                string applicationTemp = SupportPlatformPaths.ApplicationExecutable + ".new";
                File.Copy(source, applicationTemp, true);
                _ = AuthenticodeVerifier.VerifyPinnedTrusted(applicationTemp, enrolled.SignerThumbprint);
                File.Move(applicationTemp, SupportPlatformPaths.ApplicationExecutable, true);
            }

            string serviceTemp = SupportPlatformPaths.ServiceExecutable + ".new";
            File.Copy(source, serviceTemp, true);
            _ = AuthenticodeVerifier.VerifyPinnedTrusted(serviceTemp, enrolled.SignerThumbprint);
            File.Move(serviceTemp, SupportPlatformPaths.ServiceExecutable, true);

            var configuration = new SupportConfiguration(
                SupportPlatformPaths.ProtocolVersion,
                SupportPlatformPaths.ApplicationExecutable,
                request.RegisteredUserSid,
                enrolled.SignerThumbprint,
                DateTimeOffset.UtcNow,
                typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown");
            WriteConfiguration(configuration);
            ConfigureService();
            RunSc(true, "start", SupportPlatformPaths.ServiceName);
            WaitForServiceReady();
            WriteReceipt(new(true, SupportPlatformPaths.ApplicationExecutable, SupportPlatformPaths.ServiceExecutable,
                enrolled.SignerThumbprint, request.RegisteredUserSid, "demand", DateTimeOffset.UtcNow));
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(SupportPlatformPaths.StateDirectory);
                File.WriteAllText(Path.Combine(SupportPlatformPaths.StateDirectory, "provisioning-error.txt"), ex.ToString());
            }
            catch { }
            return 2;
        }
    }

    public static int ExecuteUpgradeElevated(string encodedRequest)
    {
        try
        {
            if (!Native.IsElevated()) return 3;
            var request = JsonSerializer.Deserialize<SupportUpgradeRequest>(Convert.FromBase64String(encodedRequest), Json.Options)
                ?? throw new InvalidDataException("Missing protected application upgrade request.");
            UpdatePolicy.ValidateSha256(request.ExpectedExecutableSha256, "upgrade executable SHA-256");
            _ = new SecurityIdentifier(request.RegisteredUserSid);

            string source = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("Upgrader executable path is unavailable."));
            if (!PathsEqual(source, SupportPlatformPaths.UserApplicationExecutable))
                throw new UnauthorizedAccessException("Protected application upgrades must originate from the per-user installation.");
            var configuration = JsonSerializer.Deserialize<SupportConfiguration>(
                File.ReadAllText(SupportPlatformPaths.ConfigurationPath), Json.Options)
                ?? throw new InvalidDataException("Support service configuration is empty.");
            if (configuration.ProtocolVersion != SupportPlatformPaths.ProtocolVersion ||
                !PathsEqual(configuration.RegisteredApplicationPath, SupportPlatformPaths.ApplicationExecutable) ||
                !string.Equals(request.RegisteredUserSid, configuration.RegisteredUserSid, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Protected application upgrade does not match the provisioned support identity.");
            var requester = ProcessIdentity.Capture(request.RequestingProcessId);
            if (requester.StartTicks != request.RequestingProcessStartTicks ||
                requester.SessionId <= 0 ||
                !string.Equals(requester.UserSid, request.RegisteredUserSid, StringComparison.OrdinalIgnoreCase) ||
                !PathsEqual(requester.ExecutablePath, source))
                throw new UnauthorizedAccessException("Protected application upgrade request does not match the signed interactive process.");
            _ = AuthenticodeVerifier.VerifyPinnedTrusted(source, configuration.PublisherThumbprint);
            string actualHash;
            using (var sourceStream = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                actualHash = Convert.ToHexString(SHA256.HashData(sourceStream));
            if (!UpdatePolicy.FixedHexEquals(actualHash, request.ExpectedExecutableSha256))
                throw new UnauthorizedAccessException("Protected application upgrade bytes changed after administrator consent was requested.");

            StopManagedApplication(configuration.RegisteredApplicationPath, requester.SessionId, requester.UserSid);
            StopExistingService();
            ReplaceProtectedPayload(source, configuration);
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(SupportPlatformPaths.StateDirectory);
                File.WriteAllText(Path.Combine(SupportPlatformPaths.StateDirectory, "upgrade-error.txt"), ex.ToString());
            }
            catch { }
            return 2;
        }
    }

    private static void StopManagedApplication(string path, int sessionId, string userSid)
    {
        foreach (var process in Process.GetProcessesByName("RemoteDebugger"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                string executablePath;
                try { executablePath = Path.GetFullPath(process.MainModule?.FileName ?? ""); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { continue; }
                if (!PathsEqual(executablePath, path)) continue;
                var identity = ProcessIdentity.Capture(process.Id);
                if (identity.SessionId != sessionId || !string.Equals(identity.UserSid, userSid, StringComparison.OrdinalIgnoreCase)) continue;
                if (process.HasExited) continue;
                _ = process.CloseMainWindow();
                if (!process.WaitForExit(5000) && !process.HasExited)
                {
                    process.Kill(true);
                    process.WaitForExit(10000);
                }
                if (!process.HasExited) throw new IOException("The protected Remote Debugger process did not exit before replacement.");
            }
        }
    }

    private static void ReplaceProtectedPayload(string source, SupportConfiguration configuration)
    {
        string applicationTemp = SupportPlatformPaths.ApplicationExecutable + ".upgrade.new";
        string serviceTemp = SupportPlatformPaths.ServiceExecutable + ".upgrade.new";
        try
        {
            Directory.CreateDirectory(SupportPlatformPaths.ProductDirectory);
            Directory.CreateDirectory(SupportPlatformPaths.InstallDirectory);
            File.Copy(source, applicationTemp, true);
            _ = AuthenticodeVerifier.VerifyPinnedTrusted(applicationTemp, configuration.PublisherThumbprint);
            File.Copy(source, serviceTemp, true);
            _ = AuthenticodeVerifier.VerifyPinnedTrusted(serviceTemp, configuration.PublisherThumbprint);
            File.Move(applicationTemp, SupportPlatformPaths.ApplicationExecutable, true);
            File.Move(serviceTemp, SupportPlatformPaths.ServiceExecutable, true);
            var updated = configuration with { ServiceVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? configuration.ServiceVersion };
            WriteConfiguration(updated);
            ConfigureService();
            RunSc(true, "start", SupportPlatformPaths.ServiceName);
            WaitForServiceReady();
            WriteReceipt(new(true, SupportPlatformPaths.ApplicationExecutable, SupportPlatformPaths.ServiceExecutable,
                updated.PublisherThumbprint, updated.RegisteredUserSid, "demand", updated.ProvisionedUtc));
        }
        finally
        {
            DeleteIfExists(applicationTemp);
            DeleteIfExists(serviceTemp);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void StopExistingService()
    {
        RunSc(false, "stop", SupportPlatformPaths.ServiceName);
        for (int attempt = 0; attempt < 40; attempt++)
        {
            var query = RunSc(false, "query", SupportPlatformPaths.ServiceName);
            if (query.ExitCode != 0 || query.Stdout.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return;
            Thread.Sleep(250);
        }
        throw new InvalidOperationException("The previous support service did not stop in time.");
    }

    private static void ConfigureService()
    {
        string binaryCommand = $"\"{SupportPlatformPaths.ServiceExecutable}\" --platform-service";
        var create = RunSc(false, "create", SupportPlatformPaths.ServiceName, "binPath=", binaryCommand, "start=", "demand", "obj=", "LocalSystem", "DisplayName=", "Remote Debugger privileged local support");
        if (create.ExitCode != 0 && !create.Stdout.Contains("1073", StringComparison.Ordinal) && !create.Stderr.Contains("1073", StringComparison.Ordinal))
            throw new InvalidOperationException("Windows could not create the support service: " + create.Stderr + create.Stdout);
        RunSc(true, "config", SupportPlatformPaths.ServiceName, "binPath=", binaryCommand, "start=", "demand", "obj=", "LocalSystem", "DisplayName=", "Remote Debugger privileged local support");
        RunSc(true, "description", SupportPlatformPaths.ServiceName, "Local-only signed broker for Remote Debugger firewall, maintenance, and transactional updates.");
        RunSc(true, "sidtype", SupportPlatformPaths.ServiceName, "unrestricted");
        RunSc(true, "failure", SupportPlatformPaths.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/15000//");
        // Registered desktop user may query and demand-start the broker, but may
        // not stop, reconfigure, delete, or change its security descriptor.
        var configuration = JsonSerializer.Deserialize<SupportConfiguration>(File.ReadAllText(SupportPlatformPaths.ConfigurationPath), Json.Options)!;
        string fullControl = "CCDCLCSWRPWPDTLOCRSDRCWDWO";
        string sddl = $"D:(A;;{fullControl};;;SY)(A;;{fullControl};;;BA)(A;;CCLCRPRC;;;{configuration.RegisteredUserSid})";
        RunSc(true, "sdset", SupportPlatformPaths.ServiceName, sddl);
    }

    private static void WaitForServiceReady()
    {
        using var service = new ServiceController(SupportPlatformPaths.ServiceName);
        var deadline = Stopwatch.StartNew();
        ServiceControllerStatus? last = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            service.Refresh();
            last = service.Status;
            if (last == ServiceControllerStatus.Running) return;
            if (last != ServiceControllerStatus.StartPending)
                throw new InvalidOperationException($"Support service entered {last} instead of RUNNING during provisioning.");
            Thread.Sleep(250);
        }
        throw new System.TimeoutException($"Support service remained {last?.ToString() ?? "unavailable"} and did not reach RUNNING within 30 seconds.");
    }

    private static (int ExitCode, string Stdout, string Stderr) RunSc(bool required, params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Service Control Manager command did not start.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (required && process.ExitCode != 0)
            throw new InvalidOperationException($"Service Control Manager rejected '{arguments[0]}': {stderr}{stdout}");
        return (process.ExitCode, stdout, stderr);
    }

    private static void WriteConfiguration(SupportConfiguration configuration)
    {
        string temp = SupportPlatformPaths.ConfigurationPath + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(configuration, Json.Options));
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(configuration.RegisteredUserSid), FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(temp).SetAccessControl(security);
        File.Move(temp, SupportPlatformPaths.ConfigurationPath, true);
    }

    private static void WriteReceipt(SupportProvisioningReceipt receipt)
    {
        string temp = SupportPlatformPaths.ProvisioningReceiptPath + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(receipt, Json.Options));
        File.Move(temp, SupportPlatformPaths.ProvisioningReceiptPath, true);
    }

    private static void SecureDirectory(string path, string userSid, bool allowUserRead)
    {
        Directory.CreateDirectory(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        if (allowUserRead)
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(userSid), FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
}

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
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

internal sealed record SupportRefreshResult(int ExitCode, string? Error);

internal static partial class SupportInstaller
{
    private static string RequireInstalledRefreshHost()
    {
        if (!Native.IsElevated()) throw new UnauthorizedAccessException("Support refresh requires the protected service identity.");
        string source = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("Current executable path is unavailable."));
        if (!PathsEqual(source, SupportPlatformPaths.ApplicationExecutable))
            throw new UnauthorizedAccessException("Support refresh must run from the Program Files installation.");
        return source;
    }

    private static string ValidateRefreshSignalPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || value.Length > 1024)
            throw new ArgumentException("Support refresh signal path is invalid.");
        return Path.GetFullPath(value);
    }

    public static int LaunchServiceRefresh(string signalPath)
    {
        if (!Native.IsElevated()) return 3;
        try
        {
            string source = RequireInstalledRefreshHost();
            signalPath = ValidateRefreshSignalPath(signalPath);
            if (File.Exists(signalPath)) throw new InvalidOperationException("Support refresh signal already exists.");
            // Redirecting the worker's streams does not stop .NET 8 from also
            // inheriting these pipes, which would keep the maintenance reply open.
            foreach (int standardHandle in new[] { -10, -11, -12 })
            {
                nint handle = GetStdHandle(standardHandle);
                if (handle != 0 && handle != -1 && !SetHandleInformation(handle, 1, 0))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            var start = new ProcessStartInfo(source)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            start.ArgumentList.Add("--support-refresh-worker");
            start.ArgumentList.Add(signalPath);
            using var worker = Process.Start(start) ?? throw new IOException("Windows did not start the support refresh worker.");
            return 0;
        }
        catch (Exception ex)
        {
            TryWriteMaintenanceError("support-refresh-launch-error.txt", ex);
            return 2;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);

    public static int RefreshServiceAfterSignal(string signalPath)
    {
        if (!Native.IsElevated()) return 3;
        string? resultPath = null;
        string? error = null;
        int result;
        try
        {
            _ = RequireInstalledRefreshHost();
            signalPath = ValidateRefreshSignalPath(signalPath);
            resultPath = signalPath + ".result";
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(signalPath))
            {
                if (deadline.Elapsed >= TimeSpan.FromMinutes(2))
                    throw new System.TimeoutException("The agent did not release the existing support service for refresh.");
                Thread.Sleep(100);
            }
            try { File.Delete(signalPath); } catch (IOException) { }
            result = RefreshService(out error);
        }
        catch (Exception ex)
        {
            TryWriteMaintenanceError("support-refresh-error.txt", ex);
            error = ex.GetBaseException().Message;
            result = 2;
        }
        if (resultPath != null) WriteRefreshResult(resultPath, new(result, error));
        return result;
    }

    private static void WriteRefreshResult(string path, SupportRefreshResult result)
    {
        try
        {
            string temp = path + ".new";
            File.WriteAllText(temp, JsonSerializer.Serialize(result, Json.Options));
            File.Move(temp, path, true);
        }
        catch (Exception ex) { TryWriteMaintenanceError("support-refresh-result-error.txt", ex); }
    }

    public static int StopForInstaller()
    {
        try
        {
            if (Native.IsElevated() && File.Exists(SupportPlatformPaths.ConfigurationPath))
            {
                using var installationLock = SupportPlatformPaths.AcquireUpdateLock();
                RequireNoActiveUpdate();
                StopKnownApplicationProcesses();
                StopExistingService();
            }
            else StopKnownApplicationProcesses();
            return 0;
        }
        catch (Exception ex)
        {
            TryWriteMaintenanceError("installer-shutdown-error.txt", ex);
            return 2;
        }
    }

    public static int RefreshService() => RefreshService(out _);

    private static int RefreshService(out string? error)
    {
        error = null;
        if (!Native.IsElevated()) return 3;
        try
        {
            string source = RequireInstalledRefreshHost();
            if (!File.Exists(SupportPlatformPaths.ConfigurationPath)) return 0;

            var configuration = JsonSerializer.Deserialize<SupportConfiguration>(
                File.ReadAllText(SupportPlatformPaths.ConfigurationPath), Json.Options)
                ?? throw new InvalidDataException("Support service configuration is empty.");
            if (configuration.ProtocolVersion != SupportPlatformPaths.ProtocolVersion ||
                !PathsEqual(configuration.RegisteredApplicationPath, SupportPlatformPaths.ApplicationExecutable))
                throw new UnauthorizedAccessException("Support service configuration names an unexpected application.");
            _ = new SecurityIdentifier(configuration.RegisteredUserSid);
            _ = AuthenticodeVerifier.VerifyPinnedTrusted(source, configuration.PublisherThumbprint);

            using var installationLock = AcquireUpdateLock(TimeSpan.FromSeconds(30));
            RequireNoActiveUpdate();
            Directory.CreateDirectory(SupportPlatformPaths.InstallDirectory);
            string serviceTemp = SupportPlatformPaths.ServiceExecutable + ".installer.new";
            string serviceBackup = SupportPlatformPaths.ServiceExecutable + "." + Guid.NewGuid().ToString("N") + ".backup";
            string configurationBackup = serviceBackup + ".json";
            _ = PrivilegedPathSafety.RequireUnderNonReparseRoot(serviceTemp, SupportPlatformPaths.ProductDirectory, includeLeaf: File.Exists(serviceTemp));
            _ = AuthenticodeVerifier.VerifyPinnedTrusted(SupportPlatformPaths.ServiceExecutable, configuration.PublisherThumbprint);
            File.Copy(SupportPlatformPaths.ServiceExecutable, serviceBackup);
            File.Copy(SupportPlatformPaths.ConfigurationPath, configurationBackup);
            bool restoredOrUpdated = false;
            try
            {
                File.Copy(source, serviceTemp, true);
                _ = AuthenticodeVerifier.VerifyPinnedTrusted(serviceTemp, configuration.PublisherThumbprint);
                StopExistingService();
                MoveServiceBinary(serviceTemp);
                var updated = configuration with
                {
                    ServiceVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? configuration.ServiceVersion
                };
                WriteConfiguration(updated);
                ConfigureService();
                RunSc(true, "start", SupportPlatformPaths.ServiceName);
                WaitForServiceReady();
                VerifyServicePipe();
                WriteReceipt(new(true, SupportPlatformPaths.ApplicationExecutable, SupportPlatformPaths.ServiceExecutable,
                    updated.PublisherThumbprint, updated.RegisteredUserSid, "demand", updated.ProvisionedUtc));
                restoredOrUpdated = true;
                return 0;
            }
            catch (Exception updateError)
            {
                try
                {
                    StopExistingService();
                    File.Copy(serviceBackup, serviceTemp, true);
                    MoveServiceBinary(serviceTemp);
                    WriteConfiguration(configuration);
                    ConfigureService();
                    RunSc(true, "start", SupportPlatformPaths.ServiceName);
                    WaitForServiceReady();
                    VerifyServicePipe();
                    restoredOrUpdated = true;
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException($"Support refresh and restoration failed. Recovery files: {serviceBackup}, {configurationBackup}.", updateError, rollbackError);
                }
                throw;
            }
            finally
            {
                DeleteIfExists(serviceTemp);
                if (restoredOrUpdated) { DeleteIfExists(serviceBackup); DeleteIfExists(configurationBackup); }
            }
        }
        catch (Exception ex)
        {
            TryWriteMaintenanceError("support-refresh-error.txt", ex);
            error = ex.GetBaseException().Message;
            return 2;
        }
    }

    private static FileStream AcquireUpdateLock(TimeSpan timeout)
    {
        var waiting = Stopwatch.StartNew();
        while (true)
        {
            try { return SupportPlatformPaths.AcquireUpdateLock(); }
            catch (IOException) when (waiting.Elapsed < timeout) { Thread.Sleep(100); }
        }
    }

    private static void MoveServiceBinary(string source)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try { File.Move(source, SupportPlatformPaths.ServiceExecutable, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException &&
                deadline.Elapsed < TimeSpan.FromSeconds(30)) { Thread.Sleep(100); }
        }
    }

    private static void VerifyServicePipe()
    {
        using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", SupportPlatformPaths.PipeName,
            System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        pipe.Connect(30000);
        SupportPipeIdentity.VerifyServer(pipe);
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
        {
            string? detail = null;
            string errorPath = Path.Combine(SupportPlatformPaths.StateDirectory, "provisioning-error.txt");
            try { if (File.GetLastWriteTimeUtc(errorPath) >= process.StartTime.ToUniversalTime()) detail = File.ReadAllText(errorPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw new InvalidOperationException($"Support service provisioning failed (exit code {process.ExitCode}). No privileged capability was reported as ready." +
                (detail == null ? "" : "\n" + detail));
        }
    }

    private static void ValidateInstallerPipe(string name)
    {
        if (!name.StartsWith("RemoteDebugger-Setup-", StringComparison.Ordinal) || name.Length > 128 ||
            name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-')))
            throw new ArgumentException("Invalid installer provisioning endpoint.");
    }

    public static int WriteInstallerProvisionRequest(string pipeName)
    {
        try
        {
            ValidateInstallerPipe(pipeName);
            string source = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("Current executable path is unavailable."));
            if (!PathsEqual(source, SupportPlatformPaths.ApplicationExecutable))
                throw new UnauthorizedAccessException("Installer provisioning request must run from the Program Files installation.");
            string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Current Windows user SID is unavailable.");
            using var current = Process.GetCurrentProcess();
            using var sourceStream = File.OpenRead(source);
            string hash = Convert.ToHexString(SHA256.HashData(sourceStream));
            var request = new SupportProvisionRequest(hash, sid, current.Id, current.StartTime.ToUniversalTime().Ticks);
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            foreach (var identity in new[] { new SecurityIdentifier(sid), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                security.AddAccessRule(new(identity, PipeAccessRights.FullControl, AccessControlType.Allow));
            using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, security);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            pipe.WaitForConnectionAsync(deadline.Token).GetAwaiter().GetResult();
            // The DACL admits the elevated administrator across account boundaries.
            // The elevated consumer verifies this pipe's server PID and signed image;
            // a standard user cannot inspect the separate administrator's process token.
            Wire.WriteAsync(pipe, request, deadline.Token).GetAwaiter().GetResult();
            byte[] result = new byte[1];
            pipe.ReadExactlyAsync(result, deadline.Token).AsTask().GetAwaiter().GetResult();
            return result[0];
        }
        catch (Exception ex) { Trace.WriteLine(ex); return 2; }
    }

    public static int EnsureSupportFromInstaller(string pipeName)
    {
        try
        {
            _ = RequireInstalledRefreshHost();
            ValidateInstallerPipe(pipeName);
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            pipe.ConnectAsync(60000, deadline.Token).GetAwaiter().GetResult();
            if (!SupportPipeIdentity.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var request = Wire.ReadAsync<SupportProvisionRequest>(pipe, deadline.Token).GetAwaiter().GetResult();
            if (request.RequestingProcessId != checked((int)serverPid))
                throw new UnauthorizedAccessException("Provisioning request does not match its pipe owner.");
            int result = File.Exists(SupportPlatformPaths.ConfigurationPath) ? RefreshService() :
                ExecuteElevated(Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request, Json.Options)));
            pipe.WriteByte(checked((byte)result));
            return result;
        }
        catch (Exception ex) { TryWriteMaintenanceError("installer-support-error.txt", ex); return 2; }
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

    private static void StopKnownApplicationProcesses()
    {
        string userSid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Current Windows user SID is unavailable.");
        int sessionId = Process.GetCurrentProcess().SessionId;
        foreach (var process in Process.GetProcessesByName("RemoteDebugger"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                string executablePath;
                try { executablePath = Path.GetFullPath(process.MainModule?.FileName ?? ""); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { continue; }
                if (!PathsEqual(executablePath, SupportPlatformPaths.ApplicationExecutable) &&
                    !PathsEqual(executablePath, SupportPlatformPaths.LegacyUserApplicationExecutable)) continue;
                VerifiedProcessIdentity identity;
                try { identity = ProcessIdentity.Capture(process.Id); }
                catch (ArgumentException) { continue; }
                if (!(Native.IsElevated() && PathsEqual(executablePath, SupportPlatformPaths.ApplicationExecutable)) &&
                    (identity.SessionId != sessionId || !string.Equals(identity.UserSid, userSid, StringComparison.OrdinalIgnoreCase))) continue;
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

    private static void TryWriteMaintenanceError(string fileName, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(SupportPlatformPaths.StateDirectory);
            File.WriteAllText(Path.Combine(SupportPlatformPaths.StateDirectory, fileName), ex.ToString());
        }
        catch { }
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
        for (int attempt = 0; attempt < 120; attempt++)
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
        while (deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            service.Refresh();
            last = service.Status;
            if (last == ServiceControllerStatus.Running) return;
            if (last != ServiceControllerStatus.StartPending)
                throw new InvalidOperationException($"Support service entered {last} instead of RUNNING during provisioning.");
            Thread.Sleep(250);
        }
        throw new System.TimeoutException($"Support service remained {last?.ToString() ?? "unavailable"} and did not reach RUNNING within 60 seconds.");
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

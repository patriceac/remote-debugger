using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

internal sealed record SupportProvisionRequest(
    string ExpectedExecutableSha256,
    string RegisteredUserSid,
    int RequestingProcessId,
    long RequestingProcessStartTicks);

internal static class SupportInstaller
{
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
            bool running = false;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                Thread.Sleep(100);
                var query = RunSc(false, "query", SupportPlatformPaths.ServiceName);
                if (query.ExitCode == 0 && query.Stdout.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)) { running = true; break; }
            }
            if (!running) throw new InvalidOperationException("Support service did not reach the RUNNING state after provisioning.");
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

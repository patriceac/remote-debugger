using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using RemoteDebugger.Core;

namespace RemoteDebugger;

internal static class SupportPlatformPaths
{
    public const string ServiceName = "RemoteDebuggerSupport";
    public const string PipeName = "RemoteDebugger.Support.v1";
    public const int ProtocolVersion = 1;
    public static string ProductDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RemoteDebugger");
    public static string ApplicationExecutable => Path.Combine(ProductDirectory, "RemoteDebugger.exe");
    public static string LegacyUserApplicationExecutable => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Remote Debugger", "RemoteDebugger.exe");
    public static string InstallDirectory => Path.Combine(ProductDirectory, "Support");
    public static string ServiceExecutable => Path.Combine(InstallDirectory, "RemoteDebugger.Support.exe");
    public static string StateDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteDebugger", "Support");
    public static string ConfigurationPath => Path.Combine(StateDirectory, "platform.json");
    public static string ProvisioningReceiptPath => Path.Combine(StateDirectory, "provisioning-receipt.json");
    public static string TransactionsDirectory => Path.Combine(StateDirectory, "transactions");
    public static string UpdateLockPath => Path.Combine(ProductDirectory, "support-update.lock");
    internal static FileStream AcquireUpdateLock()
    {
        _ = PrivilegedPathSafety.RequireUnderNonReparseRoot(UpdateLockPath, ProductDirectory, includeLeaf: File.Exists(UpdateLockPath));
        return new FileStream(UpdateLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
}

internal static class SupportOperationTimeouts
{
    // Cold signature validation and NetSecurity/CIM startup can take materially
    // longer than an ordinary pipe request. Keep each client alive beyond its
    // broker deadline so the broker returns a verified result or truthful error.
    public const int PlatformStatusExecutionSeconds = 60;
    public const int PlatformStatusRoundTripSeconds = 75;
    public const int FirewallEnsureExecutionSeconds = 120;
    public const int FirewallEnsureRoundTripSeconds = 135;
    public const int PairingHandshakeSeconds = 120;
    public const int ControllerSynchronizationSeconds = 1200;
    public const int UpdateStageSeconds = 300;
    public const int UpdateStartupHealthReportSeconds = 300;
    public const int UpdateStartupHealthRollbackSeconds = 360;
}

internal sealed record SupportConfiguration(
    int ProtocolVersion,
    string RegisteredApplicationPath,
    string RegisteredUserSid,
    string PublisherThumbprint,
    DateTimeOffset ProvisionedUtc,
    string ServiceVersion);

internal sealed record SupportProvisioningReceipt(
    bool Provisioned,
    string ManagedApplicationPath,
    string ServiceExecutablePath,
    string PublisherThumbprint,
    string RegisteredUserSid,
    string ServiceStartMode,
    DateTimeOffset ProvisionedUtc);

internal sealed record VerifiedProcessIdentity(
    int ProcessId,
    long StartTicks,
    int SessionId,
    string UserSid,
    string ExecutablePath)
{
    public MaintenanceLease CreateLease() => new(ProcessId, StartTicks, SessionId, UserSid, ExecutablePath, Guid.NewGuid().ToString("N"));
}

internal static class ProcessIdentity
{
    private const uint TokenQuery = 0x0008;
    private static readonly string LocalSystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;

    public static VerifiedProcessIdentity Capture(int processId)
    {
        using var process = Process.GetProcessById(processId);
        string path = Path.GetFullPath(process.MainModule?.FileName ?? throw new UnauthorizedAccessException("Process executable path is unavailable."));
        if (!OpenProcessToken(process.Handle, TokenQuery, out var token)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            string sid = identity.User?.Value ?? throw new UnauthorizedAccessException("Process user SID is unavailable.");
            return new(process.Id, process.StartTime.ToUniversalTime().Ticks, process.SessionId, sid, path);
        }
    }

    public static bool IsLocalSystem(VerifiedProcessIdentity identity) =>
        string.Equals(identity.UserSid, LocalSystemSid, StringComparison.OrdinalIgnoreCase) && identity.SessionId == 0;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);
}

internal static class SupportPipeSecurity
{
    public static PipeSecurity Create(string registeredUserSid)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(registeredUserSid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }
}

internal static class SupportPipeIdentity
{
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);

    public static VerifiedProcessIdentity VerifyClient(NamedPipeServerStream pipe, SupportConfiguration configuration)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var identity = ProcessIdentity.Capture(checked((int)pid));
        if (identity.SessionId <= 0 ||
            !string.Equals(identity.UserSid, configuration.RegisteredUserSid, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(identity.ExecutablePath), Path.GetFullPath(configuration.RegisteredApplicationPath), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Broker client process path, user, or interactive session does not match provisioning.");
        _ = AuthenticodeVerifier.VerifyPinnedTrusted(identity.ExecutablePath, configuration.PublisherThumbprint);
        return identity;
    }

    public static void VerifyServer(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        string executablePath = ServiceProcessIdentity.VerifyRunningService(checked((int)pid));
        var localSigner = AuthenticodeVerifier.InspectForEnrollment(Environment.ProcessPath!);
        _ = AuthenticodeVerifier.VerifyPinnedTrusted(executablePath, localSigner.SignerThumbprint);
    }
}

/// <summary>
/// Verifies a privileged broker from a medium-integrity desktop process without
/// requesting access to the LocalSystem process token. The SCM attests the
/// registered service account, protected command line, state, and PID; the pipe
/// PID must be that exact own-process service. It deliberately does not open the
/// LocalSystem process, whose process DACL need not grant a desktop user query
/// access. The configured protected executable is signature-checked by the
/// caller after this attestation succeeds.
/// </summary>
internal static class ServiceProcessIdentity
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceRunning = 0x00000004;
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const int ErrorInsufficientBuffer = 122;

    public static string VerifyRunningService(int pipeServerProcessId)
    {
        nint manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            nint service = OpenService(manager, SupportPlatformPaths.ServiceName, ServiceQueryConfig | ServiceQueryStatus);
            if (service == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var status = QueryStatus(service);
                var config = QueryConfiguration(service);
                return ValidateScmAttestation(pipeServerProcessId, status.CurrentState, status.ProcessId,
                    status.ServiceType, config.StartName, config.BinaryPath);
            }
            finally
            {
                _ = CloseServiceHandle(service);
            }
        }
        finally
        {
            _ = CloseServiceHandle(manager);
        }
    }

    private static ServiceStatusProcess QueryStatus(nint service)
    {
        int size = Marshal.SizeOf<ServiceStatusProcess>();
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryServiceStatusEx(service, ScStatusProcessInfo, buffer, size, out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return Marshal.PtrToStructure<ServiceStatusProcess>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (string BinaryPath, string StartName) QueryConfiguration(nint service)
    {
        _ = QueryServiceConfig(service, 0, 0, out int bytesNeeded);
        int error = Marshal.GetLastWin32Error();
        if (bytesNeeded <= 0 || error != ErrorInsufficientBuffer)
            throw new System.ComponentModel.Win32Exception(error);
        nint buffer = Marshal.AllocHGlobal(bytesNeeded);
        try
        {
            if (!QueryServiceConfig(service, buffer, bytesNeeded, out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var config = Marshal.PtrToStructure<QueryServiceConfigData>(buffer);
            string binaryPath = Marshal.PtrToStringUni(config.BinaryPathName)
                ?? throw new InvalidDataException("The support service command line is unavailable.");
            string startName = Marshal.PtrToStringUni(config.ServiceStartName)
                ?? throw new InvalidDataException("The support service account is unavailable.");
            return (binaryPath, startName);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static string ValidateScmAttestation(
        int pipeServerProcessId,
        uint serviceState,
        uint serviceProcessId,
        uint serviceType,
        string serviceStartName,
        string serviceCommand)
    {
        if (serviceState != ServiceRunning || serviceProcessId == 0 ||
            serviceProcessId != checked((uint)pipeServerProcessId) ||
            (serviceType & ServiceWin32OwnProcess) == 0)
            throw new UnauthorizedAccessException("The local support pipe PID does not match the running protected service.");
        if (!string.Equals(serviceStartName, "LocalSystem", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The local support service is not configured for LocalSystem.");
        return ValidateServiceCommand(serviceCommand);
    }

    private static string ValidateServiceCommand(string command)
    {
        nint arguments = CommandLineToArgvW(command, out int count);
        if (arguments == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (count != 2) throw new UnauthorizedAccessException("The local support service command line is not the provisioned command.");
            string executable = Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, 0)) ?? string.Empty;
            string mode = Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, IntPtr.Size)) ?? string.Empty;
            if (!PathsEqual(executable, SupportPlatformPaths.ServiceExecutable) ||
                !string.Equals(mode, "--platform-service", StringComparison.Ordinal))
                throw new UnauthorizedAccessException("The local support service command line is not the provisioned command.");
            return Path.GetFullPath(executable);
        }
        finally
        {
            _ = LocalFree(arguments);
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryServiceConfigData
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public nint BinaryPathName;
        public nint LoadOrderGroup;
        public uint TagId;
        public nint Dependencies;
        public nint ServiceStartName;
        public nint DisplayName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenService(nint serviceControlManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint serviceHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(nint service, int infoLevel, nint buffer, int bufferSize, out int bytesNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(nint service, nint config, int bufferSize, out int bytesNeeded);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}

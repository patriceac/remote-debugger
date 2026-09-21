using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using RemoteDebugger.Core;

namespace RemoteDebugger;

internal sealed record PowerPreflight(string Machine, string Account, bool OneTimeLoginAvailable, string LoginConstraint,
    string ExpectedReturn = "manual_sign_in", int WaitSeconds = 3600);
internal sealed record SavedLogonValue(string Name, RegistryValueKind Kind, string? Text, int? Number);
internal sealed record PowerJournal(string Id, string UserSid, string BootId, string Action, DateTimeOffset ExpiresUtc,
    DateTimeOffset CountdownEndUtc, bool OneTimeLogin, int ServiceStart, SavedLogonValue[] Values, bool Issued = false);

/// <summary>Owned by the signed SYSTEM broker. Secrets never enter its journal or command line.</summary>
internal sealed class PrivilegedPowerManager(SupportConfiguration configuration, CancellationToken lifetime)
{
    private const string WinlogonPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private static readonly string[] LogonNames = ["AutoAdminLogon", "AutoLogonCount", "DefaultUserName", "DefaultDomainName"];
    private static string JournalPath => Path.Combine(SupportPlatformPaths.StateDirectory, "power-operation.json");
    private const string OwnedLogonMarker = "RemoteDebuggerOneTimeSignIn";
    private readonly object gate = new();
    private PowerJournal? journal;
    public bool HasActiveWork { get { lock (gate) return journal != null; } }
    public void Stop(bool windowsShutdown)
    {
        lock (gate)
        {
            if (journal == null || windowsShutdown && journal.Issued && DateTimeOffset.UtcNow < journal.ExpiresUtc) return;
            if (journal.Issued && journal.BootId == WindowsBootIdentity.Read())
            {
                EnableShutdownPrivilege();
                // Windows may already be shutting down. Preserve the recovery journal
                // and automatic cleanup service until the next boot in that case.
                if (!AbortSystemShutdown(null)) return;
            }
            Cleanup();
        }
    }

    public void Start()
    {
        lock (gate)
        {
            if (File.Exists(JournalPath) || File.Exists(JournalPath + ".recovery"))
            {
                try { journal = ReadJournal(JournalPath); }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
                {
                    try { journal = ReadJournal(JournalPath + ".recovery"); }
                    catch (Exception recovery) when (recovery is IOException or JsonException or InvalidDataException)
                    {
                        DisableOwnedOrphanLogin();
                        Trace.WriteLine("Power recovery journal unavailable; temporary automatic sign-in disabled.");
                    }
                }
            }
        }
        _ = Task.Run(WatchAsync);
    }

    public object Dispatch(VerifiedProcessIdentity caller, string operation, JsonElement args)
    {
        caller.CreateLease().Validate();
        if (!string.Equals(caller.UserSid, configuration.RegisteredUserSid, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Power operations require the registered support user.");
        lock (gate)
        {
            return operation switch
            {
                "power.preflight" => Preflight(caller),
                "power.validateLogin" => ValidateLogin(caller, args.Str("password")),
                "power.issue" => Issue(caller, args),
                "power.cancel" => Cancel(caller, args.Str("operationId")),
                "power.returned" => Returned(caller),
                _ => throw new ArgumentException("Unsupported power operation.")
            };
        }
    }

    private PowerPreflight Preflight(VerifiedProcessIdentity caller)
    {
        string account = ((NTAccount)new SecurityIdentifier(caller.UserSid).Translate(typeof(NTAccount))).Value;
        string constraint = "";
        using var key = Registry.LocalMachine.OpenSubKey(WinlogonPath);
        using var policy = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
        bool existingAuto = Convert.ToString(key?.GetValue("AutoAdminLogon")) == "1";
        bool existingSecret = key?.GetValue("DefaultPassword") != null || LogonSecret.Exists();
        bool interactive = !string.IsNullOrWhiteSpace(Convert.ToString(policy?.GetValue("legalnoticetext"))) ||
            !string.IsNullOrWhiteSpace(Convert.ToString(policy?.GetValue("legalnoticecaption"))) || Convert.ToInt32(policy?.GetValue("scforceoption", 0)) != 0;
        if (!account.StartsWith(Environment.MachineName + "\\", StringComparison.OrdinalIgnoreCase)) constraint = "local_account_required";
        else if (journal != null || existingAuto || existingSecret) constraint = "existing_logon_configuration";
        else if (interactive) constraint = "interactive_sign_in_policy";
        else if (!UnattendedBootAvailable()) constraint = "preboot_input_or_unknown";
        bool existingReturn = existingAuto && existingSecret && !interactive &&
            string.Equals(Convert.ToString(key?.GetValue("DefaultUserName")), account[(account.IndexOf('\\') + 1)..], StringComparison.OrdinalIgnoreCase) &&
            Convert.ToString(key?.GetValue("DefaultDomainName")) is { } domain && (domain is "" or "." || domain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) &&
            Convert.ToInt32(key?.GetValue("AutoLogonCount", 1)) > 0 && UnattendedBootAvailable();
        return new(Environment.MachineName, account, constraint.Length == 0, constraint, existingReturn ? "existing_automatic_desktop" : "manual_sign_in");
    }

    private static bool UnattendedBootAvailable()
    {
        // A TPM+PIN/startup-key configuration must not be advertised as an unattended desktop return.
        const string script = "try { $v = Get-CimInstance -Namespace root/CIMV2/Security/MicrosoftVolumeEncryption -ClassName Win32_EncryptableVolume -ErrorAction Stop | Where-Object DriveLetter -eq $env:SystemDrive; if ($null -eq $v) { 'unknown' } elseif ($v.ProtectionStatus -eq 0) { 'unattended' } elseif ($v.ProtectionStatus -eq 1) { $p = Invoke-CimMethod -InputObject $v -MethodName GetKeyProtectors -Arguments @{KeyProtectorType=[uint32]1} -ErrorAction Stop; if ($p.ReturnValue -eq 0 -and $p.VolumeKeyProtectorID.Count -gt 0) { 'unattended' } else { 'manual' } } else { 'unknown' } } catch { 'unknown' }";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var result = Json.Element(Operations.RunAsync(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script], timeout.Token).GetAwaiter().GetResult());
            return result.Int("exitCode", -1) == 0 && result.Str("stdout").Trim() == "unattended";
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or Win32Exception) { return false; }
    }

    private object ValidateLogin(VerifiedProcessIdentity caller, string password)
    {
        var preflight = Preflight(caller);
        if (!preflight.OneTimeLoginAvailable) throw new InvalidOperationException("One-time sign-in is unavailable: " + preflight.LoginConstraint);
        if (password.Length is < 1 or > 512) throw new ArgumentException("Enter the Windows account password, not a PIN.");
        string user = preflight.Account[(preflight.Account.IndexOf('\\') + 1)..];
        if (!LogonUser(user, Environment.MachineName, password, 2, 0, out var token))
            throw new InvalidOperationException("Windows could not validate that account password. Manual sign-in remains available.");
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            if (!string.Equals(identity.User?.Value, caller.UserSid, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Sign-in must use the account already receiving support.");
        return new { valid = true, expectedReturn = "automatic_desktop", account = preflight.Account };
    }

    private object Issue(VerifiedProcessIdentity caller, JsonElement args)
    {
        string id = args.Str("operationId"), action = args.Str("action");
        if (!Guid.TryParseExact(id, "N", out _) || action is not ("restart" or "shutdown")) throw new ArgumentException("Invalid power request.");
        if (journal != null)
        {
            if (journal.Id == id && journal.UserSid == caller.UserSid && journal.Action == action && journal.Issued) return Accepted(journal);
            throw new InvalidOperationException("Another power operation is still active.");
        }
        bool once = action == "restart" && args.TryGetProperty("oneTimeLogin", out var enabled) && enabled.ValueKind == JsonValueKind.True;
        string password = once ? args.Str("password") : "";
        if (once) _ = ValidateLogin(caller, password);
        var now = DateTimeOffset.UtcNow;
        using var key = Registry.LocalMachine.OpenSubKey(WinlogonPath, true) ?? throw new InvalidOperationException("Windows sign-in configuration is unavailable.");
        var values = once ? LogonNames.Select(name => SaveValue(key, name)).ToArray() : [];
        int startMode = OwnServiceStart();
        journal = new(id, caller.UserSid, WindowsBootIdentity.Read(), action, now.AddHours(1), now.AddSeconds(10), once, startMode, values);
        Save(); // Write the recovery journal before changing either the service or Winlogon.
        string recoveryPath = JournalPath + ".recovery";
        _ = PrivilegedPathSafety.RequireUnderNonReparseRoot(recoveryPath, SupportPlatformPaths.StateDirectory, File.Exists(recoveryPath));
        File.Copy(JournalPath, recoveryPath, true);
        try
        {
            if (once)
            {
                SetOwnServiceStart(2); // Cleanup must run after reboot even if the application never starts.
                key.SetValue(OwnedLogonMarker, id, RegistryValueKind.String); key.Flush();
                string account = ((NTAccount)new SecurityIdentifier(caller.UserSid).Translate(typeof(NTAccount))).Value;
                key.SetValue("DefaultUserName", account[(account.IndexOf('\\') + 1)..], RegistryValueKind.String);
                key.SetValue("DefaultDomainName", Environment.MachineName, RegistryValueKind.String);
                key.SetValue("AutoLogonCount", 1, RegistryValueKind.DWord);
                LogonSecret.Store(password);
                key.SetValue("AutoAdminLogon", "1", RegistryValueKind.String);
                key.Flush();
            }
            EnableShutdownPrivilege();
            if (!InitiateSystemShutdownEx(null, "Remote Debugger: " + action, 10, false, action == "restart", 0x80040000))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows did not accept the power request.");
            journal = journal with { Issued = true }; Save();
            return Accepted(journal);
        }
        catch { Cleanup(); throw; }
    }

    private static object Accepted(PowerJournal current) => new
    {
        accepted = true, operationId = current.Id, action = current.Action, countdownEndUtc = current.CountdownEndUtc,
        serverUtc = DateTimeOffset.UtcNow, expiresUtc = current.ExpiresUtc, expectedReturn = current.OneTimeLogin ? "automatic_desktop" : "manual_sign_in",
        powerOffConfirmed = false
    };

    private object Cancel(VerifiedProcessIdentity caller, string id)
    {
        if (journal == null) return new { cancelled = false, noPendingRequest = true };
        if (journal.UserSid != caller.UserSid || journal.Id != id) throw new UnauthorizedAccessException("This power request belongs to another session.");
        bool sameBoot = journal.BootId == WindowsBootIdentity.Read();
        bool cancelled = false;
        if (sameBoot && journal.Issued)
        {
            EnableShutdownPrivilege();
            cancelled = AbortSystemShutdown(null);
        }
        Cleanup();
        return new { cancelled, waitingStopped = true, restartMayAlreadyBeInProgress = !cancelled };
    }

    private object Returned(VerifiedProcessIdentity caller)
    {
        bool afterBoot = journal != null && journal.UserSid == caller.UserSid && journal.BootId != WindowsBootIdentity.Read();
        if (afterBoot) Cleanup();
        return new { returned = afterBoot, temporaryLogonRemoved = journal?.OneTimeLogin != true };
    }

    private async Task WatchAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    lock (gate)
                    {
                        if (journal != null)
                        {
                            string boot = WindowsBootIdentity.Read();
                            bool returned = journal.BootId != boot && LoggedOnUser(journal.UserSid);
                            if (!journal.Issued || returned || DateTimeOffset.UtcNow >= journal.ExpiresUtc) Cleanup();
                        }
                    }
                }
                catch (Exception ex) { Trace.WriteLine("Retrying temporary sign-in cleanup: " + ex.GetType().Name); }
                await Task.Delay(1000, lifetime);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private void Cleanup()
    {
        if (journal == null) return;
        if (journal.OneTimeLogin)
        {
            using var key = Registry.LocalMachine.OpenSubKey(WinlogonPath, true) ?? throw new InvalidOperationException("Cannot clean up temporary Windows sign-in.");
            // Disable first; the retained journal permits cleanup to retry after a crash.
            key.SetValue("AutoAdminLogon", "0", RegistryValueKind.String); key.Flush();
            LogonSecret.Store(null);
            foreach (var saved in journal.Values)
            {
                if (saved.Kind == RegistryValueKind.None) key.DeleteValue(saved.Name, false);
                else key.SetValue(saved.Name, (object?)saved.Number ?? saved.Text ?? "", saved.Kind);
            }
            key.Flush();
            SetOwnServiceStart(journal.ServiceStart);
            key.DeleteValue(OwnedLogonMarker, false);
        }
        File.Delete(JournalPath + ".recovery"); File.Delete(JournalPath); journal = null;
    }

    private static PowerJournal ReadJournal(string path)
    {
        var saved = JsonSerializer.Deserialize<PowerJournal>(File.ReadAllText(path), Json.Options);
        if (saved == null || !Guid.TryParseExact(saved.Id, "N", out _) || saved.Values == null ||
            saved.ServiceStart is < 2 or > 4 || saved.Action is not ("restart" or "shutdown"))
            throw new InvalidDataException("Invalid power recovery journal.");
        return saved;
    }
    private static void DisableOwnedOrphanLogin()
    {
        using var key = Registry.LocalMachine.OpenSubKey(WinlogonPath, true);
        if (!Guid.TryParseExact(Convert.ToString(key?.GetValue(OwnedLogonMarker)), "N", out _)) return;
        key!.SetValue("AutoAdminLogon", "0", RegistryValueKind.String); key.Flush();
        LogonSecret.Store(null); key.DeleteValue("AutoLogonCount", false); key.DeleteValue(OwnedLogonMarker, false);
        SetOwnServiceStart(3);
    }

    private void Save()
    {
        _ = PrivilegedPathSafety.RequireUnderNonReparseRoot(JournalPath, SupportPlatformPaths.StateDirectory, File.Exists(JournalPath));
        string temp = JournalPath + ".new";
        _ = PrivilegedPathSafety.RequireUnderNonReparseRoot(temp, SupportPlatformPaths.StateDirectory, File.Exists(temp));
        File.WriteAllText(temp, JsonSerializer.Serialize(journal, Json.Options)); File.Move(temp, JournalPath, true);
    }

    private static SavedLogonValue SaveValue(RegistryKey key, string name)
    {
        object? value = key.GetValue(name);
        if (value == null) return new(name, RegistryValueKind.None, null, null);
        var kind = key.GetValueKind(name);
        if (kind is not (RegistryValueKind.String or RegistryValueKind.DWord)) throw new InvalidOperationException("Unsupported existing Windows sign-in setting.");
        return new(name, kind, value as string, value as int?);
    }

    private static bool LoggedOnUser(string sid)
    {
        uint session = WTSGetActiveConsoleSessionId();
        if (session == uint.MaxValue || !WTSQueryUserToken(session, out var token)) return false;
        using (token) using (var identity = new WindowsIdentity(token.DangerousGetHandle())) return identity.User?.Value == sid;
    }

    private static int OwnServiceStart()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + SupportPlatformPaths.ServiceName);
        return Convert.ToInt32(key?.GetValue("Start") ?? throw new InvalidOperationException("Support service start mode unavailable."));
    }
    private static void SetOwnServiceStart(int mode)
    {
        IntPtr manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            IntPtr service = OpenService(manager, SupportPlatformPaths.ServiceName, 2);
            if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { if (!ChangeServiceConfig(service, uint.MaxValue, (uint)mode, uint.MaxValue, null, null, IntPtr.Zero, null, null, null, null)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    private static void EnableShutdownPrivilege()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.AdjustPrivileges | TokenAccessLevels.Query);
        if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
        if (!AdjustTokenPrivileges(identity.AccessToken, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastWin32Error() != 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LogonUser(string user, string domain, string password, int type, int provider, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool InitiateSystemShutdownEx(string? machine, string message, uint seconds, bool force, bool reboot, uint reason);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool AbortSystemShutdown(string? machine);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disable, ref TokenPrivileges state, int length, IntPtr previous, IntPtr returned);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ChangeServiceConfig(IntPtr service, uint type, uint start, uint error, string? binary, string? group, IntPtr tag, string? dependencies, string? account, string? password, string? display);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr service);
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint session, out SafeAccessTokenHandle token);
}

internal static class LogonSecret
{
    internal static bool Exists()
    {
        using var key = new LsaString("DefaultPassword");
        IntPtr policy = Open(4);
        try
        {
            uint status = LsaRetrievePrivateData(policy, ref key.Value, out IntPtr data);
            if (status == 0) { LsaFreeMemory(data); return true; }
            if (LsaNtStatusToWinError(status) == 2) return false;
            throw new Win32Exception((int)LsaNtStatusToWinError(status));
        }
        finally { LsaClose(policy); }
    }
    internal static void Store(string? password)
    {
        using var key = new LsaString("DefaultPassword");
        using var secret = password == null ? null : new LsaString(password);
        IntPtr policy = Open(0x20);
        try
        {
            uint status;
            if (secret == null) status = LsaStorePrivateData(policy, ref key.Value, IntPtr.Zero);
            else
            {
                IntPtr data = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
                try { Marshal.StructureToPtr(secret.Value, data, false); status = LsaStorePrivateData(policy, ref key.Value, data); }
                finally { Marshal.FreeHGlobal(data); }
            }
            if (status != 0 && !(password == null && LsaNtStatusToWinError(status) == 2)) throw new Win32Exception((int)LsaNtStatusToWinError(status));
        }
        finally { LsaClose(policy); }
    }
    private static IntPtr Open(uint access)
    {
        var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>() };
        uint status = LsaOpenPolicy(IntPtr.Zero, ref attributes, access, out var handle);
        if (status != 0) throw new Win32Exception((int)LsaNtStatusToWinError(status));
        return handle;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes { public int Length; public IntPtr Root, Name; public uint Attributes; public IntPtr Descriptor, Quality; }
    private sealed class LsaString : IDisposable
    {
        public UnicodeString Value;
        public LsaString(string text) => Value = new() { Length = checked((ushort)(text.Length * 2)), MaximumLength = checked((ushort)((text.Length + 1) * 2)), Buffer = Marshal.StringToHGlobalUni(text) };
        public void Dispose() => Marshal.ZeroFreeGlobalAllocUnicode(Value.Buffer);
    }
    [DllImport("advapi32.dll")] private static extern uint LsaOpenPolicy(IntPtr system, ref ObjectAttributes attributes, uint access, out IntPtr handle);
    [DllImport("advapi32.dll")] private static extern uint LsaStorePrivateData(IntPtr policy, ref UnicodeString key, IntPtr data);
    [DllImport("advapi32.dll")] private static extern uint LsaRetrievePrivateData(IntPtr policy, ref UnicodeString key, out IntPtr data);
    [DllImport("advapi32.dll")] private static extern uint LsaNtStatusToWinError(uint status);
    [DllImport("advapi32.dll")] private static extern uint LsaClose(IntPtr handle);
    [DllImport("advapi32.dll")] private static extern uint LsaFreeMemory(IntPtr memory);
}

using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

internal sealed record UnattendedProfile(bool Enabled, byte[]? Certificate = null,
    InternetSettings? Internet = null, byte[]? Invitation = null, string DesktopRoot = "", string RegisteredUserSid = "")
{
    internal void Validate()
    {
        if (!Enabled) return;
        if (!Path.IsPathFullyQualified(DesktopRoot)) throw new InvalidDataException("The desktop data directory is required.");
        if (Certificate is not { Length: > 0 and <= 16384 } || Invitation is not { Length: > 0 and <= 2048 } || Internet == null)
            throw new InvalidDataException("Unattended support requires an unlocked private setup and a computer identity.");
        Internet.Validate(requirePairingKey: true);
        using var certificate = new X509Certificate2(Certificate, (string?)null, X509KeyStorageFlags.EphemeralKeySet);
        if (!certificate.HasPrivateKey) throw new InvalidDataException("The computer identity has no private key.");
        var invitation = JsonSerializer.Deserialize<JsonElement>(Invitation);
        _ = InternetSettings.SessionId(invitation.Str("id"));
        if (!PairingExchange.ValidHash(invitation.Str("key"))) throw new InvalidDataException("Invalid private invitation.");
    }
}

internal sealed record DesktopHandoff(string Fingerprint, string GrantHash, string ControllerHash, string BinaryHash,
    DateTimeOffset ExpiresUtc, bool UpdateOnly);

internal static class UnattendedSupport
{
    internal const string SystemSid = "S-1-5-18";
    internal static string Root => Path.Combine(SupportPlatformPaths.StateDirectory, "Unattended");
    internal static string ProfilePath => Path.Combine(Root, "profile.dpapi");
    internal static bool IsWorker { get; private set; }
    internal static DesktopHandoff? PendingHandoff { get; private set; }
    internal static DesktopHandoff? TakeHandoff()
    {
        var saved = PendingHandoff;
        PendingHandoff = null;
        return saved;
    }

    internal static async Task ConfigureAsync(string root, bool enabled, CancellationToken ct = default)
        => await SupportPlatform.BrokerCallAsync("unattended.configure", CreateProfile(root, enabled), ct);

    internal static UnattendedProfile CreateProfile(string root, bool enabled)
    {
        var settings = enabled ? InternetSettings.Load(root) : null;
        return enabled && settings != null
            ? new UnattendedProfile(true, Vault.Read(Path.Combine(root, "identity.pfx.dpapi")), settings,
                Vault.Read(Path.Combine(root, "internet-invitation.dpapi")), Path.GetFullPath(root))
            : new UnattendedProfile(false);
    }

    internal static async Task ClaimDesktopAsync(string root, CancellationToken ct = default)
    {
        if (!File.Exists(SupportPlatformPaths.ConfigurationPath)) return;
        // Older installed brokers are upgraded by the normal preparation flow.
        try
        {
            var result = await SupportPlatform.BrokerCallAsync("unattended.claim", new { }, ct);
            PendingHandoff = result.TryGetProperty("handoff", out var value) && value.ValueKind == JsonValueKind.Object
                ? value.Deserialize<DesktopHandoff>(Json.Options) : null;
            if (new UpdateAdminStore(root).IsAdmin && !AdminMaintenancePreference.Load(root))
                await ConfigureAsync(root, false, ct);
        }
        catch (RemoteOperationException ex) when (ex.Code == "broker_invalid_request") { }
    }

    internal static async Task<int> RunAsync()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem || Process.GetCurrentProcess().SessionId <= 0 ||
            !string.Equals(Environment.ProcessPath, SupportPlatformPaths.ApplicationExecutable, StringComparison.OrdinalIgnoreCase)) return 3;
        using var lifetime = new CancellationTokenSource();
        AgentServer? agent = null;
        try
        {
            // Only the exact process started by the protected service may attach.
            await SupportPlatform.BrokerCallAsync("unattended.attach", new { }, lifetime.Token);
            IsWorker = true;
            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
            agent = new AgentServer(Root, reuseInvitation: true);
            agent.TerminationRequested += _ => lifetime.Cancel();
            agent.Start();
            string[] args = Environment.GetCommandLineArgs();
            int transaction = Array.IndexOf(args, "--update-transaction"), ticket = Array.IndexOf(args, "--resume-update");
            if (transaction >= 0 && transaction + 1 < args.Length && ticket >= 0 && ticket + 1 < args.Length)
                await SupportPlatform.ReportStartupHealthyAsync(args[transaction + 1], args[ticket + 1], lifetime.Token);
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(1000, lifetime.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var state = await SupportPlatform.BrokerCallAsync("unattended.attach", new { handoff = agent.DesktopHandoff() }, timeout.Token);
                if (!state.GetProperty("active").GetBoolean()) break;
            }
            return 0;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return 0; }
        catch (Exception ex)
        {
            // Never persist request arguments, pairing secrets, or Windows input.
            Trace.WriteLine("Unattended agent stopped: " + ex.GetType().Name);
            return 2;
        }
        finally
        {
            if (lifetime.IsCancellationRequested)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await SupportPlatform.BrokerCallAsync("unattended.attach", new { handoff = (DesktopHandoff?)null }, timeout.Token); }
                catch (Exception) { }
            }
            agent?.Dispose();
        }
    }
}

internal sealed class UnattendedSupportHost(SupportConfiguration configuration, CancellationToken lifetime) : IDisposable
{
    private readonly object sync = new();
    private Process? worker;
    private VerifiedProcessIdentity? workerIdentity;
    private UnattendedProfile? profile;
    private int disposed;
    private int stopping;
    private DesktopHandoff? handoff;
    internal Func<bool> UpdateActive { get; set; } = () => false;
    internal bool Enabled => profile?.Enabled == true;

    internal void Initialize()
    {
        SecureStateDirectory();
        if (File.Exists(UnattendedSupport.ProfilePath))
        {
            try
            {
                profile = JsonSerializer.Deserialize<UnattendedProfile>(Vault.Read(UnattendedSupport.ProfilePath), Json.Options);
                profile?.Validate();
                if (profile?.RegisteredUserSid != configuration.RegisteredUserSid) profile = null;
            }
            catch (Exception ex) { profile = null; Trace.WriteLine("Unattended setup unavailable: " + ex.GetType().Name); }
        }
    }

    internal void Start() => _ = Task.Run(WatchAsync);

    internal static bool ShouldRun(bool enabled, int consoleSession, string? consoleUserSid) =>
        enabled && consoleSession > 0 && consoleUserSid == null;

    internal static bool IsWorkerIdentity(VerifiedProcessIdentity actual, VerifiedProcessIdentity? expected) =>
        expected != null && actual == expected && actual.UserSid == UnattendedSupport.SystemSid && actual.SessionId > 0;

    internal bool Owns(VerifiedProcessIdentity identity)
    {
        return Volatile.Read(ref disposed) == 0 && IsWorkerIdentity(identity, Volatile.Read(ref workerIdentity));
    }

    internal object Configure(VerifiedProcessIdentity caller, JsonElement args)
    {
        if (caller.UserSid != configuration.RegisteredUserSid)
            throw new UnauthorizedAccessException("Only the registered desktop user can configure unattended support.");
        var updated = args.Deserialize<UnattendedProfile>(Json.Options) ?? throw new InvalidDataException("Missing unattended setup.");
        updated.Validate();
        updated = updated with { RegisteredUserSid = caller.UserSid };
        lock (sync)
        {
            StopWorker();
            SecureStateDirectory();
            // All credentials are re-encrypted for SYSTEM under a SYSTEM/admin-only ACL.
            Vault.Save(UnattendedSupport.ProfilePath, JsonSerializer.SerializeToUtf8Bytes(updated, Json.Options));
            if (updated.Enabled)
            {
                Vault.Save(Path.Combine(UnattendedSupport.Root, "identity.pfx.dpapi"), updated.Certificate!);
                updated.Internet!.Save(UnattendedSupport.Root);
                Vault.Save(Path.Combine(UnattendedSupport.Root, "internet-invitation.dpapi"), updated.Invitation!);
            }
            profile = updated;
            return new { enabled = updated.Enabled };
        }
    }

    internal object Claim(VerifiedProcessIdentity caller)
    {
        if (caller.UserSid != configuration.RegisteredUserSid)
            throw new UnauthorizedAccessException("Only the registered user can take over the desktop agent.");
        lock (sync)
        {
            if (UpdateActive()) throw new InvalidOperationException("An unattended update is still finishing. Retry when it completes.");
            StopWorker();
            var saved = handoff;
            handoff = null;
            return new { released = true, handoff = saved };
        }
    }

    internal object Attach(VerifiedProcessIdentity caller, JsonElement args)
    {
        if (!Owns(caller)) throw new UnauthorizedAccessException("Unrecognized unattended agent.");
        if (args.TryGetProperty("handoff", out var value))
            Volatile.Write(ref handoff, value.ValueKind == JsonValueKind.Object ? value.Deserialize<DesktopHandoff>(Json.Options) : null);
        var console = InteractiveProcessLauncher.ConsoleSession();
        return new { active = Volatile.Read(ref stopping) == 0 &&
            ShouldRun(profile?.Enabled == true, console.Id, console.UserSid) && console.Id == caller.SessionId };
    }

    internal int LaunchReplacement(int sessionId, IReadOnlyList<string> arguments)
    {
        lock (sync)
        {
            var console = InteractiveProcessLauncher.ConsoleSession();
            if (!ShouldRun(profile?.Enabled == true, console.Id, console.UserSid) || console.Id != sessionId)
                throw new InvalidOperationException("The signed-out console session changed during the update.");
            StopWorker();
            return LaunchWorker(sessionId, arguments);
        }
    }

    private int LaunchWorker(int sessionId, IReadOnlyList<string> arguments)
    {
        Volatile.Write(ref stopping, 0);
        handoff = null;
        int pid = InteractiveProcessLauncher.StartUnattendedAgent(sessionId, arguments);
        worker = Process.GetProcessById(pid);
        workerIdentity = ProcessIdentity.Capture(pid);
        return pid;
    }

    private async Task WatchAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    lock (sync)
                    {
                        if (disposed != 0) return;
                        var console = InteractiveProcessLauncher.ConsoleSession();
                        bool run = ShouldRun(profile?.Enabled == true, console.Id, console.UserSid);
                        if (!UpdateActive())
                        {
                            bool handoff = worker != null && !run && console.UserSid == configuration.RegisteredUserSid;
                            if (worker != null && (!run || worker.HasExited || workerIdentity?.SessionId != console.Id)) StopWorker();
                            if (run && worker == null) LaunchWorker(console.Id, ["--unattended-agent"]);
                            if (handoff)
                                InteractiveProcessLauncher.Start(console.Id, configuration.RegisteredUserSid,
                                    configuration.RegisteredApplicationPath, ["--startup", "--data-root", profile!.DesktopRoot], SupportPlatformPaths.ProductDirectory);
                        }
                    }
                }
                catch (Exception ex) { Trace.WriteLine("Unattended supervision: " + ex.GetType().Name); }
                await Task.Delay(1000, lifetime);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private static void SecureStateDirectory()
    {
        _ = PrivilegedPathSafety.RequireUnderNonReparseRoot(UnattendedSupport.Root, SupportPlatformPaths.StateDirectory,
            includeLeaf: Directory.Exists(UnattendedSupport.Root));
        Directory.CreateDirectory(UnattendedSupport.Root);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(UnattendedSupport.Root).SetAccessControl(acl);
    }

    private void StopWorker()
    {
        if (worker == null) return;
        try
        {
            Volatile.Write(ref stopping, 1);
            if (!worker.HasExited && !worker.WaitForExit(7000))
            {
                worker.Kill(entireProcessTree: true);
                if (!worker.WaitForExit(10000)) throw new TimeoutException("The signed-out agent did not release its listener.");
            }
        }
        catch (InvalidOperationException) { }
        worker.Dispose(); worker = null; workerIdentity = null;
    }

    public void Dispose()
    {
        lock (sync) { disposed = 1; StopWorker(); }
    }
}

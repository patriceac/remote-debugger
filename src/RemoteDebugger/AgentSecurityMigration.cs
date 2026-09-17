using System.Net.Security;
using System.Security.Authentication;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

public sealed partial class AgentServer
{
    private readonly string securityRoot;
    private SecurityCandidate? securityCandidate;

    private sealed class SecurityCandidate(InternetSettings settings, string directory)
    {
        public InternetSettings Settings { get; } = settings;
        public string Directory { get; } = directory;
        public PairingGate Gate { get; } = new();
        public SemaphoreSlim PairSlot { get; } = new(1, 1);
        public InternetAgent Agent { get; set; } = null!;
        public string TokenHash { get; set; } = "";
        public string ControllerHash { get; set; } = "";
        public DateTimeOffset Expires { get; } = DateTimeOffset.UtcNow.AddMinutes(10);
        public volatile bool Promoted;
    }

    private async Task<object> StageSecurityAsync(JsonElement args, CancellationToken ct)
    {
        if (Internet == null || Volatile.Read(ref terminating) != 0 || updates.PendingExitPlan != null)
            throw new InvalidOperationException("Internet support must be active before migration.");
        var next = args.GetProperty("settings").Deserialize<InternetSettings>(Json.Options) ?? throw new ArgumentException("Missing settings.");
        next.Validate(true);
        var current = InternetSettings.Load(securityRoot)!;
        if (next.RelayUrl != current.RelayUrl || next.SecurityId.Length == 0)
            throw new ArgumentException("Migration must use the current relay and an identified security profile.");
        if (next == current) return new { host = Internet.SupportId, fingerprint = Fingerprint, securityId = current.SecurityId, alreadyProtected = true };
        if (current.SecurityId.Length > 0 || Safety.Equal(next.AccessKey, current.AccessKey) || Safety.Equal(next.PairingKey, current.PairingKey))
            throw new InvalidOperationException("Migration requires fresh relay and pairing credentials and cannot overwrite protected access.");
        var staged = securityCandidate;
        if (staged == null || staged.Settings != next || staged.Expires <= DateTimeOffset.UtcNow)
        {
            staged?.Agent.Dispose();
            staged = new SecurityCandidate(next, Path.Combine(securityRoot, "security-staged"));
            var candidate = staged;
            staged.Agent = new InternetAgent(staged.Directory, next, false, async stream =>
            {
                if (Volatile.Read(ref disposed) != 0 || !slots.Wait(0)) return;
                await ServeAsync(stream, candidate);
            });
            securityCandidate = staged;
            staged.Agent.Start();
        }
        staged.TokenHash = "";
        staged.Gate.OpenPrivate(staged.Agent.AuthenticationSecret);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(35));
        while (!staged.Agent.Connected) await Task.Delay(200, deadline.Token);
        return new { host = staged.Agent.SupportId, fingerprint = Fingerprint, securityId = next.SecurityId, alreadyProtected = false };
    }

    private async Task ServeSecurityCandidateAsync(SslStream tls, Request request, SecurityCandidate candidate, CancellationToken ct)
    {
        if (candidate.Expires <= DateTimeOffset.UtcNow || Volatile.Read(ref terminating) != 0 ||
            !ReferenceEquals(securityCandidate, candidate)) throw new AuthenticationException("Security migration expired.");
        if (request.Operation == "pair.v2")
        {
            if (!await candidate.PairSlot.WaitAsync(0, ct)) throw new AuthenticationException("Pairing is busy.");
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
                await PairingTransport.AcceptAsync(tls, request, candidate.Gate, Fingerprint, (token, binary) =>
                {
                    candidate.TokenHash = Safety.Hash(token); candidate.ControllerHash = binary;
                }, deadline.Token);
            }
            finally { candidate.PairSlot.Release(); }
            return;
        }
        if (candidate.TokenHash.Length == 0 || !Safety.Equal(candidate.TokenHash, Safety.Hash(request.Token ?? "")) ||
            !Safety.Equal(candidate.ControllerHash, request.BinarySha256 ?? "") || !Safety.Equal(candidate.ControllerHash, ExecutableIdentity.Sha256))
            throw new AuthenticationException("Security migration is not authorized.");
        if (request.Operation != "security.commit" || request.Args.Str("securityId") != candidate.Settings.SecurityId)
        {
            await Wire.WriteAsync(tls, Reply.Failure(request.Id, "migration_only", "This temporary connection can only confirm security migration."), ct);
            return;
        }
        await updateGate.WaitAsync(ct);
        InternetAgent? previous = null;
        try
        {
            if (Volatile.Read(ref terminating) != 0 || candidate.Expires <= DateTimeOffset.UtcNow || updates.PendingExitPlan != null)
                throw new AuthenticationException("Security migration is no longer available.");
            // Persist first. An uncertain reply is recovered using the controller's saved new route and credentials.
            Vault.Save(Path.Combine(securityRoot, "internet-invitation.dpapi"),
                Vault.Read(Path.Combine(candidate.Directory, "internet-invitation.dpapi")));
            candidate.Settings.Save(securityRoot);
            resumeStore.Clear(); resumed = null; resumeAccepted = false;
            lock (authLock)
            {
                tokenHash = candidate.TokenHash; controllerBinaryHash = candidate.ControllerHash;
                grantLifetime.Cancel(); grantLifetime = new();
                foreach (var job in running.Values) job.Cancel(); requests.Clear();
                session.Pair(true); Pairing.Close();
            }
            Native.ReleaseAllInput();
            previous = Internet; Internet = candidate.Agent; candidate.Promoted = true;
            await Wire.WriteAsync(tls, Reply.Success(request.Id, new { securityId = candidate.Settings.SecurityId, protectedAccess = true }), ct);
            Status?.Invoke("Security updated. Previous installer credentials and sessions are no longer accepted.");
        }
        finally { previous?.Dispose(); updateGate.Release(); }
    }
}

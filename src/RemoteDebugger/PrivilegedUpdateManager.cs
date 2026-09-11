using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

internal sealed record PrivilegedUpdateTransaction(
    string TransactionId,
    UpdateTransactionState State,
    ExecutableSnapshot Candidate,
    ExecutableSnapshot Previous,
    string StagePath,
    string ProtectedBackupPath,
    int ClientProcessId,
    long ClientStartTicks,
    int SessionId,
    string UserSid,
    string[] OriginalArguments,
    string WorkingDirectory,
    string ProtectedReconnectTicket,
    DateTimeOffset ReconnectExpiresUtc,
    DateTimeOffset PlannedDisconnectDeadlineUtc,
    int? NewProcessId,
    string? LastError,
    DateTimeOffset UpdatedUtc);

internal sealed class PrivilegedUpdateManager : IDisposable
{
    private readonly SupportConfiguration configuration;
    private readonly CancellationToken serviceLifetime;
    private readonly object stateLock = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> startupHealth = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> workerCancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> cancellationRelaunch = new(StringComparer.OrdinalIgnoreCase);
    private int disposed;

    public PrivilegedUpdateManager(SupportConfiguration configuration, CancellationToken serviceLifetime)
    {
        this.configuration = configuration;
        this.serviceLifetime = serviceLifetime;
        if (!string.Equals(Path.GetFullPath(configuration.RegisteredApplicationPath), Path.GetFullPath(SupportPlatformPaths.ApplicationExecutable), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Update target is not the protected managed application path.");
        _ = PrivilegedPathSafety.RequireUnderNonReparseRoot(configuration.RegisteredApplicationPath, SupportPlatformPaths.ProductDirectory);
        _ = PrivilegedPathSafety.RequireUnderNonReparseRoot(SupportPlatformPaths.TransactionsDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), includeLeaf: false);
        Directory.CreateDirectory(SupportPlatformPaths.TransactionsDirectory);
        _ = Task.Run(RecoverInterruptedTransactionsAsync);
    }

    public bool HasActiveWork => !workers.IsEmpty;

    public async Task<object> StageAsync(VerifiedProcessIdentity caller, JsonElement args, CancellationToken ct)
    {
        string transactionId = args.Str("transactionId");
        if (!Guid.TryParseExact(transactionId, "N", out _)) throw new ArgumentException("Transaction id must be a UUID in N format.");
        string sourcePath = Path.GetFullPath(args.Str("sourcePath"));
        if (!File.Exists(sourcePath) || File.GetAttributes(sourcePath).HasFlag(FileAttributes.ReparsePoint))
            throw new FileNotFoundException("Completed update staging file is unavailable or unsafe.", sourcePath);
        var candidate = args.GetProperty("candidate").Deserialize<ExecutableSnapshot>(Json.Options) ?? throw new InvalidDataException("Missing candidate snapshot.");
        candidate.Validate();
        UpdatePolicy.RequirePinnedPublisher(candidate, configuration.PublisherThumbprint);

        string transactionPath = TransactionPath(transactionId);
        if (File.Exists(transactionPath))
        {
            var existing = Load(transactionId);
            bool sameCandidate = UpdatePolicy.FixedHexEquals(existing.Candidate.Sha256, candidate.Sha256) && existing.Candidate.Size == candidate.Size;
            if (!sameCandidate) throw new IOException("Transaction id already refers to different executable bytes.");
            if (existing.State is UpdateTransactionState.Completed or UpdateTransactionState.Cancelled or UpdateTransactionState.RolledBack or UpdateTransactionState.Failed)
            {
                DeleteIfExists(existing.StagePath);
                DeleteIfExists(existing.ProtectedBackupPath);
                File.Delete(transactionPath);
            }
            else if (existing.State == UpdateTransactionState.Staged && !IsOriginalProcessRunning(existing))
            {
                existing = existing with
                {
                    ClientProcessId = caller.ProcessId,
                    ClientStartTicks = caller.StartTicks,
                    SessionId = caller.SessionId,
                    UserSid = caller.UserSid,
                    UpdatedUtc = DateTimeOffset.UtcNow
                };
                Save(existing);
                return Describe(existing);
            }
            else return Describe(existing);
        }

        string stagePath = Path.Combine(SupportPlatformPaths.TransactionsDirectory, transactionId + ".stage.exe");
        string stageTemp = stagePath + ".new";
        _ = PrivilegedPathSafety.RequireUnderNonReparseRoot(stageTemp, SupportPlatformPaths.StateDirectory, includeLeaf: false);
        // Keep the exact source file object open without write/delete sharing
        // through the copy. A parent junction rename cannot redirect this handle.
        await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var destination = new FileStream(stageTemp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            if (source.Length != candidate.Size) throw new InvalidDataException("Staged source size changed before the protected copy opened.");
            await source.CopyToAsync(destination, 1024 * 1024, ct);
            await destination.FlushAsync(ct);
        }
        var protectedStage = await SnapshotAsync(stageTemp, ct);
        RequireCandidate(protectedStage, candidate);
        File.Move(stageTemp, stagePath, true);

        var previous = await SnapshotAsync(configuration.RegisteredApplicationPath, ct);
        var transaction = new PrivilegedUpdateTransaction(transactionId, UpdateTransactionState.Staged,
            candidate with { Path = configuration.RegisteredApplicationPath }, previous, stagePath,
            Path.Combine(SupportPlatformPaths.TransactionsDirectory, transactionId + ".backup.exe"),
            caller.ProcessId, caller.StartTicks, caller.SessionId, caller.UserSid, [], AppContext.BaseDirectory,
            "", DateTimeOffset.MinValue, DateTimeOffset.MinValue, null, null, DateTimeOffset.UtcNow);
        Save(transaction);
        return Describe(transaction);
    }

    public async Task<object> ArmAsync(VerifiedProcessIdentity caller, JsonElement args, CancellationToken ct)
    {
        string transactionId = args.Str("transactionId");
        var transaction = Load(transactionId);
        if (caller.ProcessId != transaction.ClientProcessId || caller.StartTicks != transaction.ClientStartTicks)
            throw new UnauthorizedAccessException("Only the process that staged this transaction may arm it.");
        if (transaction.State != UpdateTransactionState.Staged)
        {
            if (transaction.State is UpdateTransactionState.Armed or UpdateTransactionState.Replacing or UpdateTransactionState.AwaitingStartupHealth or UpdateTransactionState.RunningPendingRemoteHealth)
                return Describe(transaction);
            throw new InvalidOperationException("Only a fully staged update can be armed.");
        }
        var grant = args.GetProperty("reconnect").Deserialize<UpdateReconnectGrant>(Json.Options) ?? throw new InvalidDataException("Missing reconnect grant.");
        grant.Validate(DateTimeOffset.UtcNow);
        string[] arguments = args.Strings("arguments");
        ValidateArguments(arguments);
        string workingDirectory = Path.GetFullPath(args.Str("workingDirectory", AppContext.BaseDirectory));
        if (!Directory.Exists(workingDirectory)) workingDirectory = Path.GetDirectoryName(configuration.RegisteredApplicationPath)!;

        var current = await SnapshotAsync(configuration.RegisteredApplicationPath, ct);
        UpdatePolicy.RequireExactControllerBinary(transaction.Previous, current);
        UpdatePolicy.RequireTransition(transaction.State, UpdateTransactionState.Armed);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        transaction = transaction with
        {
            State = UpdateTransactionState.Armed,
            OriginalArguments = SanitizeArguments(arguments),
            WorkingDirectory = workingDirectory,
            ProtectedReconnectTicket = Protect(grant.Ticket),
            ReconnectExpiresUtc = grant.ExpiresUtc,
            PlannedDisconnectDeadlineUtc = deadline,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        Save(transaction);
        StartWorker(transactionId, token => RunTransactionAsync(transactionId, token));
        return Describe(transaction);
    }

    public async Task<object> ReportStartupHealthyAsync(VerifiedProcessIdentity caller, JsonElement args, CancellationToken ct)
    {
        string transactionId = args.Str("transactionId");
        string ticket = args.Str("ticket");
        var transaction = Load(transactionId);
        ValidateTicket(transaction, ticket);
        if (transaction.State == UpdateTransactionState.RunningPendingRemoteHealth) return Describe(transaction);
        if (transaction.State != UpdateTransactionState.AwaitingStartupHealth)
            throw new InvalidOperationException("This update is not awaiting startup health.");
        if (transaction.NewProcessId != caller.ProcessId || caller.SessionId != transaction.SessionId ||
            !string.Equals(caller.UserSid, transaction.UserSid, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Startup health came from a different process or interactive session.");
        var running = await SnapshotAsync(caller.ExecutablePath, ct);
        RequireCandidate(running, transaction.Candidate);
        UpdatePolicy.RequireTransition(transaction.State, UpdateTransactionState.RunningPendingRemoteHealth);
        transaction = transaction with { State = UpdateTransactionState.RunningPendingRemoteHealth, UpdatedUtc = DateTimeOffset.UtcNow };
        Save(transaction);
        startupHealth.GetOrAdd(transactionId, _ => NewSignal()).TrySetResult(true);
        return Describe(transaction);
    }

    public async Task<object> ReportRemoteHealthyAsync(VerifiedProcessIdentity caller, JsonElement args, CancellationToken ct)
    {
        string transactionId = args.Str("transactionId");
        var transaction = Load(transactionId);
        ValidateTicket(transaction, args.Str("ticket"));
        if (transaction.State == UpdateTransactionState.Completed) return Describe(transaction);
        if (transaction.State != UpdateTransactionState.RunningPendingRemoteHealth)
            throw new InvalidOperationException("The replacement has not reported startup health.");
        if (transaction.NewProcessId != caller.ProcessId || caller.SessionId != transaction.SessionId ||
            !string.Equals(caller.UserSid, transaction.UserSid, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Remote health came from a different process or interactive session.");
        var running = await SnapshotAsync(caller.ExecutablePath, ct);
        RequireCandidate(running, transaction.Candidate);
        UpdatePolicy.RequireTransition(transaction.State, UpdateTransactionState.Completed);
        transaction = transaction with { State = UpdateTransactionState.Completed, UpdatedUtc = DateTimeOffset.UtcNow };
        Save(transaction);
        DeleteIfExists(transaction.ProtectedBackupPath);
        DeleteIfExists(transaction.StagePath);
        return Describe(transaction);
    }

    public object Status(JsonElement args)
    {
        string transactionId = args.Str("transactionId");
        if (transactionId.Length == 0)
        {
            var latest = Directory.EnumerateFiles(SupportPlatformPaths.TransactionsDirectory, "*.json")
                .Select(path => (Path: path, Write: File.GetLastWriteTimeUtc(path))).OrderByDescending(item => item.Write).FirstOrDefault();
            return latest.Path == null ? new { active = false } : Describe(Load(Path.GetFileNameWithoutExtension(latest.Path)));
        }
        if (!Guid.TryParseExact(transactionId, "N", out _)) throw new ArgumentException("Invalid update transaction id.");
        return File.Exists(TransactionPath(transactionId)) ? Describe(Load(transactionId)) : new { active = false, transactionId };
    }

    public async Task<object> CancelAsync(JsonElement args, CancellationToken ct)
    {
        string transactionId = args.Str("transactionId");
        bool relaunchPrevious = !args.TryGetProperty("relaunchPrevious", out var relaunch) || relaunch.GetBoolean();
        cancellationRelaunch[transactionId] = relaunchPrevious;
        if (workerCancellations.TryGetValue(transactionId, out var cancellation)) cancellation.Cancel();
        if (workers.TryGetValue(transactionId, out var worker))
            try { await worker.WaitAsync(TimeSpan.FromSeconds(20), ct); }
            catch (TimeoutException) { throw new InvalidOperationException("Update cancellation did not reach a safe rollback point in time."); }
            catch (Exception) when (!ct.IsCancellationRequested) { }
        var transaction = Load(transactionId);
        if (transaction.State == UpdateTransactionState.Staged)
            transaction = Transition(transactionId, UpdateTransactionState.Cancelled, "Update cancelled before replacement.");
        else if (transaction.State == UpdateTransactionState.Armed)
            transaction = Transition(transactionId, UpdateTransactionState.Cancelled, "Update cancelled before the agent exited.");
        else if (transaction.State is UpdateTransactionState.Replacing or UpdateTransactionState.AwaitingStartupHealth or UpdateTransactionState.RunningPendingRemoteHealth)
        {
            await RollbackAsync(transaction, "Update cancelled explicitly; previous executable restored.", relaunchPrevious);
            transaction = Load(transactionId);
        }
        else if (transaction.State is not (UpdateTransactionState.Cancelled or UpdateTransactionState.RolledBack or UpdateTransactionState.Completed or UpdateTransactionState.Failed))
            throw new InvalidOperationException("Update is not in a cancellable state.");
        DeleteIfExists(transaction.StagePath);
        cancellationRelaunch.TryRemove(transactionId, out _);
        return Describe(transaction);
    }

    private async Task RunTransactionAsync(string transactionId, CancellationToken transactionCancellation)
    {
        try
        {
            using var power = PowerRequestScope.HoldSystemAwakeForUpdate();
            var transaction = Load(transactionId);
            while (DateTimeOffset.UtcNow < transaction.PlannedDisconnectDeadlineUtc && IsOriginalProcessRunning(transaction))
                await Task.Delay(100, transactionCancellation);
            if (IsOriginalProcessRunning(transaction))
            {
                Transition(transactionId, UpdateTransactionState.Cancelled, "The agent did not exit before the planned update deadline.");
                return;
            }

            transaction = Transition(transactionId, UpdateTransactionState.Replacing);
            _ = PrivilegedPathSafety.RequireUnderNonReparseRoot(configuration.RegisteredApplicationPath, SupportPlatformPaths.ProductDirectory);
            _ = PrivilegedPathSafety.RequireUnderNonReparseRoot(transaction.ProtectedBackupPath, SupportPlatformPaths.StateDirectory, includeLeaf: false);
            File.Copy(configuration.RegisteredApplicationPath, transaction.ProtectedBackupPath, true);
            var backup = await SnapshotAsync(transaction.ProtectedBackupPath, transactionCancellation);
            UpdatePolicy.RequireExactControllerBinary(transaction.Previous, backup);

            string replacement = configuration.RegisteredApplicationPath + "." + transactionId + ".new";
            _ = PrivilegedPathSafety.RequireUnderNonReparseRoot(replacement, SupportPlatformPaths.ProductDirectory, includeLeaf: false);
            File.Copy(transaction.StagePath, replacement, true);
            RequireCandidate(await SnapshotAsync(replacement, transactionCancellation), transaction.Candidate);
            File.Replace(replacement, configuration.RegisteredApplicationPath, null, true);
            RequireCandidate(await SnapshotAsync(configuration.RegisteredApplicationPath, transactionCancellation), transaction.Candidate);

            int pid = LaunchReplacement(transaction);
            transaction = Load(transactionId);
            UpdatePolicy.RequireTransition(transaction.State, UpdateTransactionState.AwaitingStartupHealth);
            transaction = transaction with { State = UpdateTransactionState.AwaitingStartupHealth, NewProcessId = pid, UpdatedUtc = DateTimeOffset.UtcNow };
            Save(transaction);
            await AwaitStartupHealthOrRollbackAsync(transaction, transactionCancellation);
        }
        catch (OperationCanceledException) when (serviceLifetime.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            try
            {
                var transaction = Load(transactionId);
                if (transaction.State == UpdateTransactionState.Armed)
                    Transition(transactionId, UpdateTransactionState.Cancelled, "Update cancelled before the agent exited.");
                else if (transaction.State is UpdateTransactionState.Replacing or UpdateTransactionState.AwaitingStartupHealth or UpdateTransactionState.RunningPendingRemoteHealth)
                    await RollbackAsync(transaction, "Update cancelled explicitly; previous executable restored.",
                        !cancellationRelaunch.TryGetValue(transactionId, out bool relaunch) || relaunch);
            }
            catch (Exception rollback) { ForceState(transactionId, UpdateTransactionState.Failed, "Explicit update cancellation could not roll back safely: " + rollback.Message); }
        }
        catch (Exception ex)
        {
            try { await RollbackAsync(Load(transactionId), ex.Message); }
            catch (Exception rollback)
            {
                ForceState(transactionId, UpdateTransactionState.Failed, ex.Message + " Rollback also failed: " + rollback.Message);
            }
        }
    }

    private async Task AwaitStartupHealthOrRollbackAsync(PrivilegedUpdateTransaction transaction, CancellationToken transactionCancellation)
    {
        var signal = startupHealth.GetOrAdd(transaction.TransactionId, _ => NewSignal());
        Task exited;
        try
        {
            using var process = Process.GetProcessById(transaction.NewProcessId!.Value);
            exited = process.WaitForExitAsync(transactionCancellation);
            Task completed = await Task.WhenAny(signal.Task, exited,
                Task.Delay(TimeSpan.FromSeconds(SupportOperationTimeouts.UpdateStartupHealthRollbackSeconds), transactionCancellation));
            transactionCancellation.ThrowIfCancellationRequested();
            if (completed != signal.Task || !await signal.Task)
            {
                string reason = completed == exited
                    ? "Updated application exited before reporting startup health."
                    : $"Updated application did not report startup health within {SupportOperationTimeouts.UpdateStartupHealthRollbackSeconds} seconds.";
                await RollbackAsync(Load(transaction.TransactionId), reason);
            }
        }
        catch (ArgumentException) { await RollbackAsync(Load(transaction.TransactionId), "Updated application process was not found."); }
    }

    private async Task RollbackAsync(PrivilegedUpdateTransaction transaction, string reason, bool relaunchPrevious = true)
    {
        if (transaction.NewProcessId is int pid)
        {
            try { using var process = Process.GetProcessById(pid); process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
            catch (ArgumentException) { }
        }
        var current = await SnapshotAsync(configuration.RegisteredApplicationPath, CancellationToken.None);
        if (UpdatePolicy.FixedHexEquals(current.Sha256, transaction.Previous.Sha256) && current.Size == transaction.Previous.Size)
        {
            if (relaunchPrevious && !IsOriginalProcessRunning(transaction))
                _ = InteractiveProcessLauncher.Start(transaction.SessionId, transaction.UserSid, configuration.RegisteredApplicationPath,
                    EnsureAgentArgument(transaction.OriginalArguments), transaction.WorkingDirectory);
            ForceState(transaction.TransactionId, UpdateTransactionState.RolledBack, reason);
            return;
        }
        if (!File.Exists(transaction.ProtectedBackupPath))
            throw new IOException("Protected previous executable is missing and the managed application no longer matches it.");
        string restored = configuration.RegisteredApplicationPath + "." + transaction.TransactionId + ".rollback";
        _ = PrivilegedPathSafety.RequireUnderNonReparseRoot(restored, SupportPlatformPaths.ProductDirectory, includeLeaf: false);
        _ = PrivilegedPathSafety.RequireUnderNonReparseRoot(transaction.ProtectedBackupPath, SupportPlatformPaths.StateDirectory);
        File.Copy(transaction.ProtectedBackupPath, restored, true);
        UpdatePolicy.RequireExactControllerBinary(transaction.Previous, await SnapshotAsync(restored, CancellationToken.None));
        File.Replace(restored, configuration.RegisteredApplicationPath, null, true);
        UpdatePolicy.RequireExactControllerBinary(transaction.Previous, await SnapshotAsync(configuration.RegisteredApplicationPath, CancellationToken.None));
        if (relaunchPrevious && !IsOriginalProcessRunning(transaction))
            _ = InteractiveProcessLauncher.Start(transaction.SessionId, transaction.UserSid, configuration.RegisteredApplicationPath,
                EnsureAgentArgument(transaction.OriginalArguments), transaction.WorkingDirectory);
        ForceState(transaction.TransactionId, UpdateTransactionState.RolledBack, reason);
    }

    private int LaunchReplacement(PrivilegedUpdateTransaction transaction)
    {
        string ticket = Unprotect(transaction.ProtectedReconnectTicket);
        var arguments = EnsureAgentArgument(transaction.OriginalArguments).ToList();
        arguments.Add("--resume-update");
        arguments.Add(ticket);
        arguments.Add("--update-transaction");
        arguments.Add(transaction.TransactionId);
        return InteractiveProcessLauncher.Start(transaction.SessionId, transaction.UserSid,
            configuration.RegisteredApplicationPath, arguments, transaction.WorkingDirectory);
    }

    private Task RecoverInterruptedTransactionsAsync()
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(SupportPlatformPaths.TransactionsDirectory, "*.json"))
            {
                var transaction = Load(Path.GetFileNameWithoutExtension(path));
                if (transaction.State == UpdateTransactionState.Armed)
                    StartWorker(transaction.TransactionId, token => RunTransactionAsync(transaction.TransactionId, token));
                else if (transaction.State is UpdateTransactionState.Replacing or UpdateTransactionState.AwaitingStartupHealth)
                    StartWorker(transaction.TransactionId, token => RecoverReplacementAsync(transaction, token));
            }
        }
        catch (OperationCanceledException) when (serviceLifetime.IsCancellationRequested) { }
        return Task.CompletedTask;
    }

    private async Task RecoverReplacementAsync(PrivilegedUpdateTransaction transaction, CancellationToken transactionCancellation)
    {
        try
        {
            using var power = PowerRequestScope.HoldSystemAwakeForUpdate();
            var target = await SnapshotAsync(configuration.RegisteredApplicationPath, transactionCancellation);
            if (UpdatePolicy.FixedHexEquals(target.Sha256, transaction.Candidate.Sha256) && target.Size == transaction.Candidate.Size)
            {
                if (transaction.NewProcessId is not int pid || !IsProcessRunning(pid))
                {
                    pid = LaunchReplacement(transaction);
                    transaction = transaction with { NewProcessId = pid };
                }
                ForceState(transaction.TransactionId, UpdateTransactionState.AwaitingStartupHealth, "Service resumed an interrupted update health check.", transaction.NewProcessId);
                await AwaitStartupHealthOrRollbackAsync(Load(transaction.TransactionId), transactionCancellation);
            }
            else await RollbackAsync(transaction, "Service recovered an interrupted executable replacement.");
        }
        catch (OperationCanceledException) when (serviceLifetime.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            try
            {
                await RollbackAsync(Load(transaction.TransactionId), "Update cancellation interrupted recovery; previous executable restored.",
                    !cancellationRelaunch.TryGetValue(transaction.TransactionId, out bool relaunch) || relaunch);
            }
            catch (Exception rollback) { ForceState(transaction.TransactionId, UpdateTransactionState.Failed, "Interrupted update cancellation could not roll back safely: " + rollback.Message); }
        }
        catch (Exception ex)
        {
            try { await RollbackAsync(transaction, "Interrupted update recovery failed: " + ex.Message); }
            catch (Exception rollback) { ForceState(transaction.TransactionId, UpdateTransactionState.Failed, ex.Message + " Rollback also failed: " + rollback.Message); }
        }
    }

    private static bool IsOriginalProcessRunning(PrivilegedUpdateTransaction transaction)
    {
        try
        {
            using var process = Process.GetProcessById(transaction.ClientProcessId);
            return process.StartTime.ToUniversalTime().Ticks == transaction.ClientStartTicks;
        }
        catch (ArgumentException) { return false; }
    }

    private static bool IsProcessRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private async Task<ExecutableSnapshot> SnapshotAsync(string path, CancellationToken ct)
    {
        var signature = AuthenticodeVerifier.VerifyPinnedTrusted(path, configuration.PublisherThumbprint);
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new(Path.GetFullPath(path), stream.Length, Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)),
            FileVersionInfo.GetVersionInfo(path).FileVersion, signature.SignerThumbprint);
    }

    private static void RequireCandidate(ExecutableSnapshot actual, ExecutableSnapshot candidate)
    {
        if (actual.Size != candidate.Size || !UpdatePolicy.FixedHexEquals(actual.Sha256, candidate.Sha256) ||
            !UpdatePolicy.FixedHexEquals(actual.SignerThumbprint, candidate.SignerThumbprint))
            throw new InvalidDataException("Staged update does not match the controller executable snapshot.");
    }

    private void ValidateTicket(PrivilegedUpdateTransaction transaction, string ticket)
    {
        if (DateTimeOffset.UtcNow >= transaction.ReconnectExpiresUtc || string.IsNullOrWhiteSpace(ticket) ||
            !Safety.Equal(Safety.Hash(ticket), Safety.Hash(Unprotect(transaction.ProtectedReconnectTicket))))
            throw new UnauthorizedAccessException("Update reconnect ticket is invalid or expired.");
    }

    private static string[] SanitizeArguments(string[] arguments)
    {
        var result = new List<string>();
        for (int index = 0; index < arguments.Length; index++)
        {
            if (arguments[index] is "--resume-update" or "--update-transaction") { index++; continue; }
            if (arguments[index] == "--wait-for-process-exit") { index += 2; continue; }
            result.Add(arguments[index]);
        }
        return result.ToArray();
    }

    private static IReadOnlyList<string> EnsureAgentArgument(IReadOnlyList<string> arguments) =>
        arguments.Contains("--agent", StringComparer.OrdinalIgnoreCase) ? arguments : arguments.Concat(["--agent"]).ToArray();

    private static void ValidateArguments(string[] arguments)
    {
        if (arguments.Length > 128 || arguments.Any(argument => argument.Length > 32767) || arguments.Sum(argument => (long)argument.Length) > 131072)
            throw new ArgumentException("Relaunch arguments exceed update bounds.");
    }

    private static string Protect(string ticket) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(ticket), null, DataProtectionScope.LocalMachine));
    private static string Unprotect(string protectedTicket) => Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedTicket), null, DataProtectionScope.LocalMachine));
    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void StartWorker(string transactionId, Func<CancellationToken, Task> work)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(serviceLifetime);
        if (!workerCancellations.TryAdd(transactionId, cancellation)) { cancellation.Dispose(); return; }
        var worker = Task.Run(() => work(cancellation.Token), CancellationToken.None);
        workers[transactionId] = worker;
        _ = worker.ContinueWith(completed =>
        {
            workers.TryRemove(transactionId, out _);
            if (workerCancellations.TryRemove(transactionId, out var removed)) removed.Dispose();
            cancellationRelaunch.TryRemove(transactionId, out _);
            _ = completed.Exception;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private PrivilegedUpdateTransaction Transition(string transactionId, UpdateTransactionState next, string? error = null)
    {
        lock (stateLock)
        {
            var transaction = LoadUnsafe(transactionId);
            UpdatePolicy.RequireTransition(transaction.State, next);
            transaction = transaction with { State = next, LastError = error, UpdatedUtc = DateTimeOffset.UtcNow };
            SaveUnsafe(transaction);
            return transaction;
        }
    }

    private void ForceState(string transactionId, UpdateTransactionState state, string? error, int? newProcessId = null)
    {
        lock (stateLock)
        {
            var transaction = LoadUnsafe(transactionId);
            SaveUnsafe(transaction with { State = state, LastError = error, NewProcessId = newProcessId ?? transaction.NewProcessId, UpdatedUtc = DateTimeOffset.UtcNow });
        }
    }

    private PrivilegedUpdateTransaction Load(string transactionId)
    {
        if (!Guid.TryParseExact(transactionId, "N", out _)) throw new ArgumentException("Invalid update transaction id.");
        lock (stateLock) return LoadUnsafe(transactionId);
    }

    private static PrivilegedUpdateTransaction LoadUnsafe(string transactionId) =>
        JsonSerializer.Deserialize<PrivilegedUpdateTransaction>(File.ReadAllText(TransactionPath(transactionId)), Json.Options)
        ?? throw new InvalidDataException("Update transaction is empty.");

    private void Save(PrivilegedUpdateTransaction transaction) { lock (stateLock) SaveUnsafe(transaction); }

    private static void SaveUnsafe(PrivilegedUpdateTransaction transaction)
    {
        string path = TransactionPath(transaction.TransactionId), temp = path + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(transaction, Json.Options));
        File.Move(temp, path, true);
    }

    private static string TransactionPath(string transactionId) => Path.Combine(SupportPlatformPaths.TransactionsDirectory, transactionId + ".json");
    private static object Describe(PrivilegedUpdateTransaction transaction) => new
    {
        active = transaction.State is not (UpdateTransactionState.Completed or UpdateTransactionState.Cancelled or UpdateTransactionState.RolledBack or UpdateTransactionState.Failed),
        transactionId = transaction.TransactionId,
        state = transaction.State.ToString(),
        candidate = transaction.Candidate,
        previous = transaction.Previous,
        transaction.PlannedDisconnectDeadlineUtc,
        transaction.ReconnectExpiresUtc,
        transaction.NewProcessId,
        transaction.LastError,
        transaction.UpdatedUtc
    };

    private static void DeleteIfExists(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        foreach (var cancellation in workerCancellations.Values)
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }
}

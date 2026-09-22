using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using RemoteDebugger.Core;

namespace RemoteDebugger;

internal sealed record RestartAuthorization(string TicketHash, string GrantHash, string ControllerHash,
    string BinaryHash, string BootId, DateTimeOffset ExpiresUtc);

/// <summary>A separate, one-boot authorization; update tickets retain their ten-minute limit.</summary>
internal sealed class RestartResumeStore(string root, Func<string>? bootIdentity = null, TimeProvider? clock = null)
{
    private readonly string path = Path.Combine(root, "restart-session.resume");
    private readonly Func<string> boot = bootIdentity ?? WindowsBootIdentity.Read;
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    internal const string RunOnceName = "RemoteDebuggerResumeSupport";
    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";

    public (string Ticket, DateTimeOffset ExpiresUtc) Create(string grant, string controller, string binary)
    {
        if (!PairingExchange.ValidHash(grant) || !PairingExchange.ValidHash(controller) || !PairingExchange.ValidHash(binary))
            throw new ArgumentException("Invalid restart session identity.");
        string ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expires = time.GetUtcNow().AddHours(1);
        Vault.Save(path, JsonSerializer.SerializeToUtf8Bytes(new RestartAuthorization(Safety.Hash(ticket), grant, controller, binary, boot(), expires), Json.Options));
        return (ticket, expires);
    }

    public RestartAuthorization? Restore(string binary)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var saved = JsonSerializer.Deserialize<RestartAuthorization>(Vault.Read(path), Json.Options);
            if (saved == null || saved.ExpiresUtc <= time.GetUtcNow() || saved.ExpiresUtc > time.GetUtcNow().AddHours(1) ||
                saved.BootId == boot() || !Safety.Equal(saved.BinaryHash, binary) ||
                !PairingExchange.ValidHash(saved.GrantHash) || !PairingExchange.ValidHash(saved.ControllerHash)) return null;
            return saved;
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException) { return null; }
    }

    public void RegisterStartup()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunOnceKey, true);
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Application path unavailable.");
        // Paths cannot contain a quote on Windows. No ticket or password enters the command line.
        key.SetValue(RunOnceName, $"\"{executable}\" --startup --resume-restart --data-root \"{Path.GetFullPath(root).TrimEnd('\\')}\"", RegistryValueKind.String);
    }

    public void Clear(bool removeStartup = true)
    {
        File.Delete(path);
        if (removeStartup)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunOnceKey, true);
            key?.DeleteValue(RunOnceName, false);
        }
    }
}

internal static class WindowsBootIdentity
{
    internal static string Read()
    {
        // The boot-environment GUID can survive a restart; use the OS boot time.
        const string query = "(Get-CimInstance Win32_OperatingSystem -Property LastBootUpTime -ErrorAction Stop).LastBootUpTime.ToUniversalTime().Ticks";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            // Restore can run on the UI thread; the command's awaits must not capture it.
            var result = Json.Element(Task.Run(() => Operations.RunAsync(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", query], timeout.Token)).GetAwaiter().GetResult());
            if (result.Int("exitCode", -1) != 0 || !long.TryParse(result.Str("stdout").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
                || ticks <= 0 || ticks > DateTime.MaxValue.Ticks)
                throw new IOException("Windows boot time is unavailable; restart recovery cannot be armed.");
            return ticks.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is OperationCanceledException or Win32Exception)
        { throw new IOException("Windows boot time is unavailable; restart recovery cannot be armed.", ex); }
    }
}

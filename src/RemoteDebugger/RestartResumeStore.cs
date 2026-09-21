using System.Runtime.InteropServices;
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
        int status = NtQuerySystemInformation(90, out var information, Marshal.SizeOf<BootEnvironment>(), out _);
        if (status != 0 || information.Identifier == Guid.Empty)
            throw new InvalidOperationException("Windows boot identity is unavailable; restart recovery cannot be armed.");
        return information.Identifier.ToString("N");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BootEnvironment { public Guid Identifier; public int FirmwareType; public ulong BootFlags; }
    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, out BootEnvironment information, int length, out int returned);
}

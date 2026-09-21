using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace RemoteDebugger.Lab;

// Elevated setup is executed only by GuestSetupV1 in its disposable guest.
// It provisions the real Release and adds narrowly scoped test networking.
internal static class PowerGuestSetup
{
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            string setupRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CodexHarness", "GuestSetup") + Path.DirectorySeparatorChar;
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ||
                !Path.GetFullPath(Environment.ProcessPath!).StartsWith(setupRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Power setup requires the broker's elevated guest staging path.");
            if (args.Length != 4) throw new ArgumentException("Power setup requires the Release path, Lab path and Release SHA-256.");
            string release = Path.GetFullPath(args[1]), lab = Path.GetFullPath(args[2]);
            if (!string.Equals(Hash(release), args[3], StringComparison.Ordinal) || Hash(lab) != Hash(Environment.ProcessPath!))
                throw new InvalidDataException("Power setup payload hashes do not match the declared fixtures.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(270));
            var start = new ProcessStartInfo(release) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add("cli"); start.ArgumentList.Add("platform-provision");
            using var process = Process.Start(start) ?? throw new IOException("The Release provisioner did not start.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            try { await process.WaitForExitAsync(deadline.Token); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            string provisionerOutput = await stdout, provisionerError = await stderr;
            if (process.ExitCode != 0) throw new IOException($"The Release provisioner exited {process.ExitCode}: {provisionerError}");
            var provisioned = JsonSerializer.Deserialize<JsonElement>(provisionerOutput);
            if (!provisioned.GetProperty("ok").GetBoolean()) throw new IOException("The Release provisioner did not confirm success.");
            if (Hash(SupportPlatformPaths.ApplicationExecutable) != args[3] || Hash(SupportPlatformPaths.ServiceExecutable) != args[3])
                throw new IOException("The installed application/service differ from the requested Release.");
            await FirewallManager.EnsureAsync(SupportPlatformPaths.ApplicationExecutable, deadline.Token);
            AddLabCoordinationRule(lab);
            Console.WriteLine(JsonSerializer.Serialize(new { provisioned = true, releaseSha256 = args[3], privateLocalSubnetRules = new[] { "product TCP 45832", "product UDP 45833", "Lab UDP 45835" } }));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    private static void AddLabCoordinationRule(string lab)
    {
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
        try
        {
            rule.Name = "RemoteDebugger-Lab-Power-Private-UDP";
            rule.ApplicationName = lab; rule.Protocol = 17; rule.LocalPorts = "45835";
            rule.Direction = 1; rule.Action = 1; rule.Profiles = 2;
            rule.RemoteAddresses = "LocalSubnet"; rule.Enabled = true;
            policy.Rules.Add(rule);
        }
        finally { Marshal.FinalReleaseComObject(rule); Marshal.FinalReleaseComObject(policy); }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

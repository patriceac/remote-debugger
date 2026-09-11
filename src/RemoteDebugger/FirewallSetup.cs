using System.Text;
using RemoteDebugger.Core;

namespace RemoteDebugger;

public static class FirewallSetup
{
    // Called only by the explicit local GUI button, never by agent startup or a remote RPC.
    public static Task<object> ConfigurePrivateAsync(string root, CancellationToken ct)
    {
        string executable = Environment.ProcessPath!, escaped = executable.Replace("'", "''"), suffix = Safety.Hash(executable)[..12];
        string script = "$ErrorActionPreference='Stop'; " +
            $"Get-NetFirewallRule -Name 'RemoteDebugger-{suffix}-*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule; " +
            $"New-NetFirewallRule -Name 'RemoteDebugger-{suffix}-TCP' -DisplayName 'Remote Debugger - Private TCP' -Direction Inbound -Action Allow -Profile Private -RemoteAddress LocalSubnet -Program '{escaped}' -Protocol TCP -LocalPort 45832 | Out-Null; " +
            $"New-NetFirewallRule -Name 'RemoteDebugger-{suffix}-UDP' -DisplayName 'Remote Debugger - Private UDP' -Direction Inbound -Action Allow -Profile Private -RemoteAddress LocalSubnet -Program '{escaped}' -Protocol UDP -LocalPort 45833 | Out-Null; " +
            "Write-Output 'Private LocalSubnet rules configured.'";
        return ElevatedJob.RunAsync(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell/v1.0/powershell.exe"), ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], root, ct);
    }
}

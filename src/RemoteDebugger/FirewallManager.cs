using System.Text;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

internal static class FirewallManager
{
    private const string TcpName = "RemoteDebugger-Private-TCP";
    private const string UdpName = "RemoteDebugger-Private-UDP";

    public static async Task<bool> IsReadyAsync(string applicationPath, CancellationToken ct)
    {
        string script = BuildScript(applicationPath, ensure: false);
        var result = Json.Element(await RunPowerShellAsync(script, ct));
        if (result.Int("exitCode") != 0) return false;
        try
        {
            var value = JsonSerializer.Deserialize<JsonElement>(result.Str("stdout").Trim(), Json.Options);
            return value.TryGetProperty("ready", out var ready) && ready.GetBoolean();
        }
        catch (JsonException) { return false; }
    }

    public static async Task<object> EnsureAsync(string applicationPath, CancellationToken ct)
    {
        var result = Json.Element(await RunPowerShellAsync(BuildScript(applicationPath, ensure: true), ct));
        if (result.Int("exitCode") != 0)
            throw new InvalidOperationException("Windows Firewall configuration failed: " + result.Str("stderr"));
        var value = JsonSerializer.Deserialize<JsonElement>(result.Str("stdout").Trim(), Json.Options);
        if (!value.TryGetProperty("ready", out var ready) || !ready.GetBoolean())
            throw new InvalidOperationException("Windows Firewall did not confirm the required Private LocalSubnet rules.");
        return new { changed = value.TryGetProperty("changed", out var changed) && changed.GetBoolean(), ready = true, tcpPort = 45832, udpPort = 45833, profile = "Private", remoteAddress = "LocalSubnet" };
    }

    private static string BuildScript(string applicationPath, bool ensure)
    {
        string escaped = applicationPath.Replace("'", "''");
        string ensureLiteral = ensure ? "$true" : "$false";
        return "$ErrorActionPreference='Stop';" +
            $"$program='{escaped}';$ensure={ensureLiteral};$changed=$false;" +
            "function Test-Rule([string]$name,[string]$protocol,[int]$port){" +
            "$rule=Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue;" +
            "if($null -eq $rule -or @($rule).Count -ne 1){return $false};" +
            "if($rule.Enabled -ne 'True' -or $rule.Direction -ne 'Inbound' -or $rule.Action -ne 'Allow' -or $rule.Profile -ne 'Private'){return $false};" +
            "$app=$rule|Get-NetFirewallApplicationFilter;$address=$rule|Get-NetFirewallAddressFilter;$ports=$rule|Get-NetFirewallPortFilter;" +
            "return ($app.Program -eq $program -and $address.RemoteAddress -contains 'LocalSubnet' -and $ports.Protocol -eq $protocol -and [int]$ports.LocalPort -eq $port)};" +
            $"$tcp=Test-Rule '{TcpName}' 'TCP' 45832;$udp=Test-Rule '{UdpName}' 'UDP' 45833;" +
            "if($ensure -and (-not $tcp -or -not $udp)){" +
            $"Remove-NetFirewallRule -Name '{TcpName}','{UdpName}' -ErrorAction SilentlyContinue;" +
            $"New-NetFirewallRule -Name '{TcpName}' -DisplayName 'Remote Debugger - Private TCP' -Direction Inbound -Action Allow -Enabled True -Profile Private -RemoteAddress LocalSubnet -Program $program -Protocol TCP -LocalPort 45832|Out-Null;" +
            $"New-NetFirewallRule -Name '{UdpName}' -DisplayName 'Remote Debugger - Private UDP' -Direction Inbound -Action Allow -Enabled True -Profile Private -RemoteAddress LocalSubnet -Program $program -Protocol UDP -LocalPort 45833|Out-Null;" +
            "$changed=$true;$tcp=Test-Rule 'RemoteDebugger-Private-TCP' 'TCP' 45832;$udp=Test-Rule 'RemoteDebugger-Private-UDP' 'UDP' 45833};" +
            "[pscustomobject]@{ready=($tcp -and $udp);changed=$changed}|ConvertTo-Json -Compress";
    }

    private static Task<object> RunPowerShellAsync(string script, CancellationToken ct) =>
        Operations.RunAsync(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], ct);
}

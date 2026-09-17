param([string]$InstallerPath, [switch]$Demo, [switch]$LeaveRunning, [switch]$Handoff, [switch]$LockedSetup)
$ErrorActionPreference = 'Stop'
if ($LeaveRunning -and -not $Demo) { throw 'LeaveRunning requires Demo.' }
if ($Handoff -and ($Demo -or $LeaveRunning)) { throw 'Handoff cannot be combined with Demo or LeaveRunning.' }
if ($LockedSetup -and ($Demo -or $LeaveRunning -or $Handoff)) { throw 'LockedSetup is a disconnected first-launch check.' }
$projectRoot = Split-Path $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    [xml]$properties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
    $InstallerPath = Join-Path $projectRoot ('artifacts\installer\RemoteDebugger-' + $properties.Project.PropertyGroup.Version + '-Private-Setup.exe')
}
$resolvedInstaller = (Resolve-Path -LiteralPath $InstallerPath).Path
$runner = Join-Path $env:USERPROFILE '.agents\skills\hyperv-test-executables\scripts\Invoke-HyperVExecutableTest.ps1'
$actions = @(
    @{ type = 'wait_result_file'; path = '{OUTDIR}\lab-result.json'; timeoutMs = $(if ($Demo) { 1080000 } else { 300000 }) }
    @{ type = 'screenshot'; name = 'installer-final.png' }
)
if ($LeaveRunning -or $Handoff) { $actions += @{ type = 'wait_process_exit'; timeoutMs = 7200000; expectedExitCode = 0 } }
$request = @{
    ArtifactPath = Join-Path $projectRoot 'artifacts\lab'
    ExecutableRelativePath = 'RemoteDebugger.Lab.exe'
    Arguments = 'internetinstaller "{OUTDIR}" ' + $(if ($Handoff) { 'handoff' } elseif ($LeaveRunning) { 'demo-hold' } elseif ($Demo) { 'demo' } else { 'runtime' }) + ' none "{HOSTINPUT:installer}"'
    ReadOnlyHostInput = @(@{ Name = 'installer'; Path = $resolvedInstaller; Mode = 'Vhdx' })
    AllowNetworkWithHostInputs = -not $LockedSetup
    NetworkProfile = $(if ($LockedSetup) { 'None' } else { 'InternetOnly' })
    ActionsJson = ConvertTo-Json -InputObject $actions -Compress
    AssertResultFile = '{OUTDIR}\lab-result.json'
    AssertResultJsonPointer = '/passed'
    AssertResultEqualsJson = 'true'
    ExecutionTimeoutSeconds = $(if ($LeaveRunning -or $Handoff) { 7200 } elseif ($Demo) { 1200 } else { 360 })
    ThrowOnFailure = $true
}
& $runner @request

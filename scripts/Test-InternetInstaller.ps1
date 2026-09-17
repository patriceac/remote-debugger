param([string]$InstallerPath, [switch]$Demo)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    [xml]$properties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
    $InstallerPath = Join-Path $projectRoot ('artifacts\installer\RemoteDebugger-' + $properties.Project.PropertyGroup.Version + '-Private-Setup.exe')
}
$resolvedInstaller = (Resolve-Path -LiteralPath $InstallerPath).Path
$runner = Join-Path $env:USERPROFILE '.agents\skills\hyperv-test-executables\scripts\Invoke-HyperVExecutableTest.ps1'
$request = @{
    ArtifactPath = Join-Path $projectRoot 'artifacts\lab'
    ExecutableRelativePath = 'RemoteDebugger.Lab.exe'
    Arguments = 'internetinstaller "{OUTDIR}" ' + $(if ($Demo) { 'demo' } else { 'runtime' }) + ' none "{HOSTINPUT:installer}"'
    ReadOnlyHostInput = @(@{ Name = 'installer'; Path = $resolvedInstaller; Mode = 'Vhdx' })
    AllowNetworkWithHostInputs = $true
    NetworkProfile = 'InternetOnly'
    ActionsJson = '[{"type":"wait_result_file","path":"{OUTDIR}\\lab-result.json","timeoutMs":' + $(if ($Demo) { '1080000' } else { '300000' }) + '},{"type":"screenshot","name":"installer-final.png"}]'
    AssertResultFile = '{OUTDIR}\lab-result.json'
    AssertResultJsonPointer = '/passed'
    AssertResultEqualsJson = 'true'
    ExecutionTimeoutSeconds = $(if ($Demo) { 1200 } else { 360 })
    ThrowOnFailure = $true
}
& $runner @request

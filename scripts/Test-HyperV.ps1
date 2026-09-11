param(
    [ValidateSet('Local','Agent','Controller')] [string]$Role = 'Local',
    [string]$Cohort = 'remote-debugger-acceptance'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$runner = Join-Path $env:USERPROFILE '.agents\skills\hyperv-test-executables\scripts\Invoke-HyperVExecutableTest.ps1'
$request = @{
    ArtifactPath = Join-Path $projectRoot 'artifacts'
    ExecutableRelativePath = 'lab\RemoteDebugger.Lab.exe'
    Arguments = $Role.ToLowerInvariant() + ' "{OUTDIR}"'
    ActionsPath = Join-Path $projectRoot 'tests\hyperv-actions.json'
    AssertResultFile = '{OUTDIR}\lab-result.json'
    AssertResultJsonPointer = '/passed'
    AssertResultEqualsJson = 'true'
    ExecutionTimeoutSeconds = 780
    ThrowOnFailure = $true
}
if ($Role -ne 'Local') { $request.NetworkProfile = 'IsolatedTestNet'; $request.NetworkCohort = $Cohort }
& $runner @request
# The PowerShell runner throws on failure and does not populate LASTEXITCODE on
# success. That variable can be unset or left by an unrelated native command.

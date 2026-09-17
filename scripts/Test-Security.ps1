param([string]$FixturePath = (Join-Path (Split-Path $PSScriptRoot) 'artifacts\security-fixture\security-fixture.rdrelay'))
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$fixture = (Resolve-Path -LiteralPath $FixturePath).Path
$runner = Join-Path $env:USERPROFILE '.agents\skills\hyperv-test-executables\scripts\Invoke-HyperVExecutableTest.ps1'
$request = @{
    ArtifactPath = Join-Path $projectRoot 'artifacts\lab'
    ExecutableRelativePath = 'RemoteDebugger.Lab.exe'
    Arguments = 'security "{OUTDIR}" runtime none "{HOSTINPUT:release}\RemoteDebugger.exe" "{HOSTINPUT:fixture}"'
    ReadOnlyHostInput = @(
        @{ Name = 'release'; Path = (Join-Path $projectRoot 'artifacts\release'); Mode = 'Vhdx' },
        @{ Name = 'fixture'; Path = $fixture; Mode = 'Vhdx' }
    )
    AllowNetworkWithHostInputs = $true
    NetworkProfile = 'InternetOnly'
    ActionsJson = '[{"type":"wait_result_file","path":"{OUTDIR}\\lab-result.json","timeoutMs":480000},{"type":"screenshot","name":"security-final.png"}]'
    AssertResultFile = '{OUTDIR}\lab-result.json'
    AssertResultJsonPointer = '/passed'
    AssertResultEqualsJson = 'true'
    ExecutionTimeoutSeconds = 600
    ThrowOnFailure = $true
}
& $runner @request

param([string]$ProfilePath = (Join-Path (Split-Path $PSScriptRoot) 'dist\internet\RemoteDebugger-Internet.rdrelay'))
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$resolvedProfile = (Resolve-Path -LiteralPath $ProfilePath).Path
$runner = Join-Path $env:USERPROFILE '.agents\skills\hyperv-test-executables\scripts\Invoke-HyperVExecutableTest.ps1'
$request = @{
    ArtifactPath = Join-Path $projectRoot 'artifacts\lab'
    ExecutableRelativePath = 'RemoteDebugger.Lab.exe'
    Arguments = 'internet "{OUTDIR}" runtime none "{HOSTINPUT:release}\RemoteDebugger.exe" "{HOSTINPUT:profile}\' + [IO.Path]::GetFileName($resolvedProfile) + '"'
    ReadOnlyHostInput = @(
        @{ Name = 'release'; Path = (Join-Path $projectRoot 'artifacts\release'); Mode = 'Vhdx' },
        @{ Name = 'profile'; Path = (Split-Path $resolvedProfile); Mode = 'Vhdx' }
    )
    AllowNetworkWithHostInputs = $true
    NetworkProfile = 'InternetOnly'
    ActionsJson = '[{"type":"wait_result_file","path":"{OUTDIR}\\lab-result.json","timeoutMs":360000},{"type":"screenshot","name":"internet-final.png"}]'
    AssertResultFile = '{OUTDIR}\lab-result.json'
    AssertResultJsonPointer = '/passed'
    AssertResultEqualsJson = 'true'
    ExecutionTimeoutSeconds = 480
    ThrowOnFailure = $true
}
& $runner @request

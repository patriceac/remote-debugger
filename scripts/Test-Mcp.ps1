param([string]$RunnerPath = (Join-Path $env:USERPROFILE '.agents\skills\hyperv-test-executables\scripts\Invoke-HyperVExecutableTest.ps1'))
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$artifacts = Join-Path $projectRoot 'artifacts'
& $RunnerPath -ArtifactPath (Join-Path $artifacts 'lab') -ExecutableRelativePath 'RemoteDebugger.Lab.exe' `
    -Arguments 'loopbackmcp "{OUTDIR}" runtime none "{HOSTINPUT:release}\RemoteDebugger.exe" "{HOSTINPUT:mcp}"' `
    -ReadOnlyHostInput @(@{ Name = 'release'; Path = (Join-Path $artifacts 'release'); Mode = 'Vhdx' }, @{ Name = 'mcp'; Path = (Join-Path $artifacts 'mcp'); Mode = 'Vhdx' }) `
    -ActionsPath (Join-Path $projectRoot 'tests\hyperv-actions.json') -NetworkProfile None `
    -AssertResultFile '{OUTDIR}\lab-result.json' -AssertResultJsonPointer '/passed' -AssertResultEqualsJson 'true' `
    -ExecutionTimeoutSeconds 600 -ThrowOnFailure

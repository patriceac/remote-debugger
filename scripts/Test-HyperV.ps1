param(
    [ValidateSet('Both', 'Agent', 'Controller', 'Loopback', 'LoopbackLifetime')]
    [string]$Role = 'Both',
    [ValidateSet('Runtime', 'Provisioned', 'Full')]
    [string]$Scope = 'Runtime',
    [ValidateSet('None', 'Upgrade', 'Downgrade', 'SameVersion', 'Rollback')]
    [string]$UpdateVariant = 'None',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$Cohort = 'remote-debugger-acceptance',
    [int]$ExecutionTimeoutSeconds = 1200
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$runner = Join-Path $env:USERPROFILE '.agents\skills\hyperv-test-executables\scripts\Invoke-HyperVExecutableTest.ps1'
$actions = Join-Path $projectRoot 'tests\hyperv-actions.json'
$artifact = Join-Path $projectRoot 'artifacts'

if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) { throw "Hyper-V executable runner not found: $runner" }
if (-not (Test-Path -LiteralPath $actions -PathType Leaf)) { throw "Acceptance actions file not found: $actions" }
if (-not (Test-Path -LiteralPath (Join-Path $artifact 'release\RemoteDebugger.exe') -PathType Leaf)) { throw 'Build the Release artifact before submitting acceptance.' }
if (-not (Test-Path -LiteralPath (Join-Path $artifact 'lab\RemoteDebugger.Lab.exe') -PathType Leaf)) { throw 'Build the Lab artifact with scripts\Build.ps1 -IncludeLab before submitting acceptance.' }
if ($ExecutionTimeoutSeconds -lt 300 -or $ExecutionTimeoutSeconds -gt 1800) { throw 'ExecutionTimeoutSeconds must be between 300 and 1800 seconds.' }
if ($UpdateVariant -ne 'None' -and $Scope -notin @('Provisioned', 'Full')) { throw 'UpdateVariant requires a Provisioned or Full scope.' }
if ($Role -in @('Loopback', 'LoopbackLifetime') -and ($Scope -ne 'Runtime' -or $UpdateVariant -ne 'None')) { throw 'Loopback roles require Runtime scope and None update variant.' }
if ($UpdateVariant -ne 'None') {
    foreach ($fixture in @('older', 'newer', 'same-version')) {
        if (-not (Test-Path -LiteralPath (Join-Path $artifact "update-fixtures\$fixture\RemoteDebugger.exe") -PathType Leaf)) {
            throw "Update fixture is missing: $fixture. Run scripts\Build-UpdateFixtures.ps1 after the Release build."
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $artifact 'update-fixtures\manifest.json') -PathType Leaf)) { throw 'Update fixture manifest is missing.' }
}

function New-RoleRequest([string]$roleName) {
    $request = @{
        ArtifactPath = $artifact
        ExecutableRelativePath = 'lab\RemoteDebugger.Lab.exe'
        Arguments = "$($roleName.ToLowerInvariant()) `"{OUTDIR}`" $($Scope.ToLowerInvariant()) $($UpdateVariant.ToLowerInvariant())"
        ActionsPath = $actions
        AssertResultFile = '{OUTDIR}\lab-result.json'
        AssertResultJsonPointer = '/passed'
        AssertResultEqualsJson = 'true'
        ExecutionTimeoutSeconds = $ExecutionTimeoutSeconds
        ThrowOnFailure = $true
    }
    if ($roleName -notin @('Local', 'Loopback', 'LoopbackLifetime')) {
        $request.NetworkProfile = 'IsolatedTestNet'
        $request.NetworkCohort = $Cohort
    }
    return $request
}

function Invoke-Role([string]$roleName) {
    $request = New-RoleRequest $roleName
    & $runner @request
}

if ($Role -eq 'Both') {
    # Start both broker requests from one host-side coordinator. This starts
    # runner clients only; the product is launched exclusively by the SYSTEM
    # broker inside the two disconnected guest VMs.
    $agentJob = Start-Job -Name 'RemoteDebugger-Lab-Agent' -ScriptBlock {
        param($runnerPath, $request)
        & $runnerPath @request
    } -ArgumentList $runner, (New-RoleRequest 'Agent')
    $controllerJob = Start-Job -Name 'RemoteDebugger-Lab-Controller' -ScriptBlock {
        param($runnerPath, $request)
        & $runnerPath @request
    } -ArgumentList $runner, (New-RoleRequest 'Controller')
    $jobs = @($agentJob, $controllerJob)
    try {
        $jobs | Wait-Job | Out-Null
        $failed = @()
        foreach ($job in $jobs) {
            $jobOutput = @(Receive-Job -Job $job -ErrorAction SilentlyContinue)
            if ($jobOutput.Count -gt 0) { $jobOutput }
            if ($job.State -ne 'Completed') { $failed += "$($job.Name): $($job.State)" }
        }
        if ($failed.Count -gt 0) { throw ('One or more acceptance runner clients failed: ' + ($failed -join '; ')) }
    }
    finally {
        $jobs | Remove-Job -Force -ErrorAction SilentlyContinue
    }
    return
}

Invoke-Role $Role

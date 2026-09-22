param(
    [ValidateSet('Both', 'Agent', 'Controller', 'Input', 'Loopback', 'LoopbackEconomy', 'LoopbackTray', 'LoopbackLifetime', 'LoopbackUi', 'LoopbackExit', 'LoopbackScale', 'LoopbackColumns', 'LoopbackWan', 'LoopbackWake', 'LoopbackVersions', 'LoopbackTransport', 'LoopbackNavigation', 'LoopbackViewer', 'Localization', 'LanguageSelection', 'SingleInstance')]
    [string]$Role = 'Both',
    [ValidateSet('Runtime', 'Provisioned', 'Full')]
    [string]$Scope = 'Runtime',
    [ValidateSet('None', 'Upgrade', 'Downgrade', 'SameVersion', 'Rollback')]
    [string]$UpdateVariant = 'None',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$Cohort = 'remote-debugger-acceptance',
    [int]$ExecutionTimeoutSeconds = 1200,
    [string]$BrokerRoot,
    [string]$RunnerPath
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$defaultRunner = Join-Path $env:USERPROFILE '.agents\skills\hyperv-test-executables\scripts\Invoke-HyperVExecutableTest.ps1'
$runner = if ([string]::IsNullOrWhiteSpace($RunnerPath)) { $defaultRunner } else { (Resolve-Path -LiteralPath $RunnerPath -ErrorAction Stop).Path }
$actions = Join-Path $projectRoot 'tests\hyperv-actions.json'
$artifact = Join-Path $projectRoot 'artifacts'

if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) { throw "Hyper-V executable runner not found: $runner" }
if (-not (Test-Path -LiteralPath $actions -PathType Leaf)) { throw "Acceptance actions file not found: $actions" }
if (-not (Test-Path -LiteralPath (Join-Path $artifact 'release\RemoteDebugger.exe') -PathType Leaf)) { throw 'Build the Release artifact before submitting acceptance.' }
if (-not (Test-Path -LiteralPath (Join-Path $artifact 'lab\RemoteDebugger.Lab.exe') -PathType Leaf)) { throw 'Build the Lab artifact with scripts\Build.ps1 -IncludeLab before submitting acceptance.' }
if ($ExecutionTimeoutSeconds -lt 300 -or $ExecutionTimeoutSeconds -gt 1800) { throw 'ExecutionTimeoutSeconds must be between 300 and 1800 seconds.' }
if ($UpdateVariant -ne 'None' -and $Scope -notin @('Provisioned', 'Full')) { throw 'UpdateVariant requires a Provisioned or Full scope.' }
if ($Role -eq 'Input' -and ($Scope -ne 'Provisioned' -or $UpdateVariant -ne 'None')) { throw 'Input requires Provisioned scope and None update variant.' }
if ($Role -in @('Loopback', 'LoopbackEconomy', 'LoopbackTray', 'LoopbackLifetime', 'LoopbackUi', 'LoopbackExit', 'LoopbackScale', 'LoopbackColumns', 'LoopbackWan', 'LoopbackWake', 'LoopbackVersions', 'LoopbackTransport', 'LoopbackNavigation', 'LoopbackViewer', 'Localization', 'LanguageSelection', 'SingleInstance') -and ($Scope -ne 'Runtime' -or $UpdateVariant -ne 'None')) { throw 'Loopback roles require Runtime scope and None update variant.' }
if ($Scope -in @('Provisioned', 'Full')) {
    $runnerCommand = Get-Command -Name $runner -ErrorAction Stop
    foreach ($parameterName in @('GuestSetupExecutableRelativePath', 'GuestSetupExecutableSha256', 'GuestSetupArguments', 'GuestSetupTimeoutSeconds', 'AcceptWindowsFirewallPrompt', 'WindowsFirewallProfiles', 'SystemPromptTimeoutSeconds')) {
        if (-not $runnerCommand.Parameters.ContainsKey($parameterName)) { throw "Runner does not support -$parameterName; install the generic GuestSetupV1 harness capability: $runner" }
    }
}
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
    if ($roleName -in @('Loopback', 'LoopbackEconomy', 'LoopbackTray', 'LoopbackLifetime', 'LoopbackUi', 'LoopbackExit', 'LoopbackScale', 'LoopbackColumns', 'LoopbackWan', 'LoopbackWake', 'LoopbackVersions', 'LoopbackTransport', 'LoopbackNavigation', 'LoopbackViewer', 'Localization', 'LanguageSelection', 'SingleInstance')) {
        # Runtime checks consume the two canonical build outputs only. Old
        # installers, tampered signing fixtures and update variants are unrelated.
        $request.ArtifactPath = Join-Path $artifact 'lab'
        $request.ExecutableRelativePath = 'RemoteDebugger.Lab.exe'
        $request.ReadOnlyHostInput = @(@{ Name = 'release'; Path = (Join-Path $artifact 'release'); Mode = 'Vhdx' })
        $request.Arguments += ' "{HOSTINPUT:release}\RemoteDebugger.exe"'
    }
    if ($Scope -in @('Provisioned', 'Full')) {
        $setupRelativePath = switch ($UpdateVariant.ToLowerInvariant()) {
            'upgrade' { if ($roleName -eq 'Agent') { 'update-fixtures\older\RemoteDebugger.exe' } else { 'update-fixtures\newer\RemoteDebugger.exe' } }
            'downgrade' { if ($roleName -eq 'Agent') { 'update-fixtures\newer\RemoteDebugger.exe' } else { 'update-fixtures\older\RemoteDebugger.exe' } }
            'sameversion' { if ($roleName -eq 'Agent') { 'update-fixtures\same-version\RemoteDebugger.exe' } else { 'release\RemoteDebugger.exe' } }
            'rollback' { if ($roleName -eq 'Agent') { 'update-fixtures\older\RemoteDebugger.exe' } else { 'update-fixtures\newer\RemoteDebugger.exe' } }
            default { 'release\RemoteDebugger.exe' }
        }
        $setupPath = Join-Path $artifact $setupRelativePath
        if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) { throw "Guest setup fixture is missing: $setupRelativePath" }
        $setupHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToUpperInvariant()
        $request.GuestSetupExecutableRelativePath = $setupRelativePath
        $request.GuestSetupExecutableSha256 = $setupHash
        $request.GuestSetupArguments = @('cli', 'platform-provision')
        $request.GuestSetupTimeoutSeconds = 300
        # Only the Agent Lab opens an unsolicited inbound coordination listener.
        if ($roleName -eq 'Agent') {
            $request.AcceptWindowsFirewallPrompt = $true
            $request.WindowsFirewallProfiles = @('Private')
            $request.SystemPromptTimeoutSeconds = 300
        }
    }
    if ($roleName -notin @('Local', 'Input', 'Loopback', 'LoopbackEconomy', 'LoopbackTray', 'LoopbackLifetime', 'LoopbackUi', 'LoopbackExit', 'LoopbackScale', 'LoopbackColumns', 'LoopbackWan', 'LoopbackWake', 'LoopbackVersions', 'LoopbackTransport', 'LoopbackNavigation', 'LoopbackViewer', 'Localization', 'LanguageSelection', 'SingleInstance')) {
        $request.NetworkProfile = 'IsolatedTestNet'
        $request.NetworkCohort = $Cohort
    }
    if (-not [string]::IsNullOrWhiteSpace($BrokerRoot)) {
        $request.BrokerRoot = $BrokerRoot
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

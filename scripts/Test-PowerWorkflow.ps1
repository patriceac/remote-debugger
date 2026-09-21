param(
    [ValidateSet('Once', 'Cancel', 'Expiry', 'Shutdown')]
    [string]$Scenario = 'Once',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$Cohort = ('power-workflow-' + [guid]::NewGuid().ToString('N').Substring(0, 8)),
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$artifact = Join-Path $projectRoot 'artifacts'
$runner = Join-Path $env:USERPROFILE '.agents\skills\hyperv-test-executables\scripts\Invoke-HyperVExecutableTest.ps1'
$releaseHash = (Get-FileHash -LiteralPath (Join-Path $artifact 'release\RemoteDebugger.exe') -Algorithm SHA256).Hash
$labHash = (Get-FileHash -LiteralPath (Join-Path $artifact 'lab\RemoteDebugger.Lab.exe') -Algorithm SHA256).Hash
$executionSeconds = if ($Scenario -eq 'Expiry') { 7200 } else { 1800 }
$work = Join-Path $projectRoot "work\power-$Cohort"
if (-not (Test-Path -LiteralPath $work)) { New-Item -ItemType Directory -Path $work | Out-Null }

function Get-LabArguments([string]$RoleName) {
    $value = "$RoleName `"{OUTDIR}`" provisioned none `"{PAYLOAD}\release\RemoteDebugger.exe`""
    if ($Scenario -eq 'Once') { $value += ' "{GUEST_CREDENTIAL_FILE}"' }
    return $value
}

$requests = foreach ($roleName in @('poweragent', ('powercontroller-' + $Scenario.ToLowerInvariant()))) {
    $request = @{
        ArtifactPath = $artifact; ExecutableRelativePath = 'lab\RemoteDebugger.Lab.exe'
        Arguments = (Get-LabArguments $roleName)
        AssertResultFile = '{OUTDIR}\lab-result.json'; AssertResultJsonPointer = '/passed'; AssertResultEqualsJson = 'true'
        GuestSetupExecutableRelativePath = 'release\RemoteDebugger.exe'; GuestSetupExecutableSha256 = $releaseHash
        GuestSetupArguments = @('cli', 'platform-provision'); GuestSetupTimeoutSeconds = 300
        NetworkProfile = 'IsolatedTestNet'; NetworkCohort = $Cohort; ExecutionTimeoutSeconds = $executionSeconds; ThrowOnFailure = $true
    }
    if ($Scenario -eq 'Once') { $request.GuestCredentialFixture = $true }
    if ($roleName -eq 'poweragent') {
        $request.GuestSetupExecutableRelativePath = 'lab\RemoteDebugger.Lab.exe'
        $request.GuestSetupExecutableSha256 = $labHash
        $request.GuestSetupArguments = @('powersetup', '{PAYLOAD}\release\RemoteDebugger.exe', '{PAYLOAD}\lab\RemoteDebugger.Lab.exe', $releaseHash)
        if ($Scenario -eq 'Shutdown') {
            $request.ExpectGuestPowerOff = $true
            $request.GuestPowerOffRecoveryTimeoutSeconds = 300
        }
        else {
            $bootCount = if ($Scenario -eq 'Once') { 2 } else { 1 }
            $boots = @(for ($boot = 1; $boot -le $bootCount; $boot++) {
                $automatic = $Scenario -eq 'Once' -and $boot -eq 1
                $observation = if ($automatic) { 0 } elseif ($Scenario -eq 'Expiry') { 3700 } elseif ($Scenario -eq 'Cancel') { 60 } else { 30 }
                $continuationRole = if ($boot -eq 1) { 'powerafterfirst' } else { 'poweraftersecond' }
                @{
                    ExpectedSignIn = $(if ($automatic) { 'Automatic' } else { 'Manual' })
                    BootTimeoutSeconds = 300; SignedOutObservationSeconds = $observation
                    BeforeRestart = @{ ResultFile = "{OUTDIR}\power-before-boot-$boot.json"; JsonPointer = '/passed'; EqualsJson = 'true' }
                    Continuation = @{
                        ExecutableRelativePath = 'lab\RemoteDebugger.Lab.exe'; ExecutableSha256 = $labHash
                        Arguments = (Get-LabArguments $continuationRole); Actions = @()
                    }
                }
            })
            $planPath = Join-Path $work 'restart-plan.json'
            @{ FormatVersion = 1; Boots = $boots } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $planPath -Encoding utf8
            $request.GuestRestartPlanPath = $planPath
        }
    }
    else {
        $request.ActionsJson = @(
            @{ type = 'wait_result_file'; path = '{OUTDIR}\lab-result.json'; timeoutMs = $executionSeconds * 1000 },
            @{ type = 'screenshot'; name = 'final-desktop.png' }
        ) | ConvertTo-Json -Compress
    }
    [pscustomobject]@{ Role = $roleName; Parameters = $request }
}
$requests | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $work 'requests.json') -Encoding utf8
if ($PrepareOnly) { $requests; return }
$runnerCommand = Get-Command -Name $runner
foreach ($parameterName in @('GuestRestartPlanPath', 'GuestCredentialFixture')) {
    if (-not $runnerCommand.Parameters.ContainsKey($parameterName)) { throw "The canonical runner lacks -$parameterName; wait for qualified harness deployment." }
}
$jobs = @()
try {
    foreach ($request in $requests) {
        $jobs += Start-Job -Name $request.Role -ScriptBlock {
            param($runnerPath, $parameters)
            $ErrorActionPreference = 'Stop'
            & $runnerPath @parameters
        } -ArgumentList $runner, $request.Parameters
    }
    $jobs | Wait-Job | Out-Null
    $failed = @()
    foreach ($job in $jobs) {
        Receive-Job -Job $job -ErrorAction Continue
        if ($job.State -ne 'Completed') { $failed += "$($job.Name): $($job.State)" }
    }
    if ($failed.Count) { throw ($failed -join '; ') }
}
finally { $jobs | Where-Object State -In @('Completed', 'Failed', 'Stopped') | Remove-Job -ErrorAction SilentlyContinue }

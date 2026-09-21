param([string]$Cohort = ('support-workflow-' + [guid]::NewGuid().ToString('N').Substring(0, 8)))
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$artifact = Join-Path $projectRoot 'artifacts'
$runner = Join-Path $env:USERPROFILE '.agents\skills\hyperv-test-executables\scripts\Invoke-HyperVExecutableTest.ps1'
$setupHash = (Get-FileHash -LiteralPath (Join-Path $artifact 'release\RemoteDebugger.exe') -Algorithm SHA256).Hash
$jobs = @()
foreach ($roleName in @('workflowagent', 'workflowcontroller')) {
    $request = @{
        ArtifactPath = $artifact; ExecutableRelativePath = 'lab\RemoteDebugger.Lab.exe'
        Arguments = "$roleName `"{OUTDIR}`" provisioned none"
        ActionsPath = (Join-Path $projectRoot 'tests\hyperv-actions.json')
        AssertResultFile = '{OUTDIR}\lab-result.json'; AssertResultJsonPointer = '/passed'; AssertResultEqualsJson = 'true'
        GuestSetupExecutableRelativePath = 'release\RemoteDebugger.exe'; GuestSetupExecutableSha256 = $setupHash
        GuestSetupArguments = @('cli', 'platform-provision'); GuestSetupTimeoutSeconds = 300
        NetworkProfile = 'IsolatedTestNet'; NetworkCohort = $Cohort; ExecutionTimeoutSeconds = 1200; ThrowOnFailure = $true
    }
    if ($roleName -eq 'workflowagent') {
        $request.AcceptWindowsFirewallPrompt = $true
        $request.WindowsFirewallProfiles = @('Private')
        $request.SystemPromptTimeoutSeconds = 300
    }
    $jobs += Start-Job -Name $roleName -ScriptBlock { param($runnerPath, $arguments) & $runnerPath @arguments } -ArgumentList $runner, $request
}
try {
    $jobs | Wait-Job | Out-Null
    foreach ($job in $jobs) { Receive-Job -Job $job; if ($job.State -ne 'Completed') { throw "$($job.Name) failed: $($job.State)" } }
}
finally { $jobs | Remove-Job -ErrorAction SilentlyContinue }

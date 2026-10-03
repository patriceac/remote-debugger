$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../scripts/InstallerArguments.ps1')

if (@(Get-InstallerArguments).Count -ne 0) {
    throw 'The normal installer must keep its interactive prompts.'
}
$silent = @(Get-InstallerArguments -Unattended)
foreach ($required in @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/LOG')) {
    if ($required -notin $silent) { throw "Unattended setup is missing $required." }
}
if ($silent.Count -ne 5) { throw 'Unattended setup changed unrelated installer choices.' }
[pscustomobject]@{ Passed = 2; Failed = 0 } | ConvertTo-Json

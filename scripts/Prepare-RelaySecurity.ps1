#Requires -Version 7.0
param([string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'RemoteDebugger'))
$ErrorActionPreference = 'Stop'
$profilePath = Join-Path $DataRoot 'internet.dpapi'
$preparationPath = Join-Path $DataRoot 'security-relay-prepared.dpapi'
$scope = [Security.Cryptography.DataProtectionScope]::CurrentUser
$current = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($profilePath), $null, $scope)) | ConvertFrom-Json
if ($current.securityId -or (Test-Path -LiteralPath (Join-Path $DataRoot 'security-migration.dpapi'))) {
    throw 'Security migration already exists. Resume it instead of replacing its relay credential.'
}
if (Test-Path -LiteralPath $preparationPath) {
    $prepared = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($preparationPath), $null, $scope)) | ConvertFrom-Json
    if ($prepared.relayUrl -ne $current.relayUrl) { throw 'Saved preparation belongs to another relay.' }
} else {
    $prepared = @{ relayUrl = $current.relayUrl; accessKey = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) }
    [IO.File]::WriteAllBytes($preparationPath, [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes(($prepared | ConvertTo-Json -Compress)), $null, $scope))
}
Push-Location (Join-Path (Split-Path $PSScriptRoot) 'relay')
try {
    # Wrangler must already be authenticated as the relay administrator. The
    # secret travels only through stdin, never arguments or plaintext files.
    $prepared.accessKey | & npx --no-install wrangler secret put PROTECTED_ACCESS_KEY
    if ($LASTEXITCODE -ne 0) { throw 'Relay preparation failed. The saved credential is retained for a safe retry.' }
} finally { Pop-Location; $prepared = $null }
Write-Output 'Relay prepared. Open Remote Debugger > Security to choose your passphrase.'

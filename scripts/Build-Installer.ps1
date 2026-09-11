#Requires -Version 7.0
param(
    [string]$CompilerPath,
    [switch]$Sign
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$releaseExecutable = Join-Path $projectRoot 'artifacts\release\RemoteDebugger.exe'
$definition = Join-Path $projectRoot 'installer\RemoteDebugger.iss'
$outputDirectory = Join-Path $projectRoot 'artifacts\installer'

if (!(Test-Path -LiteralPath $releaseExecutable -PathType Leaf)) {
    throw 'Build the signed Release executable before building the installer.'
}
$payloadSignature = Get-AuthenticodeSignature -LiteralPath $releaseExecutable
if ($null -eq $payloadSignature.SignerCertificate -or $payloadSignature.Status -eq 'HashMismatch') {
    throw 'The Release executable must have a valid Authenticode signature before it can be packaged.'
}

if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) { $CompilerPath = $command.Source }
}
if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $installed = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
    if (Test-Path -LiteralPath $installed -PathType Leaf) { $CompilerPath = $installed }
}
if ([string]::IsNullOrWhiteSpace($CompilerPath) -or !(Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw 'Inno Setup compiler not found. Install Inno Setup or pass -CompilerPath.'
}

[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$version = [string]$buildProperties.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw 'Directory.Build.props does not define Version.' }

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
& $CompilerPath ('/DAppVersion=' + $version) $definition
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

$installer = Join-Path $outputDirectory ('RemoteDebugger-' + $version + '-Setup.exe')
if (!(Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'The installer compiler did not produce the expected file.' }
if ($Sign) { & (Join-Path $PSScriptRoot 'Sign-Release.ps1') -ExecutablePath $installer }

$signature = Get-AuthenticodeSignature -LiteralPath $installer
[pscustomobject]@{
    Installer = $installer
    Bytes = (Get-Item -LiteralPath $installer).Length
    SHA256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
    SignatureStatus = $signature.Status.ToString()
    Signed = $null -ne $signature.SignerCertificate -and $signature.Status -ne 'HashMismatch'
} | ConvertTo-Json

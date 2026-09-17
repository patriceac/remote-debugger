#Requires -Version 7.0
param(
    [string]$CompilerPath,
    [switch]$Sign,
    [string]$InternetProfilePath = (Join-Path (Split-Path $PSScriptRoot) 'dist\internet\RemoteDebugger-Internet.rdrelay'),
    [switch]$WithoutInternetProfile
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

$compilerArguments = @('/DAppVersion=' + $version)
if (-not $WithoutInternetProfile) {
    $profileFile = Get-Item -LiteralPath $InternetProfilePath -ErrorAction Stop
    if ($profileFile.PSIsContainer -or $profileFile.Length -gt 4096) { throw 'Invalid internet setup file.' }
    try { $profile = Get-Content -LiteralPath $profileFile.FullName -Raw | ConvertFrom-Json -ErrorAction Stop }
    catch { throw 'Invalid internet setup JSON.' }
    $relayUri = $null
    if (-not [Uri]::TryCreate([string]$profile.relayUrl, [UriKind]::Absolute, [ref]$relayUri) -or
        $relayUri.Scheme -ne 'https' -or $relayUri.AbsolutePath -ne '/' -or
        $relayUri.UserInfo.Length -ne 0 -or $relayUri.Query.Length -ne 0 -or $relayUri.Fragment.Length -ne 0 -or
        [string]$profile.accessKey -cnotmatch '^[A-Fa-f0-9]{64}$' -or
        [string]$profile.pairingKey -cnotmatch '^[A-Fa-f0-9]{64}$') { throw 'Invalid private internet setup settings.' }
    # Pass the local filename to the compiler, never the credential itself.
    $compilerArguments += '/DRelayProfilePath=' + $profileFile.FullName
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
& $CompilerPath @compilerArguments $definition
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

$suffix = if ($WithoutInternetProfile) { '-Setup.exe' } else { '-Private-Setup.exe' }
$installer = Join-Path $outputDirectory ('RemoteDebugger-' + $version + $suffix)
if (!(Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'The installer compiler did not produce the expected file.' }
if ($Sign) { & (Join-Path $PSScriptRoot 'Sign-Release.ps1') -ExecutablePath $installer }

$signature = Get-AuthenticodeSignature -LiteralPath $installer
[pscustomobject]@{
    Installer = $installer
    Bytes = (Get-Item -LiteralPath $installer).Length
    SHA256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
    SignatureStatus = $signature.Status.ToString()
    Signed = $null -ne $signature.SignerCertificate -and $signature.Status -ne 'HashMismatch'
    InternetPreconfigured = -not $WithoutInternetProfile
} | ConvertTo-Json

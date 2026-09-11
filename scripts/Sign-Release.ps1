#Requires -Version 7.0
param(
    [string]$ExecutablePath = (Join-Path (Split-Path $PSScriptRoot) 'artifacts\release\RemoteDebugger.exe'),
    [string]$SigningDirectory = (Join-Path $env:LOCALAPPDATA 'RemoteDebugger-build\signing')
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) { throw 'Build the Release executable before signing.' }
$keyPath = Join-Path $SigningDirectory 'publisher.pfx'
$passwordPath = Join-Path $SigningDirectory 'publisher.password.dpapi'
if (!(Test-Path -LiteralPath $keyPath) -or !(Test-Path -LiteralPath $passwordPath)) { throw 'Initialize the local signing identity with scripts/Initialize-Signing.ps1 first.' }
$plain = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($passwordPath), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
$certificate = $null
try {
    $password = [Text.Encoding]::UTF8.GetString($plain)
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($keyPath, $password, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet)
    $result = Set-AuthenticodeSignature -LiteralPath $ExecutablePath -Certificate $certificate -HashAlgorithm SHA256
    # A local publisher is enrolled once on the receiving PC. Signing does not
    # install a host trust root and is not a claim of public CA trust.
    if ($null -eq $result.SignerCertificate -or $result.SignerCertificate.Thumbprint -ne $certificate.Thumbprint -or $result.Status -eq 'HashMismatch') {
        throw ('Release signing failed: ' + $result.StatusMessage)
    }
    $destination = Join-Path (Split-Path $ExecutablePath) 'RemoteDebugger.publisher.cer'
    [IO.File]::WriteAllBytes($destination, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    [pscustomobject]@{ Executable = $ExecutablePath; Sha256 = (Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash; PublisherSha256 = $certificate.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256); TrustStatus = $result.Status.ToString(); PublicCertificate = $destination }
} finally {
    [Security.Cryptography.CryptographicOperations]::ZeroMemory($plain)
    $password = $null
    if ($null -ne $certificate) { $certificate.Dispose() }
}

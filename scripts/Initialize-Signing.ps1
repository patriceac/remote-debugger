#Requires -Version 7.0
param([string]$SigningDirectory = (Join-Path $env:LOCALAPPDATA 'RemoteDebugger-build\signing'))
$ErrorActionPreference = 'Stop'
$keyPath = Join-Path $SigningDirectory 'publisher.pfx'
$passwordPath = Join-Path $SigningDirectory 'publisher.password.dpapi'
$publicPath = Join-Path $SigningDirectory 'publisher.cer'
if ((Test-Path -LiteralPath $keyPath) -or (Test-Path -LiteralPath $passwordPath)) {
    if (!(Test-Path -LiteralPath $keyPath) -or !(Test-Path -LiteralPath $passwordPath) -or !(Test-Path -LiteralPath $publicPath)) {
        throw 'The local signing identity is incomplete. Restore its original files; do not silently rotate the enrolled publisher.'
    }
    [pscustomobject]@{ Existing = $true; PublicCertificate = $publicPath; SigningDirectory = $SigningDirectory }
    return
}
New-Item -ItemType Directory -Path $SigningDirectory -Force | Out-Null
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
$rule = [Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
$acl.AddAccessRule($rule)
Set-Acl -LiteralPath $SigningDirectory -AclObject $acl
$rsa = [Security.Cryptography.RSA]::Create(3072)
try {
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=Remote Debugger Local Publisher', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
    $purposes = [Security.Cryptography.OidCollection]::new()
    $purposes.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3')) | Out-Null
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($purposes, $true))
    $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddYears(3))
    try {
        $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        [IO.File]::WriteAllBytes($keyPath, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password))
        [IO.File]::WriteAllBytes($passwordPath, [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($password), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
        [IO.File]::WriteAllBytes($publicPath, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        [pscustomobject]@{ Existing = $false; PublicCertificate = $publicPath; PublisherSha256 = $certificate.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256); SigningDirectory = $SigningDirectory }
    } finally { $certificate.Dispose(); $password = $null }
} finally { $rsa.Dispose() }

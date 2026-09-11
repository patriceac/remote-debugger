$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$executable = Join-Path $projectRoot 'artifacts\release\RemoteDebugger.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Build the Release first.' }
$distribution = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Path $distribution -Force | Out-Null
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable).FileVersion
$packagePath = Join-Path $distribution ('RemoteDebugger-' + $version + '-win-x64.zip')
$packageStream = [IO.File]::Open($packagePath, [IO.FileMode]::Create)
$zip = [IO.Compression.ZipArchive]::new($packageStream, [IO.Compression.ZipArchiveMode]::Create)
try {
    $files = @([pscustomobject]@{ Path = $executable; Entry = 'RemoteDebugger.exe' })
    $publicCertificate = Join-Path (Split-Path $executable) 'RemoteDebugger.publisher.cer'
    if (Test-Path -LiteralPath $publicCertificate) { $files += [pscustomobject]@{ Path = $publicCertificate; Entry = 'RemoteDebugger.publisher.cer' } }
    $files += [pscustomobject]@{ Path = (Join-Path $projectRoot 'README.md'); Entry = 'README.md' }
    $documentationRoot = Join-Path $projectRoot 'docs'
    foreach ($file in Get-ChildItem -LiteralPath $documentationRoot -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($documentationRoot, $file.FullName).Replace('\', '/')
        $files += [pscustomobject]@{ Path = $file.FullName; Entry = 'docs/' + $relative }
    }
    foreach ($file in $files) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.Path, $file.Entry, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    $entry = $zip.CreateEntry('SHA256.txt'); $writer = [IO.StreamWriter]::new($entry.Open())
    try { $writer.WriteLine((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash + '  RemoteDebugger.exe') } finally { $writer.Dispose() }
}
finally { $zip.Dispose(); $packageStream.Dispose() }
[pscustomobject]@{ Package = $packagePath; Bytes = (Get-Item -LiteralPath $packagePath).Length; SHA256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash } | ConvertTo-Json

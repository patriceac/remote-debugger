$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$executable = Join-Path $projectRoot 'artifacts\release\RemoteDebugger.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Build the Release first.' }
$distribution = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Path $distribution -Force | Out-Null
$packagePath = Join-Path $distribution 'RemoteDebugger-0.1.0-win-x64.zip'
$packageStream = [IO.File]::Open($packagePath, [IO.FileMode]::Create)
$zip = [IO.Compression.ZipArchive]::new($packageStream, [IO.Compression.ZipArchiveMode]::Create)
try {
    $files = @([pscustomobject]@{ Path = $executable; Entry = 'RemoteDebugger.exe' })
    $files += [pscustomobject]@{ Path = (Join-Path $projectRoot 'README.md'); Entry = 'README.md' }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'docs') -File) {
        $files += [pscustomobject]@{ Path = $file.FullName; Entry = 'docs/' + $file.Name }
    }
    $evidencePath = Join-Path $projectRoot 'evidence'
    if (Test-Path -LiteralPath $evidencePath) {
        foreach ($file in Get-ChildItem -LiteralPath $evidencePath -File) {
            $files += [pscustomobject]@{ Path = $file.FullName; Entry = 'evidence/' + $file.Name }
        }
    }
    foreach ($file in $files) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.Path, $file.Entry, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    $entry = $zip.CreateEntry('SHA256.txt'); $writer = [IO.StreamWriter]::new($entry.Open())
    try { $writer.WriteLine((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash + '  RemoteDebugger.exe') } finally { $writer.Dispose() }
}
finally { $zip.Dispose(); $packageStream.Dispose() }
[pscustomobject]@{ Package = $packagePath; Bytes = (Get-Item -LiteralPath $packagePath).Length; SHA256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash } | ConvertTo-Json

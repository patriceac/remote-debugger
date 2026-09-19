param([switch]$IncludeLab, [switch]$Sign, [string]$Version)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$artifactRoot = Join-Path $projectRoot 'artifacts'
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$buildProperties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
    $Version = [string]$buildProperties.Project.PropertyGroup.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must use MAJOR.MINOR.PATCH format: $Version" }
& dotnet publish (Join-Path $projectRoot 'src\RemoteDebugger\RemoteDebugger.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none ('-p:Version=' + $Version) -o (Join-Path $artifactRoot 'release')
if ($LASTEXITCODE -ne 0) { throw 'Release publish failed.' }
if ($Sign) { & (Join-Path $PSScriptRoot 'Sign-Release.ps1') }
$sourceCommit = & git -C $projectRoot rev-parse HEAD
$sourceChanges = & git -C $projectRoot status --porcelain
[ordered]@{
    version = $Version
    sourceCommit = $sourceCommit
    sourceDirty = [bool]$sourceChanges
    executableSha256 = (Get-FileHash -LiteralPath (Join-Path $artifactRoot 'release\RemoteDebugger.exe') -Algorithm SHA256).Hash
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifactRoot 'release\build-info.json') -Encoding utf8
if ($IncludeLab) {
    $fixtureRoot = Join-Path $artifactRoot 'lab\fixtures'
    New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'v1'),(Join-Path $fixtureRoot 'v2') -Force | Out-Null
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $source = Join-Path $projectRoot 'tests\Fixture\Fixture.cs'
    & $compiler /nologo /target:winexe /optimize+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ('/out:' + (Join-Path $fixtureRoot 'v1\Fixture.exe')) $source
    if ($LASTEXITCODE -ne 0) { throw 'Fixture v1 build failed.' }
    & $compiler /nologo /target:winexe /optimize+ /define:VERSION2 /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ('/out:' + (Join-Path $fixtureRoot 'v2\Fixture.exe')) $source
    if ($LASTEXITCODE -ne 0) { throw 'Fixture v2 build failed.' }
    & dotnet publish (Join-Path $projectRoot 'tests\RemoteDebugger.Lab\RemoteDebugger.Lab.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o (Join-Path $artifactRoot 'lab')
    if ($LASTEXITCODE -ne 0) { throw 'Lab publish failed.' }
}

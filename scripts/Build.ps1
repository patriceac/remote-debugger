param([switch]$IncludeLab, [switch]$Sign, [string]$Version = '0.4.1')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$artifactRoot = Join-Path $projectRoot 'artifacts'
& dotnet publish (Join-Path $projectRoot 'src\RemoteDebugger\RemoteDebugger.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none ('-p:Version=' + $Version) -o (Join-Path $artifactRoot 'release')
if ($LASTEXITCODE -ne 0) { throw 'Release publish failed.' }
if ($Sign) { & (Join-Path $PSScriptRoot 'Sign-Release.ps1') }
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

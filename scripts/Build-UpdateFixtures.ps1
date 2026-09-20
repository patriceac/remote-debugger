#Requires -Version 7.0
# Build real signed application variants for the isolated update acceptance lab.
# The disposable authority is compile-time only; the real update protocol is unchanged.
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$source = Join-Path $projectRoot 'src\RemoteDebugger\RemoteDebugger.csproj'
$fixtures = @(
    @{ Name = 'older'; Version = '0.1.9'; InformationalVersion = '0.1.9+update-acceptance' },
    @{ Name = 'newer'; Version = '0.2.1'; InformationalVersion = '0.2.1+update-acceptance' },
    @{ Name = 'same-version'; Version = '0.2.0'; InformationalVersion = '0.2.0+different-build-acceptance' }
)
$manifest = @()
foreach ($fixture in $fixtures) {
    $destination = Join-Path $projectRoot ('artifacts\update-fixtures\' + $fixture.Name)
    & dotnet publish $source -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DefineConstants=REMOTEDEBUGGER_UPDATE_ACCEPTANCE ('-p:Version=' + $fixture.Version) ('-p:InformationalVersion=' + $fixture.InformationalVersion) -o $destination
    if ($LASTEXITCODE -ne 0) { throw ('Update fixture publish failed: ' + $fixture.Name) }
    $executable = Join-Path $destination 'RemoteDebugger.exe'
    $signed = & (Join-Path $PSScriptRoot 'Sign-Release.ps1') -ExecutablePath $executable
    $manifest += [pscustomobject]@{ name = $fixture.Name; version = $fixture.Version; relativePath = $fixture.Name + '/RemoteDebugger.exe'; sha256 = $signed.Sha256; publisherSha256 = $signed.PublisherSha256; testOnlyUpdateAuthority = $true }
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $projectRoot 'artifacts\update-fixtures\manifest.json') -Encoding utf8

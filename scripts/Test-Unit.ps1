$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
& (Join-Path $projectRoot 'tests\InstallerArguments.Tests.ps1')
& dotnet test (Join-Path $projectRoot 'tests\RemoteDebugger.Core.Tests\RemoteDebugger.Core.Tests.csproj') -c Release --logger 'trx;LogFileName=core.trx'
if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
& dotnet test (Join-Path $projectRoot 'tests\RemoteDebugger.Platform.Tests\RemoteDebugger.Platform.Tests.csproj') -c Release --logger 'trx;LogFileName=platform.trx'
if ($LASTEXITCODE -ne 0) { throw 'Platform unit tests failed.' }

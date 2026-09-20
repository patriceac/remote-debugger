param([string]$NodePath = (Get-Command node.exe -ErrorAction Stop).Source)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$source = Join-Path $projectRoot 'mcp'
$package = Join-Path $projectRoot 'artifacts\mcp'
$app = Join-Path $package 'app'
Push-Location $source
try {
    & npm.cmd ci --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'MCP dependency installation failed.' }
} finally { Pop-Location }
New-Item -ItemType Directory -Path $app,(Join-Path $app 'test') -Force | Out-Null
Copy-Item -LiteralPath $NodePath -Destination (Join-Path $package 'node.exe') -Force
foreach ($name in @('package.json','package-lock.json','server.mjs','tools.mjs','cli.mjs')) {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination $app -Force
}
Copy-Item -LiteralPath (Join-Path $source 'node_modules') -Destination $app -Recurse -Force
Copy-Item -LiteralPath (Join-Path $source 'test\probe.mjs') -Destination (Join-Path $app 'test\probe.mjs') -Force
[pscustomobject]@{ Package = $package; Server = (Join-Path $app 'server.mjs'); NodeSha256 = (Get-FileHash -LiteralPath (Join-Path $package 'node.exe')).Hash }

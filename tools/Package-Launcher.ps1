[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$publishPath = Join-Path $workspaceRoot 'publish'
$launcherPath = Join-Path $publishPath 'DestinyLauncher.exe'
$packagePath = Join-Path $publishPath 'DestinyLauncher-win-x64.zip'

if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
    throw "Primero publica el launcher: no se encontró $launcherPath"
}

Compress-Archive -LiteralPath $launcherPath -DestinationPath $packagePath -Force
$package = Get-Item -LiteralPath $packagePath
$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash

[pscustomobject]@{
    Package = $package.FullName
    Bytes   = $package.Length
    SHA256  = $hash
}

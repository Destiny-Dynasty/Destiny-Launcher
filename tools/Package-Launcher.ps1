[CmdletBinding()]
param([string]$PublishPath)

$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$publishPath = if ($PublishPath) { [IO.Path]::GetFullPath($PublishPath) } else { Join-Path $workspaceRoot 'publish' }
$launcherPath = Join-Path $publishPath 'DestinyLauncher.exe'
$packagePath = Join-Path $publishPath 'DestinyLauncher-win-x64.zip'

if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
    throw "Primero publica el launcher: no se encontró $launcherPath"
}

$contents = @($launcherPath)
foreach ($name in @('LICENSE', 'THIRD_PARTY_NOTICES.md')) {
    $destination = Join-Path $publishPath $name
    Copy-Item -LiteralPath (Join-Path $workspaceRoot $name) -Destination $destination -Force
    $contents += $destination
}
Compress-Archive -LiteralPath $contents -DestinationPath $packagePath -Force
$package = Get-Item -LiteralPath $packagePath
$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash

[pscustomobject]@{
    Package = $package.FullName
    Bytes   = $package.Length
    SHA256  = $hash
}

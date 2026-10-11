[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $workspaceRoot 'DestinyLauncher\DestinyLauncher.csproj'
$outputPath = Join-Path $workspaceRoot 'publish-experiment'

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "No se encontró el proyecto: $projectPath"
}

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$publishArgs = @(
    'publish', $projectPath,
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--output', $outputPath,
    '-p:PublishSingleFile=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false'
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Output "Launcher experimental actualizado en $outputPath\DestinyLauncher.exe"

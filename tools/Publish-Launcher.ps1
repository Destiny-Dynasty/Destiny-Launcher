[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $workspaceRoot 'DestinyLauncher\DestinyLauncher.csproj'
$outputPath = Join-Path $workspaceRoot 'publish'
$localSdkPath = Join-Path $env:LOCALAPPDATA 'Programs\dotnet-sdk-10.0.401\dotnet.exe'

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "No se encontró el proyecto: $projectPath"
}

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$dotnetCommand = if (Test-Path -LiteralPath $localSdkPath) {
    $localSdkPath
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}

$sdkVersion = & $dotnetCommand --version
if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.') {
    throw "Destiny Launcher requiere el SDK de .NET 10. SDK detectado: $sdkVersion"
}

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

& $dotnetCommand @publishArgs
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Output "Launcher actualizado en $outputPath\DestinyLauncher.exe"

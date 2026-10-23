[CmdletBinding()]
param([switch]$Experiment, [string]$OutputPath, [string]$Version)

$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $workspaceRoot 'DestinyLauncher\DestinyLauncher.csproj'
$outputFolder = if ($Experiment) { 'publish-experiment' } else { 'publish' }
$outputPath = if ($OutputPath) { [IO.Path]::GetFullPath($OutputPath) } else { Join-Path $workspaceRoot $outputFolder }
$localSdkPath = Join-Path $env:LOCALAPPDATA 'Programs\dotnet-sdk-10.0.401\dotnet.exe'

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "No se encontró el proyecto: $projectPath"
}

if ((Test-Path -LiteralPath $outputPath) -and
    ((Get-Item -LiteralPath $outputPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw "La salida no puede ser un enlace o una unión de directorios: $outputPath"
}
$runningOutput = Get-Process DestinyLauncher -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq (Join-Path $outputPath 'DestinyLauncher.exe') }
if ($runningOutput) { throw "Cierra el launcher de $outputFolder antes de volver a publicarlo." }

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

if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version debe tener formato X.Y.Z.' }
    $publishArgs += @("-p:Version=$Version", "-p:AssemblyVersion=$Version.0", "-p:FileVersion=$Version.0")
}
& $dotnetCommand @publishArgs
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Output "Launcher actualizado en $outputPath\DestinyLauncher.exe"

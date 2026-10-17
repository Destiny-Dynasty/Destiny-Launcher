[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $workspaceRoot 'DestinyLauncher\DestinyLauncher.csproj'
$outputPath = Join-Path $workspaceRoot 'publish-experiment-2'
$localSdkPath = Join-Path $env:LOCALAPPDATA 'Programs\dotnet-sdk-10.0.401\dotnet.exe'
$protectedPath = Join-Path $workspaceRoot 'publish-experiment'

function Get-ProtectedFingerprint {
    if (-not (Test-Path -LiteralPath $protectedPath)) { return '' }
    $items = @(Get-ChildItem -LiteralPath $protectedPath -File -Force -Recurse | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{ Path = $_.FullName; Length = $_.Length; Modified = $_.LastWriteTimeUtc.ToString('O'); Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    return ConvertTo-Json -InputObject $items -Compress
}

# This script has one fixed output. A junction must not redirect it into an older distribution.
if ((Test-Path -LiteralPath $outputPath) -and ((Get-Item -LiteralPath $outputPath).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'La salida experimental no puede ser un enlace ni una unión de directorios.'
}
$protectedBefore = Get-ProtectedFingerprint

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
$publishExitCode = $LASTEXITCODE
if ((Get-ProtectedFingerprint) -cne $protectedBefore) { throw 'La distribución original cambió durante la publicación. Revisa publish-experiment.' }
if ($publishExitCode -ne 0) { exit $publishExitCode }

Write-Output "Launcher experimental actualizado en $outputPath\DestinyLauncher.exe"

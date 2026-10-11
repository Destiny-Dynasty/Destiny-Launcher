[CmdletBinding()]
param(
    [string]$CompilerPath
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$installerScript = Join-Path $workspaceRoot 'installer\DestinyLauncherSetup.iss'
$launcherPath = Join-Path $workspaceRoot 'publish\DestinyLauncher.exe'

if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
    throw "Primero publica el launcher: no se encontró $launcherPath"
}

if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $compilerCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($compilerCommand) {
        $CompilerPath = $compilerCommand.Source
    } else {
        $candidates = @(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
        )
        $CompilerPath = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
}

if ([string]::IsNullOrWhiteSpace($CompilerPath) -or -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw 'No se encontró ISCC.exe. Instala Inno Setup 6.5+ o pasa -CompilerPath con su ruta.'
}

& (Join-Path $PSScriptRoot 'Package-Launcher.ps1') | Out-Null
$payloadSize = (Get-Item -LiteralPath $launcherPath).Length
& $CompilerPath "/dPayloadSize=$payloadSize" $installerScript
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Output "Instalador online creado en $workspaceRoot\publish\DestinyLauncherSetup.exe"

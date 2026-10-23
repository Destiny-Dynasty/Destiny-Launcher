[CmdletBinding()]
param(
    [string]$CompilerPath, [string]$PayloadUrl, [string]$PublishPath, [string]$OutputPath,
    [switch]$Offline, [switch]$Experimental, [string]$DataRoot,
    [switch]$SignedUninstaller, [string]$SignedUninstallerDir, [string]$SignToolCommand,
    [switch]$PrepareSigningTemplates, [string]$BackgroundPath, [switch]$UiAudit,
    [switch]$LegacyButtonPaint
)
$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$publishPath = if ($PublishPath) { [IO.Path]::GetFullPath($PublishPath) } else { Join-Path $workspaceRoot 'publish' }
$outputPath = if ($OutputPath) { [IO.Path]::GetFullPath($OutputPath) } else { $publishPath }
$launcherPath = Join-Path $publishPath 'DestinyLauncher.exe'
if (-not(Test-Path -LiteralPath $launcherPath)) { throw 'Primero publica el launcher.' }
$version = (Get-Item -LiteralPath $launcherPath).VersionInfo.FileVersion
if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'El launcher no contiene una versión válida.' }
$releaseVersion = ([version]$version).ToString(3)
if (-not $CompilerPath) {
    $candidates = @((Join-Path $workspaceRoot 'artifacts\tooling\InnoSetup\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'))
    $CompilerPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $CompilerPath) { throw 'No se encontró el compilador gratuito Inno Setup 6.5+.' }
if ($DataRoot -and -not $Experimental) { throw 'Una raíz de datos personalizada solo está permitida en el instalador experimental.' }
if ($Experimental -and -not $DataRoot) { $DataRoot = Join-Path $workspaceRoot 'artifacts\installed-experiment\data' }
if ($DataRoot) { $DataRoot = [IO.Path]::GetFullPath($DataRoot) }
if ($LegacyButtonPaint -and -not $UiAudit) { throw 'El pintado anterior solo se permite en una prueba aislada -UiAudit.' }
if ($UiAudit) {
    $auditBoundary = [IO.Path]::GetFullPath((Join-Path $workspaceRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
    if (-not $Experimental -or -not $Offline -or -not $outputPath.StartsWith($auditBoundary,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'La prueba de interfaz requiere -Experimental -Offline y una salida dentro de artifacts.'
    }
    $DataRoot = Join-Path $outputPath 'data'
}
if (-not $BackgroundPath) {
    $customBackground = Join-Path $workspaceRoot 'installer\Assets\background.png'
    $BackgroundPath = if (Test-Path -LiteralPath $customBackground) { $customBackground } else { Join-Path $workspaceRoot 'DestinyLauncher\Assets\destiny-hero-gold-v3.png' }
}
$artwork = Join-Path $outputPath 'artwork\setup-background.png'
$logo = Join-Path $workspaceRoot 'DestinyLauncher\Assets\experiment\obsidian-gold-v1\Combination-Mark-Destiny.png'
& (Join-Path $PSScriptRoot 'Prepare-InstallerArtwork.ps1') -BackgroundPath $BackgroundPath -OutputPath $artwork -LogoPath $logo | Out-Null
& (Join-Path $PSScriptRoot 'Package-Launcher.ps1') -PublishPath $publishPath | Out-Null
$zipPath = Join-Path $publishPath 'DestinyLauncher-win-x64.zip'
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$size = @($launcherPath, (Join-Path $publishPath 'LICENSE'), (Join-Path $publishPath 'THIRD_PARTY_NOTICES.md')) | ForEach-Object { (Get-Item -LiteralPath $_).Length } | Measure-Object -Sum
$requiredMB = [Math]::Ceiling(($size.Sum + $(if ($Offline) { 0 } else { (Get-Item -LiteralPath $zipPath).Length })) / 1MB) + 25
if (-not $PayloadUrl) { $PayloadUrl = "https://github.com/Destiny-Dynasty/Destiny-Launcher/releases/download/v$releaseVersion/DestinyLauncher-win-x64.zip" }
$uri = [uri]$PayloadUrl
if ($uri.Scheme -ne 'https' -and -not($Experimental -and $uri.IsLoopback -and $uri.Scheme -eq 'http')) { throw 'El paquete debe descargarse mediante HTTPS.' }
$compilerArgs = @("/dAppVersion=$releaseVersion", "/dPayloadDir=$publishPath", "/dOutputPath=$outputPath",
    "/dPayloadUrl=$PayloadUrl", "/dPayloadHash=$hash", "/dPayloadSize=$($size.Sum)", "/dRequiredMB=$requiredMB", "/dBackgroundImage=$artwork", "/dLogoImage=$(Join-Path (Split-Path -Parent $artwork) 'setup-logo.png')", "/dArtworkDir=$(Split-Path -Parent $artwork)")
if ($Offline) { $compilerArgs += '/dOffline=1' }
if ($Experimental) { $compilerArgs += @('/dExperimental=1', "/dDataRoot=$DataRoot") }
if ($UiAudit) { $compilerArgs += @('/dInstallerUiAudit=1', "/dUiAuditRoot=$outputPath") }
if ($LegacyButtonPaint) { $compilerArgs += '/dUiAuditLegacyPaint=1' }
if ($SignedUninstaller -or $PrepareSigningTemplates) {
    if (-not $SignedUninstallerDir) { throw 'Especifica SignedUninstallerDir para la caché de firma del desinstalador.' }
    $compilerArgs += @('/dUseSignedUninstaller=1', "/dSignedUninstallerCache=$SignedUninstallerDir")
}
if ($SignToolCommand) { $compilerArgs += @('/dUseSignTool=1', "/Sdestiny=$SignToolCommand") }
$compilerArgs += Join-Path $workspaceRoot 'installer\DestinyLauncherSetup.iss'
$compilerOutput = & $CompilerPath @compilerArgs
$compilerExit = $LASTEXITCODE
$compilerOutput | Write-Output
if ($compilerExit -ne 0) {
    if ($PrepareSigningTemplates -and ($compilerOutput -join "`n") -match 'Signed uninstaller mode is enabled' -and
        @(Get-ChildItem -LiteralPath $SignedUninstallerDir -Filter '*.e32').Count -gt 0) {
        Write-Output 'Plantilla del desinstalador preparada para firma externa.'
        return
    }
    throw "Inno Setup terminó con código $compilerExit."
}
$fileName = if ($Offline) { 'DestinyLauncherSetup.exe' } else { 'DestinyLauncherWebSetup.exe' }
Write-Output "Instalador $releaseVersion creado en $outputPath\$fileName"

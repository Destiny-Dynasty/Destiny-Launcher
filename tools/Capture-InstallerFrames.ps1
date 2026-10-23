[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FixtureRoot,
    [ValidateSet('Prepare','Record','Clean')][string]$Mode = 'Record',
    [int]$Seconds = 26,
    [int]$FramesPerSecond = 60
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$boundary = [IO.Path]::GetFullPath((Join-Path $workspace 'artifacts')) + [IO.Path]::DirectorySeparatorChar
$fixture = [IO.Path]::GetFullPath($FixtureRoot)
if (-not $fixture.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase)) { throw 'La captura requiere una prueba dentro de artifacts.' }
if ($Seconds -lt 5 -or $Seconds -gt 60 -or $FramesPerSecond -lt 1 -or $FramesPerSecond -gt 120) { throw 'Duración o frecuencia fuera del rango de prueba.' }
if ($Mode -eq 'Clean') {
    $program = Join-Path $fixture 'program'
    $marker = Get-Content -LiteralPath (Join-Path $program 'launcher-installation.json') -Raw | ConvertFrom-Json
    if ($marker.Experimental -ne $true -or $marker.DataRoot -ne (Join-Path $fixture 'data')) { throw 'No es la instalación aislada esperada.' }
    $registered = (Get-ItemProperty -LiteralPath 'HKCU:\Software\DestinyDynasty\Launcher.UiTest').InstallPath
    if ($registered.TrimEnd('\') -ne $program) { throw 'El registro no corresponde a esta prueba.' }
    $cleanup = Start-Process -FilePath (Join-Path $program 'unins000.exe') -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -WindowStyle Hidden -PassThru -Wait
    if ($cleanup.ExitCode -ne 0) { throw "Desinstalación de prueba fallida: $($cleanup.ExitCode)" }
    Write-Output 'Se retiró solo la instalación de prueba; las grabaciones se conservaron.'
    return
}
if ($Mode -eq 'Prepare') {
    $setup = Join-Path $fixture 'DestinyLauncherSetup.exe'
    if (-not(Test-Path -LiteralPath $setup)) { throw 'Compila primero con -UiAudit.' }
    if (Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $setup }) { throw 'La prueba ya está abierta.' }
    foreach ($name in @('recording.ready','ready.txt','progress.txt','finished.txt')) {
        $marker = Join-Path $fixture $name
        if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker -Force }
    }
    $setupLog = Join-Path $fixture 'setup.log'
    $process = Start-Process -FilePath $setup -ArgumentList "/NORESTART /LOG=`"$setupLog`"" -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (-not(Test-Path -LiteralPath (Join-Path $fixture 'ready.txt'))) {
        if ($process.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'La ventana de prueba no se preparó.' }
        Start-Sleep -Milliseconds 100
    }
    Write-Output 'Portada preparada. La instalación espera a que comience la grabación.'
    return
}
if (-not(Test-Path -LiteralPath (Join-Path $fixture 'ready.txt'))) { throw 'Ejecuta primero -Mode Prepare.' }
$ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source
$recording = Join-Path $fixture 'capture.mkv'
$captureLog = Join-Path $fixture 'capture.log'
# Read-only, bounded window recording. No desktop capture, input injection,
# window enumeration helper, image upload, cursor capture or UI Automation.
$arguments = @('-hide_banner','-y','-loglevel','warning','-f','gdigrab','-framerate',"$FramesPerSecond",'-draw_mouse','0',
    '-i','"title=Instalar - Destiny Launcher (UI test)"','-t',"$Seconds",'-an','-c:v','libx264rgb','-crf','0',
    '-preset','ultrafast','-fps_mode','passthrough',"`"$recording`"")
$recorder = Start-Process -FilePath $ffmpeg -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardError $captureLog
Start-Sleep -Milliseconds 700
if ($recorder.HasExited) { throw (Get-Content -LiteralPath $captureLog -Raw) }
Set-Content -LiteralPath (Join-Path $fixture 'recording.ready') -Value 'Recorder running; begin the real isolated install.'
$recorder.WaitForExit()
if ($recorder.ExitCode -ne 0) { throw (Get-Content -LiteralPath $captureLog -Raw) }
$completed = Test-Path -LiteralPath (Join-Path $fixture 'finished.txt')
[pscustomobject]@{ file=$recording; requested_fps=$FramesPerSecond; requested_seconds=$Seconds; installed=$completed;
    source='FFmpeg gdigrab, installer window only, mouse excluded'; codec='lossless RGB H.264';
    note='Only the test build paces genuine installation progress; production builds contain no audit timer.' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'capture-metadata.json') -Encoding utf8
Write-Output "Grabación terminada. Instalación real completada: $completed. Archivo: $recording"

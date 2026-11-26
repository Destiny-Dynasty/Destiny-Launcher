param(
    [string]$OutputDirectory = "artifacts\ui-compare",
    [switch]$UseExistingCaptures,
    [double]$Tolerance = 0.01
)
$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$outputPath = if ([IO.Path]::IsPathFullyQualified($OutputDirectory)) { $OutputDirectory } else { Join-Path $repoRoot $OutputDirectory }
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
if (-not $UseExistingCaptures) {
    $sdkPath = Join-Path $env:TEMP "DestinyLauncher-dotnet-sdk-10\dotnet.exe"
    if (-not (Test-Path $sdkPath)) { $sdkPath = (Get-Command dotnet).Source }
    $previewOutput = Join-Path $repoRoot "artifacts\AvaloniaWindowsPreview"
    & $sdkPath publish (Join-Path $repoRoot "DestinyLauncher.Linux\DestinyLauncher.Linux.csproj") -c Release -r win-x64 --self-contained true -o $previewOutput
    if ($LASTEXITCODE -ne 0) { throw "No se pudo compilar la vista Avalonia." }
    foreach ($size in @("1000x620", "820x560")) {
        foreach ($page in @("home", "installation", "settings")) {
            $suffix = if ($size -eq "820x560") { "-compact" } else { "" }
            $capturePath = Join-Path $outputPath ("avalonia-" + $page + $suffix + ".png")
            $process = Start-Process -FilePath (Join-Path $previewOutput "DestinyLauncher.Linux.exe") -ArgumentList "--render-preview", ('"' + $capturePath + '"'), "--page", $page, "--size", $size -WindowStyle Hidden -PassThru -Wait
            if ($process.ExitCode -ne 0) { throw "No se pudo capturar $page ($size)." }
        }
    }
    foreach ($page in @("profile", "game-menu", "accounts", "add-account", "repair", "settings-bottom")) {
        $capturePath = Join-Path $outputPath ("avalonia-" + $page + ".png")
        $process = Start-Process -FilePath (Join-Path $previewOutput "DestinyLauncher.Linux.exe") -ArgumentList "--render-preview", ('"' + $capturePath + '"'), "--page", $page -WindowStyle Hidden -PassThru -Wait
        if ($process.ExitCode -ne 0) { throw "No se pudo capturar $page." }
    }
    & $sdkPath run --project (Join-Path $repoRoot "tools\UiSmoke\UiSmoke.csproj") -c Release -- $outputPath --parity-reference
    if ($LASTEXITCODE -ne 0) { throw "No se pudieron generar las referencias WPF." }
}
$controlMap = @{ HeroRegion = "Hero"; NewsCard = "News" }
$comparisons = @()
foreach ($page in @("home", "installation", "settings", "home-compact", "installation-compact", "settings-compact")) {
    $avalonia = Get-Content -LiteralPath (Join-Path $outputPath ("avalonia-" + $page + ".png.layout.json")) -Raw | ConvertFrom-Json -AsHashtable
    $windows = Get-Content -LiteralPath (Join-Path $outputPath ("windows-" + $page + ".png.layout.json")) -Raw | ConvertFrom-Json -AsHashtable
    foreach ($control in $avalonia.Keys) {
        $reference = if ($controlMap.ContainsKey($control)) { $controlMap[$control] } else { $control }
        if (-not $windows.ContainsKey($reference)) { continue }
        $differences = foreach ($dimension in @("x", "y", "width", "height")) { [Math]::Round([double]$avalonia[$control][$dimension] - [double]$windows[$reference][$dimension], 3) }
        $comparisons += [pscustomobject]@{
            page = $page; control = $control; dx = $differences[0]; dy = $differences[1]; dw = $differences[2]; dh = $differences[3]
            passed = -not ($differences | Where-Object { [Math]::Abs($_) -gt $Tolerance })
        }
    }
}
$failures = @($comparisons | Where-Object { -not $_.passed })
$result = [pscustomobject]@{ measuredControls = $comparisons.Count; toleranceDips = $Tolerance; failed = $failures.Count; comparisons = $comparisons }
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputPath "geometry-comparison.json") -Encoding utf8
$rows = ($comparisons | ForEach-Object { '<tr><td>' + $_.page + '</td><td>' + $_.control + '</td><td>' + $_.dx + '</td><td>' + $_.dy + '</td><td>' + $_.dw + '</td><td>' + $_.dh + '</td></tr>' }) -join ""
$html = @'
<!doctype html><html lang="es"><meta charset="utf-8"><title>Comparación Destiny Launcher</title>
<style>body{background:#10100f;color:#f2ebdd;font:14px system-ui;margin:24px}h1{font-size:24px}button,select,input{background:#25231e;color:#f2ebdd;border:1px solid #77603b;padding:8px;border-radius:5px}header{display:flex;align-items:center;gap:16px;margin-bottom:20px}.views{display:grid;grid-template-columns:1fr 1fr;gap:20px;align-items:start}figure{margin:0;min-width:0}figcaption{color:#c6a362;margin-bottom:8px}img{width:100%;height:auto;display:block}.overlay{position:relative;max-width:1000px;display:none}.overlay img:last-child{position:absolute;inset:0;opacity:.5}.mode-overlay .views{display:none}.mode-overlay .overlay{display:block}table{border-collapse:collapse;margin-top:24px;font-size:12px}td,th{border:1px solid #35332c;padding:6px 12px;text-align:left}.note{color:#b3ac9e;max-width:950px}</style>
<h1>Windows WPF / Avalonia</h1><p class="note">Capturas reproducibles con la misma portada, datos de prueba aislados y sin iniciar Minecraft. El JSON mide posiciones y tamaños; la comparación visual permite revisar también fuentes, imágenes y colores. Los números de versión y las rutas de usuario pueden diferir.</p>
<header><select id="scene"><option value="home">Inicio</option><option value="installation">Instalación</option><option value="settings">Ajustes</option><option value="settings-bottom">Ajustes: memoria y Java</option><option value="profile">Perfil</option><option value="game-menu">Menú del juego</option><option value="accounts">Cuentas</option><option value="add-account">Añadir cuenta</option><option value="repair">Reparación</option><option value="home-compact">Inicio compacto</option><option value="installation-compact">Instalación compacta</option><option value="settings-compact">Ajustes compactos</option></select><button id="mode">Superponer</button><label>Opacidad Avalonia <input id="alpha" type="range" min="0" max="1" value=".5" step=".01"></label></header>
<div class="views"><figure><figcaption>Windows WPF</figcaption><img id="wpf"></figure><figure><figcaption>Avalonia</figcaption><img id="ava"></figure></div><div class="overlay"><img id="base"><img id="top"></div>
<h2>Medidas de los controles</h2><p>__MEASUREMENTS__ controles medidos; __FAILURES__ diferencias por encima de __TOLERANCE__ DIP.</p><table><thead><tr><th>Pantalla</th><th>Control</th><th>Δ X</th><th>Δ Y</th><th>Δ ancho</th><th>Δ alto</th></tr></thead><tbody>__ROWS__</tbody></table>
<script>function update(){const s=document.querySelector('#scene').value;for(const id of ['wpf','base'])document.querySelector('#'+id).src='windows-'+s+'.png';for(const id of ['ava','top'])document.querySelector('#'+id).src='avalonia-'+s+'.png'}document.querySelector('#scene').onchange=update;document.querySelector('#mode').onclick=()=>document.body.classList.toggle('mode-overlay');document.querySelector('#alpha').oninput=e=>document.querySelector('#top').style.opacity=e.target.value;update()</script></html>
'@
$html = $html.Replace("__MEASUREMENTS__", [string]$comparisons.Count).Replace("__FAILURES__", [string]$failures.Count).Replace("__TOLERANCE__", [string]$Tolerance).Replace("__ROWS__", $rows)
Set-Content -LiteralPath (Join-Path $outputPath "comparison.html") -Value $html -Encoding utf8
$comparisons | Format-Table -AutoSize
if ($failures.Count -gt 0) { throw "$($failures.Count) controles aún difieren en su geometría." }
Write-Output "PASS: $($comparisons.Count) controles coinciden dentro de $Tolerance DIP."



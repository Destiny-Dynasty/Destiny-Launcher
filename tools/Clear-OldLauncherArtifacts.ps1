[CmdletBinding()]
param([switch]$Execute)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifacts = Join-Path $workspace 'artifacts'
$desktop = [Environment]::GetFolderPath('Desktop')
$dataRoot = Join-Path $env:LOCALAPPDATA 'DestinyLauncher'
$backup = Join-Path $artifacts 'clean-install-backup'
$latest = Join-Path $artifacts 'release-1.0.0/DestinyLauncherSetup.exe'
$desktopSetup = Join-Path $desktop 'DestinyLauncherSetup-1.0.0.exe'
if (-not (Test-Path -LiteralPath $latest) -or -not (Test-Path -LiteralPath $desktopSetup)) {
    throw 'Debe existir el instalador 1.0.0 en artifacts y en el escritorio.'
}
if ((Get-FileHash -LiteralPath $latest).Hash -ne (Get-FileHash -LiteralPath $desktopSetup).Hash) {
    throw 'El instalador del escritorio no coincide con el actual.'
}
$running = @(Get-Process -Name DestinyLauncher -ErrorAction SilentlyContinue)
if ($running.Count) { throw 'Cierra el launcher antes de ejecutar la limpieza.' }
$keep = @('release-1.0.0', 'tooling', 'java-managed', 'modpack-lite-repository', 'news-feed-repository', 'clean-install-backup')
$targets = [Collections.Generic.List[string]]::new()
foreach ($directory in Get-ChildItem -LiteralPath $artifacts -Directory -Force) {
    if ($directory.Name -notin $keep) { $targets.Add($directory.FullName) }
}
foreach ($item in Get-ChildItem -LiteralPath (Join-Path $artifacts 'java-managed') -Force) {
    if ($item.Name -ne 'modpack-feed') { $targets.Add($item.FullName) }
}
foreach ($name in @('publish-experiment', 'publish-experiment-2')) {
    $path = Join-Path $workspace $name
    if (Test-Path -LiteralPath $path) { $targets.Add($path) }
}
$workspaceBoundary = $workspace + [IO.Path]::DirectorySeparatorChar
foreach ($path in $targets) {
    $resolved = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $path).Path)
    if (-not $resolved.StartsWith($workspaceBoundary, [StringComparison]::OrdinalIgnoreCase)) { throw "Ruta fuera del workspace: $resolved" }
    $item = Get-Item -LiteralPath $resolved -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Raíz enlazada: $resolved" }
    if ($item.PSIsContainer -and (Get-ChildItem -LiteralPath $resolved -Recurse -Force -Filter '.git' -ErrorAction Stop)) { throw "Repositorio protegido: $resolved" }
}
$extraFiles = @(Get-ChildItem -LiteralPath $desktop -File | Where-Object {
    ($_.Name -match '^DestinyLauncherSetup-\d+\.\d+\.\d+\.exe$' -and $_.Name -ne 'DestinyLauncherSetup-1.0.0.exe') -or
    $_.Name -in @('Probar actualización del launcher.lnk', 'Probar vinculación Discord.lnk')
})
$dataItems = @(if (Test-Path -LiteralPath $dataRoot) { Get-ChildItem -LiteralPath $dataRoot -Force | Where-Object Name -ne 'instances' })
$allTargets = @($targets) + @($extraFiles.FullName) + @($dataItems.FullName)
$total = 0L
foreach ($path in $allTargets) {
    $item = Get-Item -LiteralPath $path -Force
    $total += $(if ($item.PSIsContainer) { (Get-ChildItem -LiteralPath $path -Recurse -File -Force | Measure-Object Length -Sum).Sum } else { $item.Length })
    Write-Output "Eliminar: $path"
}
Write-Output ('Espacio aproximado: {0:N2} GiB' -f ($total / 1GB))
if (-not $Execute) { Write-Output 'Solo revisión. Usa -Execute para borrar los elementos listados.'; return }
New-Item -ItemType Directory -Path $backup -Force | Out-Null
foreach ($name in @('launcher.json', 'installations.json', 'discord-session.bin')) {
    $path = Join-Path $dataRoot $name
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $backup -Force }
}
foreach ($path in $targets) { Remove-Item -LiteralPath $path -Recurse -Force }
foreach ($file in $extraFiles) { Remove-Item -LiteralPath $file.FullName -Force }
foreach ($item in $dataItems) {
    $resolved = [IO.Path]::GetFullPath($item.FullName)
    if (-not $resolved.StartsWith(([IO.Path]::GetFullPath($dataRoot) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) { throw 'Ruta de datos inesperada.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Output 'Limpieza completada. Instalador actual y juegos conservados. No se ha instalado nada.'

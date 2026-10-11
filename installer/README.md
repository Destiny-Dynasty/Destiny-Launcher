# Instalador online

El instalador es pequeño y se instala sólo para el usuario actual: ofrece acceso desde Inicio, un acceso opcional en el escritorio y desinstalación desde Windows. Descarga el paquete de la última release desde una URL pública y deja intactos los datos guardados por el launcher bajo `%LOCALAPPDATA%\DestinyLauncher`.

La URL predeterminada usa la última release pública de `Destiny-Dynasty/Destiny-Launcher`. El repositorio ya es público; el setup podrá descargar el ZIP cuando `DestinyLauncher-win-x64.zip` esté publicado como asset. No se necesita un repositorio privado separado para los binarios: el instalador debe poder descargarlos sin iniciar sesión.

## Flujo de publicación

1. Ejecutar `tools/Publish-Launcher.ps1` para actualizar el ejecutable de `publish/`.
2. Ejecutar `tools/Package-Launcher.ps1` y crear una release pública que incluya `publish/DestinyLauncher-win-x64.zip`.
3. Ejecutar `tools/Build-Installer.ps1`; se genera `publish/DestinyLauncherSetup.exe` con la URL pública predeterminada.
4. Subir `publish/DestinyLauncherSetup.exe` como asset de esa misma release.

El setup sólo podrá instalar cuando exista el ZIP en una release pública. No se suben compilaciones de prueba al repositorio como commits. Antes de una distribución amplia, conviene añadir firma digital al instalador y al paquete para que Windows pueda verificar al editor.

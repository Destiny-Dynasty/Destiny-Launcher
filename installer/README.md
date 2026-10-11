# Instalador online

El instalador es pequeño y se instala sólo para el usuario actual: ofrece acceso desde Inicio, un acceso opcional en el escritorio y desinstalación desde Windows. Descarga el paquete de la última release de este repositorio y deja intactos los datos guardados por el launcher bajo `%LOCALAPPDATA%\DestinyLauncher`.

## Flujo de publicación

1. Ejecutar `tools/Publish-Launcher.ps1`.
2. Ejecutar `tools/Build-Installer.ps1`; se crea el ZIP del launcher y el setup en `publish/`.
3. Subir `publish/DestinyLauncher-win-x64.zip` como asset de una release de GitHub con ese nombre.

El setup sólo podrá instalar cuando exista ese asset en una release. No se suben compilaciones de prueba al repositorio como commits. Antes de una distribución pública, conviene añadir firma digital del instalador y del paquete para que Windows pueda verificar al editor.

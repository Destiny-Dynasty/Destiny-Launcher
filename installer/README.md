# Instalador online

El instalador es pequeño y se instala sólo para el usuario actual: ofrece acceso desde Inicio, un acceso opcional en el escritorio y desinstalación desde Windows. Descarga el paquete de la última release desde una URL pública y deja intactos los datos guardados por el launcher bajo `%LOCALAPPDATA%\DestinyLauncher`.

La URL predeterminada usa este repositorio como destino de releases. Como el repositorio de código no es público, no hay que distribuir ese setup tal cual: al publicar, hay que indicar una URL pública con `tools/Build-Installer.ps1 -PayloadUrl <URL-del-ZIP>` o acordar que este repositorio sea público. No se cambia la privacidad del repo automáticamente.

## Flujo de publicación

1. Ejecutar `tools/Publish-Launcher.ps1`.
2. Ejecutar `tools/Build-Installer.ps1 -PayloadUrl <URL-del-ZIP>`; se crea el ZIP del launcher y el setup en `publish/`.
3. Subir `publish/DestinyLauncher-win-x64.zip` como asset de una release de GitHub con ese nombre.

El setup sólo podrá instalar cuando exista ese asset en una release pública. No se suben compilaciones de prueba al repositorio como commits. Antes de una distribución pública, conviene añadir firma digital del instalador y del paquete para que Windows pueda verificar al editor.

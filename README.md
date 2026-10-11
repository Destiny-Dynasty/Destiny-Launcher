# Destiny Launcher

Launcher de escritorio para el modpack Destiny, construido con WPF y .NET 10.

## Desarrollo

- Proyecto: `DestinyLauncher/DestinyLauncher.csproj`
- Compilación local: `dotnet build DestinyLauncher/DestinyLauncher.csproj -c Release`
- Publicación para Windows x64: `tools/Publish-Launcher.ps1`
- La salida de publicación se reemplaza en `publish/`; no se versiona en Git.
- El feed del modpack vive en un repositorio independiente: `Shady0935/Destiny-Modpack-Feed`.

## Datos del usuario

La configuración, el perfil, las instancias, la caché y los logs se guardan bajo `%LOCALAPPDATA%\DestinyLauncher`, separados de los archivos de la aplicación para conservarlos al actualizar o desinstalar el launcher.

## Distribución

El instalador online y su proceso de publicación se mantienen en `installer/` y `tools/`. El paquete debe estar en una release pública. Como este repositorio de código no es público, antes de distribuir el setup hay que proporcionar una URL pública con `tools/Build-Installer.ps1 -PayloadUrl <URL-del-ZIP>` o decidir hacer público este repositorio.

# Destiny Launcher

Launcher de escritorio para el modpack Destiny, construido con WPF y .NET 10.

## Desarrollo

- Proyecto: `DestinyLauncher/DestinyLauncher.csproj`
- Compilación local: `dotnet build DestinyLauncher/DestinyLauncher.csproj -c Release`
- Publicación para Windows x64: `tools/Publish-Launcher.ps1`
- La salida de publicación se reemplaza en `publish/`; no se versiona en Git.
- El feed del modpack vive en un repositorio independiente: `Shady0935/Destiny-Modpack-Feed`.

## Datos del usuario

La configuración, el perfil, la caché y los logs se guardan bajo `%LOCALAPPDATA%\DestinyLauncher`, separados de los archivos de la aplicación para conservarlos al actualizar o desinstalar el launcher. Por defecto, el juego y los archivos del modpack Destiny se instalan en `%APPDATA%\.minecraft\versions\DestinyDynasty`; Destiny Lite usa la carpeta hermana `DestinyDynasty-Lite`. La carpeta principal se puede cambiar desde Ajustes.

El launcher descarga Minecraft Java 1.20.1, instala Fabric y después sincroniza los archivos del modpack desde su manifiesto. En cada instalación consulta Fabric Meta para elegir el loader estable más reciente compatible con Minecraft 1.20.1; si el servicio no está disponible, utiliza la versión fijada en el manifiesto o la configuración.

## Distribución

El instalador online y su proceso de publicación se mantienen en `installer/` y `tools/`. Este repositorio es público y sirve también para alojar los paquetes en GitHub Releases. El setup descarga `DestinyLauncher-win-x64.zip` desde la última release pública; el archivo debe existir allí antes de distribuir el instalador.

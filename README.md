# Destiny Launcher

Launcher de escritorio para el modpack Destiny, construido con WPF y .NET 10.

## Desarrollo

- Proyecto: `DestinyLauncher/DestinyLauncher.csproj`
- Compilación local: `dotnet build DestinyLauncher/DestinyLauncher.csproj -c Release`
- Publicación para Windows x64: `tools/Publish-Launcher.ps1`
- La salida de publicación se reemplaza en `publish/`; no se versiona en Git.
- Publicación experimental refinada: `tools/Publish-Launcher-Experiment.ps1`, con salida fija en `publish-experiment-2/`, autocontenida para Windows x64 y en un solo ejecutable. El script comprueba que `publish-experiment/` mantiene sus archivos, fechas y SHA-256; nunca se usa como salida ni como carpeta temporal.
- Destiny consulta `Destiny-Dynasty/Modpack/main/manifests/destiny.json`, con los archivos alojados en las releases de ese repositorio.
- Destiny Lite consulta `Destiny-Dynasty/Modpack-Lite/main/manifests/destiny-lite.json`. El repositorio está preparado para su primera publicación; mientras no exista un pack real no se publica un manifiesto vacío ni una versión ficticia.

## Datos del usuario

La configuración, el perfil, la caché y los logs se guardan bajo `%LOCALAPPDATA%\DestinyLauncher`, separados de los archivos de la aplicación para conservarlos al actualizar o desinstalar el launcher. Por defecto, el juego y los archivos del modpack Destiny se instalan en `%APPDATA%\.minecraft\versions\DestinyDynasty`; Destiny Lite usa la carpeta hermana `DestinyDynasty-Lite`. La carpeta principal se puede cambiar desde Ajustes.

El launcher descarga Minecraft Java 1.20.1, instala Fabric y después sincroniza los archivos del modpack desde su manifiesto. En cada instalación consulta Fabric Meta para elegir el loader estable más reciente compatible con Minecraft 1.20.1; si el servicio no está disponible, utiliza la versión fijada en el manifiesto o la configuración.

## Pruebas de interfaz y aislamiento

Para abrir una sesión con configuración, caché, registros e instalaciones de prueba independientes:

```powershell
.\publish-experiment-2\DestinyLauncher.exe --data-root E:\Destiny\Launcher\artifacts\ui-refinement\manual-data
```

En una raíz nueva, Destiny se instala dentro de `instances/Destiny` y Lite dentro de `instances/Destiny-Lite`. La ventana indica `DATOS AISLADOS`. Sin este argumento se conservan las rutas habituales del launcher. Los comandos de renderizado también usan datos temporales aislados.

```powershell
dotnet run --project tools/UiSmoke/UiSmoke.csproj -c Release -- artifacts/ui-refinement
dotnet run --project tools/PackUpdaterSmoke/PackUpdaterSmoke.csproj -c Release
```

Ambos requieren el SDK de .NET 10. `UiSmoke` verifica estados, exclusividad de variantes, validación, persistencia, contraste y distribución, y guarda capturas WPF y un informe JSON. `--remote-skin` permite comprobar además el servicio externo de cabezas con un perfil de prueba. Los estados instalados y de descarga de estas capturas son fixtures explícitos; no descargan Minecraft. Los renderizados a 100/125/150/200 % comprueban la resolución de salida, no sustituyen una prueba manual cambiando la escala de Windows o moviendo la ventana entre monitores.

`PackUpdaterSmoke` utiliza respuestas HTTP en memoria y archivos temporales: comprueba instalación, actualización incremental, ediciones locales, rechazo de descargas corruptas, rutas protegidas y cancelación. No necesita manifiestos públicos ni toca partidas existentes.

La distribución futura de Lite se prepara en [Destiny-Dynasty/Modpack-Lite](https://github.com/Destiny-Dynasty/Modpack-Lite). Su README incluye el orden de publicación y un preparador de manifiestos y archivos para Releases. La primera publicación real habilitará Lite en el launcher sin recompilarlo; actualizar Lite no cambia el feed ni la instalación de Destiny completo.

Los colores y controles compartidos están en `DestinyLauncher/Themes/DestinyTheme.xaml`; los mensajes y acciones según el estado real están en `Models/LauncherPresentation.cs`. Las transiciones breves de `UiMotion.cs` consultan la preferencia de animación de Windows. La selección de variante solo persiste la elección y consulta el manifiesto; la descarga empieza con la acción principal.

Inicio presenta el pack activo, el perfil y la acción principal. La selección de Destiny / Destiny Lite está en **Instalación**, accesible desde la barra lateral o desde «Administrar instalación». En una primera instalación, el botón de Inicio abre esa sección antes de pedir el perfil o descargar archivos. Con un pack ya instalado, «Jugar» conserva el acceso directo al flujo habitual.

Los emblemas pequeños comparten una imagen decodificada a 112 píxeles y remuestreo `HighQuality` de WPF; el título la presenta a 28 px y la barra lateral a 40 px. El arte original del banner se conserva. El marco usa un radio de 18 px, con recorte del contenido y región nativa de `WindowChrome`.

La ventana tiene tamaño fijo: 1000 × 620 unidades de WPF, respetando el escalado de Windows. Si el área útil del escritorio es menor, se ajusta una sola vez al abrir y queda bloqueada en ese tamaño. Solo permite mover, minimizar y cerrar; no hay botón de maximizar ni bordes redimensionables. `WindowWorkArea.cs` bloquea también los comandos nativos de maximización y cambio de tamaño, incluido el doble clic en la barra de título. Las herramientas de vista previa conservan el cambio programático de tamaño para verificar distribución y escalado sin afectar a sesiones reales.

## Novedades del launcher

El carrusel de Inicio consume `https://destiny-dynasty.github.io/Launcher-News/news.json`. El repositorio editorial independiente es [Destiny-Dynasty/Launcher-News](https://github.com/Destiny-Dynasty/Launcher-News); su README explica cómo subir imágenes y editar el feed desde GitHub. No se incluyen credenciales en el launcher y publicar una noticia no requiere recompilarlo.

La imagen cambia aproximadamente cada cinco segundos. El texto breve y las flechas aparecen al pasar el cursor o dar foco con el teclado. Al avanzar, la imagen nueva entra desde la derecha mientras la anterior sale por la izquierda; al retroceder se invierte el sentido. Ambas recorren el ancho completo y frenan suavemente al final. Los indicadores permiten seleccionar una noticia. Hover, foco, ventana inactiva/minimizada y operaciones del juego pausan el temporizador. Con movimiento reducido del sistema se usa navegación manual sin transiciones.

`Services/NewsFeedService.cs` descarga el feed y sus imágenes en segundo plano. Guarda la última versión válida dentro de `cache/news`, valida HTTPS, tamaño y formato, y limita el contenido a ocho banners con textos de hasta 90 caracteres. Al fallar la red conserva la caché; sin caché incluye dos muestras del arte de Destiny. Un feed válido con `items: []` oculta el carrusel. Las novedades nunca alteran el estado de instalación ni bloquean Jugar.

```powershell
dotnet run --project tools/NewsFeedSmoke/NewsFeedSmoke.csproj -c Release
dotnet run --project tools/NewsFeedSmoke/NewsFeedSmoke.csproj -c Release -- --live
```

La primera prueba usa respuestas HTTP en memoria y datos temporales. `--live` comprueba además el feed público y sus dos muestras actuales. `tools/UiSmoke` comprueba la navegación del carrusel y su separación del logo, perfil y acción principal en los tamaños de ventana de prueba.

## Distribución

El instalador online y su proceso de publicación se mantienen en `installer/` y `tools/`. Este repositorio es público y sirve también para alojar los paquetes en GitHub Releases. El setup descarga `DestinyLauncher-win-x64.zip` desde la última release pública; el archivo debe existir allí antes de distribuir el instalador.

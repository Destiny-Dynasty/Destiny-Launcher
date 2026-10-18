# Destiny Launcher

Launcher de escritorio para el modpack Destiny, construido con WPF y .NET 10.

## Desarrollo

- Proyecto: `DestinyLauncher/DestinyLauncher.csproj`
- Compilación local: `dotnet build DestinyLauncher/DestinyLauncher.csproj -c Release`
- Publicación para Windows x64: `tools/Publish-Launcher.ps1`
- La salida de publicación se reemplaza en `publish/`; no se versiona en Git.
- La interfaz refinada es la versión principal. La publicación es autocontenida para Windows x64 y genera un solo ejecutable, sin requerir una instalación de .NET.
- Destiny consulta `Destiny-Dynasty/Modpack/main/manifests/destiny.json`, con los archivos alojados en las releases de ese repositorio.
- Destiny Lite consulta `Destiny-Dynasty/Modpack-Lite/main/manifests/destiny-lite.json`. El repositorio está preparado para su primera publicación; mientras no exista un pack real no se publica un manifiesto vacío ni una versión ficticia.

## Datos del usuario

La configuración, el perfil, la caché y los logs se guardan bajo `%LOCALAPPDATA%\DestinyLauncher`, separados de los archivos de la aplicación para conservarlos al actualizar o desinstalar el launcher. Por defecto, el juego y los archivos del modpack Destiny se instalan en `%APPDATA%\.minecraft\versions\DestinyDynasty`; Destiny Lite usa la carpeta hermana `DestinyDynasty-Lite`. La carpeta principal se puede cambiar desde Ajustes.

En Ajustes se edita únicamente la carpeta base, por defecto `%APPDATA%\.minecraft\versions`. El nombre final se muestra bloqueado y se añade al guardar. La configuración conserva la ruta completa para mantener la compatibilidad; si ya había una instalación con un nombre personalizado se conserva ese nombre. Abrir o guardar otros ajustes no mueve ni renombra archivos existentes.

Los argumentos de Java se guardan en `JavaArguments` y se aplican a ambas variantes. «Restablecer» devuelve el campo a `-XX:+UseG1GC -XX:MaxGCPauseMillis=200`; los cambios se aplican con «Guardar ajustes». Se usa una base conservadora de G1 para Java 17, de acuerdo con las [recomendaciones de Oracle](https://docs.oracle.com/en/java/javase/17/gctuning/garbage-first-garbage-collector-tuning.html), sin prometer mejoras universales de rendimiento ni fijar tamaños de regiones o generaciones. La RAM sigue generando `-Xmx` desde su selector, salvo que el usuario lo indique explícitamente entre sus argumentos. Un campo vacío desactiva estos extras, manteniendo los argumentos obligatorios del perfil y del sistema. Los argumentos admiten comillas dobles y rutas de Windows con espacios; los errores de comillas se muestran al guardar.

El launcher descarga Minecraft Java 1.20.1, instala Fabric y después sincroniza los archivos del modpack desde su manifiesto. En cada instalación consulta Fabric Meta para elegir el loader estable más reciente compatible con Minecraft 1.20.1; si el servicio no está disponible, utiliza la versión fijada en el manifiesto o la configuración.

## Pruebas de interfaz y aislamiento

Para abrir una sesión con configuración, caché, registros e instalaciones de prueba independientes:

```powershell
.\publish\DestinyLauncher.exe --data-root E:\Destiny\Launcher\artifacts\ui-refinement\manual-data
```

En una raíz nueva, Destiny se instala dentro de `instances/Destiny` y Lite dentro de `instances/Destiny-Lite`. La ventana indica `DATOS AISLADOS`. Sin este argumento se conservan las rutas habituales del launcher. Los comandos de renderizado también usan datos temporales aislados.

```powershell
dotnet run --project tools/UiSmoke/UiSmoke.csproj -c Release -- artifacts/ui-refinement
dotnet run --project tools/UiSmoke/UiSmoke.csproj -c Release -- artifacts/ui-headless --headless
dotnet run --project tools/PackUpdaterSmoke/PackUpdaterSmoke.csproj -c Release
```

Ambos requieren el SDK de .NET 10. `UiSmoke` verifica estados, exclusividad de variantes, validación, persistencia, contraste y distribución, y guarda capturas WPF y un informe JSON. `--remote-skin` permite comprobar además el servicio externo de cabezas con un perfil de prueba. Los estados instalados y de descarga de estas capturas son fixtures explícitos; no descargan Minecraft. Los renderizados a 100/125/150/200 % comprueban la resolución de salida, no sustituyen una prueba manual cambiando la escala de Windows o moviendo la ventana entre monitores.

`--headless` usa únicamente controles WPF desconectados, sin mostrar o activar ventanas, crear una fuente de presentación ni inyectar entrada. Comprueba el selector de RAM, la visibilidad condicional de mensajes y la acción de carpeta mediante un delegado de prueba; no abre el Explorador. Permite revisar estos cambios sin interrumpir una partida. Los PNG se renderizan en memoria y no sustituyen la revisión visual de la aplicación real.

`PackUpdaterSmoke` utiliza respuestas HTTP en memoria y archivos temporales: comprueba instalación, actualización incremental, ediciones locales, rechazo de descargas corruptas, rutas protegidas y cancelación. No necesita manifiestos públicos ni toca partidas existentes.

La distribución futura de Lite se prepara en [Destiny-Dynasty/Modpack-Lite](https://github.com/Destiny-Dynasty/Modpack-Lite). Su README incluye el orden de publicación y un preparador de manifiestos y archivos para Releases. La primera publicación real habilitará Lite en el launcher sin recompilarlo; actualizar Lite no cambia el feed ni la instalación de Destiny completo.

Los colores y controles compartidos están en `DestinyLauncher/Themes/DestinyTheme.xaml`; los mensajes y acciones según el estado real están en `Models/LauncherPresentation.cs`. Las transiciones breves de `UiMotion.cs` consultan la preferencia de animación de Windows. La selección de variante solo persiste la elección y consulta el manifiesto; la descarga empieza con la acción principal.

Inicio presenta el pack activo, el perfil y la acción principal. La selección de Destiny / Destiny Lite está en **Instalación**, accesible desde la barra lateral o desde «Administrar instalación». En una primera instalación, el botón de Inicio abre esa sección antes de pedir el perfil o descargar archivos. Con un pack ya instalado, «Jugar» conserva el acceso directo al flujo habitual.

Los emblemas pequeños comparten una imagen decodificada a 112 píxeles y remuestreo `HighQuality` de WPF; el título la presenta a 28 px y la barra lateral a 40 px. El arte original del banner se conserva. El marco usa un radio de 18 px, con recorte del contenido y región nativa de `WindowChrome`.

La ventana tiene tamaño fijo: 1000 × 620 unidades de WPF, respetando el escalado de Windows. Si el área útil del escritorio es menor, se ajusta una sola vez al abrir y queda bloqueada en ese tamaño. Solo permite mover, minimizar y cerrar; no hay botón de maximizar ni bordes redimensionables. `WindowWorkArea.cs` bloquea también los comandos nativos de maximización y cambio de tamaño, incluido el doble clic en la barra de título. Las herramientas de vista previa conservan el cambio programático de tamaño para verificar distribución y escalado sin afectar a sesiones reales.

En reposo se omiten el pie de estado y las frases bajo Jugar/Instalar. Las operaciones, avisos y errores mantienen sus mensajes. Instalación ofrece «Abrir carpeta» para la variante seleccionada cuando la ruta existe, sin crear directorios. Ajustes conserva etiquetas y validaciones; la barra rápida de RAM incluye divisiones discretas cada 2 GB, sin números ni puntos adicionales, permite pasos de 1 GB y mantiene la entrada manual del rango admitido por el backend. Los iconos de Inicio, Instalación, Ajustes y carpeta usan los SVG de `Assets`, convertidos a geometrías WPF con el color heredado del control.

## Durante la partida

La ventana permanece visible mientras comprueba, descarga y prepara los archivos. Solo se oculta después de iniciar correctamente el proceso de Minecraft. El launcher conserva el identificador de ese proceso y espera su cierre con `Process.WaitForExitAsync`, mediante una notificación del sistema operativo, sin sondeos ni un hilo bloqueado. Al terminar el juego, vuelve a mostrar y activar la ventana en su tamaño normal; un código de salida distinto de cero se muestra como error y permite reintentar. Un fallo anterior al arranque mantiene la ventana visible.

Mientras está oculto no hay temporizador del carrusel, animación de progreso ni peticiones pendientes de novedades o skins. Los botones de juego e instalación permanecen bloqueados durante esa sesión. Se conserva únicamente una cola de hasta 16.384 caracteres de la salida de error de Java, con lectura asíncrona y sin escribir cada línea al disco. Si el juego termina con error, se guarda en `%LOCALAPPDATA%\DestinyLauncher\logs\minecraft-startup-*.log`, incluso si Minecraft no alcanzó a crear `logs/latest.log`. Minecraft mantiene sus registros habituales en la instancia. El launcher conserva en memoria la interfaz para recuperarla rápidamente; su consumo de RAM no puede ser cero. No se fuerza el vaciado de memoria de Windows ni se cierra Minecraft si termina el launcher.

`UiSmoke` también comprueba ocultación, espera sin bloquear WPF, retorno normal, salida con error, proceso ya terminado, sesiones consecutivas y pausa del carrusel durante más de cinco segundos. Usa procesos auxiliares propios y datos aislados, sin arrancar Java ni modificar partidas reales. Guarda una muestra de CPU y memoria del proceso de pruebas; no equivale a una medición de una partida real.

La preparación de Fabric instala el perfil de la versión estable seleccionada mediante la sobrecarga explícita `(minecraftVersion, loaderVersion, instance)`, y después ejecuta `MinecraftLauncher.InstallAsync(profile)` para descargar también loader, intermediary, Mixin y ASM antes del arranque. Escribir solamente el JSON de Fabric no instala esas bibliotecas. La siguiente ejecución repara las dependencias que falten sin borrar el modpack ni las partidas.

`dotnet run --project tools/MinecraftSmoke/MinecraftSmoke.csproj -c Release -- artifacts/minecraft-smoke --live-fabric` verifica esa secuencia con HTTP simulado y, opcionalmente, descarga las bibliotecas reales de Fabric en una instancia de prueba mínima. No instala ni ejecuta el modpack del usuario. Si encuentra el Java ya instalado, lo usa para reproducir el fallo de clase ausente en una carpeta aislada y comprobar la captura del error.

## Comunidad

Los tres iconos inferiores de la barra lateral abren en el navegador predeterminado [Discord](https://dsc.gg/destiny-dynasty-mc), [YouTube](https://www.youtube.com/@destiny_dynasty) y [X](https://x.com/DestinyDynasty_). Tienen etiquetas accesibles, foco de teclado y tooltips; no cambian la vista ni la selección del modpack. Los trazados vectoriales proceden de Simple Icons (CC0) y se incluyen directamente en `IconGlyphs.cs`, sin descargas de iconos en tiempo de ejecución.

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

# Desarrollo 1.1.0

Rama: `1.1.0`. El propietario autorizó publicar estos cambios para Windows como **1.0.5**, el 24 de octubre de 2026. Linux sigue aplazado. No crear tags ni publicar releases sin indicación del propietario. `main`, la release `v1.0.0`, el instalador del escritorio y sus assets públicos se conservan.

## Instalador público fijo

El enlace público se mantiene fijado a la primera distribución:

https://github.com/Destiny-Dynasty/Destiny-Launcher/releases/download/v1.0.0/DestinyLauncherSetup.exe

Las siguientes versiones del programa se ofrecerán mediante el actualizador. No reemplazar ni recompilar el asset de la 1.0.0. Una sustitución del instalador inicial exige una necesidad explícita de instalación/compatibilidad. Las correcciones visuales del instalador de esta rama no alteran el instalador público existente.

## Correcciones implementadas

- Memoria: detectar RAM física; usar el 40 % por defecto y limitar al 80 %, redondeando hacia abajo a GB enteros. En Linux se respeta también un límite cgroup menor. La configuración antigua se reduce al máximo permitido, se guarda y se informa al usuario. Guardar o lanzar con valores superiores falla con una advertencia. El slider y sus marcas utilizan el rango detectado. Los argumentos que sustituyen el heap se rechazan en Ajustes y en el lanzamiento real.
- Instalación: enlace discreto «Más opciones» con un modal y el flujo de vinculación de Discord existente. No añade otro OAuth ni cambia los permisos del bot.
- Fondo: instalador, desinstalador, actualizador y fallback del launcher usan `Assets/Video/reino-poster.jpg`. Las ilustraciones anteriores dejan de empaquetarse. Las imágenes temáticas de las cards y las noticias son recursos distintos y se conservan.

## Linux: bloqueo real, no soporte terminado

El producto actual tiene `TargetFramework=net10.0-windows` y `UseWPF=true`. WPF solo se ejecuta en Windows: cambiar el RID o empaquetar el EXE en un AppImage no lo convierte en una aplicación Linux. No se afirma compatibilidad por haber pasado pruebas de backend.

Dependencias que deben sustituirse antes de distribuir una versión Linux:

| Área | Implementación actual | Trabajo necesario |
| --- | --- | --- |
| Interfaz | WPF, WindowChrome, fuentes y HWND | Portar vistas y modales a una UI multiplataforma, propuesta: Avalonia |
| Vídeo e intro | MediaElement y renderizado WPF | Reproducción nativa Linux con pausa/restauración y fallback |
| Credenciales | Windows DPAPI | Secret Service/keyring; no guardar tokens sin protección por defecto |
| Java | Temurin Windows ZIP, javaw.exe, jvm.dll | Temurin Linux por arquitectura, tar.gz, permisos de ejecución, extracción segura y symlinks del runtime |
| Lanzamiento | Flags y gestión de ventanas Windows | Validar natives Linux, permisos, X11/Wayland y restauración del launcher |
| Actualización | Setup EXE, Authenticode e Inno | Payload Linux firmado, instalación atómica y relanzamiento, conservando datos |
| Instalación/desinstalación | Inno Setup, registro Windows y accesos .lnk | Paquete Linux y entrada .desktop; borrado opcional con las mismas protecciones |
| Rutas | AppData y comparaciones sin distinguir mayúsculas | XDG para datos/config/caché y rutas Linux sensibles a mayúsculas |

Primeros objetivos: Ubuntu 22.04/24.04 y Mint 21/22 x86_64. Después Debian/Fedora. Paquete separado por SO; no añadir dependencias Linux al instalador Windows. Preferir un paquete autocontenido sin navegador Chromium, medir tamaño antes de elegir AppImage/.deb. Ni todas las distribuciones ni todas las GPU pueden certificarse al 100 % sin una matriz de pruebas real.

La CI añade Ubuntu para ejecutar el backend compartido de modpacks y el presupuesto de RAM. Esas pruebas no arrancan Minecraft ni validan una UI Linux. En la máquina de desarrollo hay WSL pero no una distribución instalada; falta un escritorio Linux de prueba para la validación visual y de juego.

Fuentes: [Microsoft: WPF solo en Windows](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/), [Avalonia: Linux](https://docs.avaloniaui.net/docs/platform-specific-guides/linux).

## Investigación Microsoft / XboxAuthNet

XboxAuthNet implementa OAuth de Microsoft y autenticación Xbox (Basic, Full, SISU); eso no implica por sí solo una sesión de Minecraft Java con licencia y perfil. Hace falta completar el intercambio con Minecraft Services, comprobar licencia/perfil, refrescar tokens y almacenar las credenciales de forma segura.

Con un Client ID propio, la documentación de CmlLib indica que hay que habilitarlo para Minecraft para evitar `403 Forbidden`. Los ejemplos SISU con `XboxGameTitles.MinecraftJava` usan el ID del juego oficial; no representan la aprobación de un Client ID de Destiny. Es un camino técnicamente distinto, cuya disponibilidad para nuestro caso no se ha verificado con una cuenta real. No afirmar que la dependencia elimina el trámite para una app propia.

Se ha implementado un [experimento SISU opt-in](SISU-EXPERIMENT.md), habilitado también en producción desde 1.0.5. La opción experimental solo separa los datos de prueba. Las credenciales se introducen únicamente en la página de Microsoft dentro de WebView2. El propietario confirmó login, lanzamiento y renovación con su cuenta real; las pruebas aisladas cubren cancelación, validación de licencia y errores. No se ha certificado cada combinación de MFA o restricciones familiares. Linux queda aplazado por decisión del propietario; esta prueba es Windows y no genera instalador.

Fuentes: [XboxAuthNet](https://github.com/AlphaBs/XboxAuthNet), [Client ID y allowlist](https://cmllib.github.io/CmlLib.Core-wiki/en/auth.microsoft/xboxauthnet.game.msal/clientid/), [restricciones y ejemplo SISU](https://cmllib.github.io/CmlLib.Core-wiki/en/auth.microsoft/xboxauthnet.game/xboxauth/).

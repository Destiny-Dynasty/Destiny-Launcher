# Capturas del instalador

El fondo predeterminado del instalador es `DestinyLauncher/Assets/Video/reino-poster.jpg`, el mismo fotograma estático que usan el launcher, el actualizador y el desinstalador. Para una prueba local se puede pasar `-BackgroundPath "ruta\captura.png"` a `tools/Build-Installer.ps1` (también acepta JPEG). No se seleccionan fondos antiguos automáticamente.

Usa una captura horizontal, preferiblemente de 1920×1080 o superior, sin HUD ni texto. El centro superior queda detrás del logotipo; la franja inferior contiene las acciones. La imagen se recorta de forma proporcional para llenar una ventana de 840×520 y recibe un degradado oscuro al compilar. El archivo original se conserva intacto.

El logotipo se mantiene separado para cambiar la captura sin retocar la marca. Estos recursos se incluyen en el instalador y no requieren conexión para mostrar la portada.

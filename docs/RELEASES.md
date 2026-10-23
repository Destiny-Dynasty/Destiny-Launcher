# Distribución y actualizaciones

Para usuarios, compartir únicamente `DestinyLauncherSetup.exe` del instalador offline.
Incluye el programa autocontenido, dependencias y avisos; no necesita un ZIP junto a él.
Instala por usuario en `%LOCALAPPDATA%\Programs\Destiny Launcher`, registra la
desinstalación en Windows y conserva juegos y preferencias al actualizar.

## Java administrado

Cada manifiesto del modpack admite `javaVersion` (entero, 21 por defecto para feeds
anteriores). `tools/New-PackManifest.ps1 -JavaVersion 21` conserva ese requisito.
Se descarga el JRE Windows x64 oficial de Eclipse Temurin, se comprueba su SHA-256,
se extrae con límites de tamaño y rutas, y se prueba `java -version` antes de activarlo.
Los archivos `legal` y avisos originales se mantienen. No se modifica PATH ni Java global.

Los runtimes se guardan en `%LOCALAPPDATA%\DestinyLauncher\cache\java`, compartidos
entre variantes con la misma versión principal. La primera preparación ocurre al
instalar/jugar, nunca en el arranque del launcher. Un runtime válido se reutiliza
sin consultas durante siete días. Después se comprueban parches de esa versión;
si falla la conexión se conserva el instalado. No se cambia de versión principal
sin cambiar el requisito del modpack. El marcador de instalación conserva la versión
principal para partidas sin acceso al manifiesto.

## Release oficial

1. Aumentar versión X.Y.Z y compilar con `tools/Publish-Launcher.ps1`.
2. Crear instalador offline con `tools/Build-Installer.ps1 -Offline -OutputPath ...`.
3. Firmar metadatos con `tools/New-LauncherReleaseManifest.ps1`. La clave privada
   permanece fuera del repositorio. La clave pública se incorpora al ejecutable.
4. Publicar una release **estable**, etiqueta `vX.Y.Z`, con `DestinyLauncherSetup.exe`,
   `launcher-update.json` y `launcher-update.sig`. Si se distribuye el web setup,
   añadir también `DestinyLauncher-win-x64.zip` y `DestinyLauncherWebSetup.exe`.
5. El launcher instalado consulta `/releases/latest`, autentica el manifiesto,
   descarga y verifica el instalador y, con la aceptación del jugador, se cierra,
   actualiza en su directorio y vuelve a abrirse. Si el jugador elige Más tarde,
   continúa normalmente. Los portables y las instalaciones experimentales no reciben
   actualizaciones automáticas. No se publican contraseñas ni claves en las releases.

La firma RSA del canal de actualización no es una firma Authenticode de Windows.
SmartScreen puede seguir mostrando un editor desconocido hasta disponer de una
firma de editor reconocida y reputación suficiente.

## Prueba repetible aislada

`tools/UpdatePreview` utiliza el diálogo y descargador de producción y un transporte
HTTP de prueba con una clave efímera. Resuelve una release firmada, muestra progreso,
ejecuta un instalador experimental real y reinicia el launcher instalado. No introduce
URLs de prueba ni excepciones de firma en la aplicación distribuida.

Las carpetas `artifacts/release-validation/update-demo/old` y `new` deben contener los
instaladores 1.1.0 y 1.2.0 compilados con `-Experimental -Offline` y el mismo `-DataRoot`
aislado. El programa solo acepta destinos dentro de `artifacts`; se puede repetir
para volver a observar el aviso y el proceso completo. No compartir esta herramienta
con los jugadores: distribuir el instalador oficial.

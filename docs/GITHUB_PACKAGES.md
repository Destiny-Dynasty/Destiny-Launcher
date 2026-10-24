# Distribución y actualización del modpack

El repositorio público `Destiny-Dynasty/Modpack` contiene una base compartida y reglas para Destiny, Lite y la futura edición Staff. No se mantienen tres copias del pack. La guía completa, los ejemplos de manifiestos y los comandos de publicación están en [MODPACK_VARIANTS.md](MODPACK_VARIANTS.md).

## Flujo actual

- `base/manifest.json`: catálogo común.
- `variants/*.json`: herencia, exclusiones, inclusiones y sustituciones.
- `manifests/*.json`: resultados completos, generados juntos y consumidos por el launcher.
- `Destiny-Dynasty/Mod-Releases/channels/*.json`: canales públicos independientes de cada mod propio, conservando el código privado.

`Build-PackVariants.ps1` prepara todas las variantes habilitadas. `Publish-PackVariants.ps1` publica una sola release compartida y después actualiza sus manifiestos juntos. Los archivos sin cambios reutilizan sus URLs; los nuevos assets se verifican y nunca se sobrescriben. `New-PackManifest.ps1` continúa disponible para generar el catálogo base desde una carpeta local.

Las referencias GitHub pueden fijar una release concreta o seguir `latest`. Las fijas se resuelven al publicar. Las flotantes se consultan al pulsar Jugar y se actualizan independientemente de la versión del pack. Para varios mods en el repositorio compartido se indica su `channel`.

## Comprobaciones y datos personales

Al abrir el launcher se consulta el manifiesto para comparar versiones; no se recorren ni se calculan hashes de todos los mods. Al jugar con la misma versión solo se resuelven los mods flotantes. Un cambio de versión del pack aplica diferencias respecto al índice administrado. Se conservan los archivos editados, eliminados voluntariamente y añadidos por el jugador; Reparar permite restaurar explícitamente los oficiales.

Las descargas se preparan en staging, verificando tamaño y SHA-256 antes de aplicarlas con respaldo. Los directorios administrados son `mods`, `config`, `defaultconfigs`, `resourcepacks`, `shaderpacks`, `kubejs` y `scripts`. Saves, capturas y ajustes personales no se convierten en contenido del pack.

## Pruebas aisladas

```powershell
dotnet run --project tools/PackUpdaterSmoke -c Release
dotnet run --project tools/PackUpdaterSmoke -c Release -- --floating-only artifacts/tests/floating
./tools/Test-PackVariants.ps1
```

Estas pruebas usan fixtures y carpetas independientes de las instalaciones del usuario. Para comprobar una descarga real del canal público sin ejecutar el mod:

```powershell
dotnet run --project tools/PackUpdaterSmoke -c Release -- --live-github-mod artifacts/tests/live-mod
```
# Base común, variantes y mods de GitHub

Repositorio de distribución único: `Destiny-Dynasty/Modpack`. El código de cada mod
puede seguir en su repositorio privado; los jugadores solo descargan los JAR compilados.
El launcher no incluye tokens ni credenciales de GitHub.

## Estructura

```
base/manifest.json          # catálogo completo del pack normal
variants/destiny.json       # reglas de la variante normal
variants/destiny-lite.json  # herencia, exclusiones, inclusiones y sustituciones
variants/destiny-staff.json # preparado, deshabilitado hasta definir acceso/contenido
extras/lite/mods/...        # JAR exclusivos de Lite, si se proporcionan localmente
overrides/lite/config/...   # configuraciones específicas
channels/<mod-id>.json      # punteros públicos a releases de cada mod propio
manifests/*.json            # resultados completos que consume el launcher
```

Las carpetas de instancias siguen separadas: Destiny, Destiny-Lite y Destiny-Staff.
Staff aún no aparece en la interfaz; el bot y la habilitación de usuarios siguen pendientes.

Ejemplo de definición de Lite:

```json
{
  "id": "destiny-lite",
  "enabled": true,
  "extends": "destiny",
  "exclude": ["mods/visual-*.jar"],
  "include": ["extras/lite/mods/performance.jar"],
  "overrides": "overrides/lite"
}
```

`include` también acepta entradas remotas completas (`path`, `url`, `size`, `sha256`)
o referencias GitHub. `overrides` puede ser una carpeta con estructura de instancia,
o una lista de entradas que sustituyan rutas heredadas existentes. Las inclusiones
que colisionan con una ruta existente fallan: la sustitución debe ser explícita.
`exclude` admite glob de rutas y `mod:<id>` cuando el catálogo declara `modId`.
Los JAR locales incluidos por el compositor extraen su ID de `fabric.mod.json`.
Se rechazan ciclos, rutas fuera de los directorios administrados, enlaces, IDs
duplicados declarados y dependencias explícitas `requires` ausentes. Esto no reemplaza
probar la compatibilidad real entre mods ni resuelve rangos de dependencias de Fabric.

## Release fija y latest

Referencia fijada en una definición:

```json
{
  "path": "mods/destiny-patches.jar",
  "github": {
    "repository": "Destiny-Dynasty/Modpack",
    "release": "mod-destiny-patches-0.1.0",
    "asset": "destiny-patches-*.jar"
  }
}
```

Al publicar se resuelve a una URL inmutable, tamaño y SHA-256. El jugador utiliza
siempre ese archivo hasta que se publique un nuevo manifiesto del modpack.

Referencia flotante para varios mods alojados en el mismo repositorio:

```json
{
  "path": "mods/destiny-patches.jar",
  "github": {
    "repository": "Destiny-Dynasty/Modpack",
    "release": "latest",
    "channel": "destiny-patches",
    "asset": "destiny-patches-*.jar"
  }
}
```

El canal es necesario para distinguir el último Patches del último Kingdoms o del
último modpack. `Publish-ModRelease.ps1` mantiene ese JSON después de publicar y
verificar el JAR. Para un repositorio público dedicado a un solo mod puede omitirse
`channel`: entonces se utiliza la API oficial `/releases/latest`, que sigue las
releases estables. El selector admite `*` y `?`, omite fuentes/javadoc y debe coincidir
con un único JAR (seleccionar explícitamente Minecraft, Fabric y cliente si hay varias builds).

El manifiesto compilado con `latest` conserva una instantánea verificable y usa
`schemaVersion: 2`. Los clientes antiguos rechazan ese esquema; deben actualizar el
launcher antes de habilitarlo. Las variantes solo fijas mantienen esquema 1.

Al pulsar Jugar se comprueban únicamente los mods `latest`, incluso si el pack sigue
en 1.4.9. Se aprovecha ETag/304 y se agrupan consultas del mismo origen. El archivo
de destino es estable, por ejemplo `mods/destiny-patches.jar`, aunque la release
publique `destiny-patches-0.1.1.jar`. Así se sustituye el anterior sin duplicarlo.
Las descargas se verifican y se aplican con staging y respaldo. Sin conexión o con
límite de API se conserva la resolución instalada, sin degradarla a la instantánea.
Las modificaciones y eliminaciones voluntarias siguen protegidas; Reparar es la
acción explícita para restaurarlas. Añadir mods ajenos sigue estando permitido.

Un `latest` debe reservarse para builds compatibles con la versión de Minecraft,
Java y mods del pack. El sistema verifica integridad, no puede garantizar que un mod
nuevo no rompa compatibilidad. Una release ambigua o sin SHA-256 se rechaza.

## Publicar todo el modpack

Desde PowerShell 7 y un checkout actualizado de Modpack:

```powershell
./tools/Build-PackVariants.ps1 -RepositoryRoot . -PackDirectory 'E:/Pack-Normal' `
  -ContentDirectory . -Version 1.4.9 -OutputDirectory 'E:/Builds/modpack-1.4.9'
./tools/Publish-PackVariants.ps1 -RepositoryRoot . -PreparedDirectory 'E:/Builds/modpack-1.4.9'
```

El generador de base reutiliza URLs de archivos sin cambios. El compositor genera
todas las variantes habilitadas y deduplica sus extras locales. El publicador sube
una sola release compartida, verifica los digests y confirma todos los manifiestos
en un único commit, después de que los assets sean públicos. Las salidas se conservan
para revisión y recuperación. Nunca sobrescribe una release ya publicada.
Puede omitirse `PackDirectory` para preparar solo cambios de reglas sobre el catálogo
existente. Los scripts antiguos por edición siguen disponibles para compatibilidad;
el flujo recomendado es Build-PackVariants + Publish-PackVariants.

## Publicar solo un mod propio

```powershell
./tools/Publish-ModRelease.ps1 -RepositoryRoot . -ModId destiny-patches `
  -Version 0.1.1 -JarPath 'E:/Destiny-Patches/build/libs/destiny-patches-0.1.1.jar'
```

Solo se sube ese JAR compilado, a `mod-destiny-patches-0.1.1`; después se actualiza su
canal público. El comando no cambia la versión ni los manifiestos del modpack.
Los jugadores que tengan `latest` recibirán la nueva build al jugar; los fijados a
0.1.0 seguirán en 0.1.0.

El repositorio de distribución contiene `templates/publish-private-mod.yml` para
copiar al repositorio privado del mod. Requiere el secreto `MOD_DISTRIBUTION_TOKEN`,
con permiso Contents de escritura exclusivamente sobre Modpack. El token vive en
GitHub Actions, nunca en el launcher ni en un manifiesto. También puede publicarse
manualmente usando la sesión local de `gh`. No se hicieron públicos repositorios
de código privado ni se crearon credenciales persistentes automáticamente.

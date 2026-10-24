# Base compartida, variantes y mods independientes

## Repositorios

- **Destiny-Dynasty/Modpack**: catálogo `base/manifest.json`, reglas `variants/*.json`,
  manifiestos completos `manifests/*.json` y assets del pack.
- **Destiny-Dynasty/Mod-Releases**: releases de JAR propios o parcheados y canales
  `channels/<mod-id>.json`. Las fuentes privadas permanecen en su repositorio original.

Las instancias Destiny, Destiny-Lite y Destiny-Staff son independientes. Staff se
muestra únicamente después de verificar el acceso mediante Destiny Bot y Discord.

## Versiones

Formato `AÑO.MAYOR.MENOR[-etapa]`: `2026.0.1-alpha`, `2026.0.1-beta`, `2026.0.1`.
También se admite `rc` e iteraciones como `-alpha.2`. Las tres variantes llevan la
misma versión; las etapas se publican como pre-release en GitHub.
El launcher compara el identificador del pack como texto: no necesita convertirlo
a System.Version. Este cambio no modifica la versión del ejecutable del launcher.

## Composición

`extends` hereda otra variante; `exclude` admite glob de rutas o `mod:<id>` cuando
se declara el ID; `include` añade archivos y `overrides` sustituye rutas existentes.
Las inclusiones que colisionan se rechazan. `fabricLoaderVersion` puede personalizarse
por variante y se hereda. Todas usan actualmente 0.19.5 por los requisitos de los JAR.

Las entradas remotas incluyen path, URL HTTPS, size y sha256. Los archivos locales
van en `extras/<variante>/<directorio-administrado>/...`; `overrides` admite una carpeta
con estructura de instancia. Se rechazan ciclos, rutas inseguras y IDs declarados
duplicados. Las pruebas no sustituyen una partida real para comprobar compatibilidad.

## Mods latest

Destiny Dynasty está configurado como override del pack normal y se hereda también en Lite y Staff:

```json
{
  "path": "mods/destiny-dynasty.jar",
  "modId": "destinydynasty",
  "github": {
    "repository": "Destiny-Dynasty/Mod-Releases",
    "release": "latest",
    "channel": "destiny-dynasty",
    "asset": "destiny-dynasty-*.jar"
  }
}
```

El canal es obligatorio para distinguir cada mod en este repositorio compartido.
No utilizar `/releases/latest` del repositorio completo. Combat 0.1.1+ef20.14.17 usa la ruta estable mods/destiny-combat.jar y una URL fija, compartida entre las tres variantes; no usa latest. Destiny Dynasty tiene como snapshot actual la release 0.1.1. La primera build de Destiny Dynasty
es la importada del pack normal existente, sin recompilar el HEAD privado.

El compositor genera schemaVersion 2 y una instantánea con URL, tamaño y SHA-256.
Al pulsar Jugar se revisan los mods latest, se agrupan consultas y se usa ETag/304.
El nombre de destino estable permite sustituir el JAR sin duplicarlo. Sin conexión,
se conserva la última resolución instalada. Las modificaciones/eliminaciones locales
siguen protegidas; Reparar restaura explícitamente los archivos administrados.
Publicar en el canal solo builds compatibles con Minecraft, Java, Fabric y dependencias.

Para fijar un mod: usar su tag exacto en `release`, sin `channel`. El compositor
resuelve la URL y el checksum al publicar; no se comprueba latest durante el juego.

## Publicar un mod

Desde un checkout limpio y actualizado de **Mod-Releases**:

```powershell
./tools/Publish-ModRelease.ps1 -RepositoryRoot . -ModId destiny-dynasty `
  -Version 0.1.1 -JarPath 'E:/destiny-realms/build/libs/destinydynasty-0.1.1.jar'
```

La versión y la ruta son ejemplos. Se publica un solo JAR, se verifica el digest y
se actualiza el canal únicamente después. Nunca se sobrescriben builds publicadas.
El comando no cambia la versión del pack. También se puede publicar una release estable desde GitHub con tag mod-<id>-<versión> y un único JAR <id>-<versión>.jar: el workflow de Mod-Releases actualiza su canal automáticamente. La sincronización verifica digest y es idempotente. El mismo script está aquí por conveniencia,
pero exige un checkout de Mod-Releases como RepositoryRoot y rechaza Modpack.

El repositorio privado puede usar `templates/publish-private-mod.yml`, adaptando
la selección del JAR cuando hay varios módulos. Necesita el secreto
`MOD_DISTRIBUTION_TOKEN` con Contents: write únicamente sobre Mod-Releases.
La configuración de ese secreto no se hace automáticamente ni se distribuye al cliente.

## Publicar el pack

Desde un checkout actualizado de Modpack, PowerShell 7 y GitHub CLI:

```powershell
./tools/Build-PackVariants.ps1 -RepositoryRoot . -PackDirectory 'E:/Pack-Normal' `
  -ContentDirectory . -Version 2026.0.2-alpha -OutputDirectory 'E:/Builds/2026.0.2-alpha'
./tools/Publish-PackVariants.ps1 -RepositoryRoot . -PreparedDirectory 'E:/Builds/2026.0.2-alpha'
```

Omitir PackDirectory para regenerar usando el catálogo existente. Al cambiar el
catálogo conservar la ruta estable de Destiny Dynasty para que su override siga aplicando;
no volver a añadir otra copia del mismo mod con el nombre versionado del JAR.
Los assets se publican y verifican antes de actualizar los manifiestos. Las URLs de
archivos sin cambios se reutilizan, por lo que una release usada por un manifiesto
no debe borrarse sin consolidar antes sus assets. La migración inicial sí consolida
todas las referencias antes de retirar las releases anteriores.
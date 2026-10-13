# Distribución y actualización del modpack

El launcher ya admite un manifiesto HTTPS por edición (`destiny` y `destiny-lite`). En cada comprobación descarga el manifiesto pequeño, calcula los SHA-256 de los archivos administrados y sólo baja los que falten o hayan cambiado. También elimina archivos retirados únicamente si constaban en su índice administrado, y conserva modificaciones locales. Saves, ajustes personales y archivos ajenos al manifiesto no se reemplazan.

## Arquitectura recomendada

- Un repositorio público sirve `manifests/destiny.json` y `manifests/destiny-lite.json` desde `raw.githubusercontent.com`. El launcher no necesita iniciar sesión ni consumir la API REST de GitHub.
- Los archivos pequeños que tengamos permiso de distribuir pueden vivir como archivos normales del mismo repositorio, por ejemplo bajo `files/`. El generador les asigna un nombre direccionado por contenido (`SHA-256-nombre`): si cambia un mod, obtiene una URL nueva; si no cambia, el launcher reutiliza el mismo archivo y no lo vuelve a descargar.
- Las URLs de cada archivo son independientes. Podemos mezclar GitHub Raw, assets de Releases y fuentes oficiales/CDN permitidas por los autores. Releases son opcionales y útiles para archivos grandes.
- Mantener los manifiestos en Git permite revisar el historial de versiones. El repositorio normal no es buen almacén para binarios grandes o que se actualicen continuamente: los objetos anteriores permanecen en el historial. Para esos casos conviene usar Releases o la fuente oficial del mod.

GitHub advierte a partir de 50 MiB y bloquea archivos normales de 100 MiB o más. El generador de modo Raw avisa en el primer umbral y falla en el segundo. [Límites de repositorios](https://docs.github.com/en/repositories/creating-and-managing-repositories/repository-limits) · [Archivos grandes](https://docs.github.com/en/repositories/working-with-files/managing-large-files/about-large-files-on-github).

## Publicar desde un repositorio Raw

Genera una salida nueva y vacía por versión. `RepositoryFilesBaseUrl` termina en `/files` y apunta a la rama pública que servirá el contenido:

```powershell
./tools/New-PackManifest.ps1 `
  -PackId destiny `
  -PackDirectory ./pack-content/destiny `
  -Version 1.5.0 `
  -RepositoryFilesBaseUrl https://raw.githubusercontent.com/OWNER/REPOSITORY/main/files `
  -OutputDirectory "$env:TEMP/destiny-1.5.0"
```

En el checkout del repositorio de distribución, copia `repository-files/*` generado a `files/` y `manifests/destiny.json` generado a `manifests/destiny.json`; repite para Lite. Revisa los cambios, confirma y sube ambos directorios juntos. Los archivos de `files/` se agregan, no se sustituyen: los nombres con SHA mantienen válidas las versiones anteriores. El launcher apunta a estos manifiestos de forma interna; no se editan desde Ajustes.

El generador conserva el modo basado en Releases existente. Pasa `-ReleaseBaseUrl https://github.com/OWNER/REPOSITORY/releases/download/ETIQUETA` para crear `release-assets/` en vez de `repository-files/`; cada entrada del manifiesto puede apuntar a un origen distinto.

## Primera instalación y actualizaciones

El launcher instala desde los archivos enumerados en el manifiesto. En una instalación vacía descargará todos los archivos de esa edición; en las siguientes comprobaciones sólo descargará los que tengan una diferencia de tamaño o SHA-256. No hace falta publicar un ZIP completo del modpack.

Los archivos gestionados se restringen a `mods`, `config`, `defaultconfigs`, `resourcepacks`, `shaderpacks`, `kubejs` y `scripts`. El actualizador prepara las descargas en staging, comprueba tamaño y SHA-256 durante el streaming y aplica los cambios con respaldo temporal. Si falla la operación, intenta restaurar los archivos previos.

## Derechos y seguridad

El prototipo del repositorio contiene únicamente archivos de texto sintéticos. Antes de subir un `.jar`, confirma que el autor o su licencia permite redistribuirlo. Si no, conserva el checksum y la URL oficial de descarga autorizada en el manifiesto, o usa el mecanismo oficial de la plataforma.

SHA-256 detecta corrupción o contenido que no corresponde al manifiesto, pero no demuestra quién autorizó un manifiesto. Como el manifiesto puede ordenar instalar código ejecutable (mods), protege el repositorio/cuenta con 2FA y protección de rama; antes de una distribución amplia, añadiremos firma criptográfica del manifiesto con la clave pública integrada en el launcher.

## Prueba de humo

`tools/PackUpdaterSmoke` prueba contra dos manifiestos públicos de prueba: instala V1, confirma que repetir V1 no descarga nada, edita localmente un archivo y pasa a V2. V2 cambia un solo archivo; el test verifica que sólo ese se actualiza y que la modificación local se conserva.

```powershell
dotnet run --project ./tools/PackUpdaterSmoke -- `
  https://raw.githubusercontent.com/OWNER/REPOSITORY/main/test-fixtures/v1.json `
  https://raw.githubusercontent.com/OWNER/REPOSITORY/main/test-fixtures/v2.json
```

Los fixtures del feed son de texto, no mods instalables. Este test ejercita el mismo `GitHubPackUpdater` de la aplicación dentro de una carpeta temporal y no toca la instancia normal del usuario.

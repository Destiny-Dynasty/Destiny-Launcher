# Dependencias del launcher Linux

Esta es la lista de paquetes que el instalador debe comprobar antes de colocar o ejecutar Destiny Launcher. En Ubuntu y Debian, el paquete `.deb` debe declarar las dependencias como `Depends` para que APT las resuelva durante la instalación. Un instalador gráfico propio debe detectar las que falten y pedir autorización al gestor de paquetes del sistema; no debe intentar instalar paquetes sin esa autorización.

## Paquetes del sistema

| Paquetes | Para qué se necesitan | Comportamiento si faltan |
| --- | --- | --- |
| `libx11-6`, `libice6`, `libsm6`, `libfontconfig1` | Bibliotecas nativas de la interfaz Avalonia en Linux. | La interfaz puede no iniciar o no cargar sus recursos gráficos. |
| `xdg-utils` | Proporciona `xdg-open`, usado para abrir carpetas y enlaces con las aplicaciones predeterminadas. | Los botones para abrir carpetas o enlaces no funcionarán. |
| `libsecret-tools` | Proporciona `secret-tool`, usado para guardar credenciales Microsoft mediante Secret Service. | El inicio de sesión no podrá guardar credenciales de forma segura. |
| `gnome-keyring` o un proveedor equivalente de Secret Service | Servicio de sesión que almacena las credenciales cifradas. GNOME Keyring es el proveedor habitual en Ubuntu Desktop. | El inicio de sesión persistente no estará disponible mientras no haya un proveedor desbloqueado. |
| `pkexec`, `dpkg` | Permiten solicitar autorización y desinstalar el paquete Debian desde la interfaz del launcher. | Se puede desinstalar desde el gestor de aplicaciones del sistema. |
| `libvlc5`, `libvlccore9`, `vlc-plugin-base`, `vlc-plugin-video-output` | Motor nativo y plugins que reproducen el video MP4 de la portada. `LibVLCSharp` aporta el enlace .NET, pero no estas bibliotecas nativas. | El launcher conserva el póster estático y muestra un aviso visible si LibVLC o sus plugins no están disponibles. |

## Componentes que no se instalan por APT

- La publicación de Linux es autocontenida; el usuario no necesita instalar el SDK ni el runtime de .NET.
- El launcher descarga Java Temurin para la arquitectura compatible y valida el checksum antes de usarlo; Java no es una dependencia previa del instalador.
- Las solicitudes HTTPS del catálogo usan `HttpClient`; se espera que el sistema tenga su paquete base de certificados (`ca-certificates`).

## Lista actual para el paquete Debian

```text
libx11-6 libice6 libsm6 libfontconfig1 fonts-liberation xdg-utils libsecret-tools gnome-keyring libvlc5 libvlccore9 vlc-plugin-base vlc-plugin-video-output pkexec dpkg
```

Los nombres/versiones de paquetes pueden variar entre distribuciones y versiones. El instalador debe consultar el gestor de paquetes de la distribución antes de instalar, y presentar los faltantes al usuario.

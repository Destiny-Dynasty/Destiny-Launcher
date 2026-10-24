# Instalador de Destiny Launcher

El instalador conserva Inno Setup 6.5.4 como motor. Su interfaz personalizada utiliza controles nativos, sin navegador, DLL de terceros ni runtime adicional. La portada, el progreso real y la finalización se muestran en la misma ventana de 840×520; el directorio y el acceso directo se configuran en «Instalación personalizada». La instalación rápida conserva el directorio anterior si existe, o utiliza `%LOCALAPPDATA%\Programs\Destiny Launcher`. No activa inicio automático con Windows.

Se registra en Inicio y en Aplicaciones instaladas de Windows. La carpeta del programa es independiente de los datos del launcher y de las instalaciones de Minecraft. LICENSE y THIRD_PARTY_NOTICES.md acompañan al ejecutable.

## Compilar el experimento

```powershell
./tools/Publish-Launcher.ps1 -OutputPath artifacts/installer-experiment/payload
./tools/Build-Installer.ps1 -PublishPath artifacts/installer-experiment/payload -OutputPath artifacts/installer-experiment/offline -Offline -Experimental -DataRoot artifacts/installed-experiment/data
./tools/Build-Installer.ps1 -PublishPath artifacts/installer-experiment/payload -OutputPath artifacts/installer-experiment/online -Experimental -DataRoot artifacts/installed-experiment/data
```

`DestinyLauncherSetup.exe` incluye todos los archivos. `DestinyLauncherWebSetup.exe` descarga el ZIP de la release correspondiente a su versión y comprueba el SHA-256 fijado al compilar. La descarga pública requiere que exista ese asset en GitHub; no utiliza una URL mutable de «latest».

No se modifica `publish` al utilizar las rutas anteriores. El experimento tiene registro, accesos y datos separados de la instalación normal.

## Repintado del progreso

`InstallerProgressSurface.iss` compone fondo, texto y barra en dos bitmaps persistentes. Un único panel con HWND propio presenta el último fotograma completo mediante `BitBlt` en `WM_PAINT`; procesa `WM_ERASEBKGND` sin un borrado separado. Se intercambian los buffers solo después de terminar la composición. Esto evita el borrado del padre que afectaba a los controles gráficos sin ventana. El callback se retira antes de liberar el panel y los bitmaps, y no se crea en instalaciones silenciosas.

Se utiliza el patrón documentado por Microsoft para [contextos de memoria](https://learn.microsoft.com/en-us/windows/win32/gdi/memory-device-contexts), [WM_ERASEBKGND](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-erasebkgnd) y [SetWindowSubclass](https://learn.microsoft.com/en-us/windows/win32/api/commctrl/nf-commctrl-setwindowsubclass), sin copiar bibliotecas de terceros. Como referencia del comportamiento anterior se revisó [TBitmapImage de Inno Setup 6.5.4](https://github.com/jrsoftware/issrc/blob/is-6_5_4/Components/BitmapImage.pas).

Las pruebas `-UiAudit -Experimental -Offline` instalan únicamente en su carpeta de `artifacts`, con AppId separado. `Capture-InstallerFrames.ps1` registra la ventana a 60 fps. `Analyze-InstallerFrames.py --expect-status` comprueba cada fotograma: bloques de borrado, desaparición de la barra, retrocesos del relleno y ausencia del texto. Los temporizadores y la ralentización de prueba no se incluyen en los instaladores normales.

## Capturas del servidor

Consulta [Assets/README.md](Assets/README.md). Por defecto se utiliza `DestinyLauncher/Assets/Video/reino-poster.jpg`, compartido con el launcher y el desinstalador. También se puede pasar `-BackgroundPath "ruta/captura.jpg"`. El recurso original no se modifica y el instalador muestra la portada sin conexión.

## Actualizaciones y desinstalación

Los parámetros silenciosos del motor se conservan para las actualizaciones automáticas. La interfaz personalizada no se crea durante `/SILENT` o `/VERYSILENT`. `/AUTORESTART=1` sigue devolviendo el launcher al usuario al terminar una actualización.

La desinstalación interactiva utiliza la ventana propia de Destiny; cancelar aborta antes de eliminar el programa. Las opciones de eliminar perfiles o instalaciones del juego están desmarcadas por defecto. Una desinstalación administrativa silenciosa conserva los datos personales.

Los workflows preparan artefactos y un borrador de release. La firma Windows mediante SignPath depende de aprobación y configuración externas; compilar un instalador no confirma que esté firmado ni que el canal público de actualizaciones esté publicado.

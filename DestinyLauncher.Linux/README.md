# Destiny Launcher para Linux

El proyecto Linux usa Avalonia y comparte modelos, configuración, rutas, recursos visuales y reglas de instalación con la edición WPF de Windows.

## Estado

- Inicio, instalación y ajustes siguen la composición visual del launcher de Windows: navegación lateral, portada, carrusel de noticias, selector de edición y panel de juego.
- La portada, marca, imágenes de noticias y arte de Destiny/Destiny Lite son los mismos archivos de `DestinyLauncher/Assets` usados por Windows.
- Inicio, instalación, ajustes, perfiles, multicuentas, reparación y menú contextual conservan la composición y los controles del launcher Windows. `tools/Compare-LauncherUi.ps1` compara la geometría con la referencia WPF en dos tamaños; la salida incluye capturas de las pantallas y diálogos.
- La intro utiliza los 73 fotogramas originales de Windows. El MP4 `reino-rayos-v6.mp4` se reproduce con LibVLC y conserva el póster si falla. Al minimizar, el reproductor se pausa; al ocultar la ventana o preparar el juego, se libera.
- Rutas XDG para datos, configuración, caché y registros; `--data-root` mantiene un modo aislado para desarrollo.
- Lee los manifiestos HTTPS públicos de Destiny y Destiny Lite, guarda la edición seleccionada e instala o actualiza sus archivos gestionados.
- El inicio preliminar de Microsoft abre el navegador predeterminado, recibe el callback por loopback y guarda credenciales en Secret Service. El redirect aún debe probarse con el client ID Xbox/Minecraft real.
- El perfil offline no inicia sesión en el servidor Destiny. En la VM el juego llegó a abrirse, pero falló por una limitación gráfica aparente; falta probarlo en un equipo Linux nativo.
- Java Temurin se descarga para Linux x64 o ARM64, se valida por checksum y se guarda en la caché del launcher.

## Dependencias

Requiere el SDK .NET 10 para desarrollo. La publicación autocontenida no necesita que el usuario instale .NET. La lista anotada de dependencias de ejecución y los requisitos para el instalador están en [`docs/LINUX-INSTALLER-DEPENDENCIES.md`](../docs/LINUX-INSTALLER-DEPENDENCIES.md).

El paquete `.deb` declara las dependencias de interfaz, integración de escritorio, Secret Service y video para que APT pueda resolverlas. Para preparar manualmente el Ubuntu de pruebas:

```sh
sudo apt install libx11-6 libice6 libsm6 libfontconfig1 fonts-liberation xdg-utils libsecret-tools gnome-keyring libvlc5 libvlccore9 vlc-plugin-base vlc-plugin-video-output
```

En Instalación, «Gestionar instalaciones» permite desinstalar cada modpack con confirmación explícita y abrir la desinstalación del paquete Debian. La desinstalación del launcher solicita autorización al sistema y conserva los perfiles, ajustes y modpacks; también puede hacerse desde el gestor de aplicaciones. La instalación Debian comprueba actualizaciones estables firmadas al abrirse; tras aceptar, descarga y verifica el paquete y abre el instalador del sistema. La instalación requiere intervención del usuario y el flujo completo aún debe probarse en Linux.

## Desarrollo

```sh
dotnet run --project DestinyLauncher.Linux/DestinyLauncher.Linux.csproj
```

Para crear un `.deb` autocontenido en Ubuntu, con .NET SDK 10 y `dpkg-deb` instalados:

```sh
bash tools/package-linux.sh linux-x64
```

Para ARM64, usa `bash tools/package-linux.sh linux-arm64`. Los paquetes se generan bajo `artifacts/linux/`. Los datos de usuario y las instalaciones del juego quedan fuera del paquete y se conservan al desinstalarlo.

La vista previa `linux-x64` de esta iteración ya se compiló y copió a la carpeta compartida de la VM. Esta compilación no se abrió ni se ejecutó en Ubuntu; queda lista para que el usuario la abra desde la VM.

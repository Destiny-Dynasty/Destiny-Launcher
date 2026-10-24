# Autenticación Microsoft / SISU

El login Microsoft queda habilitado en las compilaciones oficiales desde 1.0.5. La variante de prueba conserva sus datos aislados mediante `-SisuExperiment`; no se migran ni se borran credenciales del usuario. El instalador público fijo de `v1.0.0` se conserva.

```powershell
./tools/Publish-Launcher.ps1 -Experiment -SisuExperiment
```

Ejecutable: `publish-experiment/DestinyLauncher.exe`. Sin instalador. La compilación usa una raíz de datos separada por defecto: `%LOCALAPPDATA%/DestinyLauncher-SisuExperiment`. Se puede pasar `--data-root` para una prueba aislada adicional. Nunca importar tokens desde Minecraft Launcher, otros launchers o cuentas del usuario.

## Prueba manual

1. Abrir el EXE experimental y pulsar la tarjeta del nombre. Elegir «Iniciar sesión con Microsoft». Con multicuentas activo, abrir «Gestionar cuentas», «Añadir cuenta» y «Microsoft».
2. Autorizar en la ventana de Microsoft. La ruta experimental usa el Client ID público del juego `00000000402b5328` (`XboxGameTitles.MinecraftJava`), por lo que no se presenta como una app de Destiny registrada y aprobada. No pedir al usuario contraseñas dentro de formularios propios.
3. Comprobar OAuth → Xbox SISU → token Minecraft Java → licencia → perfil/UUID. No guardar ni seleccionar una cuenta si falla cualquiera de esos pasos.
4. Pulsar Jugar solo cuando se quiera probar el juego. El lanzamiento renueva la sesión sin abrir otro navegador; si necesita autorización nueva, informa del fallo. No cambia a offline automáticamente. El juego recibe UUID, access token y tipo `msa` de esa cuenta, nunca el UUID derivado del nickname offline.
5. Probar cancelar, MFA, cuenta sin Java, fallo 403 de Minecraft, cierre y reapertura del launcher, cambio de cuenta local/Microsoft y cerrar sesión de Microsoft. El nombre premium no es editable como si fuera una cuenta local.

## Gestor de cuentas

En multicuentas, «Gestionar cuentas» muestra las cuentas locales y Microsoft con su cabeza. «Añadir cuenta» abre un selector Offline/Microsoft: Offline muestra el campo del nombre; Microsoft abre el flujo de autorización con el gestor como propietario. El símbolo oficial de Microsoft acompaña al nombre del proveedor en la tarjeta, el desplegable y la lista.

Las altas locales y las eliminaciones se mantienen como borrador hasta Guardar. Cancelar conserva las cuentas previas y elimina del almacén local únicamente las nuevas cuentas Microsoft vinculadas durante ese borrador. Reautorizar una cuenta ya existente renueva su sesión sin duplicarla. Una cuenta local y una Microsoft con el mismo nickname permanecen separadas por su tipo/UUID.

El 24 de octubre de 2026 el propietario confirmó una prueba humana satisfactoria de inicio de sesión, lanzamiento del juego y persistencia/renovación tras reabrir el launcher. Esa confirmación corresponde a su cuenta y equipo; no significa que estén cubiertas todas las cuentas, MFA o restricciones familiares. El agente no introdujo credenciales ni realizó ese lanzamiento real.

## Credenciales y diagnóstico

- `microsoft-accounts.bin` está cifrado con Windows DPAPI del usuario actual y separado del JSON de configuración. La biblioteca escribe primero en una transacción en memoria; solo se confirma después de verificar el resultado y la cancelación.
- El WebView2 usa un perfil InPrivate dentro de `cache/microsoft-webview`. Sin lectura de HTML/formularios/cookies, sin mensajes del navegador hacia el host ni código inyectado. El redirect se comprueba por HTTPS, host, ruta y estado aleatorio.
- No se registran respuestas OAuth, refresh/access tokens ni mensajes crudos del SDK. Los errores de Microsoft se convierten a mensajes visibles acotados.
- «Cerrar sesión de Microsoft» elimina las credenciales locales de esa cuenta. El borrado opcional de perfiles del desinstalador también elimina el almacén Microsoft. No revoca globalmente la cuenta Microsoft.
- Si Windows no tiene Microsoft Edge WebView2 Runtime, hay que instalar el runtime oficial para esta prueba. No se descarga ni instala automáticamente como parte de este experimento.

Fuentes: [XboxAuthNet](https://github.com/AlphaBs/XboxAuthNet), [ejemplo CodeFlow + XboxSisuAuth](https://github.com/CmlLib/CmlLib.Core.Auth.Microsoft/blob/master/examples/ConsoleTest/Program.cs), [documentación SISU](https://cmllib.github.io/CmlLib.Core-wiki/en/auth.microsoft/xboxauthnet.game/xboxauth/).

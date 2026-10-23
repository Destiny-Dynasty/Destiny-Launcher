# Vinculación de Discord y acceso Staff

El módulo `launcher` de Destiny Bot comparte servidor web y callback con la dashboard. No concede sesiones ni permisos administrativos del panel.

## Servidor de pruebas

- Base: `http://66.45.252.226:24431`
- Callback existente: `/auth/discord/callback`.
- Módulo: `bot/modules/launcher.js`.
- Servicio: `bot/features/launcher/service.cjs`.
- Deben estar activos el bot y la dashboard, con el OAuth existente configurado.

Configurar opcionalmente en `destinybot/bot/.env`:

```dotenv
LAUNCHER_STAFF_ROLE_IDS=ID_ROL_1,ID_ROL_2
```

Si está vacío, utiliza los `adminRoleIds` del puente. Si tampoco hay ninguno, permite al propietario o a miembros con Administrator. Una lista explícita sustituye ese criterio. No utiliza los permisos del panel de anuncios. Recargar las reglas con `destinybot js reload launcher`; los cambios del sistema OAuth requieren `destinybot js restart` (solo reinicia Node, no Minecraft).

## Flujo

Ajustes → Discord → Vincular Discord. El cliente comprueba disponibilidad, genera un secreto aleatorio y envía solo su SHA-256. El navegador abre la autorización usando el callback ya registrado. El bot comprueba la identidad y sus roles; el launcher recoge una sola vez el resultado demostrando que conoce el secreto original.

La sesión dura 30 días y el bot vuelve a consultar los roles al verificarla. Se guarda únicamente el hash del token en `destinybot/data/js/launcher/sessions.json`, que se conserva al reiniciar. El launcher guarda la credencial cifrada con Windows DPAPI en su directorio de datos (`discord-session.bin`), separada de los nombres offline y de `launcher.json`. Desvincular revoca la sesión del bot. Desinstalar con eliminación de perfiles también elimina la credencial local.

Staff solo se muestra tras verificar el permiso. Se comprueba al abrir el launcher, al elegir Staff y antes de jugar esa variante. Si el servicio falla no se concede acceso nuevo ni se conserva una autorización en memoria como si estuviera verificada; Destiny y Lite siguen disponibles. El diálogo muestra error de conexión, Reintentar y Cancelar. No se ejecutan comprobaciones continuas durante una partida.

La vinculación es del launcher con Discord; no transforma el nombre offline en una cuenta premium ni cambia permisos dentro de Minecraft.

## Producción

Cambiar `DASHBOARD_PUBLIC_URL` y registrar su callback HTTPS en Discord. Actualizar `DiscordLinkService.DefaultEndpoint` y publicar el launcher correspondiente; las credenciales están asociadas al origen y se debe vincular de nuevo al cambiarlo. Ningún Client Secret ni token del bot se incluye en el cliente.

El acceso está integrado, pero el contenido del pack Staff sigue pendiente de definir/publicar. No se ha creado un pack ficticio ni se han modificado los packs Destiny o Lite.

## Verificación

- `node --test test/*.test.cjs` en el directorio bot: callback OAuth simulado, servicio a través del gestor de módulos, acceso y revocación, expiración, sesión persistente y separación de la dashboard.
- `dotnet run --project tools/DiscordLinkSmoke -c Release`: cliente, prueba del secreto, DPAPI, restauración, revocación, cancelación y servidor sin respuesta.
- `dotnet run --project tools/UiSmoke -c Release -- artifacts/discord-ui --discord-only`: Staff visible/oculto y modal de error.
- Despliegue en Tests Minecolonies: health 200, módulo cargado y redirección a Discord con el callback existente. El consentimiento final de una cuenta real debe hacerlo su propietario.
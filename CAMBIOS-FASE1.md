# Fase 1: chat seguro, ingesta protegida y datos reales

Objetivo: que lo que ya existía **funcione de verdad** y quede sobre una base segura, antes de agregar funcionalidades nuevas (Fase 2: chat al nivel de Twitch/Kick, Fase 3: simulación de usuarios, Fase 4: video y OBS profesional).

---

## 1. Qué estaba roto y cómo quedó

| Problema | Causa | Arreglo |
|---|---|---|
| **El chat no enviaba nada** | `Watch.cshtml` llamaba `SendMessage(channelId, msg)` con 2 argumentos y `channelId` numérico; el hub esperaba 3 strings → SignalR rechazaba la llamada. | Hub nuevo con `SendMessage(int channelId, string content)`. |
| **Contador y lista de viewers sin actualizar** | `JoinChannel` recibía un número y el hub esperaba un string, así que fallaba igual que el envío. | `JoinChannel(int channelId)`. |
| **El cliente de SignalR no existía** | Las vistas cargaban `~/lib/signalr/dist/browser/signalr.min.js`, pero ese archivo no estaba en el proyecto → `signalR is not defined` en Watch, Perfil, Simular viewers y Susurros. | Incluido `@microsoft/signalr` 9.0.19 en `wwwroot/lib/signalr`. |
| **Cualquiera podía escribir como otro usuario** | El hub aceptaba el nombre de usuario que mandaba el navegador. | La identidad sale de la cookie (`Context.UserIdentifier`) y el nombre de `DisplayUsername` en la base. |
| **XSS en el chat, donaciones y buscador** | Mensajes, nombres y textos de usuarios se insertaban con `innerHTML`. | El chat viaja como fragmentos texto/emote y se pinta con `textContent`. Las donaciones también, y el buscador del navbar escapa el HTML. |
| **La stream key se veía en la página** | La URL HLS era `/live/{StreamKey}/index.m3u8`. Con esa clave cualquiera podía transmitir en tu canal. | La URL pública es `/live/{IdCanal}/...`. MediaMTX valida la clave con la app (`authMethod: http`). |
| **Cualquiera podía publicar en MediaMTX** | MediaMTX aceptaba cualquier path; el webhook solo marcaba en vivo si coincidía la clave, pero el video igual se servía. | Una clave incorrecta se rechaza en la ingesta ("authentication failed" en OBS). |
| **Webhooks abiertos** | `GET /api/stream/onconnect` y `/ondisconnect`, sin protección. | `POST /api/media/hooks/...` con secreto compartido. Sin secreto responden 401. |
| **Error 500 en /Clip y /Notification** | Los controladores pedían clases concretas no registradas en DI. | `ClipService` registrado; `NotificationController` usa `INotificationService`. `DebugController` eliminado (estaba roto y no pedía login). |
| **Viewers y pico siempre falsos** | `Channel.ViewerCount` nunca se actualizaba y `LiveMetric` nunca se incrementaba. | Cada 15 s se guarda el conteo real en SQL (la portada ordena por eso) y el pico en Mongo. |
| **Canales "en vivo" sin video** | El seed marcaba 6 canales en vivo con viewers inventados. | Los canales demo nacen offline. Los ya existentes se corrigen solos cuando MediaMTX está corriendo (ver *reconciliación*). |
| **Editar el canal de otro streamer** | `Update`, `UploadEmote` y `DeleteEmote` recibían `channelId` del formulario sin verificar el dueño. | Siempre se usa el canal del usuario logueado. |
| **Subida de emotes insegura** | El código del emote formaba parte de la ruta del archivo (path traversal) y no se validaba el tipo. | Nombre de archivo generado por el servidor, solo PNG/GIF/WEBP verificados por firma, máximo 1 MB. |
| **Eliminar emote siempre daba 404** | El formulario no enviaba `channelId`. | Ya no lo necesita. |
| **Historial del chat invertido** | Mongo devolvía los más nuevos primero y la vista los pintaba arriba. | Orden cronológico + índice `{ChannelId, SentAt}`. |
| **Formularios sin protección CSRF** | Las acciones POST de canal y admin no validaban el antiforgery token. | `[ValidateAntiForgeryToken]` / `[AutoValidateAntiforgeryToken]`. |

---

## 2. Lo nuevo

**Chat**
- Pipeline en el servidor (`Application/Services/Chat/ChatService.cs`), en este orden:
  1. Cuenta suspendida.
  2. Ban o timeout del canal.
  3. Solo seguidores.
  4. Palabras bloqueadas.
  5. Modo lento.
  6. Límite de 20 mensajes cada 30 s.
  7. Guardar en Mongo y difundir.
- El usuario ve por qué no salió su mensaje (por ejemplo, "Modo lento activo: espera 7 s") y el botón muestra la cuenta regresiva.
- Insignias (🎙 dueño del canal, 🛡 admin), color de nombre estable por usuario y emotes del canal (`nombre` o `:nombre:`).
- Moderación para el dueño del canal y los Admin: al pasar el mouse sobre un mensaje aparecen 🗑 (borrar), ⏱ (silenciar 10 min) y ⛔ (banear).
  - Silenciar o banear borra los mensajes de ese usuario en vivo y en el historial.
  - El sancionado ve el input bloqueado con cuenta regresiva.
- Dashboard → **Configuración del chat** (modo lento, solo seguidores, palabras bloqueadas) y **Usuarios sancionados** (para quitar sanciones).
- Si hacías scroll hacia arriba para leer, el chat ya no te arrastra al final: aparece "Mensajes nuevos ↓".

**Presencia**
- Un usuario con varias pestañas cuenta como 1. Los anónimos aparecen como "+N espectadores sin sesión".
- El conteo se difunde cada 2 s, en vez de reenviar la lista completa a todos en cada entrada o salida.

**Streaming**
- OBS usa como clave `ID?key=CLAVE`, que se copia desde el Dashboard (con botón copiar y ocultar).
- **Regenerar clave** corta la transmisión actual. **Admin → Terminar transmisión** reemplaza a "Marcar en vivo".
- **Reconciliación**: cada 30 s se compara SQL con lo que MediaMTX transmite realmente y se corrige en ambas direcciones.
- Claves nuevas criptográficamente aleatorias (`sk_...`). Las GUID antiguas siguen funcionando hasta regenerarlas.
- `scripts/stream-de-prueba.ps1`: transmite una señal de prueba o un video en bucle con ffmpeg, sin abrir OBS.
- hls.js 1.7.3 incluido localmente (antes era `@latest` desde un CDN), con reconexión automática si el stream se corta.

**Proyecto**
- Domain, Application e Infrastructure ahora son bibliotecas de clases (antes eran proyectos Web con un `Program.cs` vacío).
- El Domain ya no depende de Entity Framework.

---

## 3. Cómo actualizar tu entorno

1. **Extrae el zip en una carpeta nueva**, o borra las carpetas `bin` y `obj` de los 4 proyectos. Tres proyectos cambiaron de tipo (Web → biblioteca), y los binarios viejos pueden confundir a Visual Studio.
2. **SQL Server: no hay migraciones nuevas.** La tabla `BannedUsers` ya existía. **MongoDB:** los campos nuevos del chat y el índice se crean solos al arrancar.
3. `appsettings.json` trae la sección nueva `MediaServer`. La cadena de conexión no cambia.
4. **Reemplaza el `mediamtx.yml`** de tu carpeta de MediaMTX por `mediamtx-config/mediamtx.yml`. El secreto ya coincide con el de `appsettings.json`.
5. Arranca la app (F5) y **después** `mediamtx.exe`.
6. En OBS pega el **Servidor** y la **Clave de transmisión** nuevos desde el Dashboard. La clave cambió de formato, así que la anterior ya no sirve.

---

## 4. Checklist para probar

Abre el mismo canal en dos navegadores (o uno normal y uno de incógnito) con usuarios distintos.

- [ ] **Chat:** escribe un mensaje → aparece en ambos con tu nombre público, no con el email.
- [ ] **XSS:** escribe `<b>hola</b>` → se ve el texto literal, no en negrita.
- [ ] **Emotes:** sube un emote en el Dashboard y escríbelo en el chat → se ve la imagen.
- [ ] **Modo lento:** actívalo (Dashboard, 10 s) y escribe dos veces seguidas desde el viewer → el botón muestra la cuenta regresiva. El dueño no tiene ese límite.
- [ ] **Solo seguidores:** actívalo → un usuario que no sigue el canal no puede escribir.
- [ ] **Palabras bloqueadas:** agrega una palabra y escríbela desde el viewer → se rechaza.
- [ ] **Silenciar:** como dueño, pasa el mouse sobre un mensaje del viewer → ⏱ → sus mensajes pasan a `<mensaje eliminado>` y su input queda bloqueado. Quita la sanción desde el Dashboard.
- [ ] **Viewers:** abre 3 pestañas con el mismo usuario → el contador sube solo 1. El modal 👁 muestra los nombres.
- [ ] **Clave mala:** en OBS pon una clave inventada → OBS falla al conectar y el log de MediaMTX dice `authentication failed`.
- [ ] **Clave buena:** el canal pasa a EN VIVO solo y la página del canal se recarga con el video.
- [ ] **Código fuente:** en la página del canal, el código ya no contiene tu stream key; la URL HLS usa el número del canal.
- [ ] **Regenerar clave en vivo:** OBS se desconecta y el canal pasa a OFFLINE.
- [ ] **Admin → Canales → Terminar transmisión:** corta el stream.
- [ ] **Simular viewers** (Admin): el contador del canal sube con viewers simulados.
- [ ] `/Clip/Popular` y `/Notification` ya no dan error 500.

---

## 5. Cómo se verificó

- **Compilación completa** de la solución, incluidas las vistas Razor, contra los mismos paquetes que usa el proyecto (EF Core 9, MongoDB.Driver 3.4, Dapper, Identity).
- **46 pruebas automáticas del chat** contra el `StreamHub`, `ChatService` y `PresenceTracker` reales, sobre SignalR, con datos en memoria. Cubren:
  - Envío, suplantación imposible, XSS como texto, emotes e insignias.
  - Vacío/largo, cuenta suspendida, historial en orden.
  - Rate limit, modo lento, palabras bloqueadas y solo seguidores.
  - Permisos de moderación, timeout, ban por canal y unban.
  - Borrar mensaje (incluso intentando borrar uno de otro canal) y `/clear`.
  - Presencia deduplicada y viewers simulados.
- **Prueba de punta a punta con MediaMTX v1.21 + ffmpeg**, usando el `mediamtx.yml` de este repo:
  - 5 publicaciones inválidas rechazadas: clave mala, sin clave, path con letras, formato viejo con la clave en el path, dueño suspendido.
  - Publicación válida → canal EN VIVO, notificación a seguidores y evento `StreamStarted` en el navegador.
  - HLS público sin clave → 200. La URL vieja con la clave → rechazada.
  - Conteo real de viewers guardado y pico actualizado.
  - Expulsión vía API (como al regenerar la clave) → OFFLINE + `StreamEnded`.
  - Reconciliación en ambas direcciones.
  - Endpoints sin secreto → 401.
- **Prueba en navegador** (Chromium) de la página Watch con 3 usuarios a la vez: render seguro del historial y de mensajes nuevos, emotes, botones de moderación solo para el dueño, silenciar desde la UI, cuenta regresiva del modo lento, modal de viewers y sin errores de JavaScript.

**No se pudo probar aquí:**
- La ejecución contra tu SQL Server y tu MongoDB reales. Las consultas Dapper nuevas usan los nombres de tablas y columnas del snapshot de migraciones (`Channels`, `Users`, `Categories`, `Emotes`), así que el primer arranque en tu máquina es la prueba definitiva.
- La reproducción del video en el navegador de pruebas, porque ese Chromium no trae el códec H.264. Sí se comprobó que el reproductor descarga el HLS correctamente; en Chrome/Edge se reproduce como antes.

---

## 6. Archivos

**Nuevos**
- `Domain`: `DTOs/ChatDtos.cs`, `DTOs/ChannelStreamInfo.cs`, `Models/ChatFragment.cs`, `Settings/MediaServerSettings.cs`, `Interfaces/IChatService.cs`, `IModerationService.cs`, `IStreamLifecycleService.cs`, `IMediaServerClient.cs`
- `Application`: `Services/Chat/ChatService.cs`, `EmoteParser.cs`, `ChatColors.cs`, `Services/StreamLifecycleService.cs`, `Services/StreamKeyGenerator.cs`
- `Infrastructure`: `MediaServer/MediaMtxClient.cs`, `MongoServices/MongoIndexInitializer.cs`
- `Web`: `Controllers/MediaHooksController.cs`, `Realtime/PresenceTracker.cs`, `PresenceBroadcaster.cs`, `LiveStateReconciler.cs`, `Models/WatchPageConfig.cs`, `wwwroot/js/watch.js`, `wwwroot/lib/signalr/…`, `wwwroot/lib/hls.js/…`
- `scripts/stream-de-prueba.ps1`, `CAMBIOS-FASE1.md`

**Modificados**
- `Hubs/StreamHub.cs` (reescrito), `ChannelController.cs`, `AdminController.cs`, `NotificationController.cs`, `Program.cs`, `appsettings.json`
- `Views/Channel/Watch.cshtml`, `Dashboard.cshtml`, `Views/Admin/Channels.cshtml`, `SimulateViewers.cshtml`, `Views/Shared/_Layout.cshtml`
- `ChannelService.cs`, `ChannelRepository.cs` (consultas Dapper nuevas), `ModerationService.cs` (ahora con interfaz y fechas UTC), `ChatMessageService.cs`, `LiveMetricService.cs`, `ChatMessage.cs`, interfaces del Domain, los 3 `.csproj`, `mediamtx.yml`, `README.md`

**Eliminados**
- `StreamWebhookController.cs` (reemplazado por `MediaHooksController`), `DebugController.cs`, `Infrastructure/MongoServices/ChatService.cs` (código muerto), los `Program.cs` vacíos de Domain/Application/Infrastructure, `Infrastructure/Nuevo archivo`

---

## 7. Lo que queda para la Fase 2

- Pasar a **Redis** la presencia, el modo lento y el rate limit, más un backplane de SignalR para correr más de una instancia. Hoy viven en memoria de una sola instancia.
- **Moderadores y VIP por canal**, y comandos de chat (`/ban`, `/timeout`, `/slow`, `/mod`, `/me`, `/w`, `/color`…).
- **AutoMod** con tu microservicio Python de moderación con IA: retener mensajes dudosos para que un mod los apruebe.
- Respuestas, menciones resaltadas, mensaje fijado, encuestas y predicciones con puntos de canal, raids.
- Contar viewers por **reproducción del video** (latido del reproductor o sesiones HLS de MediaMTX), no por conexiones al chat.

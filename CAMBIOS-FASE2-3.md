# Fases 2 y 3: chat al nivel de Twitch/Kick y simulación de usuarios

- **Fase 2** convierte el chat en algo parecido al de Twitch o Kick: moderadores y VIP, comandos, AutoMod, respuestas, mensajes fijados, encuestas, raids y puntos del canal.
- **Fase 3** agrega una audiencia simulada creíble (bots que conversan), streams simulados con ffmpeg, miniaturas en el inicio y una prueba de carga.

Todo se hizo **sin paquetes NuGet nuevos**.

> **Antes de abrir:** en `appsettings.json` hay secciones nuevas (`AutoMod`, `Ollama`, `Points`, `Simulation`). La tabla nueva `ChannelRoles` se crea sola al arrancar; si prefieres crearla a mano, el script es `sql/fase2_channel_roles.sql`. No hay migraciones de EF nuevas.

---

## Fase 2: el chat

### Roles por canal: moderadores y VIP

- El dueño del canal (o un Admin) nombra moderadores y VIP. Puede hacerlo de tres formas:
  - con `/mod nombre` y `/vip nombre` en el chat;
  - desde la tarjeta de usuario;
  - en **Dashboard → Moderadores y VIP**.
- Tabla `ChannelRoles` en SQL Server, consultada con Dapper.
- **Moderador:** puede borrar mensajes, silenciar, banear, fijar, crear encuestas, aprobar AutoMod y activar los modos del chat. No puede sancionar al dueño ni a otro moderador.
- **VIP:** no le afectan el modo lento, "solo seguidores" ni el bloqueo de enlaces, y tiene un límite de mensajes más alto.
- **Insignias:** 🎙 streamer · ⚔️ moderador · 💎 VIP · 🛡 admin.

### Comandos

Escribe `/help` en el chat para ver los comandos que te corresponden.

| Quién | Comandos |
|---|---|
| Todos | `/me acción` · `/color rojo` (o `#ff8800`) · `/w usuario mensaje` · `/mods` · `/vips` |
| Moderadores | `/ban usuario [motivo]` · `/unban` · `/timeout usuario [90 \| 10m \| 1h \| 2d] [motivo]` · `/untimeout` · `/clear` · `/slow [s]` · `/slowoff` · `/followers` · `/followersoff` · `/emoteonly` · `/emoteonlyoff` · `/announce texto` · `/poll [duración] Pregunta \| op1 \| op2` · `/endpoll` · `/unpin` |
| Dueño y Admin | `/mod` · `/unmod` · `/vip` · `/unvip` · `/raid canal` |

### Filtros, en este orden

Cada mensaje pasa por estos filtros en el servidor, y se aplica a todos por igual (personas y bots):

1. Comando o `/me`.
2. Cuenta suspendida.
3. Ban o timeout en el canal.
4. Solo seguidores (mods y VIP exentos).
5. Solo emotes (mods exentos).
6. Enlaces bloqueados (mods y VIP exentos).
7. Palabras bloqueadas (mods exentos).
8. Mensaje repetido en 30 s.
9. Modo lento.
10. Límite de 20 mensajes cada 30 s (100 para mods y VIP).
11. AutoMod.
12. Guardar y difundir.

### AutoMod

- Primero se aplican las reglas: palabras bloqueadas y enlaces.
- Además, opcionalmente, un **clasificador con IA**. Se configura en `AutoMod:Provider`:
  - `"Ollama"`: un LLM local, el mismo de `Ollama:Url` y `Ollama:Model`.
  - `"Http"`: tu propio servicio. Recibe `POST {text}` y responde `{flagged, category, reason}`.
  - `"None"`: solo reglas.
- Si el modelo marca el mensaje, queda **retenido**:
  - El autor ve "tu mensaje está en revisión".
  - Los moderadores lo ven en el panel 🛡 AutoMod, con botones Aprobar o Rechazar.
  - Si nadie lo revisa en 10 minutos, se descarta.
- Si el modelo no responde en `AutoMod:TimeoutMs`, el mensaje **pasa igual**. Un modelo lento nunca congela el chat.
- Se activa por canal en **Dashboard → Configuración del chat → AutoMod**.

### Lo que ve el viewer

- **Responder:** pasa el mouse sobre un mensaje y pulsa ↩. La respuesta muestra a quién responde, y al destinatario se le resalta.
- **Menciones:** si alguien te nombra con `@tunombre` o te responde, el mensaje se resalta.
- **Mensaje fijado:** 📌 arriba del chat. Lo fija y lo quita un moderador.
- **Encuestas:**
  - Se crean con `/poll 60 ¿Pregunta? | A | B | C` y tienen entre 2 y 5 opciones.
  - Cada persona vota una vez y puede cambiar su voto.
  - Hay cuenta regresiva y, al terminar, se marca la ganadora.
  - El conteo se envía como máximo una vez por segundo, aunque voten mil personas.
- **Tarjeta de usuario:** al hacer clic en un nombre se ven la fecha de creación de la cuenta, desde cuándo sigue el canal y sus sanciones. Tiene botones para susurrar y mencionar y, si eres moderador, para silenciar, banear y dar o quitar roles.
- **Raids:**
  - Se inician con `/raid canal`, y el canal destino tiene que estar en vivo.
  - Los viewers ven una cuenta regresiva de 10 s ("Quedarme aquí" la cancela) y luego pasan al otro canal.
  - En el canal destino aparece "🚀 X está raideando con N espectadores".
  - El streamer que lanzó el raid se queda en su página.
- **Reglas del chat:** el dueño las escribe en el Dashboard. La primera vez que alguien va a escribir, aparece un cuadro para aceptarlas. Los moderadores no lo ven.
- **Puntos del canal:**
  - Se ganan 10 puntos cada 5 minutos por ver un stream en vivo con sesión iniciada (`Points:*`).
  - Se ven abajo del chat.
  - Sirven para las donaciones que ya existían.
- **Modos visibles** sobre el input (modo lento, solo seguidores, solo emotes). Avisan cuando cambian.
- `/color`: elige el color de tu nombre. Los colores ilegibles sobre fondo oscuro se rechazan.

### Rendimiento

- Los mensajes ya no se guardan en Mongo uno por uno. Van a una cola, y un proceso en segundo plano los inserta en lotes de hasta 500 cada 250 ms. Antes de leer el historial, la cola se vacía.
- Los últimos 300 mensajes de cada canal se guardan en memoria, para responder, fijar y purgar sin consultar Mongo.
- En el navegador, los mensajes se pintan en lotes (`requestAnimationFrame`) y quedan como máximo 150 en pantalla. Así un chat con 10 mensajes por segundo no traba la página.

### Cómo está armado (para extenderlo)

- `IChatService` hace toda la lógica y devuelve **efectos** (`ChatEffect`): mensaje nuevo, ban, encuesta, raid, etc.
- `ChatEffectDispatcher` los traduce a eventos de SignalR. Lo usan el hub, el Dashboard y el simulador, así que todos difunden igual.

---

## Fase 3: simulación

Todo se maneja desde **Admin → 🤖 Simulador** (`/Simulator`).

### Bots que conversan

- Los bots son **cuentas reales** en la tabla `Users`:
  - Tienen rol Viewer y no tienen contraseña, así que nadie puede iniciar sesión con ellas.
  - Su email es `bot0001@bots.twitchclone.local`, `bot0002@...`, etc.
  - Sus nombres son creíbles (`Cata.Leyenda`, `xjavipebrex`, `Juanpi_Leyenda`...).
  - Al crearlas se les asignan puntos acumulados al azar, para que puedan donar.
- Sus mensajes pasan por `IChatService` igual que los tuyos: modo lento, palabras bloqueadas, AutoMod, bans y límites.
- Cuentan en el contador de viewers, ganan puntos y aparecen en la lista de "Viendo ahora".
- **Personalidades** (cada bot tiene siempre la misma, en cualquier canal):
  - 55 % miran sin escribir.
  - 25 % comentan de vez en cuando.
  - 8 % son de hype ("VAMOOO").
  - 6 % hacen preguntas.
  - 5 % solo mandan emotes.
  - 1 % trolea (y a veces lo frenan los filtros).
- **Mensajes según la categoría** del canal (juegos, música, cocina, arte, deportes, tecnología...), con el nombre del streamer, ciudades y los emotes del canal. Tienen variaciones de escritura (minúsculas, sin tildes, "jajaja") para que no se repitan.
- **Reaccionan a lo que pasa en el chat**:
  - Si saludas, te saludan.
  - Si nombras a un bot, te contesta con "responder".
  - Si el streamer pregunta algo, el chat responde.
  - Si banean a alguien, "F en el chat".
  - Si nombran a un mod, lo felicitan.
- **Olas:** cada pocos minutos, o cuando 3 personas escriben lo mismo, todo el chat se suma ("KEKW KEKW", "GG", un copypasta).
- **Encuestas:** votan entre 35 y 75 % de los bots, la mayoría en los primeros segundos, con una opción favorita.
- **Modos del chat:**
  - En modo lento esperan su turno.
  - En "solo emotes" mandan solo emotes del canal.
  - En "solo seguidores" algunos siguen el canal para poder escribir, y eso dispara la alerta de follow.
- **Eventos del stream:**
  - Siguen el canal, con notificación al streamer y alerta.
  - Donan puntos de verdad: se descuentan y se registra la donación. La alerta sale **encima del video**.
- **Raids:** si el streamer de un canal con bots hace `/raid`, entre el 60 y el 90 % de esos bots se mudan al canal destino y llegan escribiendo "🚀 RAID DE ...".
- **Entran y salen:**
  - Llegan en unos 15 s.
  - Rotan: cada viewer se queda unos 10 minutos en promedio.
  - Cuando termina el stream, la mayoría se va.
- **Mensajes con IA (opcional):** con `Simulation:UseAi: true`, los bots piden frases a Ollama según el título, la categoría y lo que se está hablando. Las plantillas siguen funcionando si Ollama no responde.
- **Actividad:** Tranquilo, Normal, Movido o Caótico. Hay un tope de 12 mensajes por segundo por canal.

### Streams simulados (video)

- Botón **Transmitir** por canal: ffmpeg publica en `rtmp://localhost/live/{canal}?key={clave}`.
  - Es **exactamente el camino de OBS**: MediaMTX valida la clave con la app, el webhook marca el canal EN VIVO y los viewers lo ven por HLS.
- **Video:**
  - Si `Simulation:VideosFolder` apunta a una carpeta con `.mp4`, cada canal transmite uno en bucle.
  - Si no, usa una señal generada distinta por canal (barras, fractal, juego de la vida, degradados...).
  - Lleva encima el nombre del canal, el streamer, la hora y el tiempo al aire.
  - El audio es un acorde suave o un ruido tipo lluvia.
- **Calidad** configurable: `StreamResolution`, `StreamFps` y `StreamBitrateKbps`. Por defecto 720p30 a 2500 kbps, lo que recomienda Twitch. Si simulas muchos canales en un PC modesto, bájala a `854x480`.
- **Si se corta** (MediaMTX lo expulsa, se reinicia), se reconecta solo. Después de 5 fallos seguidos se detiene y muestra el error en el panel.
- **Al cerrar la app**, los ffmpeg se cierran. En Windows quedan además dentro de un *Job Object*, así que mueren aunque detengas Visual Studio de golpe.
- **Admin → Canales → Terminar transmisión** también apaga el ffmpeg simulado. Si no, volvería a conectarse.

### Miniaturas

- Cada 15 s se revisan los canales en vivo.
- Los que no tienen miniatura, o la tienen con más de 45 s, reciben un fotograma nuevo con ffmpeg en `wwwroot/thumbnails/{id}.jpg`. Sirve para cualquier stream, simulado o de OBS.
- El inicio y "Canales que sigo" muestran tarjetas estilo Twitch: miniatura 16:9, etiqueta EN VIVO y espectadores encima.

### Alertas sobre el video

Los follows y las donaciones aparecen como alerta encima del reproductor, como las que pone OBS. Se encolan y se muestran de a una, 4 s cada una. Los follows no ensucian el chat, igual que en Twitch.

### Simulación completa

En el panel eliges cuántos canales, cuántos viewers en total, la actividad y si quieres video.

- Se crean los bots que falten.
- Se pone en vivo cada canal.
- La audiencia se reparte como en la vida real: pocos canales grandes y muchos chicos (ley de potencias). Por ejemplo, 150 viewers en 3 canales quedan como 86 / 39 / 25.

### Prueba de carga: proyecto `TwitchClonePorSeparadoConMongoMasSusurros.LoadTest`

- Es una consola sin paquetes NuGet que habla el protocolo de SignalR directamente.
- Abre N conexiones, cada una como un bot distinto, las une a un canal y hace escribir a algunas.
- Mide:
  - el tiempo de conexión;
  - el *ack* del hub (cuánto tarda en aceptar el mensaje);
  - la latencia de entrega a **todos** los conectados;
  - cuántas entregas se perdieron.

```powershell
# 1) La app en Development (perfil "http"), con la clave de bots definida una vez:
#    dotnet user-secrets set "Simulation:BotApiKey" "<16-o-mas-caracteres>" --project TwitchClonePorSeparadoConMongoMasSusurros.Web
# 2) Desde la carpeta de la solución:
dotnet run -c Release --project TwitchClonePorSeparadoConMongoMasSusurros.LoadTest -- `
    --url http://localhost:5145 --key <BotApiKey> --channel 1 --clients 500 --senders 50 --rate 10 --duration 60
```

- Si usas el perfil "https", pasa `--url https://localhost:7003`. Se acepta el certificado de desarrollo.
- La autenticación de bots (`X-Bot-Key` / `?bot_key=`) **solo existe en Development** y con una clave de 16 caracteres o más. En producción no se puede activar.
- Cada bot respeta el límite de 20 mensajes cada 30 s. Si `rate / senders` es mayor que 0,66, verás rechazos `rate_limited`. Sube `--senders`.
- Los mensajes de la prueba se guardan en Mongo como cualquier otro. Para limpiar, mira **Limpieza** más abajo.

---

## Cómo probarlo

1. Arranca SQL Server, MongoDB, la app (perfil **http**) y MediaMTX con `mediamtx-config/mediamtx.yml`. Para el video necesitas además ffmpeg: `winget install Gyan.FFmpeg` y abre una terminal nueva.
2. Entra como Admin → **🤖 Simulador**.
3. **Crear bots** (por ejemplo 500).
4. **Simulación completa** con 6 canales, 500 viewers, actividad Normal y con video. En unos segundos los canales demo quedan EN VIVO, con miniatura en el inicio y bots conversando.
5. Abre un canal y prueba cosas: saluda, nombra a un bot, crea `/poll 60 ¿Qué juego sigue? | A | B`, activa `/slow 10` o `/emoteonly`, banea a un bot, haz `/raid` a otro canal.
6. Con otro navegador en incógnito, entra como otro usuario para ver lo mismo desde el lado del viewer.
7. **Detener todo** apaga chats y videos.

### Configuración (appsettings.json)

| Sección | Clave | Para qué |
|---|---|---|
| `AutoMod` | `Provider` | `None` (solo reglas), `Ollama` o `Http` |
| | `HttpUrl`, `TimeoutMs` | Clasificador propio y tiempo máximo de espera (2500 ms) |
| `Ollama` | `Url`, `Model` | LLM local para AutoMod y para los bots (`llama3.2` por defecto) |
| `Points` | `IntervalMinutes`, `AmountPerInterval` | Puntos por ver streams (10 cada 5 min) |
| `Simulation` | `BotApiKey` | Clave de la prueba de carga. Se define con `dotnet user-secrets` (ver README → Secretos), nunca en el repositorio |
| | `FfmpegPath` | `ffmpeg` si está en el PATH; si no, la ruta completa al `.exe` |
| | `VideosFolder` | Carpeta con `.mp4` para los streams simulados (opcional) |
| | `UseAi` | Bots con frases de Ollama |
| | `ThumbnailIntervalSeconds` | Antigüedad máxima de las miniaturas (0 = desactivadas) |
| | `StreamResolution`, `StreamFps`, `StreamBitrateKbps` | Calidad de los streams simulados |
| | `MaxBots` | Tope de cuentas de bots (2000) |

### Limpieza

`sql/fase3_borrar_bots.sql` borra todas las cuentas de bots y sus follows, donaciones, roles y bans. Al final del archivo está el comando de `mongosh` para borrar sus mensajes (`IsSimulated: true`).

---

## Verificación hecha

NuGet está bloqueado en mi entorno. Por eso compilé la solución con .NET 10 contra los DLL de los paquetes de tu carpeta `bin`, y armé un servidor de prueba con el código real:

- `ChatService`, hub, dispatcher, simulador, `StreamSimulator`, `ThumbnailService`, autenticación de bots y controladores del simulador.
- Repositorios de SQL y Mongo en memoria, reemplazados por versiones de prueba.
- Clientes reales: `@microsoft/signalr`, Chromium, MediaMTX y ffmpeg.

| Prueba | Resultado |
|---|---|
| Compilación de la solución (5 proyectos) | sin errores ni advertencias nuevas |
| Regresión Fase 1 (chat, 43 casos) y Fase 2 (comandos, roles, AutoMod, encuestas, raids...: 70 casos) | 113/113 |
| Simulación (presencia, mensajes, IA, saludos, menciones, encuesta, ban, modo lento, solo emotes, follows, raid entre canales, detener, simulación completa) | 32/32 |
| Video punta a punta (ffmpeg → MediaMTX → webhook → EN VIVO → HLS → miniatura → donación → detener → reconexión tras expulsión) | 17/17 |
| UI del chat en Chromium (reglas, responder, menciones, fijar, encuesta, tarjeta de usuario, VIP, AutoMod, alertas sobre el video, bots, raid) | 36/36 |
| Panel del simulador en Chromium | 10/10 |
| Prueba de carga, 500 conexiones, 20 msg/s | 100 % entregado · entrega p95 14 ms |
| Prueba de carga, 2000 conexiones, 40 msg/s (80.000 entregas por segundo) | 100 % entregado · entrega p95 167 ms, p99 223 ms |

**Lo que no pude probar aquí** y conviene mirar en tu PC:

- SQL real de lo nuevo con Dapper: `ChannelRoles`, `TryDonatePointsAsync` y `GetChannelSummariesAsync`.
- La creación de bots con `UserManager` (en el servidor de prueba se usó una versión en memoria).
- MongoDB y Ollama reales.
- En Windows: el *Job Object* y las fuentes del texto sobre el video (busca `C:\Windows\Fonts\segoeui.ttf` o `arial.ttf`). Si ffmpeg no puede dibujar el texto, el stream sigue sin él y lo avisa en el log.
- La compilación en Visual Studio con .NET 9.

## Pendiente (Fases 4 y 5)

- **Redis** como backplane de SignalR, y presencia, encuestas y AutoMod compartidos: solo hace falta con varias instancias de la app.
- **MessagePack** para el hub.
- **Docker Compose** con todo (app, SQL, Mongo, Redis, MediaMTX).
- Video profesional: transcodificación a varias calidades (ABR), grabaciones/VOD y clips desde el video real.

## Archivos principales

**Nuevos**
- `Domain`: `Chat/ChatEffects.cs`, `Settings/PlatformSettings.cs`, `DTOs/ChannelSummary.cs`, `Models/ChannelRoleType.cs` e interfaces nuevas (`IChannelRoleRepository`, `IChatUserRepository`, `IAutoModClassifier`, `IPollService`, `IAiChatGenerator`...).
- `Application/Services/Chat`: `ChatService.Commands.cs`, `ChatService.Moderation.cs`, `PollService.cs`, `AutoModQueue.cs`, `RecentMessageCache.cs`, `ChatText.cs`.
- `Infrastructure`:
  - `Ai/` (clasificadores de AutoMod y generador de mensajes con Ollama);
  - `Data/SqlSchemaInitializer.cs`;
  - `Repositories/ChannelRoleRepository.cs` y `ChatUserRepository.cs`;
  - `MongoServices/ChatUserSettingsService.cs`.
- `Web`:
  - `Realtime/ChatEffectDispatcher.cs` y `ChatBackgroundServices.cs` (encuestas y puntos);
  - `Simulation/` completo;
  - `SimulatorController`, `SimulationApiController`;
  - `Views/Simulator/Index.cshtml`, `wwwroot/js/simulator.js`;
  - `Views/Shared/_LiveChannelCard.cshtml`, `wwwroot/css/live-cards.css`.
- `TwitchClonePorSeparadoConMongoMasSusurros.LoadTest/` (proyecto nuevo, incluido en la solución).
- `sql/fase2_channel_roles.sql`, `sql/fase3_borrar_bots.sql`.

**Modificados**
- `Hubs/StreamHub.cs` (reescrito).
- `wwwroot/js/watch.js` (reescrito).
- `Views/Channel/Watch.cshtml` y `Dashboard.cshtml`.
- `ChannelController`, `AdminController` (se quitó "Simular viewers", reemplazado por el Simulador).
- `Program.cs`, `appsettings*.json`, `ChatMessageService` (guardado en lote), `ChatFilterService`, `ChannelRepository`, `PresenceTracker`, `Home/Index` y `User/Following` (miniaturas).

**Eliminado:** `Views/Admin/SimulateViewers.cshtml`. Abría conexiones desde la pestaña del admin, que desaparecían al cerrarla.

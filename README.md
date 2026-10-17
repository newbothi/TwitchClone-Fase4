# TwitchClonePorSeparadoConMongoMasSusurros

Clon de Twitch construido con ASP.NET Core MVC, en una solución dividida en capas:

- **`.Domain`** — modelos, DTOs, interfaces de servicios/repositorios.
- **`.Application`** — lógica de negocio (servicios que implementan las interfaces del Domain).
- **`.Infrastructure`** — acceso a datos: EF Core + Dapper sobre SQL Server (usuarios, canales, categorías, donaciones, bans, etc.), driver nativo de MongoDB (chat, notificaciones, métricas en vivo, sesiones de stream) y el cliente de la API de MediaMTX.
- **`.Web`** — el proyecto ASP.NET Core MVC: controladores, vistas Razor, el hub de SignalR, los servicios de tiempo real (presencia de viewers, sincronización con MediaMTX, encuestas, puntos) y el simulador (`Simulation/`).
- **`.LoadTest`** — consola para medir cuántos usuarios aguanta el chat (ver [CAMBIOS-FASE2-3.md](CAMBIOS-FASE2-3.md#prueba-de-carga-proyecto-twitchcloneporseparadoconmongomassusurrosloadtest)).
- **`tests/`** — pruebas automáticas MSTest (ver [Pruebas automáticas](#pruebas-automáticas-qa--qc)).
- **`python/automod`** y **`python/analytics`** — servicios Python opcionales de la Fase 4 (ver [python/README.md](python/README.md)).

> **Fase 1 aplicada** (chat seguro y funcional, ingesta protegida, viewers reales):
> **[CAMBIOS-FASE1.md](CAMBIOS-FASE1.md)**.
>
> **Fases 2 y 3 aplicadas** (chat al nivel de Twitch/Kick con mods, VIP, comandos, AutoMod,
> encuestas y raids; simulador de audiencia con bots que conversan, streams simulados con
> ffmpeg, miniaturas y prueba de carga): **[CAMBIOS-FASE2-3.md](CAMBIOS-FASE2-3.md)**.
>
> **Fase 4 aplicada** (filtros de mayúsculas y caracteres repetidos, estadísticas del canal con
> gráficos, y dos servicios Python: AutoMod con machine learning y analytics):
> **[CAMBIOS-FASE4.md](CAMBIOS-FASE4.md)**.

Streaming en vivo real vía **MediaMTX** (servidor RTMP/HLS) + **OBS Studio** del lado del streamer.

> Esta es una variante de *TwitchClonePorSeparadoConMongo*
> que agrega **susurros** (mensajes privados 1:1 entre usuarios, persistidos en
> MongoDB, con entrega en tiempo real vía SignalR) — ver la sección
> [Susurros](#susurros) más abajo.

## Requisitos

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- SQL Server (Express o completo) — el proyecto usa autenticación integrada de Windows por defecto.
- [MongoDB](https://www.mongodb.com/try/download/community) corriendo localmente (`mongod`).
- Visual Studio 2022 (17.8+) / Visual Studio 2026, o cualquier editor con soporte para .NET 9.
- Para streaming en vivo: [MediaMTX](https://github.com/bluenviron/mediamtx/releases) y [OBS Studio](https://obsproject.com/).
- Para el simulador de streams y las miniaturas: [ffmpeg](https://ffmpeg.org/) (`winget install Gyan.FFmpeg`).
- Opcional, para AutoMod con IA y bots que escriben con contexto: [Ollama](https://ollama.com/) con un modelo como `llama3.2`.
- Opcional, para las estadísticas del canal y el AutoMod con machine learning (Fase 4): [Python 3.10 o superior](https://www.python.org/downloads/) (`winget install Python.Python.3.12`). El resto (scikit-learn, FastAPI, pyodbc...) lo instalan solos los scripts de `python/`.

## 1. Configurar las cadenas de conexión

Edita `TwitchClonePorSeparadoConMongoMasSusurros.Web/appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=TU_SERVIDOR;Database=TwitchClonePorSeparadoConMongoMasSusurros;Trusted_Connection=True;TrustServerCertificate=True;"
  },
  "MongoSettings": {
    "ConnectionString": "mongodb://localhost:27017",
    "DatabaseName": "TwitchClonePorSeparadoConMongoMasSusurros"
  }
}
```

Reemplaza `TU_SERVIDOR` por el nombre de tu instancia de SQL Server (por ejemplo `localhost`, `localhost\SQLEXPRESS`, o el hostname de tu máquina).

## 2. Aplicar la migración de base de datos

Desde la Consola del Administrador de paquetes en Visual Studio (Proyecto predeterminado: `TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure`):

```powershell
Update-Database -Project TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure -StartupProject TwitchClonePorSeparadoConMongoMasSusurros.Web
```

O con la CLI, desde la raíz de la solución:

```bash
dotnet ef database update --project TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure --startup-project TwitchClonePorSeparadoConMongoMasSusurros.Web
```

Esto crea la base de datos y todas las tablas (Identity, canales, categorías, donaciones, follows, etc.).

## 3. Arrancar la aplicación

Asegúrate de que **MongoDB esté corriendo** (`mongod`, o el servicio de Windows `net start MongoDB`) antes de arrancar — el chat, las notificaciones y las métricas en vivo dependen de él. El resto de la app (registro, login, navegación) sigue funcionando aunque Mongo esté caído, mostrando avisos donde corresponda.

Con F5 en Visual Studio (o `dotnet run` en `TwitchClonePorSeparadoConMongoMasSusurros.Web`), al primer arranque la aplicación siembra automáticamente:

- Los roles `Viewer`, `Streamer`, `Admin`.
- 10 categorías raíz (Charlando, Videojuegos, Música, Deportes, Arte, etc.) más un par de sub-categorías de ejemplo, cada una vinculada a sus tags temáticos.
- 6 streamers demo con su canal (contraseña `Demo123!`), **solo en entorno Development**. Desde la Fase 1 se crean **offline**: el estado EN VIVO solo lo da una transmisión real. Para ponerlos en vivo sin OBS usa `scripts/stream-de-prueba.ps1`.

No hace falta ejecutar ningún script manual para esto — pasa solo, una única vez (revisa si ya sembró antes de volver a insertar).

La tabla `ChannelRoles` (moderadores y VIP, Fase 2) también se crea sola al arrancar; el mismo SQL está en `sql/fase2_channel_roles.sql`.

### Scripts SQL opcionales

- **`seed_60_usuarios_demo.sql`** — crea 10 viewers (`viewer1`…`viewer10`) y 50 streamers (`streamer1`…`streamer50`) con sus canales, todos con contraseña `123456`. Útil para probar búsqueda, follows, y listas de "Siguiendo" con volumen real de datos.
- **`reset_categorias_tags.sql`** — limpia `Categories`/`Tags`/`CategoryTags` si necesitas que el seed automático las regenere desde cero (por ejemplo tras cambiar el catálogo en `Program.cs`).
- **`sql/fase3_borrar_bots.sql`** — borra las cuentas de bots del simulador y todo lo que generaron.
- **`sql/fase4_analytics.sql`** — crea el esquema `analytics` de las estadísticas. No hace falta correrlo: lo corre solo `python/analytics` al arrancar (se puede correr varias veces).

## 4. Streaming en vivo (MediaMTX + OBS)

Igual que en Twitch/Kick, **la clave de transmisión es secreta y nunca forma parte de la URL pública**:

| Quién | URL |
|---|---|
| OBS (Servidor) | `rtmp://localhost/live` |
| OBS (Clave de transmisión) | `<ID_CANAL>?key=<STREAM_KEY>` — se copia tal cual desde el Dashboard |
| Viewers (HLS) | `http://localhost:8888/live/<ID_CANAL>/index.m3u8` |

MediaMTX le pregunta a la app por cada publicación (`POST /api/media/auth`): si la clave no coincide, OBS recibe "authentication failed" y no transmite.

1. Descarga MediaMTX (probado con v1.21) y descomprímelo, por ejemplo en `C:\mediamtx\`.
2. Reemplaza su `mediamtx.yml` por el de este repo: `mediamtx-config/mediamtx.yml`.
3. Por defecto la app no usa secreto con MediaMTX (`MediaServer:HookSecret` vacío) y solo acepta sus llamadas desde la misma máquina. Para exigirlo, ver [Secretos](#secretos-nunca-en-git).
4. Si tu app no corre en `http://localhost:5145`, cambia el puerto en las 3 URLs del `mediamtx.yml`.
5. Arranca la app web y luego `mediamtx.exe` (la app tiene que estar corriendo para autorizar a OBS).
6. En OBS: **Configuración → Emisión → Servicio: Personalizado** y pega el *Servidor* y la *Clave de transmisión* del Dashboard.
7. **Iniciar transmisión** → el canal pasa a EN VIVO solo. Al detener OBS vuelve a OFFLINE.

Extras:

- **Regenerar clave** en el Dashboard corta la transmisión actual (la app expulsa a OBS vía la API de MediaMTX).
- **Admin → Canales → Terminar transmisión** corta cualquier stream.
- Cada 30 s la app compara qué canales están EN VIVO en SQL con lo que MediaMTX transmite y corrige diferencias (por ejemplo, si la app estaba apagada cuando se detuvo OBS).
- **Sin OBS:** `.\scripts\stream-de-prueba.ps1 -StreamKey "12?key=sk_..."` transmite una señal de prueba con ffmpeg (`winget install Gyan.FFmpeg`). Con `-Video archivo.mp4` transmite un video en bucle.

Configuración en `appsettings.json` (sección `MediaServer`): `IngestUrl`, `HlsBaseUrl`, `ApiBaseUrl`, `HookSecret`, `ReconcileLiveState`, `ReconcileIntervalSeconds`.

## Secretos (nunca en git)

El repositorio no trae claves: `appsettings.json` deja vacíos `MediaServer:HookSecret` y `Simulation:BotApiKey`, y la app funciona igual en tu PC:

- **Sin `HookSecret`**, los endpoints que llama MediaMTX (`/api/media/*`) solo aceptan llamadas desde la misma máquina.
- **Sin `BotApiKey`**, la API de la prueba de carga (`.LoadTest`) queda apagada. El simulador del panel Admin no la necesita.

Para definirlos, usa *User Secrets* de .NET. Quedan fuera de la carpeta del proyecto, en tu perfil de Windows:

```powershell
dotnet user-secrets set "MediaServer:HookSecret" "<valor-largo-al-azar>" --project TwitchClonePorSeparadoConMongoMasSusurros.Web
dotnet user-secrets set "Simulation:BotApiKey"   "<otro-valor-de-16-o-mas>" --project TwitchClonePorSeparadoConMongoMasSusurros.Web
```

En Visual Studio: clic derecho sobre el proyecto Web → **Administrar secretos de usuario**. Si defines `HookSecret`, pon el mismo valor en las 3 URLs de `mediamtx.yml` (donde dice `CAMBIA_ESTE_SECRETO`), pero en la copia de `C:\mediamtx`, no en la del repositorio.

## Chat en vivo

- Solo usuarios con sesión pueden escribir. El nombre que se muestra sale del servidor (`DisplayUsername`), nunca del navegador, y los mensajes se pintan con `textContent` (sin riesgo de inyectar HTML).
- Emotes del canal (`nombre` o `:nombre:`), `/me`, `/color`, respuestas (↩), menciones resaltadas, mensaje fijado, encuestas, tarjeta de usuario (clic en un nombre), raids y puntos del canal.
- Roles por canal: el dueño nombra **moderadores** y **VIP** (`/mod`, `/vip`, tarjeta de usuario o Dashboard).
- Moderación: pasar el mouse sobre un mensaje (borrar, silenciar, banear, fijar) o comandos (`/ban`, `/timeout`, `/slow`, `/followers`, `/emoteonly`, `/clear`...). Escribe `/help` para ver los tuyos.
- Dashboard → *Configuración del chat*: modo lento, solo seguidores, solo emotes, bloquear enlaces, **bloquear exceso de mayúsculas**, **bloquear caracteres repetidos**, AutoMod, palabras bloqueadas y reglas del chat.
- AutoMod con IA opcional (Ollama, o el servicio de machine learning de `python/automod`): los mensajes dudosos quedan retenidos hasta que un moderador los aprueba, y el modelo aprende de esas decisiones.
- Anti-spam: máximo 20 mensajes cada 30 s por usuario y canal (100 para mods/VIP), sin repetidos, mensajes de hasta 500 caracteres.

Detalle completo: [CAMBIOS-FASE2-3.md](CAMBIOS-FASE2-3.md).

## Estadísticas del canal (Fase 4)

**Dashboard → 📈 Estadísticas del canal**: seguidores, espectadores, horas en vivo y actividad del chat de los últimos 7, 30 o 90 días, comparados con el período anterior; espectadores de las últimas 24 horas; mejores horas para transmitir; y la tendencia de seguidores (cuántos por día y la proyección a 7 días).

Los datos los junta el servicio **`python/analytics`** (puerto 8002): lee `dbo.*` y MongoDB sin modificarlos y escribe solo en su propio esquema `analytics.*`. La app lee ese esquema con Dapper. Si el servicio no está corriendo, el Dashboard lo dice y todo lo demás funciona igual.

```powershell
.\python\iniciar-analytics.ps1      # desde la carpeta de la solución; déjalo corriendo
```

La primera vez crea el esquema (`sql/fase4_analytics.sql`, también se puede correr a mano en SSMS) y reconstruye los últimos 30 días a partir de los follows y los mensajes del chat. Los espectadores no tienen historia, así que se empiezan a juntar desde ese momento (una foto por minuto).

## AutoMod con machine learning (Fase 4)

El servicio **`python/automod`** (puerto 8001) clasifica cada mensaje en `insulto`, `acoso`, `amenaza`, `sexual`, `odio`, `spam` o `ninguna`, y **aprende de los moderadores**: cuando apruebas o rechazas un mensaje retenido, o borras uno, la app le avisa y el modelo se reentrena cada 10 decisiones.

1. `.\python\iniciar-automod.ps1` (déjalo corriendo; la primera vez instala lo necesario).
2. En `appsettings.json`: `"AutoMod": { "Provider": "Http", ... }` (las URL ya apuntan a `localhost:8001`).
3. **Dashboard → Configuración del chat → AutoMod** en el canal.

Si el servicio se cae o tarda más de `AutoMod:TimeoutMs`, el chat sigue funcionando: los mensajes pasan sin revisión. Cómo funciona el modelo y cómo ajustarlo: [python/README.md](python/README.md).

## Simulador (Admin → 🤖 Simulador)

- **Bots** que son usuarios reales: entran a los canales (cuentan como viewers), conversan según la categoría, reaccionan a lo que escribes, votan encuestas, siguen el canal, donan puntos y se van de raid. Pasan por los mismos filtros que una persona.
- **Streams simulados**: ffmpeg transmite a MediaMTX con la clave del canal (el mismo camino que OBS), con una señal generada o tus propios `.mp4` (`Simulation:VideosFolder`).
- **Simulación completa**: varios canales en vivo a la vez con la audiencia repartida como en Twitch.
- **Miniaturas** automáticas de los canales en vivo en el inicio.
- **Prueba de carga** con el proyecto `.LoadTest` (solo en Development).

## Estructura de autenticación

- ASP.NET Core Identity con cookies (no JWT).
- `UserName` = email; `DisplayUsername` = nombre público que ve la gente.
- Las tablas de Identity están renombradas (`Users`, `Roles`, `UserRoles`, etc. en vez de los nombres `AspNet*` por defecto).

## Susurros

Mensajes privados 1:1 entre usuarios, equivalentes a los "whispers" de Twitch:

- Persisten en MongoDB (`WhisperMessage`, colección `WhisperMessages`) — sin
  migración de EF Core necesaria, no toca SQL Server para nada.
- Cada conversación tiene un `ConversationId` determinístico (los dos
  `UserId` ordenados alfabéticamente), así una conversación entre A y B
  siempre cae en el mismo hilo sin importar quién escribió primero.
- Entrega en tiempo real vía SignalR (`IHubContext<StreamHub>.Clients.User(...)`)
  cuando el destinatario está conectado; si no lo está, el mensaje ya quedó
  guardado y lo ve al abrir su bandeja.
- Botón "💬 Susurrar" en el perfil de cualquier usuario (`Views/User/Profile.cshtml`).
- Bandeja de conversaciones y badge de no leídos en el navbar
  (`/Whisper`, `WhisperController`), con polling cada 30s como respaldo del
  push en vivo.

## Pruebas automáticas (QA / QC)

La solución trae tres proyectos MSTest en la carpeta `tests/`, y los servicios Python traen sus propias pruebas con pytest:

| Proyecto | Qué prueba | Tarda |
|---|---|---|
| `Tests.Unit` | La lógica sin red ni base de datos: reglas del chat (vacío, largo, ban, timeout, solo seguidores, solo emotes, enlaces, palabras bloqueadas, mayúsculas, caracteres repetidos, mensaje repetido, modo lento, anti-spam), moderadores y VIP, comandos, moderación, respuestas y mensajes fijados, AutoMod (retención, tiempo límite, servicio caído, lo que se le envía para aprender), emotes, colores, claves de transmisión, rutas de MediaMTX, inicio y fin del directo, conteo de espectadores y lo que muestran los gráficos del Dashboard. | ~1 s |
| `Tests.Integration` | La app levantada de verdad (Kestrel + SignalR) en un puerto libre: chat de punta a punta, que los mensajes no crucen de canal, que no se pueda suplantar a otro usuario, eventos de moderación, AutoMod, contador de espectadores y los endpoints que llama MediaMTX. | ~15 s |
| `Tests.Common` | No son pruebas: dobles en memoria de SQL Server y MongoDB, y un reloj que se adelanta a mano (para probar "espera 30 s" sin esperar). | — |
| `python/automod/tests` | Normalización del texto, el modelo (qué retiene y qué **no** debe retener), que aprenda de los moderadores, reentrenamiento y la API. | ~40 s |
| `python/analytics/tests` | Conexión leída del `appsettings.json`, cálculos diarios y por hora, zona horaria (incluye los días de cambio de hora en Chile), regresión de tendencias, ETL y API. | ~2 s |

**Las pruebas normales no necesitan SQL Server, MongoDB, MediaMTX, OBS ni Python**: todo corre en memoria.

- **Visual Studio:** *Prueba → Explorador de pruebas → Ejecutar todas* (Ctrl+R, A).
- **Consola:** `dotnet test TwitchClonePorSeparadoConMongoMasSusurros.slnx`
- **Python:** `.\python\probar.ps1`
- **Con cobertura:** `dotnet test TwitchClonePorSeparadoConMongoMasSusurros.slnx --collect:"XPlat Code Coverage"`
- **GitHub:** `.github/workflows/pruebas.yml` corre todo en cada push y pull request (pestaña *Actions*), incluidas las pruebas contra un SQL Server 2022 real y contra el AutoMod Python real.

### Pruebas contra servicios reales (opcionales)

Aparecen como **omitidas** en el Explorador de pruebas hasta que defines la variable correspondiente. Crean una base de datos temporal con nombre único y la borran al terminar: **no tocan la base de la app**.

```powershell
# Consultas SQL de la Fase 4 (Dapper del Dashboard) contra tu SQL Server
$env:PRUEBAS_SQLSERVER = "Server=localhost;Trusted_Connection=True;TrustServerCertificate=True;"
dotnet test tests\Integration --filter TestCategory=SqlServer

# ETL y API de python/analytics contra tu SQL Server
.\python\probar.ps1 -SqlServer "localhost"

# La app conversando con el AutoMod Python de verdad (con .\python\iniciar-automod.ps1 corriendo en otra terminal)
$env:PRUEBAS_AUTOMOD_URL = "http://127.0.0.1:8001"
dotnet test tests\Integration --filter TestCategory=ServiciosPython
```

Si tu SQL Server es una instancia con nombre, cambia `localhost` por la de tu cadena de conexión (por ejemplo `.\SQLEXPRESS`).

Regla de trabajo: **cuando aparezca un bug, primero se escribe la prueba que falla y después se corrige** (así no vuelve). Ejemplo real: `LimiteDeMensajes_EsperarLoQueIndicaRetryAfter_AlcanzaParaVolverAEscribir` detectó que, tras esperar exactamente los segundos que el chat indicaba, el mensaje seguía rechazado.

Para ver los logs de la app durante las pruebas de integración: variable de entorno `PRUEBAS_LOGS=1`.

## Notas

- **Ruta corta:** Windows limita las rutas a 260 caracteres y los nombres de este proyecto son largos (al compilar aparecen rutas de hasta ~175 caracteres dentro de `obj/`). Trabaja en una carpeta corta, por ejemplo `C:\dev\TwitchClone`. Si ves *"Ruta de acceso demasiado larga"* (0x80010135) o errores de MSBuild con rutas largas, es esto.

- Las librerías de cliente van incluidas en `wwwroot/lib` (no dependen de un CDN): `@microsoft/signalr` 9.0.19, `hls.js` 1.7.3 (licencia Apache-2.0, ver `wwwroot/lib/hls.js/LICENSE`) y `Chart.js` 4.5.1 (licencia MIT, ver `wwwroot/lib/chart.js/LICENSE.md`).

- El proyecto no incluye archivos subidos por usuarios (`wwwroot/avatars`, `/banners`, `/emotes`) en el control de versiones — se generan solos en tiempo de ejecución.
- `Views/Profile/` y `ProfileController` son remanentes de una versión anterior del flujo de perfil; el flujo activo real es `Views/User/Profile.cshtml` vía `UserController`.

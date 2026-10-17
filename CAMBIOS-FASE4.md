# Fase 4: filtros del chat, estadísticas del canal y machine learning

| | Qué | Dónde | Riesgo para lo que ya funciona |
|---|---|---|---|
| a | Filtros de mayúsculas y de caracteres repetidos (las reglas de tu `score_heuristic`) | C#, en el chat | Mínimo: dos reglas más en el pipeline, **desactivadas por defecto** |
| b | Analytics: ETL al esquema `analytics`, historia de espectadores, API de crecimiento y tendencias | Python, `python/analytics`, puerto 8002 | Ninguno: solo **lee** `dbo.*` y MongoDB, y escribe en su propio esquema |
| c | Gráficos en el Dashboard del streamer leyendo `analytics.*` | C# (Dapper + Chart.js) | Bajo: una sección nueva del Dashboard |
| d | AutoMod con machine learning que aprende de los moderadores | Python, `python/automod`, puerto 8001 | Bajo: usa el enchufe `AutoMod:Provider = "Http"` que ya existía; si el servicio se cae, el chat sigue igual |

Además, las **pruebas automáticas de la Fase 1 se traspasaron a esta versión** (ajustadas a los cambios de las Fases 2 y 3) y se agregaron las de a–d.

> **Antes de abrir:**
> - No hay paquetes NuGet nuevos ni migraciones de EF.
> - El esquema `analytics` lo crea solo `python/analytics` al arrancar. Si prefieres hacerlo a mano: `sql/fase4_analytics.sql` en SSMS (se puede correr varias veces).
> - `appsettings.json` → sección `AutoMod`: dos claves nuevas, `SendFeedback` y `FeedbackUrl`. `Provider` sigue en `"None"`: cámbialo a `"Http"` cuando quieras usar el AutoMod de Python.
> - Para los servicios Python necesitas Python 3.10 o superior (`winget install Python.Python.3.12`).

---

## a) Filtros de mayúsculas y caracteres repetidos

Dos casillas nuevas en **Dashboard → Configuración del chat**, apagadas por defecto:

| Filtro | Rechaza el mensaje si... | No cuenta |
|---|---|---|
| **Bloquear exceso de mayúsculas** | tiene **11 letras o más** y **más del 80 %** están en mayúsculas | emotes (`KEKW`, `LUL`), @menciones, enlaces, números y signos |
| **Bloquear caracteres repetidos** | repite el mismo carácter **7 veces o más seguidas** (`holaaaaaaa`, `!!!!!!!`, `😂😂😂😂😂😂😂`), sin distinguir mayúsculas | números (`1000000000 puntos`); un espacio corta la racha |

- El dueño, los moderadores y los VIP quedan exentos.
- El autor recibe un motivo claro: *"Tu mensaje tiene demasiadas mayúsculas. Escríbelo en minúsculas."* o *"Tu mensaje repite el mismo carácter 7 o más veces seguidas."*
- Un mensaje rechazado no gasta el modo lento ni el límite de mensajes.
- Se guardan en MongoDB junto con los demás filtros del canal (`BlockExcessiveCaps`, `BlockRepeatedChars`).

Orden de los filtros, actualizado (el nuevo es el 8):

1. Comando o `/me`.
2. Cuenta suspendida.
3. Ban o timeout en el canal.
4. Solo seguidores (mods y VIP exentos).
5. Solo emotes (mods exentos).
6. Enlaces bloqueados (mods y VIP exentos).
7. Palabras bloqueadas (mods exentos).
8. **Exceso de mayúsculas y caracteres repetidos (mods y VIP exentos).**
9. Mensaje repetido en 30 s.
10. Modo lento.
11. Límite de 20 mensajes cada 30 s (100 para mods y VIP).
12. AutoMod.
13. Guardar y difundir.

## b) Analytics (`python/analytics`, puerto 8002)

Junta lo que hace falta para medir el crecimiento de un canal, que la app no guardaba:

- **Cada minuto**, una foto de los espectadores de cada canal en vivo.
- **Cada 5 minutos**, resúmenes por día y por hora (seguidores, espectadores, horas vistas, chat), por categoría, y la **tendencia** de seguidores con una regresión lineal de los últimos 14 días (seguidores por día, R² y proyección a 7 días).
- La primera vez reconstruye los últimos 30 días con los follows y el chat que ya existen.
- Días y horas en hora de Chile, con sus cambios de hora (las pruebas cubren el día de 25 horas de abril y el de 23 horas de septiembre).
- API de crecimiento y tendencias: `/channels/{id}/summary`, `/daily`, `/viewers`, `/hours`, `/trends/categories`, `/trends/channels` (documentación en `http://127.0.0.1:8002/docs`).
- Si SQL Server no responde, el servicio sigue corriendo, lo avisa una vez y reintenta solo. Si MongoDB no responde, los resúmenes salen sin datos del chat.

Tablas: `analytics.ViewerSnapshots`, `ChannelDaily`, `ChannelHourly`, `CategoryDaily`, `ChannelTrends` y `EtlRuns`. Detalle en [python/README.md](python/README.md#analytics-analytics).

## c) Gráficos en el Dashboard

Sección nueva **📈 Estadísticas del canal**, con períodos de 7, 30 y 90 días:

- **Indicadores** con la comparación contra el período anterior (▲ / ▼): seguidores, nuevos seguidores, promedio y pico de espectadores, horas en vivo (y horas vistas), mensajes del chat (y mensajes por hora en vivo).
- **Seis gráficos**: espectadores de las últimas 24 horas (tramos de 5 minutos), seguidores totales con la tendencia, nuevos seguidores por día, promedio de espectadores por día, mensajes del chat por día y **mejores horas para transmitir** (las 3 mejores resaltadas).
- Cada gráfico muestra el valor exacto al pasar el mouse, y los mismos datos se pueden ver **como tabla**.
- Si `python/analytics` no está corriendo, o los datos tienen más de 20 minutos, el Dashboard lo dice (y muestra el último error del ETL, si lo hay). El resto del Dashboard no cambia.

Cómo está armado: `ChannelController.Analytics` (JSON, solo para el dueño del canal) → `ChannelAnalyticsService` (rellena días sin datos y compara períodos) → `ChannelAnalyticsRepository` (Dapper, solo lectura de `analytics.*`). Chart.js 4.5.1 va incluido en `wwwroot/lib/chart.js` (sin CDN).

## d) AutoMod con machine learning (`python/automod`, puerto 8001)

- Clasifica cada mensaje en `insulto`, `acoso`, `amenaza`, `sexual`, `odio`, `spam` o `ninguna`, con TF-IDF (trozos de letras y de palabras) y regresión logística, entrenado con 577 ejemplos en español escritos a mano.
- **Aprende de los moderadores**: aprobar o rechazar un mensaje retenido, o borrar el mensaje de otra persona, se envía al servicio (en segundo plano, sin frenar el chat) y el modelo se reentrena cada 10 decisiones.
- **Umbral 0,7**: de lo que retiene, 88 % es tóxico de verdad; retiene 74 % de lo tóxico (validación cruzada con ejemplos difíciles a propósito).
- Si el servicio se cae o tarda más de `AutoMod:TimeoutMs`, el mensaje pasa y el chat sigue igual.

Para activarlo: `.\python\iniciar-automod.ps1`, `"Provider": "Http"` en `appsettings.json`, y AutoMod activado en el canal. La tabla de umbrales, cómo aprende y los **sesgos que encontraron las pruebas** están en [python/README.md](python/README.md#automod-con-machine-learning-automod).

## Pruebas

| Proyecto | Pruebas | Qué cubre lo nuevo |
|---|---|---|
| `tests/Unit` | 240 | Los dos filtros (letras, emotes, menciones, enlaces, números, emojis, exentos, mensajes de error, configuración), lo que se envía al AutoMod para aprender, el cliente HTTP del AutoMod (servicio caído, lento, respuesta rara) y lo que reciben los gráficos (períodos, días sin datos, comparación, avisos) |
| `tests/Integration` | 54 + 7 opcionales | Los filtros y el aprendizaje del AutoMod de punta a punta por SignalR; **6 pruebas contra SQL Server real** (`SqlServer`) y **1 contra el AutoMod Python real** (`ServiciosPython`) |
| `python/automod/tests` | 69 | Normalización, lo que **debe** y lo que **no debe** retener, aprendizaje, reentrenamiento, calidad mínima del modelo y la API |
| `python/analytics/tests` | 65 + 4 opcionales | Conexión, cálculos por día y hora, cambios de hora, regresión, ETL, API, SQL Server caído; **4 pruebas contra SQL Server real** (`-m sqlserver`) |

Las opcionales aparecen como **omitidas** hasta que les dices dónde está el servicio real. Cómo correrlas: [README → Pruebas contra servicios reales](README.md#pruebas-contra-servicios-reales-opcionales). En GitHub Actions corren solas: el workflow levanta un SQL Server 2022 y el AutoMod Python.

### Correcciones que también se aplicaron aquí

**Rutas de un solo segmento (`/Admin`, `/Simulator`, `/Whisper`, `/Search`, `/Category`, `/Home`, `/Notification`) daban 404.** Venía del código original: `ProfileController` (el remanente) tenía `[HttpGet("{username}")]` sin prefijo, y las rutas por atributo le ganan a la convencional, así que `/Admin` se buscaba como "el perfil del usuario Admin". Ahora el controlador lleva `[Route("Profile")]`, y `tests/Integration/Routing/RutasTests.cs` comprueba que cada dirección llega a su controlador: sin la corrección fallan 8 de sus 13 pruebas.


El error del límite de mensajes que encontró la Fase 1 (tras esperar exactamente los segundos que indicaba el chat, el mensaje seguía rechazado: `>` en vez de `>=`) también estaba en las Fases 2 y 3. Al traspasar las pruebas falló igual, y quedó corregido.

## Verificación hecha

NuGet, Docker Hub y el registro de Microsoft están bloqueados en mi entorno. Compilé la solución con .NET 10 contra los DLL de los paquetes, y corrí las pruebas con un ejecutor propio compatible con MSTest.

| Prueba | Resultado |
|---|---|
| Compilación de la solución (5 proyectos + 3 de pruebas) | sin errores ni advertencias nuevas |
| Pruebas unitarias C# | 240/240 (5 corridas seguidas, sin fallas intermitentes) |
| Pruebas de integración C# | 54/54 (3 corridas seguidas) |
| La app C# con el AutoMod Python real: retiene el insulto, el moderador lo rechaza y la decisión llega al servicio | ✔ |
| pytest `automod` y `analytics`, en un entorno nuevo con las versiones más recientes de cada librería | 69/69 y 65/65 |
| Mutaciones: rompí a propósito 17 reglas de a–d (por ejemplo, que los moderadores no queden exentos, que los números cuenten como repetidos, invertir aprobar/rechazar, contar los días en UTC, que gane la primera decisión del moderador) | las 17 las detectaron las pruebas |
| Dashboard en Chromium, 6 casos: con datos (y el detalle al pasar el mouse), 90 días, celular (390 px), sin el esquema analytics, sin datos aún y datos viejos | sin errores de JavaScript ni desbordes horizontales |
| Scripts de PowerShell (`python/*.ps1`) con PowerShell 7: crear el entorno, instalar, correr pruebas, fallar con código 1, iniciar los dos servicios | ✔ |
| `analytics` sin driver ODBC y con SQL Server inalcanzable | mensaje claro / `degraded`, sigue corriendo y reintenta |
| Workflow de GitHub (`actionlint`) | sin errores |

**Lo que no pude probar aquí** y conviene correr en tu PC (los comandos están en el README):

- **Las consultas SQL de la Fase 4 contra SQL Server real**: las 6 pruebas `SqlServer` de C# y las 4 `sqlserver` de Python. Crean una base temporal y la borran; no tocan la tuya.
- MongoDB real para el ETL (las pruebas usan un doble con los mismos nombres de campos).
- Windows PowerShell 5.1 (el de Windows 11 por defecto): los scripts se escribieron para funcionar en 5.1 y 7, pero solo los ejecuté en PowerShell 7.
- La compilación en Visual Studio con .NET 9.

## Pendiente (Fase 5)

- **Redis** como backplane de SignalR, y presencia, encuestas y AutoMod compartidos: solo hace falta con varias instancias de la app.
- **MessagePack** para el hub.
- **Docker Compose** con todo (app, SQL, Mongo, Redis, MediaMTX y los dos servicios Python).
- Video profesional: transcodificación a varias calidades (ABR), grabaciones/VOD y clips desde el video real.
- AutoMod: un panel en el Dashboard con las métricas del modelo y las últimas decisiones de los moderadores (hoy están en `GET /model`).

## Archivos principales

**Nuevos**
- `Domain`:
  - `DTOs/AnalyticsDtos.cs`;
  - `Interfaces/IChannelAnalyticsRepository.cs` (con `IChannelAnalyticsService`);
  - `Interfaces/IAutoModFeedbackSink.cs`.
- `Application/Services/ChannelAnalyticsService.cs`.
- `Infrastructure`:
  - `Repositories/ChannelAnalyticsRepository.cs`;
  - `Ai/AutoModFeedbackSinks.cs`.
- `Web`:
  - `Realtime/AutoModFeedbackObserver.cs`;
  - `wwwroot/js/dashboard-analytics.js`;
  - `wwwroot/lib/chart.js/`.
- `sql/fase4_analytics.sql`.
- `python/`: `automod/`, `analytics/`, `iniciar-automod.ps1`, `iniciar-analytics.ps1`, `probar.ps1`, `comun.ps1`, `README.md`.
- `tests/` (Common, Unit, Integration) y `.github/workflows/pruebas.yml`.

**Modificados**
- `ChatService.cs` (filtros nuevos y corrección del límite de mensajes), `ChatService.Moderation.cs` (envía las decisiones al AutoMod), `ChatText.cs` (las dos reglas).
- `ChatFilter`, `IChatFilterService`, `ChatFilterService`, `ChatDtos`, `ChatEffects`, `PlatformSettings`.
- `DependencyInjection.cs` de Application e Infrastructure, `Program.cs`, `appsettings.json`.
- `ChannelController` (configuración de los filtros y acción `Analytics`), `Views/Channel/Dashboard.cshtml`.
- `TwitchClonePorSeparadoConMongoMasSusurros.slnx` (agrega los proyectos de prueba y el de carga), `.gitignore`, `README.md`.
- `scripts/stream-de-prueba.ps1`: solo se guardó con BOM UTF-8, para que Windows PowerShell 5.1 muestre bien los acentos.

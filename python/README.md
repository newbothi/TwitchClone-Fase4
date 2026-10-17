# Servicios Python (Fase 4)

Dos servicios **opcionales**: la app funciona igual sin ellos.

| Servicio | Puerto | Qué hace | Toca |
|---|---|---|---|
| `automod/` | 8001 | Clasifica mensajes del chat con machine learning y aprende de los moderadores. | Solo sus archivos en `automod/datos-locales/` |
| `analytics/` | 8002 | Junta la historia de espectadores, seguidores y chat, y calcula crecimiento y tendencias. | **Lee** `dbo.*` y MongoDB; **escribe** solo en el esquema `analytics.*` |

Necesitas **Python 3.10 o superior** (`winget install Python.Python.3.12`, y abre una terminal nueva).
Lo demás lo instalan los scripts en un entorno virtual (`.venv`) dentro de cada carpeta; no se instala nada en tu Python global.

```powershell
# desde la carpeta de la solución, cada uno en su propia terminal:
.\python\iniciar-automod.ps1
.\python\iniciar-analytics.ps1

# las pruebas de los dos
.\python\probar.ps1
```

Si PowerShell no deja ejecutar scripts: `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` (una vez).

---

## AutoMod con machine learning (`automod/`)

### Conectarlo a la app

En `appsettings.json` de la app web:

```json
"AutoMod": {
  "Provider": "Http",
  "HttpUrl": "http://localhost:8001/moderate",
  "TimeoutMs": 2500,
  "SendFeedback": true,
  "FeedbackUrl": "http://localhost:8001/feedback"
}
```

Y en el canal: **Dashboard → Configuración del chat → AutoMod**. Los mensajes de moderadores, VIP y del dueño no se revisan.

Si el servicio está apagado o tarda más de `TimeoutMs`, **el mensaje pasa igual** y el chat sigue funcionando. Las decisiones de los moderadores se envían en segundo plano (con dos reintentos); si el servicio no está, se pierden sin afectar al chat.

### Cómo decide

1. **Normaliza** el texto: minúsculas, sin tildes, `1d10t4` → `idiota`, `i d i o t a` → `idiota`, `holaaaaa` → `holaa`, los enlaces pasan a `enlace` y las menciones a `usuario`.
2. Lo convierte en números con **TF-IDF** de dos formas a la vez:
   - trozos de 2 a 5 **letras**, para reconocer palabras mal escritas o cortadas;
   - **palabras** sueltas y pares de palabras, para reconocer frases como "sé dónde vives".
3. Una **regresión logística** da una probabilidad para cada categoría: `ninguna`, `insulto`, `acoso`, `amenaza`, `sexual`, `odio` y `spam`.
4. **Toxicidad = 1 − P(ninguna)**. Si la toxicidad llega al **umbral (0,7)**, el mensaje queda retenido con la categoría tóxica más probable, por ejemplo "Probable insulto (87 %)".

Parte de **577 ejemplos en español** escritos a mano (`automod/data/semilla_es.csv`, con modismos chilenos). Muchos son mensajes normales que se parecen a uno tóxico ("lo mataron de un tiro jaja", "puta que es difícil este jefe"): son los que enseñan al modelo a **no** retener de más.

### Aprende de los moderadores

| Lo que pasa en el chat | Lo que aprende | Peso |
|---|---|---|
| Un moderador **aprueba** un mensaje retenido | ese mensaje era normal | 3 |
| Un moderador **rechaza** un mensaje retenido | era tóxico, de la categoría que dijo el modelo | 3 |
| Un moderador **borra** un mensaje de otra persona | era tóxico (la categoría la estima el modelo) | 1 |

- Cada **10 decisiones** se reentrena solo, en segundo plano, sin dejar de responder.
- Si el mismo mensaje aparece varias veces, **gana la última decisión**. Así un moderador puede corregir un ejemplo de la semilla con el que no está de acuerdo.
- El modelo y las decisiones se guardan en `automod/datos-locales/` (no va a git). Para volver al modelo de fábrica, borra esa carpeta.

Ejemplo real de las pruebas: "qué zorcotudo" (una palabra inventada) al principio pasa. Después de que los moderadores rechazan cuatro mensajes con esa palabra, el modelo la retiene como insulto, y "qué buen stream" sigue pasando.

### Qué tan bien funciona y cómo ajustarlo

Se mide con **validación cruzada**: entrena con 4/5 de los ejemplos y prueba con el 1/5 que nunca vio, cinco veces. Resultados con la semilla (249 tóxicos y 328 normales):

| Umbral | De lo retenido, era tóxico (precisión) | De lo tóxico, se retuvo (recall) | Normales retenidos por error | Tóxicos que pasan |
|---|---|---|---|---|
| 0,5 | 73 % | 87 % | 79 | 33 |
| 0,6 | 79 % | 80 % | 52 | 49 |
| **0,7** (por defecto) | **88 %** | **74 %** | **26** | **64** |
| 0,8 | 93 % | 60 % | 12 | 99 |

Se eligió **0,7** porque retener un mensaje normal molesta a quien lo escribió y le da trabajo a un moderador, mientras que un tóxico que pasa todavía se puede borrar a mano (y ese borrado le enseña al modelo). Para un canal más estricto: `$env:AUTOMOD_THRESHOLD = "0.6"` antes de iniciarlo.

Estos números salen de ejemplos difíciles a propósito. En un chat real, donde la mayoría de los mensajes son claramente normales, se retienen muchos menos por error. Las métricas del modelo actual se ven en `http://127.0.0.1:8001/model`.

### Lo que enseñaron las pruebas: sesgos

Las pruebas incluyen mensajes que el modelo **no debe** retener. Así aparecieron dos sesgos:

- **"las mujeres gamers son increíbles"** se retenía como **odio**. La semilla tenía a "mujeres" casi solo en frases de odio, así que el modelo aprendió que **nombrar a un grupo** ya era odio. Se corrigió agregando frases normales y positivas sobre grupos ("saludos a los peruanos del chat" está en las pruebas por lo mismo). Hoy su toxicidad es 0,33.
- La prueba de aprendizaje partía con **"eres un zorcotudo"**, pero se retenía desde el principio, sin que nadie le enseñara la palabra: el modelo aprendió que la forma "eres un ..." ya es un insulto. La semilla incluye elogios con esa misma forma ("eres un crack", "eres un genio": toxicidad 0,25 y 0,19), así que la forma sola no basta; con una palabra desconocida igual se inclina a insulto, lo que en un chat suele ser correcto. La prueba ahora parte con "qué zorcotudo".

Es el problema típico de un modelo que aprende de ejemplos: copia los sesgos de los datos. Por eso esas frases quedaron como pruebas permanentes; si alguien edita la semilla y vuelve a aparecer el sesgo, `pytest` lo avisa. Lo mismo puede pasar con las decisiones de los moderadores: si un moderador rechaza mensajes que no son tóxicos, el modelo aprende eso. Revisa `GET /model` → `feedback` de vez en cuando.

### API

| Método | Ruta | Para qué |
|---|---|---|
| GET | `/health` | Estado y versión del modelo |
| POST | `/moderate` | `{"text": "...", "channelId": 1}` → `{"flagged", "category", "reason", "score", "modelVersion"}` (lo usa la app) |
| POST | `/feedback` | `{"text", "isToxic", "category", "source", "channelId"}` → 202 (lo usa la app) |
| POST | `/train` | Reentrena ahora |
| GET | `/model` | Versión, umbral, métricas y cuántas decisiones lleva |

Documentación interactiva: `http://127.0.0.1:8001/docs`.

### Configuración (variables de entorno, todas opcionales)

| Variable | Por defecto | |
|---|---|---|
| `AUTOMOD_THRESHOLD` | `0.7` | Toxicidad mínima para retener (0 a 1) |
| `AUTOMOD_RETRAIN_EVERY` | `10` | Decisiones nuevas antes de reentrenar |
| `AUTOMOD_EVALUATE` | `1` | Calcular métricas al entrenar (tarda unos segundos más) |
| `AUTOMOD_DATA_DIR` | `automod/datos-locales` | Dónde se guardan el modelo y las decisiones |
| `AUTOMOD_SEED_FILE` | `automod/data/semilla_es.csv` | Ejemplos iniciales |
| `AUTOMOD_HOST` / `AUTOMOD_PORT` | `127.0.0.1` / `8001` | |

---

## Analytics (`analytics/`)

### Qué hace

- **Cada minuto** fotografía cuántos espectadores tiene cada canal en vivo (`dbo.Channels`) → `analytics.ViewerSnapshots`. Las fotos se guardan 90 días.
- **Cada 5 minutos** recalcula hoy y ayer:
  - `analytics.ChannelDaily`: seguidores nuevos y totales, minutos en vivo, promedio y máximo de espectadores, horas vistas, mensajes del chat, personas distintas que escribieron (los bots del simulador se cuentan aparte).
  - `analytics.ChannelHourly`: lo mismo por hora del día (para "mejores horas para transmitir").
  - `analytics.CategoryDaily`: espectadores por categoría.
  - `analytics.ChannelTrends`: **regresión lineal** de los seguidores de los últimos 14 días: cuántos gana por día, qué tan estable es esa tendencia (R²) y la proyección a 7 días.
- La primera vez reconstruye los **últimos 30 días** con los follows (`dbo.Follows`) y el chat de MongoDB (`ChatMessages`, `StreamSessions`). Los espectadores no tienen historia: se juntan desde que el servicio empieza a correr.
- Cada ejecución queda en `analytics.EtlRuns`. El Dashboard muestra el último error, si lo hay.
- Si **SQL Server** no responde, el servicio sigue corriendo: lo avisa **una vez** en la consola, `/health` dice `degraded` con el motivo y reintenta solo en cada ciclo (crea el esquema apenas SQL Server vuelve). Si **MongoDB** no responde, los resúmenes salen sin datos del chat y el error queda en `analytics.EtlRuns`.

No modifica nada fuera de `analytics.*`. Al arrancar crea el esquema con `../sql/fase4_analytics.sql` (se puede correr a mano en SSMS; es idempotente).

### Conexiones

Las lee del `appsettings.json` de la app web (`ConnectionStrings:DefaultConnection` y `MongoSettings`), así que no hay que escribirlas dos veces. Convierte la cadena de ADO.NET a ODBC y usa el mejor driver instalado: *ODBC Driver 18*, *17* o el *SQL Server* que viene con Windows. `GET /health` muestra cuál usó.

### Zona horaria

Los "días" y las "horas" se cuentan en **hora de Chile** (`America/Santiago`, con sus cambios de hora). Tiene que ser la misma zona horaria de Windows donde corre la app, porque el Dashboard calcula "hoy" con la hora local del PC. Si tu PC está en otra zona: `$env:ANALYTICS_TZ = "America/Bogota"` (o la que corresponda) antes de iniciarlo.

### API

| Método | Ruta | Para qué |
|---|---|---|
| GET | `/health` | Estado, conexiones y última ejecución de cada tarea |
| POST | `/etl/run?days=90` | Recalcula ahora (por ejemplo, 90 días hacia atrás) |
| GET | `/channels/{id}/summary?days=30` | Indicadores del período y comparación con el anterior |
| GET | `/channels/{id}/daily?days=30` | Un punto por día |
| GET | `/channels/{id}/viewers?hours=24` | Historia de espectadores |
| GET | `/channels/{id}/hours?days=28` | Promedio por hora del día |
| GET | `/trends/categories?days=7` | Categorías con más horas vistas y cuánto crecieron respecto del período anterior |
| GET | `/trends/channels?days=14` | Canales que más seguidores ganan por día (pendiente de la regresión) |

Documentación interactiva: `http://127.0.0.1:8002/docs`. El Dashboard de la app **no** usa esta API: lee `analytics.*` directo con Dapper, así que funciona aunque el servicio esté detenido (con los datos del último cálculo).

### Configuración (variables de entorno, todas opcionales)

| Variable | Por defecto | |
|---|---|---|
| `ANALYTICS_SQL` | (del appsettings.json) | Cadena ODBC completa, si quieres otra |
| `ANALYTICS_MONGO_URI` / `ANALYTICS_MONGO_DB` | (del appsettings.json) | |
| `ANALYTICS_TZ` | `America/Santiago` | Zona horaria de los días y las horas |
| `ANALYTICS_SNAPSHOT_SECONDS` | `60` | Cada cuánto se fotografían los espectadores |
| `ANALYTICS_AGGREGATE_SECONDS` | `300` | Cada cuánto se recalculan los resúmenes |
| `ANALYTICS_BACKFILL_DAYS` | `30` | Días hacia atrás la primera vez |
| `ANALYTICS_TREND_DAYS` | `14` | Ventana de la regresión de tendencias |
| `ANALYTICS_RETENTION_DAYS` | `90` | Días que se guardan las fotos por minuto |
| `ANALYTICS_HOST` / `ANALYTICS_PORT` | `127.0.0.1` / `8002` | |

---

## Pruebas

```powershell
.\python\probar.ps1                       # las dos, sin bases de datos (menos de 1 minuto; la primera vez, además, instala)
.\python\probar.ps1 -SqlServer "localhost"  # además, analytics contra tu SQL Server (base temporal que se borra al final)
```

O a mano, dentro de cada carpeta con su `.venv` activado: `python -m pytest`.

Las pruebas contra SQL Server crean una base `TwitchCloneAnalyticsPruebas_xxxxxxxx`, corren el script del esquema, ejecutan el ETL y la API sobre ella, y la borran. No tocan la base de la app.

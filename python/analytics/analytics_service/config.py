"""Configuración. Por defecto lee las conexiones del appsettings.json de la app web
(así no hay que escribirlas dos veces); cualquier valor se puede pisar con variables
de entorno ANALYTICS_*.
"""

from __future__ import annotations

import json
import os
from dataclasses import dataclass, field
from pathlib import Path

# python/analytics/  →  raíz del repositorio = ../..
BASE_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = BASE_DIR.parent.parent
DEFAULT_APPSETTINGS = REPO_ROOT / "TwitchClonePorSeparadoConMongoMasSusurros.Web" / "appsettings.json"
DEFAULT_SCHEMA_SQL = REPO_ROOT / "sql" / "fase4_analytics.sql"

# Drivers ODBC de SQL Server en orden de preferencia. "SQL Server" viene con Windows
# (antiguo, pero siempre está); los otros se instalan con SQL Server / SSMS.
PREFERRED_DRIVERS = ("ODBC Driver 18 for SQL Server", "ODBC Driver 17 for SQL Server", "SQL Server")


@dataclass(frozen=True)
class Settings:
    sql_connection: str = ""                 # cadena ODBC para pyodbc
    mongo_uri: str = "mongodb://localhost:27017"
    mongo_database: str = "TwitchClonePorSeparadoConMongoMasSusurros"
    timezone: str = "America/Santiago"
    snapshot_seconds: int = 60               # cada cuánto se fotografían los viewers
    aggregate_seconds: int = 300             # cada cuánto se recalculan los resúmenes
    backfill_days: int = 30                  # la primera vez, cuántos días hacia atrás
    trend_days: int = 14                     # ventana para la regresión de tendencias
    retention_days: int = 90                 # cuántos días se guardan las fotos por minuto
    schema_sql: Path = DEFAULT_SCHEMA_SQL
    run_etl: bool = True                     # False: solo API (útil en pruebas)
    host: str = "127.0.0.1"
    port: int = 8002
    sources: dict = field(default_factory=dict)   # de dónde salió cada valor (para /health)
    sql_problem: str = ""                    # por qué no hay sql_connection (para avisarlo claro)

    @staticmethod
    def from_env(appsettings: Path | None = None, available_drivers: list[str] | None = None) -> "Settings":
        env = os.environ
        path = Path(env.get("ANALYTICS_APPSETTINGS", appsettings or DEFAULT_APPSETTINGS))
        web = read_appsettings(path)
        sources: dict[str, str] = {}

        sql = env.get("ANALYTICS_SQL", "").strip()
        sql_problem = ""
        if sql:
            sources["sql"] = "ANALYTICS_SQL"
        elif web.get("sql"):
            if available_drivers is None:
                available_drivers = _installed_drivers()
            driver = pick_driver(available_drivers)
            sql = odbc_from_ado(web["sql"], driver) if driver else ""
            sources["sql"] = f"{path.name} ({driver or 'sin driver ODBC'})"
            if not driver:
                sql_problem = ("No encontré un driver ODBC de SQL Server. Instala 'ODBC Driver 18 for SQL Server' "
                               "(winget install Microsoft.msodbcsql.18) o define ANALYTICS_SQL con una cadena ODBC completa.")
        else:
            sql_problem = (f"No encontré ConnectionStrings:DefaultConnection en {path}. "
                           "Define ANALYTICS_SQL con una cadena ODBC completa, o ANALYTICS_APPSETTINGS con la ruta al appsettings.json.")

        mongo_uri = env.get("ANALYTICS_MONGO_URI") or web.get("mongo_uri") or Settings.mongo_uri
        mongo_db = env.get("ANALYTICS_MONGO_DB") or web.get("mongo_database") or Settings.mongo_database

        return Settings(
            sql_connection=sql,
            mongo_uri=mongo_uri,
            mongo_database=mongo_db,
            timezone=env.get("ANALYTICS_TZ", Settings.timezone),
            snapshot_seconds=int(env.get("ANALYTICS_SNAPSHOT_SECONDS", Settings.snapshot_seconds)),
            aggregate_seconds=int(env.get("ANALYTICS_AGGREGATE_SECONDS", Settings.aggregate_seconds)),
            backfill_days=int(env.get("ANALYTICS_BACKFILL_DAYS", Settings.backfill_days)),
            trend_days=int(env.get("ANALYTICS_TREND_DAYS", Settings.trend_days)),
            retention_days=int(env.get("ANALYTICS_RETENTION_DAYS", Settings.retention_days)),
            schema_sql=Path(env.get("ANALYTICS_SCHEMA_SQL", DEFAULT_SCHEMA_SQL)),
            run_etl=env.get("ANALYTICS_RUN_ETL", "1").strip().lower() not in ("0", "false", "no"),
            host=env.get("ANALYTICS_HOST", Settings.host),
            port=int(env.get("ANALYTICS_PORT", Settings.port)),
            sources=sources,
            sql_problem=sql_problem,
        )


def read_appsettings(path: Path) -> dict:
    """Saca las conexiones del appsettings.json de la app web (si existe)."""
    try:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return {}
    mongo = data.get("MongoSettings") or {}
    return {
        "sql": (data.get("ConnectionStrings") or {}).get("DefaultConnection", ""),
        "mongo_uri": mongo.get("ConnectionString", ""),
        "mongo_database": mongo.get("DatabaseName", ""),
    }


def pick_driver(available: list[str]) -> str | None:
    for name in PREFERRED_DRIVERS:
        if name in available:
            return name
    return None


def _installed_drivers() -> list[str]:
    try:
        import pyodbc  # se importa aquí: las pruebas sin SQL Server no lo necesitan
        return list(pyodbc.drivers())
    except Exception:
        return []


# Traducción de las claves de la cadena de ADO.NET (C#) a las de ODBC.
_ADO_TO_ODBC = {
    "server": "SERVER", "data source": "SERVER", "address": "SERVER", "addr": "SERVER",
    "database": "DATABASE", "initial catalog": "DATABASE",
    "user id": "UID", "uid": "UID", "user": "UID",
    "password": "PWD", "pwd": "PWD",
    "encrypt": "Encrypt",
    "trustservercertificate": "TrustServerCertificate",
    "multisubnetfailover": "MultiSubnetFailover",
    "connect timeout": "Connection Timeout", "connection timeout": "Connection Timeout",
}
_TRUE = {"true", "yes", "sspi", "1"}


def odbc_from_ado(ado: str, driver: str) -> str:
    """"Server=MI-PC;Database=X;Trusted_Connection=True;TrustServerCertificate=True;"
    → "DRIVER={ODBC Driver 18 for SQL Server};SERVER=MI-PC;DATABASE=X;Trusted_Connection=yes;TrustServerCertificate=yes"
    """
    parts = [f"DRIVER={{{driver}}}"]
    for chunk in ado.split(";"):
        if "=" not in chunk:
            continue
        key, value = (s.strip() for s in chunk.split("=", 1))
        lower = key.lower()
        if lower in ("trusted_connection", "integrated security"):
            if value.lower() in _TRUE:
                parts.append("Trusted_Connection=yes")
            continue
        odbc_key = _ADO_TO_ODBC.get(lower)
        if odbc_key is None:
            continue  # claves que ODBC no entiende (MultipleActiveResultSets, etc.)
        if value.lower() in ("true", "false"):
            value = "yes" if value.lower() == "true" else "no"
        parts.append(f"{odbc_key}={value}")
    return ";".join(parts)

"""python -m analytics_service   (desde la carpeta python/analytics)"""

import logging
import sys

import uvicorn

from .api import configure_logging, create_app
from .config import Settings


def main() -> None:
    configure_logging()
    settings = Settings.from_env()
    log = logging.getLogger("analytics")
    log.info("SQL Server: %s", settings.sources.get("sql") or "SIN CONFIGURAR (define ANALYTICS_SQL)")
    if not settings.sql_connection:
        # Sin conexión no hay nada que hacer: mejor un mensaje claro que una traza de error.
        log.error(settings.sql_problem or "Falta la conexión a SQL Server (define ANALYTICS_SQL).")
        sys.exit(1)
    log.info("MongoDB: %s / %s · zona horaria: %s", settings.mongo_uri, settings.mongo_database, settings.timezone)
    uvicorn.run(create_app(settings), host=settings.host, port=settings.port, log_level="info")


if __name__ == "__main__":
    main()

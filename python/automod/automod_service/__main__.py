"""python -m automod_service   (desde la carpeta python/automod)"""

import uvicorn

from .api import configure_logging, create_app
from .config import Settings


def main() -> None:
    configure_logging()
    settings = Settings.from_env()
    uvicorn.run(create_app(settings), host=settings.host, port=settings.port, log_level="info")


if __name__ == "__main__":
    main()

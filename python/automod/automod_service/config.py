"""Configuración del servicio. Todo tiene un valor por defecto; se puede cambiar con
variables de entorno (útil para las pruebas y para correrlo en otra máquina)."""

from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path

# python/automod/
BASE_DIR = Path(__file__).resolve().parent.parent


@dataclass(frozen=True)
class Settings:
    # Ejemplos etiquetados con los que parte el modelo (viene con el proyecto).
    seed_file: Path = BASE_DIR / "data" / "semilla_es.csv"
    # Aquí se guardan el modelo entrenado y las decisiones de los moderadores.
    data_dir: Path = BASE_DIR / "datos-locales"
    # Probabilidad mínima de que el mensaje sea tóxico para retenerlo (0 a 1).
    # Más alto = AutoMod más permisivo; más bajo = más estricto.
    threshold: float = 0.7
    # Cada cuántas decisiones nuevas de moderadores se reentrena solo.
    retrain_every: int = 10
    # Calcular métricas con validación cruzada al entrenar (tarda unos segundos más).
    evaluate: bool = True
    host: str = "127.0.0.1"
    port: int = 8001

    @property
    def model_file(self) -> Path:
        return self.data_dir / "modelo.joblib"

    @property
    def feedback_db(self) -> Path:
        return self.data_dir / "feedback.sqlite3"

    @staticmethod
    def from_env() -> "Settings":
        defaults = Settings()
        return Settings(
            seed_file=Path(os.environ.get("AUTOMOD_SEED_FILE", defaults.seed_file)),
            data_dir=Path(os.environ.get("AUTOMOD_DATA_DIR", defaults.data_dir)),
            threshold=float(os.environ.get("AUTOMOD_THRESHOLD", defaults.threshold)),
            retrain_every=int(os.environ.get("AUTOMOD_RETRAIN_EVERY", defaults.retrain_every)),
            evaluate=os.environ.get("AUTOMOD_EVALUATE", "1").strip().lower() not in ("0", "false", "no"),
            host=os.environ.get("AUTOMOD_HOST", defaults.host),
            port=int(os.environ.get("AUTOMOD_PORT", defaults.port)),
        )

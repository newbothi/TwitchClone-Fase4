"""Decisiones de los moderadores, guardadas en un SQLite local (no necesita SQL Server).

Cada fila es un ejemplo nuevo para el modelo:
  - automod_aprobado:  AutoMod lo retuvo y un mod lo aprobó  -> era un falso positivo (ninguna)
  - automod_rechazado: AutoMod lo retuvo y un mod lo rechazó -> era tóxico de verdad
  - mensaje_borrado:   un mod borró un mensaje que AutoMod dejó pasar -> probablemente tóxico
"""

from __future__ import annotations

import sqlite3
from contextlib import closing
from datetime import datetime, timezone
from pathlib import Path

from .model import Sample

# Las decisiones explícitas sobre AutoMod pesan más que un borrado (que puede ser por spoiler, etc.).
SOURCE_WEIGHTS = {
    "automod_aprobado": 3.0,
    "automod_rechazado": 3.0,
    "mensaje_borrado": 1.0,
}
DEFAULT_WEIGHT = 1.0


class FeedbackStore:
    def __init__(self, path: Path):
        self.path = path
        path.parent.mkdir(parents=True, exist_ok=True)
        with closing(self._connect()) as db, db:
            db.execute(
                """CREATE TABLE IF NOT EXISTS feedback (
                       id          INTEGER PRIMARY KEY AUTOINCREMENT,
                       text        TEXT    NOT NULL,
                       category    TEXT    NOT NULL,
                       source      TEXT    NOT NULL,
                       channel_id  INTEGER NULL,
                       created_at  TEXT    NOT NULL
                   )"""
            )

    def _connect(self) -> sqlite3.Connection:
        return sqlite3.connect(self.path, timeout=10)

    def add(self, text: str, category: str, source: str, channel_id: int | None = None) -> int:
        with closing(self._connect()) as db, db:
            cursor = db.execute(
                "INSERT INTO feedback (text, category, source, channel_id, created_at) VALUES (?, ?, ?, ?, ?)",
                (text, category, source, channel_id, datetime.now(timezone.utc).isoformat(timespec="seconds")),
            )
            return int(cursor.lastrowid)

    def count(self) -> int:
        with closing(self._connect()) as db:
            return int(db.execute("SELECT COUNT(*) FROM feedback").fetchone()[0])

    def samples(self) -> list[Sample]:
        with closing(self._connect()) as db:
            rows = db.execute("SELECT text, category, source FROM feedback ORDER BY id").fetchall()
        return [Sample(text, category, SOURCE_WEIGHTS.get(source, DEFAULT_WEIGHT), source) for text, category, source in rows]

    def stats(self) -> dict:
        with closing(self._connect()) as db:
            by_source = dict(db.execute("SELECT source, COUNT(*) FROM feedback GROUP BY source").fetchall())
            by_category = dict(db.execute("SELECT category, COUNT(*) FROM feedback GROUP BY category").fetchall())
        return {"total": sum(by_source.values()), "bySource": by_source, "byCategory": by_category}

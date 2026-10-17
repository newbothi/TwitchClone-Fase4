"""El ETL: cada minuto fotografía los viewers; cada 5 minutos recalcula los resúmenes
de hoy y ayer y las tendencias; una vez al día borra las fotos viejas.

Si MongoDB no responde, el ETL sigue: los resúmenes salen sin datos del chat (y los
minutos en vivo se estiman con las fotos), y el error queda anotado en analytics.EtlRuns.
"""

from __future__ import annotations

import logging
import threading
import time
from collections import defaultdict
from datetime import datetime, timedelta
from typing import Callable
from zoneinfo import ZoneInfo

from .config import Settings
from .repository import SqlUnavailable
from .transform import UTC, Snapshot, build_rows, day_range, window_bounds_utc
from .trends import channel_trend

log = logging.getLogger("analytics.etl")


class EtlRunner:
    def __init__(self, repo, mongo, settings: Settings, clock: Callable[[], datetime] | None = None):
        self.repo = repo
        self.mongo = mongo            # MongoSource o un doble de prueba; puede ser None
        self.settings = settings
        self.tz = ZoneInfo(settings.timezone)
        self.clock = clock or (lambda: datetime.now(UTC))
        self._sql_down: str | None = None     # último error de conexión ya avisado en el log
        self.schema_ready = False             # analytics.* creado/verificado
        self.schema_error: str | None = None

    def ensure_schema(self) -> None:
        """Crea o verifica analytics.* (el script es idempotente). Si SQL Server no estaba
        disponible al arrancar, el bucle lo vuelve a intentar en cada ciclo."""
        if self.schema_ready:
            return
        try:
            self.repo.ensure_schema(self.settings.schema_sql)
        except SqlUnavailable as ex:
            self.schema_error = str(ex)
            raise
        except Exception as ex:
            if str(ex) != self.schema_error:
                log.exception("No se pudo crear/verificar el esquema analytics (%s).", self.settings.schema_sql)
            self.schema_error = str(ex)
            raise
        self.schema_ready = True
        self.schema_error = None

    # ── Trabajos ────────────────────────────────────────────────────────

    def snapshot(self) -> int:
        """Una foto de los viewers de cada canal en vivo."""
        def job():
            now = self.clock().replace(microsecond=0)
            live = [c for c in self.repo.channels() if c.is_live]
            return self.repo.insert_snapshots([Snapshot(c.id, now, c.viewer_count, c.category_id) for c in live]), None
        return self._run("snapshot", job)

    def aggregate(self, days: int | None = None) -> int:
        """Recalcula ChannelDaily/Hourly, CategoryDaily y ChannelTrends.

        days=None → hoy y ayer (o backfill_days la primera vez que corre)."""
        def job():
            now = self.clock()
            count = days or (2 if self.repo.has_daily_data() else self.settings.backfill_days)
            today = now.astimezone(self.tz).date()
            window = day_range(today - timedelta(days=count - 1), today)
            start_utc, end_utc = window_bounds_utc(window, self.tz)

            chat, sessions, warning = [], [], None
            if self.mongo is not None:
                try:
                    chat = self.mongo.chat_between(start_utc, end_utc)
                    sessions = self.mongo.sessions_overlapping(start_utc, end_utc)
                except Exception as ex:  # Mongo caído: se sigue sin chat
                    warning = f"MongoDB no disponible: {ex}"
                    log.warning(warning)

            daily, hourly, category = build_rows(
                channels=self.repo.channels(),
                follows=self.repo.follows(),
                snapshots=self.repo.snapshots_between(start_utc, end_utc),
                chat=chat,
                sessions=sessions,
                days=window,
                tz=self.tz,
                snapshot_seconds=self.settings.snapshot_seconds,
                now_utc=now,
            )
            written = self.repo.replace_aggregates(window[0], window[-1], daily, hourly, category, now)
            written += self._update_trends(today, now)
            return written, warning
        return self._run("aggregate", job)

    def _update_trends(self, today, now) -> int:
        window = self.settings.trend_days
        rows = self.repo.channel_daily(today - timedelta(days=2 * window - 1), today)
        by_channel = defaultdict(list)
        for row in rows:
            by_channel[row.channel_id].append(row)
        trends = [channel_trend(channel_id, channel_rows, window) for channel_id, channel_rows in by_channel.items()]
        return self.repo.replace_trends(trends, now)

    def cleanup(self) -> int:
        def job():
            cutoff = self.clock() - timedelta(days=self.settings.retention_days)
            return self.repo.delete_snapshots_before(cutoff), None
        return self._run("cleanup", job)

    def _run(self, name: str, job) -> int:
        """Ejecuta un trabajo dejando constancia en analytics.EtlRuns."""
        run_id = self.repo.start_run(name, self.clock())
        try:
            rows, warning = job()
        except Exception as ex:
            log.exception("Falló el trabajo %s", name)
            self.repo.finish_run(run_id, self.clock(), 0, False, str(ex))
            raise
        self.repo.finish_run(run_id, self.clock(), rows, warning is None, warning)
        return rows

    # ── Bucle ───────────────────────────────────────────────────────────

    def run_forever(self, stop: threading.Event) -> None:
        # [cada cuántos segundos, trabajo, última vez que corrió]
        schedule = [
            [self.settings.snapshot_seconds, self.snapshot, float("-inf")],
            [self.settings.aggregate_seconds, self.aggregate, float("-inf")],
            [24 * 3600, self.cleanup, float("-inf")],
        ]
        while not stop.is_set():
            for entry in schedule:
                interval, job, last = entry
                if time.monotonic() - last >= interval:
                    self.run_safely(job, interval)
                    entry[2] = time.monotonic()
            stop.wait(1.0)

    def run_safely(self, job, interval: int) -> None:
        """Corre un trabajo del bucle sin dejar que un error lo detenga.

        Si SQL Server no responde, ni siquiera se puede anotar el error en EtlRuns: se avisa
        en la consola UNA vez (no cada minuto) y también cuando vuelve a responder."""
        try:
            self.ensure_schema()
            job()
        except SqlUnavailable as ex:
            if str(ex) != self._sql_down:
                log.error("%s. Se reintenta solo cada %d s; revisa que SQL Server esté corriendo.", ex, interval)
                self._sql_down = str(ex)
            return
        except Exception:
            return  # ya quedó en el log y en EtlRuns; se reintenta en el próximo ciclo
        if self._sql_down is not None:
            log.info("SQL Server volvió a responder; el ETL sigue normal.")
            self._sql_down = None

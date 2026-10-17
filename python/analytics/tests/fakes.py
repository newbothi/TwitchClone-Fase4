"""Dobles en memoria de SqlRepository y MongoSource: mismos métodos, sin bases de datos."""

from __future__ import annotations

from datetime import date, datetime

from analytics_service.transform import ChannelInfo, ChatEvent, Session, Snapshot, as_utc


class FakeRepository:
    def __init__(self):
        self.channels_data: list[ChannelInfo] = []
        self.follows_data: list[tuple[str, datetime]] = []
        self.categories_data: dict[int, str] = {}
        self.snapshots: list[Snapshot] = []
        self.daily = []
        self.hourly = []
        self.category = []
        self.trend_rows = []
        self.runs: list[dict] = []
        self.replaced_windows: list[tuple[date, date]] = []
        self.schema_calls = 0
        self.schema_failures: list[Exception] = []   # errores a lanzar en las próximas llamadas a ensure_schema

    # esquema
    def ensure_schema(self, path):
        self.schema_calls += 1
        if self.schema_failures:
            raise self.schema_failures.pop(0)

    # dbo
    def channels(self): return list(self.channels_data)
    def follows(self): return list(self.follows_data)
    def categories(self): return dict(self.categories_data)

    # analytics: escritura
    def insert_snapshots(self, snapshots):
        self.snapshots.extend(snapshots)
        return len(snapshots)

    def replace_aggregates(self, first, last, daily, hourly, category, now_utc):
        self.replaced_windows.append((first, last))
        keep = lambda rows: [r for r in rows if not (first <= r.day <= last)]
        self.daily = keep(self.daily) + list(daily)
        self.hourly = keep(self.hourly) + list(hourly)
        self.category = keep(self.category) + list(category)
        return len(daily) + len(hourly) + len(category)

    def replace_trends(self, trends, now_utc):
        self.trend_rows = list(trends)
        return len(trends)

    def delete_snapshots_before(self, cutoff_utc):
        before = len(self.snapshots)
        self.snapshots = [s for s in self.snapshots if as_utc(s.captured_at_utc) >= as_utc(cutoff_utc)]
        return before - len(self.snapshots)

    # analytics: lectura
    def has_daily_data(self): return bool(self.daily)

    def snapshots_between(self, start_utc, end_utc, channel_id=None):
        return sorted(
            (s for s in self.snapshots
             if as_utc(start_utc) <= as_utc(s.captured_at_utc) < as_utc(end_utc) and (channel_id is None or s.channel_id == channel_id)),
            key=lambda s: s.captured_at_utc,
        )

    def channel_daily(self, first, last, channel_id=None):
        return sorted((r for r in self.daily if first <= r.day <= last and (channel_id is None or r.channel_id == channel_id)),
                      key=lambda r: (r.channel_id, r.day))

    def channel_hourly(self, first, last, channel_id):
        return sorted((r for r in self.hourly if first <= r.day <= last and r.channel_id == channel_id), key=lambda r: (r.day, r.hour))

    def category_daily(self, first, last):
        return [r for r in self.category if first <= r.day <= last]

    def trends(self): return list(self.trend_rows)

    # bitácora
    def start_run(self, job, started_utc):
        self.runs.append({"id": len(self.runs) + 1, "job": job, "startedAtUtc": started_utc, "ok": None})
        return len(self.runs)

    def finish_run(self, run_id, finished_utc, rows, ok, error):
        self.runs[run_id - 1].update(finishedAtUtc=finished_utc, rowsWritten=rows, ok=ok, error=error)

    def last_runs(self):
        latest = {}
        for run in self.runs:
            latest[run["job"]] = run
        return [{k: (v.isoformat() if isinstance(v, datetime) else v) for k, v in run.items() if k != "id"} for run in latest.values()]


class FakeMongo:
    def __init__(self):
        self.chat: list[ChatEvent] = []
        self.sessions: list[Session] = []
        self.down = False

    def chat_between(self, start_utc, end_utc):
        if self.down:
            raise ConnectionError("Mongo apagado (simulado)")
        return [e for e in self.chat if as_utc(start_utc) <= as_utc(e.sent_at_utc) < as_utc(end_utc)]

    def sessions_overlapping(self, start_utc, end_utc):
        if self.down:
            raise ConnectionError("Mongo apagado (simulado)")
        return [s for s in self.sessions
                if as_utc(s.started_at_utc) < as_utc(end_utc) and (s.ended_at_utc is None or as_utc(s.ended_at_utc) > as_utc(start_utc))]


class Clock:
    """Reloj que se puede adelantar a mano."""

    def __init__(self, now: datetime):
        self.now = now

    def __call__(self) -> datetime:
        return self.now

"""Acceso a SQL Server con pyodbc.

Regla del servicio: de dbo.* solo LEE (Channels, Follows, Categories); escribe únicamente
en analytics.*. Todas las consultas usan parámetros (?) — nunca se arma SQL con strings.
"""

from __future__ import annotations

import re
from contextlib import contextmanager
from datetime import date, datetime
from decimal import Decimal
from pathlib import Path

from .transform import (
    UTC,
    CategoryDailyRow,
    ChannelDailyRow,
    ChannelHourlyRow,
    ChannelInfo,
    Snapshot,
)
from .trends import ChannelTrend

_GO = re.compile(r"^\s*GO\s*$", re.IGNORECASE | re.MULTILINE)


def split_batches(script: str) -> list[str]:
    """Separa un .sql en lotes por las líneas "GO" (como SSMS)."""
    return [batch.strip() for batch in _GO.split(script) if batch.strip()]


# El driver ODBC antiguo de Windows ("SQL Server") devuelve DATETIME2 y DATE como texto.
def _dt(value) -> datetime | None:
    if value is None or isinstance(value, datetime):
        return value
    text = str(value).strip().replace("T", " ")
    if "." in text:  # "2026-10-16 10:00:00.1234567" → máximo 6 decimales para Python
        head, fraction = text.split(".", 1)
        text = f"{head}.{fraction[:6]}"
    return datetime.fromisoformat(text)


def _d(value) -> date:
    if isinstance(value, datetime):
        return value.date()
    if isinstance(value, date):
        return value
    return date.fromisoformat(str(value)[:10])


def _f(value) -> float:
    return float(value) if isinstance(value, (Decimal, int, float)) else float(str(value))


def _naive_utc(value: datetime) -> datetime:
    """Para escribir en DATETIME2(0): sin zona horaria y sin microsegundos."""
    if value.tzinfo is not None:
        value = value.astimezone(UTC).replace(tzinfo=None)
    return value.replace(microsecond=0)


class SqlUnavailable(RuntimeError):
    """No se pudo conectar a SQL Server (servicio detenido, nombre del servidor, driver...).
    La API la convierte en 503 con el motivo, en vez de un error 500 genérico."""


class SqlRepository:
    def __init__(self, connection_string: str):
        if not connection_string:
            raise ValueError("Falta la conexión a SQL Server (ANALYTICS_SQL o appsettings.json de la app web).")
        self._connection_string = connection_string
        self._fast = "Driver={SQL Server}" not in connection_string.replace("DRIVER", "Driver")

    @contextmanager
    def _connect(self):
        try:
            import pyodbc  # importado aquí: las pruebas sin SQL Server no lo necesitan
            connection = pyodbc.connect(self._connection_string, autocommit=False, timeout=10)
        except Exception as ex:
            raise SqlUnavailable(f"No se pudo conectar a SQL Server: {ex}") from ex
        try:
            yield connection
            connection.commit()
        except Exception:
            connection.rollback()
            raise
        finally:
            connection.close()

    def _cursor(self, connection):
        cursor = connection.cursor()
        if self._fast:
            cursor.fast_executemany = True
        return cursor

    # ── Esquema ─────────────────────────────────────────────────────────

    def ensure_schema(self, sql_path: Path) -> None:
        script = sql_path.read_text(encoding="utf-8-sig")
        with self._connect() as connection:
            cursor = connection.cursor()
            for batch in split_batches(script):
                cursor.execute(batch)

    # ── dbo (solo lectura) ──────────────────────────────────────────────

    def channels(self) -> list[ChannelInfo]:
        with self._connect() as connection:
            rows = connection.cursor().execute(
                "SELECT Id, UserId, Name, CategoryId, CreatedAt, IsLive, ViewerCount FROM dbo.Channels"
            ).fetchall()
        return [ChannelInfo(int(r[0]), str(r[1]), str(r[2]), r[3], _dt(r[4]), bool(r[5]), int(r[6] or 0)) for r in rows]

    def follows(self) -> list[tuple[str, datetime]]:
        with self._connect() as connection:
            rows = connection.cursor().execute("SELECT FollowedId, CreatedAt FROM dbo.Follows").fetchall()
        return [(str(r[0]), _dt(r[1])) for r in rows]

    def categories(self) -> dict[int, str]:
        with self._connect() as connection:
            rows = connection.cursor().execute("SELECT Id, Name FROM dbo.Categories").fetchall()
        return {int(r[0]): str(r[1]) for r in rows}

    # ── analytics: escritura ────────────────────────────────────────────

    def insert_snapshots(self, snapshots: list[Snapshot]) -> int:
        if not snapshots:
            return 0
        with self._connect() as connection:
            self._cursor(connection).executemany(
                "INSERT INTO analytics.ViewerSnapshots (ChannelId, CapturedAtUtc, Viewers, CategoryId) VALUES (?, ?, ?, ?)",
                [(s.channel_id, _naive_utc(s.captured_at_utc), s.viewers, s.category_id) for s in snapshots],
            )
        return len(snapshots)

    def replace_aggregates(self, first: date, last: date, daily: list[ChannelDailyRow],
                           hourly: list[ChannelHourlyRow], category: list[CategoryDailyRow], now_utc: datetime) -> int:
        """Reemplaza los resúmenes de [first, last] en una sola transacción: o queda todo o nada."""
        updated = _naive_utc(now_utc)
        with self._connect() as connection:
            cursor = self._cursor(connection)
            for table in ("ChannelDaily", "ChannelHourly", "CategoryDaily"):
                cursor.execute(f"DELETE FROM analytics.{table} WHERE [Day] BETWEEN ? AND ?", first, last)
            if daily:
                cursor.executemany(
                    """INSERT INTO analytics.ChannelDaily (ChannelId, [Day], NewFollowers, TotalFollowers, MinutesLive,
                           AvgViewers, PeakViewers, ViewerMinutes, ChatMessages, UniqueChatters, SimulatedMessages, UpdatedAtUtc)
                       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
                    [(r.channel_id, r.day, r.new_followers, r.total_followers, r.minutes_live, Decimal(str(r.avg_viewers)),
                      r.peak_viewers, r.viewer_minutes, r.chat_messages, r.unique_chatters, r.simulated_messages, updated)
                     for r in daily],
                )
            if hourly:
                cursor.executemany(
                    """INSERT INTO analytics.ChannelHourly (ChannelId, [Day], [Hour], AvgViewers, PeakViewers, Samples, ChatMessages, UpdatedAtUtc)
                       VALUES (?, ?, ?, ?, ?, ?, ?, ?)""",
                    [(r.channel_id, r.day, r.hour, Decimal(str(r.avg_viewers)), r.peak_viewers, r.samples, r.chat_messages, updated)
                     for r in hourly],
                )
            if category:
                cursor.executemany(
                    """INSERT INTO analytics.CategoryDaily (CategoryId, [Day], ChannelsLive, ViewerMinutes, PeakViewers, UpdatedAtUtc)
                       VALUES (?, ?, ?, ?, ?, ?)""",
                    [(r.category_id, r.day, r.channels_live, r.viewer_minutes, r.peak_viewers, updated) for r in category],
                )
        return len(daily) + len(hourly) + len(category)

    def replace_trends(self, trends: list[ChannelTrend], now_utc: datetime) -> int:
        updated = _naive_utc(now_utc)
        with self._connect() as connection:
            cursor = self._cursor(connection)
            cursor.execute("DELETE FROM analytics.ChannelTrends")
            if trends:
                cursor.executemany(
                    """INSERT INTO analytics.ChannelTrends (ChannelId, WindowDays, FollowersPerDay, FollowersR2, ProjectedFollowers7d,
                           FollowersGrowthPct, AvgViewersPerDay, ViewersGrowthPct, UpdatedAtUtc)
                       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)""",
                    [(t.channel_id, t.window_days, Decimal(str(t.followers_per_day)), Decimal(str(t.followers_r2)),
                      t.projected_followers_7d,
                      None if t.followers_growth_pct is None else Decimal(str(t.followers_growth_pct)),
                      Decimal(str(t.avg_viewers_per_day)),
                      None if t.viewers_growth_pct is None else Decimal(str(t.viewers_growth_pct)),
                      updated)
                     for t in trends],
                )
        return len(trends)

    def delete_snapshots_before(self, cutoff_utc: datetime) -> int:
        with self._connect() as connection:
            cursor = connection.cursor()
            cursor.execute("DELETE FROM analytics.ViewerSnapshots WHERE CapturedAtUtc < ?", _naive_utc(cutoff_utc))
            return max(cursor.rowcount, 0)

    # ── analytics: lectura ──────────────────────────────────────────────

    def has_daily_data(self) -> bool:
        with self._connect() as connection:
            return connection.cursor().execute("SELECT TOP (1) 1 FROM analytics.ChannelDaily").fetchone() is not None

    def snapshots_between(self, start_utc: datetime, end_utc: datetime, channel_id: int | None = None) -> list[Snapshot]:
        sql = "SELECT ChannelId, CapturedAtUtc, Viewers, CategoryId FROM analytics.ViewerSnapshots WHERE CapturedAtUtc >= ? AND CapturedAtUtc < ?"
        params: list = [_naive_utc(start_utc), _naive_utc(end_utc)]
        if channel_id is not None:
            sql += " AND ChannelId = ?"
            params.append(channel_id)
        with self._connect() as connection:
            rows = connection.cursor().execute(sql + " ORDER BY CapturedAtUtc", *params).fetchall()
        return [Snapshot(int(r[0]), _dt(r[1]).replace(tzinfo=UTC), int(r[2]), r[3]) for r in rows]

    def channel_daily(self, first: date, last: date, channel_id: int | None = None) -> list[ChannelDailyRow]:
        sql = """SELECT ChannelId, [Day], NewFollowers, TotalFollowers, MinutesLive, AvgViewers, PeakViewers,
                        ViewerMinutes, ChatMessages, UniqueChatters, SimulatedMessages
                 FROM analytics.ChannelDaily WHERE [Day] BETWEEN ? AND ?"""
        params: list = [first, last]
        if channel_id is not None:
            sql += " AND ChannelId = ?"
            params.append(channel_id)
        with self._connect() as connection:
            rows = connection.cursor().execute(sql + " ORDER BY ChannelId, [Day]", *params).fetchall()
        return [ChannelDailyRow(int(r[0]), _d(r[1]), int(r[2]), int(r[3]), int(r[4]), _f(r[5]), int(r[6]),
                                int(r[7]), int(r[8]), int(r[9]), int(r[10])) for r in rows]

    def channel_hourly(self, first: date, last: date, channel_id: int) -> list[ChannelHourlyRow]:
        with self._connect() as connection:
            rows = connection.cursor().execute(
                """SELECT ChannelId, [Day], [Hour], AvgViewers, PeakViewers, Samples, ChatMessages
                   FROM analytics.ChannelHourly WHERE ChannelId = ? AND [Day] BETWEEN ? AND ? ORDER BY [Day], [Hour]""",
                channel_id, first, last,
            ).fetchall()
        return [ChannelHourlyRow(int(r[0]), _d(r[1]), int(r[2]), _f(r[3]), int(r[4]), int(r[5]), int(r[6])) for r in rows]

    def category_daily(self, first: date, last: date) -> list[CategoryDailyRow]:
        with self._connect() as connection:
            rows = connection.cursor().execute(
                """SELECT CategoryId, [Day], ChannelsLive, ViewerMinutes, PeakViewers
                   FROM analytics.CategoryDaily WHERE [Day] BETWEEN ? AND ? ORDER BY CategoryId, [Day]""",
                first, last,
            ).fetchall()
        return [CategoryDailyRow(int(r[0]), _d(r[1]), int(r[2]), int(r[3]), int(r[4])) for r in rows]

    def trends(self) -> list[ChannelTrend]:
        with self._connect() as connection:
            rows = connection.cursor().execute(
                """SELECT ChannelId, WindowDays, FollowersPerDay, FollowersR2, ProjectedFollowers7d,
                          FollowersGrowthPct, AvgViewersPerDay, ViewersGrowthPct
                   FROM analytics.ChannelTrends"""
            ).fetchall()
        return [ChannelTrend(int(r[0]), int(r[1]), _f(r[2]), _f(r[3]), int(r[4]),
                             None if r[5] is None else _f(r[5]), _f(r[6]), None if r[7] is None else _f(r[7]))
                for r in rows]

    # ── Bitácora del ETL ────────────────────────────────────────────────

    def start_run(self, job: str, started_utc: datetime) -> int:
        with self._connect() as connection:
            row = connection.cursor().execute(
                "INSERT INTO analytics.EtlRuns (Job, StartedAtUtc) OUTPUT INSERTED.Id VALUES (?, ?)",
                job, _naive_utc(started_utc),
            ).fetchone()
        return int(row[0])

    def finish_run(self, run_id: int, finished_utc: datetime, rows: int, ok: bool, error: str | None) -> None:
        with self._connect() as connection:
            connection.cursor().execute(
                "UPDATE analytics.EtlRuns SET FinishedAtUtc = ?, RowsWritten = ?, Ok = ?, Error = ? WHERE Id = ?",
                _naive_utc(finished_utc), rows, ok, (error or None) and error[:1000], run_id,
            )

    def last_runs(self) -> list[dict]:
        with self._connect() as connection:
            rows = connection.cursor().execute(
                """SELECT r.Job, r.StartedAtUtc, r.FinishedAtUtc, r.RowsWritten, r.Ok, r.Error
                   FROM analytics.EtlRuns r
                   WHERE r.Id IN (SELECT MAX(Id) FROM analytics.EtlRuns GROUP BY Job)
                   ORDER BY r.Job"""
            ).fetchall()
        return [
            {"job": r[0], "startedAtUtc": _iso(r[1]), "finishedAtUtc": _iso(r[2]), "rowsWritten": r[3],
             "ok": None if r[4] is None else bool(r[4]), "error": r[5]}
            for r in rows
        ]


def _iso(value) -> str | None:
    dt = _dt(value)
    return None if dt is None else dt.replace(tzinfo=UTC).isoformat(timespec="seconds")

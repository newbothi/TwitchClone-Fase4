"""API de crecimiento y tendencias (FastAPI, puerto 8002). Solo lectura de analytics.*,
salvo POST /etl/run que fuerza una pasada del ETL.

  GET  /health                          ¿está vivo? ¿cuándo corrió el ETL?
  POST /etl/run?days=30                 corre el ETL ahora (foto + resúmenes de N días)
  GET  /channels/{id}/summary?days=30   seguidores, viewers, horas en vivo, chat, tendencia
  GET  /channels/{id}/daily?days=30     una fila por día (para gráficos)
  GET  /channels/{id}/viewers?hours=24  historia de viewers (agrupada cada N minutos)
  GET  /channels/{id}/hours?days=28     viewers y mensajes promedio por hora del día
  GET  /trends/categories?days=7        categorías con más horas vistas y su crecimiento
  GET  /trends/channels?days=14         canales en alza (pendiente de la regresión)
"""

from __future__ import annotations

import logging
import threading
from contextlib import asynccontextmanager
from datetime import date, datetime, timedelta

from fastapi import FastAPI, HTTPException, Query, Request
from fastapi.responses import JSONResponse

from . import __version__
from .config import Settings
from .etl import EtlRunner
from .repository import SqlUnavailable
from .transform import UTC, ChannelDailyRow, as_utc, day_range, to_local
from .trends import best_hours, channel_trend, fill_days, growth_pct, rank_categories, rank_channels

log = logging.getLogger("analytics.api")


def create_app(settings: Settings | None = None, repo=None, mongo=None, clock=None) -> FastAPI:
    settings = settings or Settings.from_env()

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        state = app.state
        state.repo = repo
        state.mongo = mongo
        if state.repo is None:
            from .repository import SqlRepository
            state.repo = SqlRepository(settings.sql_connection)
        if state.mongo is None and repo is None:
            try:
                from .mongo_source import MongoSource
                state.mongo = MongoSource(settings.mongo_uri, settings.mongo_database)
            except Exception:
                log.exception("No se pudo crear el cliente de MongoDB; el ETL seguirá sin datos del chat.")
        state.runner = EtlRunner(state.repo, state.mongo, settings, clock)
        if repo is not None:
            state.runner.schema_ready = True   # repositorio de prueba: no hay esquema que crear
        elif not settings.run_etl:
            # Solo API: el esquema se crea aquí. Con ETL, lo crea (y reintenta) el bucle apenas parte.
            try:
                state.runner.ensure_schema()
            except SqlUnavailable as ex:
                log.error("No se pudo crear/verificar el esquema analytics. %s", ex)
            except Exception:
                pass  # ensure_schema ya lo dejó en el log
        state.stop = threading.Event()
        if settings.run_etl:
            threading.Thread(target=state.runner.run_forever, args=(state.stop,), name="analytics-etl", daemon=True).start()
        yield
        state.stop.set()

    app = FastAPI(title="TwitchClone Analytics", version=__version__, lifespan=lifespan)

    @app.exception_handler(SqlUnavailable)
    async def sql_unavailable(request: Request, ex: SqlUnavailable):
        return JSONResponse(status_code=503, content={"detail": str(ex)})

    def today(request: Request) -> date:
        return to_local(request.app.state.runner.clock(), request.app.state.runner.tz).date()

    def window(request: Request, days: int) -> list[date]:
        end = today(request)
        return day_range(end - timedelta(days=days - 1), end)

    def require_channel(request: Request, channel_id: int):
        channel = next((c for c in request.app.state.repo.channels() if c.id == channel_id), None)
        if channel is None:
            raise HTTPException(status_code=404, detail="El canal no existe.")
        return channel

    # ── Estado y ETL ────────────────────────────────────────────────────

    @app.get("/health")
    def health(request: Request):
        state = request.app.state
        runner: EtlRunner = state.runner
        try:
            runs = state.repo.last_runs() if runner.schema_ready else []
            sql_ok = runner.schema_ready
            error = None if sql_ok else (runner.schema_error or "Todavía no se verifica el esquema analytics.")
        except Exception as ex:
            runs, sql_ok, error = [], False, str(ex)
        return {
            "status": "ok" if sql_ok else "degraded",
            "version": __version__,
            "timezone": settings.timezone,
            "sqlSource": settings.sources.get("sql"),
            "error": error,
            "lastRuns": runs,
        }

    @app.post("/etl/run")
    def run_etl(request: Request, days: int | None = Query(default=None, ge=1, le=365)):
        runner: EtlRunner = request.app.state.runner
        return {"snapshots": runner.snapshot(), "rowsWritten": runner.aggregate(days)}

    # ── Canal ───────────────────────────────────────────────────────────

    @app.get("/channels/{channel_id}/daily")
    def channel_daily(request: Request, channel_id: int, days: int = Query(default=30, ge=1, le=365)):
        require_channel(request, channel_id)
        span = window(request, days)
        rows = request.app.state.repo.channel_daily(span[0], span[-1], channel_id)
        filled = fill_days(rows, span, lambda d: _empty_day(channel_id, d, rows))
        return [_daily_json(r) for r in filled]

    @app.get("/channels/{channel_id}/summary")
    def channel_summary(request: Request, channel_id: int, days: int = Query(default=30, ge=1, le=365)):
        channel = require_channel(request, channel_id)
        repo = request.app.state.repo
        span = window(request, days)
        rows = repo.channel_daily(span[0] - timedelta(days=days), span[-1], channel_id)
        current = [r for r in rows if r.day >= span[0]]
        previous = [r for r in rows if r.day < span[0]]
        trend = channel_trend(channel_id, fill_days(rows, day_range(span[0] - timedelta(days=days), span[-1]),
                                                    lambda d: _empty_day(channel_id, d, rows)), days)
        live = [r for r in current if r.minutes_live > 0]
        live_prev = [r for r in previous if r.minutes_live > 0]
        avg = _mean([r.avg_viewers for r in live])
        avg_prev = _mean([r.avg_viewers for r in live_prev])
        minutes = sum(r.minutes_live for r in current)
        messages = sum(r.chat_messages for r in current)
        new = sum(r.new_followers for r in current)
        new_prev = sum(r.new_followers for r in previous)

        return {
            "channelId": channel_id,
            "name": channel.name,
            "days": days,
            "from": span[0].isoformat(),
            "to": span[-1].isoformat(),
            "followers": {
                "total": max((r.total_followers for r in current), default=0),
                "new": new,
                "previousNew": new_prev,
                "growthPct": growth_pct(new, new_prev),
                "perDay": trend.followers_per_day,
                "r2": trend.followers_r2,
                "projected7d": trend.projected_followers_7d,
            },
            "viewers": {
                "average": round(avg, 2),
                "previousAverage": round(avg_prev, 2),
                "growthPct": growth_pct(avg, avg_prev),
                "peak": max((r.peak_viewers for r in current), default=0),
                "hoursWatched": round(sum(r.viewer_minutes for r in current) / 60, 1),
                "trendPerDay": trend.avg_viewers_per_day,
            },
            "streaming": {"hoursLive": round(minutes / 60, 1), "daysLive": len(live)},
            "chat": {
                "messages": messages,
                "simulatedMessages": sum(r.simulated_messages for r in current),
                "messagesPerHourLive": round(messages / (minutes / 60), 1) if minutes else 0.0,
            },
            "bestHours": best_hours(repo.channel_hourly(span[0], span[-1], channel_id)),
        }

    @app.get("/channels/{channel_id}/viewers")
    def channel_viewers(request: Request, channel_id: int, hours: int = Query(default=24, ge=1, le=24 * 31),
                        bucket: int = Query(default=5, ge=1, le=1440)):
        require_channel(request, channel_id)
        runner: EtlRunner = request.app.state.runner
        end = as_utc(runner.clock())
        snapshots = request.app.state.repo.snapshots_between(end - timedelta(hours=hours), end, channel_id)
        buckets: dict[datetime, list[int]] = {}
        for s in snapshots:
            t = as_utc(s.captured_at_utc)
            key = t.replace(second=0, microsecond=0) - timedelta(minutes=t.minute % bucket)
            buckets.setdefault(key, []).append(s.viewers)
        return [
            {"time": key.isoformat(timespec="minutes"), "avgViewers": round(_mean(values), 2), "peakViewers": max(values)}
            for key, values in sorted(buckets.items())
        ]

    @app.get("/channels/{channel_id}/hours")
    def channel_hours(request: Request, channel_id: int, days: int = Query(default=28, ge=1, le=365)):
        require_channel(request, channel_id)
        span = window(request, days)
        rows = request.app.state.repo.channel_hourly(span[0], span[-1], channel_id)
        result = []
        for hour in range(24):
            at_hour = [r for r in rows if r.hour == hour]
            with_viewers = [r for r in at_hour if r.samples > 0]
            result.append({
                "hour": hour,
                "avgViewers": round(_mean([r.avg_viewers for r in with_viewers]), 2),
                "peakViewers": max((r.peak_viewers for r in at_hour), default=0),
                "chatMessages": sum(r.chat_messages for r in at_hour),
                "daysWithData": len({r.day for r in with_viewers}),
            })
        return result

    # ── Tendencias de la plataforma ─────────────────────────────────────

    @app.get("/trends/categories")
    def trends_categories(request: Request, days: int = Query(default=7, ge=1, le=180)):
        repo = request.app.state.repo
        span = window(request, days)
        rows = repo.category_daily(span[0] - timedelta(days=days), span[-1])
        current = [r for r in rows if r.day >= span[0]]
        previous = [r for r in rows if r.day < span[0]]
        return rank_categories(current, previous, repo.categories())

    @app.get("/trends/channels")
    def trends_channels(request: Request, days: int = Query(default=14, ge=2, le=180), limit: int = Query(default=10, ge=1, le=100)):
        repo = request.app.state.repo
        span = window(request, days)
        all_days = day_range(span[0] - timedelta(days=days), span[-1])
        rows = repo.channel_daily(all_days[0], all_days[-1])
        by_channel: dict[int, list[ChannelDailyRow]] = {}
        for r in rows:
            by_channel.setdefault(r.channel_id, []).append(r)
        trends = [
            channel_trend(cid, fill_days(channel_rows, all_days, lambda d, cid=cid, cr=channel_rows: _empty_day(cid, d, cr)), days)
            for cid, channel_rows in by_channel.items()
        ]
        names = {c.id: c.name for c in repo.channels()}
        return rank_channels(trends, names, limit)

    return app


# ── Utilidades ──────────────────────────────────────────────────────────

def _mean(values) -> float:
    values = [float(v) for v in values]
    return sum(values) / len(values) if values else 0.0


def _empty_day(channel_id: int, day: date, rows: list[ChannelDailyRow]) -> ChannelDailyRow:
    """Día sin fila: sin actividad, con el total de seguidores del día anterior conocido."""
    before = [r for r in rows if r.day < day]
    total = max(before, key=lambda r: r.day).total_followers if before else 0
    return ChannelDailyRow(channel_id, day, 0, total, 0, 0.0, 0, 0, 0, 0, 0)


def _daily_json(r: ChannelDailyRow) -> dict:
    return {
        "day": r.day.isoformat(),
        "newFollowers": r.new_followers,
        "totalFollowers": r.total_followers,
        "minutesLive": r.minutes_live,
        "avgViewers": r.avg_viewers,
        "peakViewers": r.peak_viewers,
        "hoursWatched": round(r.viewer_minutes / 60, 1),
        "chatMessages": r.chat_messages,
        "uniqueChatters": r.unique_chatters,
        "simulatedMessages": r.simulated_messages,
    }


def configure_logging() -> None:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")

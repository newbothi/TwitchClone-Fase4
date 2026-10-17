"""Transformaciones del ETL: funciones PURAS (sin base de datos), para poder probarlas.

Entra: canales, follows, fotos de viewers, mensajes del chat y sesiones de stream.
Sale: las filas de analytics.ChannelDaily, ChannelHourly y CategoryDaily.

Ojo con las horas:
  - Follows.CreatedAt y Channels.CreatedAt los guarda C# con DateTime.Now → hora LOCAL.
  - ViewerSnapshots, ChatMessages.SentAt y StreamSessions → UTC.
  Todo se lleva a la zona configurada (America/Santiago) antes de agrupar por día u hora.
"""

from __future__ import annotations

from collections import defaultdict
from dataclasses import dataclass, field
from datetime import date, datetime, time, timedelta, timezone
from zoneinfo import ZoneInfo

UTC = timezone.utc


# ── Entradas ─────────────────────────────────────────────────────────────

@dataclass(frozen=True)
class ChannelInfo:
    id: int
    user_id: str
    name: str
    category_id: int | None = None
    created_at_local: datetime | None = None
    is_live: bool = False
    viewer_count: int = 0


@dataclass(frozen=True)
class Snapshot:
    channel_id: int
    captured_at_utc: datetime
    viewers: int
    category_id: int | None = None


@dataclass(frozen=True)
class ChatEvent:
    channel_id: int
    user_id: str
    sent_at_utc: datetime
    simulated: bool = False


@dataclass(frozen=True)
class Session:
    channel_id: int
    started_at_utc: datetime
    ended_at_utc: datetime | None
    peak_viewers: int = 0


# ── Salidas (una por fila de cada tabla de analytics) ────────────────────

@dataclass(frozen=True)
class ChannelDailyRow:
    channel_id: int
    day: date
    new_followers: int
    total_followers: int
    minutes_live: int
    avg_viewers: float
    peak_viewers: int
    viewer_minutes: int
    chat_messages: int
    unique_chatters: int
    simulated_messages: int


@dataclass(frozen=True)
class ChannelHourlyRow:
    channel_id: int
    day: date
    hour: int
    avg_viewers: float
    peak_viewers: int
    samples: int
    chat_messages: int


@dataclass(frozen=True)
class CategoryDailyRow:
    category_id: int
    day: date
    channels_live: int
    viewer_minutes: int
    peak_viewers: int


# ── Fechas ───────────────────────────────────────────────────────────────

def as_utc(value: datetime) -> datetime:
    """Datetime "naive" que viene de la base → se asume UTC."""
    return value.replace(tzinfo=UTC) if value.tzinfo is None else value.astimezone(UTC)


def to_local(value_utc: datetime, tz: ZoneInfo) -> datetime:
    return as_utc(value_utc).astimezone(tz)


def day_range(first: date, last: date) -> list[date]:
    return [first + timedelta(days=i) for i in range((last - first).days + 1)]


def local_day_bounds_utc(day: date, tz: ZoneInfo) -> tuple[datetime, datetime]:
    """[inicio, fin) del día local, en UTC. Respeta los cambios de horario."""
    start = datetime.combine(day, time.min, tzinfo=tz)
    end = datetime.combine(day + timedelta(days=1), time.min, tzinfo=tz)
    return start.astimezone(UTC), end.astimezone(UTC)


def window_bounds_utc(days: list[date], tz: ZoneInfo) -> tuple[datetime, datetime]:
    return local_day_bounds_utc(days[0], tz)[0], local_day_bounds_utc(days[-1], tz)[1]


# ── Seguidores ───────────────────────────────────────────────────────────

def followers_by_day(follow_dates_local: list[datetime], days: list[date]) -> dict[date, tuple[int, int]]:
    """{día: (nuevos ese día, total al final del día)}.

    Solo se conocen los follows que siguen vigentes (un unfollow borra la fila), así que
    al reconstruir días pasados el total es "cuántos de los seguidores ACTUALES ya te
    seguían ese día". Desde que el ETL corre todos los días, el total de hoy es exacto.
    """
    per_day: dict[date, int] = defaultdict(int)
    for created in follow_dates_local:
        per_day[created.date()] += 1

    first = days[0]
    running = sum(n for d, n in per_day.items() if d < first)
    result: dict[date, tuple[int, int]] = {}
    for day in days:
        new = per_day.get(day, 0)
        running += new
        result[day] = (new, running)
    return result


# ── Viewers ──────────────────────────────────────────────────────────────

@dataclass
class _Acc:
    samples: int = 0
    total: int = 0
    peak: int = 0

    def add(self, viewers: int) -> None:
        self.samples += 1
        self.total += viewers
        self.peak = max(self.peak, viewers)

    @property
    def average(self) -> float:
        return round(self.total / self.samples, 2) if self.samples else 0.0


@dataclass
class _CategoryAcc:
    channels: set = field(default_factory=set)
    viewer_total: int = 0
    peak: int = 0


def aggregate_snapshots(snapshots: list[Snapshot], tz: ZoneInfo):
    """Agrupa las fotos por (canal, día), (canal, día, hora) y (categoría, día) locales."""
    daily: dict[tuple[int, date], _Acc] = defaultdict(_Acc)
    hourly: dict[tuple[int, date, int], _Acc] = defaultdict(_Acc)
    category: dict[tuple[int, date], _CategoryAcc] = defaultdict(_CategoryAcc)
    # Para la categoría: picos por minuto (suma de todos los canales en ese instante).
    category_instant: dict[tuple[int, date, datetime], int] = defaultdict(int)

    for s in snapshots:
        local = to_local(s.captured_at_utc, tz)
        day, hour = local.date(), local.hour
        daily[(s.channel_id, day)].add(s.viewers)
        hourly[(s.channel_id, day, hour)].add(s.viewers)
        if s.category_id is not None:
            acc = category[(s.category_id, day)]
            acc.channels.add(s.channel_id)
            acc.viewer_total += s.viewers
            minute = as_utc(s.captured_at_utc).replace(second=0, microsecond=0)
            category_instant[(s.category_id, day, minute)] += s.viewers

    for (category_id, day, _), viewers in category_instant.items():
        acc = category[(category_id, day)]
        acc.peak = max(acc.peak, viewers)

    return daily, hourly, category


# ── Chat ─────────────────────────────────────────────────────────────────

@dataclass
class _ChatAcc:
    messages: int = 0
    simulated: int = 0
    users: set = field(default_factory=set)


def aggregate_chat(events: list[ChatEvent], tz: ZoneInfo):
    daily: dict[tuple[int, date], _ChatAcc] = defaultdict(_ChatAcc)
    hourly: dict[tuple[int, date, int], int] = defaultdict(int)
    for e in events:
        local = to_local(e.sent_at_utc, tz)
        acc = daily[(e.channel_id, local.date())]
        acc.messages += 1
        acc.simulated += 1 if e.simulated else 0
        acc.users.add(e.user_id)
        hourly[(e.channel_id, local.date(), local.hour)] += 1
    return daily, hourly


# ── Sesiones de stream ───────────────────────────────────────────────────

def session_minutes(sessions: list[Session], days: list[date], tz: ZoneInfo, now_utc: datetime) -> dict[tuple[int, date], int]:
    """Minutos en vivo por (canal, día local). Una sesión que cruza la medianoche se reparte."""
    minutes: dict[tuple[int, date], float] = defaultdict(float)
    bounds = {day: local_day_bounds_utc(day, tz) for day in days}
    for s in sessions:
        start = as_utc(s.started_at_utc)
        end = as_utc(s.ended_at_utc) if s.ended_at_utc else as_utc(now_utc)
        if end <= start:
            continue
        for day, (day_start, day_end) in bounds.items():
            overlap = (min(end, day_end) - max(start, day_start)).total_seconds()
            if overlap > 0:
                minutes[(s.channel_id, day)] += overlap / 60
    return {key: round(value) for key, value in minutes.items()}


def session_peaks(sessions: list[Session], tz: ZoneInfo) -> dict[tuple[int, date], int]:
    """Pico registrado por la app al cerrar la sesión, asignado al día en que empezó."""
    peaks: dict[tuple[int, date], int] = defaultdict(int)
    for s in sessions:
        key = (s.channel_id, to_local(s.started_at_utc, tz).date())
        peaks[key] = max(peaks[key], s.peak_viewers)
    return peaks


# ── Armar las filas ──────────────────────────────────────────────────────

def build_rows(
    channels: list[ChannelInfo],
    follows: list[tuple[str, datetime]],
    snapshots: list[Snapshot],
    chat: list[ChatEvent],
    sessions: list[Session],
    days: list[date],
    tz: ZoneInfo,
    snapshot_seconds: int,
    now_utc: datetime,
) -> tuple[list[ChannelDailyRow], list[ChannelHourlyRow], list[CategoryDailyRow]]:
    """follows: (FollowedId = id del USUARIO dueño del canal, CreatedAt local)."""
    follows_by_owner: dict[str, list[datetime]] = defaultdict(list)
    for followed_user_id, created_local in follows:
        follows_by_owner[followed_user_id].append(created_local)

    snap_daily, snap_hourly, snap_category = aggregate_snapshots(snapshots, tz)
    chat_daily, chat_hourly = aggregate_chat(chat, tz)
    live_minutes = session_minutes(sessions, days, tz, now_utc)
    peaks = session_peaks(sessions, tz)
    minutes_per_snapshot = snapshot_seconds / 60

    daily_rows: list[ChannelDailyRow] = []
    for channel in channels:
        followers = followers_by_day(follows_by_owner.get(channel.user_id, []), days)
        for day in days:
            if channel.created_at_local and channel.created_at_local.date() > day:
                continue  # el canal todavía no existía
            snaps = snap_daily.get((channel.id, day), _Acc())
            chat_acc = chat_daily.get((channel.id, day), _ChatAcc())
            new, total = followers[day]
            # Minutos en vivo: las sesiones de Mongo son exactas; si no hay (Mongo caído o
            # datos antiguos), se estiman con las fotos por minuto.
            minutes = live_minutes.get((channel.id, day))
            if minutes is None:
                minutes = round(snaps.samples * minutes_per_snapshot)
            daily_rows.append(ChannelDailyRow(
                channel_id=channel.id,
                day=day,
                new_followers=new,
                total_followers=total,
                minutes_live=minutes,
                avg_viewers=snaps.average,
                peak_viewers=max(snaps.peak, peaks.get((channel.id, day), 0)),
                viewer_minutes=round(snaps.total * minutes_per_snapshot),
                chat_messages=chat_acc.messages,
                unique_chatters=len(chat_acc.users),
                simulated_messages=chat_acc.simulated,
            ))

    day_set = set(days)
    hourly_keys = set(snap_hourly) | set(chat_hourly)
    hourly_rows = [
        ChannelHourlyRow(
            channel_id=channel_id, day=day, hour=hour,
            avg_viewers=snap_hourly.get((channel_id, day, hour), _Acc()).average,
            peak_viewers=snap_hourly.get((channel_id, day, hour), _Acc()).peak,
            samples=snap_hourly.get((channel_id, day, hour), _Acc()).samples,
            chat_messages=chat_hourly.get((channel_id, day, hour), 0),
        )
        for (channel_id, day, hour) in sorted(hourly_keys)
        if day in day_set
    ]

    category_rows = [
        CategoryDailyRow(
            category_id=category_id, day=day,
            channels_live=len(acc.channels),
            viewer_minutes=round(acc.viewer_total * minutes_per_snapshot),
            peak_viewers=acc.peak,
        )
        for (category_id, day), acc in sorted(snap_category.items(), key=lambda kv: kv[0])
        if day in day_set
    ]
    return daily_rows, hourly_rows, category_rows

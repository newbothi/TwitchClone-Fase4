"""Crecimiento y tendencias: funciones puras.

La tendencia es una regresión lineal por mínimos cuadrados, escrita a mano para que se
vea la idea (es la misma recta de tu curso de ML, pero con la fórmula cerrada en vez de
descenso de gradiente, porque con una sola variable se puede despejar directo):

    x = 0, 1, 2, ... (días)          y = valor de cada día
    pendiente  m = Σ(x - x̄)(y - ȳ) / Σ(x - x̄)²
    intercepto b = ȳ - m·x̄
    R² = 1 - Σ(y - ŷ)² / Σ(y - ȳ)²    (1 = la recta explica todo; 0 = no explica nada)
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import date


@dataclass(frozen=True)
class LinearFit:
    slope: float
    intercept: float
    r2: float

    def predict(self, x: float) -> float:
        return self.intercept + self.slope * x


def linear_regression(values: list[float]) -> LinearFit:
    n = len(values)
    if n == 0:
        return LinearFit(0.0, 0.0, 0.0)
    if n == 1:
        return LinearFit(0.0, float(values[0]), 0.0)

    xs = range(n)
    x_mean = (n - 1) / 2
    y_mean = sum(values) / n
    sxx = sum((x - x_mean) ** 2 for x in xs)
    sxy = sum((x - x_mean) * (y - y_mean) for x, y in zip(xs, values))
    slope = sxy / sxx
    intercept = y_mean - slope * x_mean

    ss_tot = sum((y - y_mean) ** 2 for y in values)
    ss_res = sum((y - (intercept + slope * x)) ** 2 for x, y in zip(xs, values))
    # Si todos los valores son iguales, la recta horizontal los explica perfecto.
    r2 = 1.0 if ss_tot == 0 else max(0.0, 1 - ss_res / ss_tot)
    return LinearFit(slope, intercept, r2)


def growth_pct(current: float, previous: float) -> float | None:
    """Variación porcentual con un decimal (igual que ChannelAnalyticsService.GrowthPct en C#).
    None si antes no había nada (no se puede dividir por 0)."""
    if previous == 0:
        return None
    return round((current - previous) / previous * 100, 1)


# ── Tendencia de un canal ────────────────────────────────────────────────

@dataclass(frozen=True)
class ChannelTrend:
    channel_id: int
    window_days: int
    followers_per_day: float
    followers_r2: float
    projected_followers_7d: int
    followers_growth_pct: float | None
    avg_viewers_per_day: float
    viewers_growth_pct: float | None


def channel_trend(channel_id: int, rows: list, window_days: int) -> ChannelTrend:
    """rows: filas de ChannelDaily del canal (con .day, .total_followers, .new_followers,
    .avg_viewers, .minutes_live), idealmente 2 × window_days para comparar períodos."""
    rows = sorted(rows, key=lambda r: r.day)
    current = rows[-window_days:]
    previous = rows[-2 * window_days:-window_days] if len(rows) > window_days else []

    followers = linear_regression([float(r.total_followers) for r in current])
    last_total = current[-1].total_followers if current else 0
    projected = max(last_total, round(followers.predict(len(current) - 1 + 7))) if current else 0

    # Viewers: solo los días que hubo stream (un día offline no es "0 viewers de promedio").
    live_current = [float(r.avg_viewers) for r in current if r.minutes_live > 0]
    live_previous = [float(r.avg_viewers) for r in previous if r.minutes_live > 0]
    viewers = linear_regression(live_current)

    return ChannelTrend(
        channel_id=channel_id,
        window_days=window_days,
        followers_per_day=round(followers.slope, 3),
        followers_r2=round(followers.r2, 4),
        projected_followers_7d=projected,
        followers_growth_pct=growth_pct(sum(r.new_followers for r in current), sum(r.new_followers for r in previous)) if previous else None,
        avg_viewers_per_day=round(viewers.slope, 3),
        viewers_growth_pct=growth_pct(_mean(live_current), _mean(live_previous)) if live_previous else None,
    )


def _mean(values: list[float]) -> float:
    return sum(values) / len(values) if values else 0.0


# ── Rankings de la plataforma ────────────────────────────────────────────

def rank_categories(current: list, previous: list, names: dict[int, str]) -> list[dict]:
    """current/previous: filas de CategoryDaily (con .category_id, .viewer_minutes,
    .peak_viewers, .channels_live). Ordenadas por horas vistas en el período actual."""
    def totals(rows):
        acc: dict[int, dict] = {}
        for r in rows:
            t = acc.setdefault(r.category_id, {"viewerMinutes": 0, "peak": 0, "channels": 0})
            t["viewerMinutes"] += r.viewer_minutes
            t["peak"] = max(t["peak"], r.peak_viewers)
            t["channels"] = max(t["channels"], r.channels_live)
        return acc

    now, before = totals(current), totals(previous)
    ranking = []
    for category_id, t in now.items():
        prev_minutes = before.get(category_id, {}).get("viewerMinutes", 0)
        ranking.append({
            "categoryId": category_id,
            "name": names.get(category_id, f"Categoría {category_id}"),
            "hoursWatched": round(t["viewerMinutes"] / 60, 1),
            "previousHoursWatched": round(prev_minutes / 60, 1),
            "growthPct": growth_pct(t["viewerMinutes"], prev_minutes),
            "peakViewers": t["peak"],
            "maxChannelsLive": t["channels"],
        })
    return sorted(ranking, key=lambda r: (-r["hoursWatched"], r["name"]))


def rank_channels(trends: list[ChannelTrend], names: dict[int, str], limit: int = 10) -> list[dict]:
    """Canales "en alza": los que más seguidores ganan por día (pendiente de la recta)."""
    ranked = sorted(trends, key=lambda t: (-t.followers_per_day, -(t.viewers_growth_pct or 0), t.channel_id))
    return [
        {
            "channelId": t.channel_id,
            "name": names.get(t.channel_id, f"Canal {t.channel_id}"),
            "followersPerDay": t.followers_per_day,
            "followersR2": t.followers_r2,
            "projectedFollowers7d": t.projected_followers_7d,
            "followersGrowthPct": t.followers_growth_pct,
            "viewersGrowthPct": t.viewers_growth_pct,
        }
        for t in ranked[:limit]
    ]


def best_hours(hourly_rows: list, top: int = 3) -> list[dict]:
    """Horas locales con más viewers promedio (con al menos una muestra)."""
    acc: dict[int, list[float]] = {}
    for r in hourly_rows:
        if r.samples > 0:
            acc.setdefault(r.hour, []).append(float(r.avg_viewers))
    ranked = sorted(((h, _mean(v)) for h, v in acc.items()), key=lambda x: (-x[1], x[0]))
    return [{"hour": h, "avgViewers": round(v, 2)} for h, v in ranked[:top]]


def fill_days(rows: list, days: list[date], factory) -> list:
    """Completa con filas vacías los días que no tienen datos (para los gráficos)."""
    by_day = {r.day: r for r in rows}
    return [by_day.get(d) or factory(d) for d in days]

from datetime import date, timedelta

import pytest

from analytics_service.transform import CategoryDailyRow, ChannelDailyRow, ChannelHourlyRow
from analytics_service.trends import (
    best_hours,
    channel_trend,
    growth_pct,
    linear_regression,
    rank_categories,
    rank_channels,
)


# ── Regresión lineal ─────────────────────────────────────────────────────

def test_una_recta_perfecta():
    fit = linear_regression([10, 12, 14, 16, 18])   # y = 10 + 2x

    assert fit.slope == pytest.approx(2)
    assert fit.intercept == pytest.approx(10)
    assert fit.r2 == pytest.approx(1)
    assert fit.predict(10) == pytest.approx(30)


def test_valores_constantes():
    fit = linear_regression([7, 7, 7])
    assert (fit.slope, fit.intercept, fit.r2) == (0, 7, 1)


def test_con_ruido_el_r2_baja():
    fit = linear_regression([1, 5, 2, 6, 3, 7])
    assert fit.slope > 0
    assert 0 < fit.r2 < 1


def test_casos_borde():
    assert linear_regression([]).slope == 0
    assert linear_regression([4]).intercept == 4


def test_coincide_con_numpy():
    numpy = pytest.importorskip("numpy")
    values = [3, 8, 4, 9, 12, 11, 15]
    slope, intercept = numpy.polyfit(range(len(values)), values, 1)
    fit = linear_regression(values)
    assert fit.slope == pytest.approx(slope)
    assert fit.intercept == pytest.approx(intercept)


# ── Crecimiento ──────────────────────────────────────────────────────────

@pytest.mark.parametrize(("current", "previous", "expected"), [(150, 100, 50.0), (50, 100, -50.0), (1, 3, -66.7), (10, 0, None), (0, 0, None)])
def test_growth_pct(current, previous, expected):
    assert growth_pct(current, previous) == expected


def _day(i, total, new=0, avg=0.0, minutes=0):
    return ChannelDailyRow(1, date(2026, 10, 1) + timedelta(days=i), new, total, minutes, avg, 0, 0, 0, 0, 0)


def test_tendencia_de_un_canal_que_crece():
    # 14 días previos con 1 seguidor nuevo cada 2 días, y 7 días actuales con 3 por día.
    rows = [_day(i, 100 + i // 2, new=i % 2, avg=10, minutes=60) for i in range(7)]
    rows += [_day(7 + i, 103 + 3 * (i + 1), new=3, avg=20 + i, minutes=60) for i in range(7)]

    trend = channel_trend(1, rows, window_days=7)

    assert trend.followers_per_day == pytest.approx(3)
    assert trend.followers_r2 == pytest.approx(1)
    assert trend.projected_followers_7d == 124 + 21
    assert trend.followers_growth_pct == pytest.approx((21 - 3) / 3 * 100)
    assert trend.avg_viewers_per_day == pytest.approx(1)
    assert trend.viewers_growth_pct == pytest.approx((23 - 10) / 10 * 100)


def test_los_dias_sin_stream_no_cuentan_como_cero_viewers():
    rows = [_day(i, 10, avg=(30 if i % 2 == 0 else 0), minutes=(60 if i % 2 == 0 else 0)) for i in range(6)]

    trend = channel_trend(1, rows, window_days=6)

    assert trend.avg_viewers_per_day == pytest.approx(0)
    assert trend.viewers_growth_pct is None, "Sin período anterior no hay comparación."


def test_la_proyeccion_nunca_baja_del_total_actual():
    rows = [_day(i, 50) for i in range(5)]
    assert channel_trend(1, rows, 5).projected_followers_7d == 50


# ── Rankings ─────────────────────────────────────────────────────────────

def test_ranking_de_categorias_por_horas_vistas_y_crecimiento():
    d0, d1 = date(2026, 10, 1), date(2026, 10, 8)
    previous = [CategoryDailyRow(1, d0, 2, 600, 20), CategoryDailyRow(2, d0, 1, 1200, 30)]
    current = [CategoryDailyRow(1, d1, 3, 1800, 50), CategoryDailyRow(2, d1, 1, 600, 15), CategoryDailyRow(3, d1, 1, 60, 2)]

    ranking = rank_categories(current, previous, {1: "Videojuegos", 2: "Música"})

    assert [r["name"] for r in ranking] == ["Videojuegos", "Música", "Categoría 3"]
    assert ranking[0]["hoursWatched"] == 30.0
    assert ranking[0]["growthPct"] == 200.0
    assert ranking[1]["growthPct"] == -50.0
    assert ranking[2]["growthPct"] is None


def test_ranking_de_canales_en_alza():
    up = channel_trend(1, [_day(i, 10 + 5 * i) for i in range(5)], 5)
    flat = channel_trend(2, [_day(i, 40) for i in range(5)], 5)

    ranking = rank_channels([flat, up], {1: "Sube", 2: "Plano"}, limit=1)

    assert [r["name"] for r in ranking] == ["Sube"]
    assert ranking[0]["followersPerDay"] == pytest.approx(5)


def test_mejores_horas():
    rows = [
        ChannelHourlyRow(1, date(2026, 10, 15), 21, 40.0, 50, 60, 10),
        ChannelHourlyRow(1, date(2026, 10, 16), 21, 20.0, 30, 60, 5),
        ChannelHourlyRow(1, date(2026, 10, 16), 15, 25.0, 30, 60, 5),
        ChannelHourlyRow(1, date(2026, 10, 16), 3, 0.0, 0, 0, 9),   # solo chat, sin fotos: no cuenta
    ]
    assert best_hours(rows, top=2) == [{"hour": 21, "avgViewers": 30.0}, {"hour": 15, "avgViewers": 25.0}]

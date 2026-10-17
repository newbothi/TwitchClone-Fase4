from datetime import date, datetime, timedelta
from zoneinfo import ZoneInfo

import pytest

from analytics_service.transform import (
    UTC,
    ChannelInfo,
    ChatEvent,
    Session,
    Snapshot,
    aggregate_chat,
    aggregate_snapshots,
    build_rows,
    day_range,
    followers_by_day,
    local_day_bounds_utc,
    session_minutes,
)

CL = ZoneInfo("America/Santiago")   # en octubre = UTC-3
OCT16 = date(2026, 10, 16)


def utc(*args) -> datetime:
    return datetime(*args, tzinfo=UTC)


# ── Días locales ─────────────────────────────────────────────────────────

def test_el_dia_local_de_chile_en_utc():
    assert local_day_bounds_utc(OCT16, CL) == (utc(2026, 10, 16, 3), utc(2026, 10, 17, 3))


@pytest.mark.parametrize(("day", "hours"), [(date(2026, 9, 6), 23), (date(2026, 4, 4), 25), (OCT16, 24)])
def test_respeta_los_cambios_de_horario(day, hours):
    start, end = local_day_bounds_utc(day, CL)
    assert end - start == timedelta(hours=hours)


def test_day_range_incluye_ambos_extremos():
    assert day_range(date(2026, 10, 14), OCT16) == [date(2026, 10, 14), date(2026, 10, 15), OCT16]


# ── Seguidores ───────────────────────────────────────────────────────────

def test_seguidores_nuevos_y_total_por_dia():
    follows = [datetime(2026, 9, 1, 12), datetime(2026, 10, 15, 9), datetime(2026, 10, 15, 22), datetime(2026, 10, 16, 1)]

    result = followers_by_day(follows, [date(2026, 10, 15), OCT16])

    assert result[date(2026, 10, 15)] == (2, 3)   # 2 nuevos; total 3 contando el de septiembre
    assert result[OCT16] == (1, 4)


def test_sin_seguidores():
    assert followers_by_day([], [OCT16]) == {OCT16: (0, 0)}


# ── Viewers ──────────────────────────────────────────────────────────────

def test_fotos_por_dia_hora_y_categoria_en_hora_local():
    snapshots = [
        # 23:30 UTC del 16 = 20:30 en Chile del 16
        Snapshot(1, utc(2026, 10, 16, 23, 30), 10, category_id=5),
        Snapshot(1, utc(2026, 10, 16, 23, 31), 30, category_id=5),
        Snapshot(2, utc(2026, 10, 16, 23, 31), 7, category_id=5),   # mismo minuto que el 30 del canal 1
        # 02:00 UTC del 17 = 23:00 en Chile del 16 (¡sigue siendo el 16!)
        Snapshot(1, utc(2026, 10, 17, 2, 0), 20, category_id=5),
    ]

    daily, hourly, category = aggregate_snapshots(snapshots, CL)

    assert set(daily) == {(1, OCT16), (2, OCT16)}
    assert daily[(1, OCT16)].samples == 3
    assert daily[(1, OCT16)].average == 20.0
    assert daily[(1, OCT16)].peak == 30
    assert hourly[(1, OCT16, 20)].average == 20.0
    assert hourly[(1, OCT16, 23)].peak == 20
    assert category[(5, OCT16)].channels == {1, 2}
    assert category[(5, OCT16)].peak == 37, "Pico de la categoría = suma de los canales en el mismo minuto (30 + 7)."


# ── Chat ─────────────────────────────────────────────────────────────────

def test_mensajes_usuarios_unicos_y_bots():
    events = [
        ChatEvent(1, "u1", utc(2026, 10, 16, 23, 0)),
        ChatEvent(1, "u1", utc(2026, 10, 16, 23, 5)),
        ChatEvent(1, "u2", utc(2026, 10, 16, 23, 6)),
        ChatEvent(1, "bot-1", utc(2026, 10, 16, 23, 7), simulated=True),
    ]

    daily, hourly = aggregate_chat(events, CL)

    acc = daily[(1, OCT16)]
    assert (acc.messages, len(acc.users), acc.simulated) == (4, 3, 1)
    assert hourly[(1, OCT16, 20)] == 4


# ── Sesiones ─────────────────────────────────────────────────────────────

def test_una_sesion_que_cruza_la_medianoche_se_reparte_entre_los_dos_dias():
    # 23:00 a 01:30 hora de Chile = 02:00 a 04:30 UTC
    session = Session(1, utc(2026, 10, 17, 2, 0), utc(2026, 10, 17, 4, 30))

    minutes = session_minutes([session], [OCT16, date(2026, 10, 17)], CL, now_utc=utc(2026, 10, 18))

    assert minutes == {(1, OCT16): 60, (1, date(2026, 10, 17)): 90}


def test_una_sesion_abierta_cuenta_hasta_ahora():
    session = Session(1, utc(2026, 10, 16, 20, 0), None)

    minutes = session_minutes([session], [OCT16], CL, now_utc=utc(2026, 10, 16, 21, 15))

    assert minutes == {(1, OCT16): 75}


# ── Filas completas ──────────────────────────────────────────────────────

def test_build_rows_arma_el_resumen_de_cada_canal():
    channels = [
        ChannelInfo(1, "owner1", "Canal Uno", category_id=5, created_at_local=datetime(2026, 1, 1)),
        ChannelInfo(2, "owner2", "Canal Dos", created_at_local=datetime(2026, 10, 16, 12)),  # creado hoy
    ]
    follows = [("owner1", datetime(2026, 10, 15, 10)), ("owner1", datetime(2026, 10, 16, 10)), ("otro", datetime(2026, 10, 16))]
    snapshots = [Snapshot(1, utc(2026, 10, 16, 23, m), 10 + m, 5) for m in range(4)]  # 4 minutos: 10, 11, 12, 13
    chat = [ChatEvent(1, "u1", utc(2026, 10, 16, 23, 1)), ChatEvent(1, "u2", utc(2026, 10, 16, 23, 2))]
    sessions = [Session(1, utc(2026, 10, 16, 22, 0), utc(2026, 10, 17, 0, 0), peak_viewers=50)]
    days = [date(2026, 10, 15), OCT16]

    daily, hourly, category = build_rows(channels, follows, snapshots, chat, sessions, days, CL, 60, utc(2026, 10, 17, 1))

    by_key = {(r.channel_id, r.day): r for r in daily}
    assert (2, date(2026, 10, 15)) not in by_key, "El canal 2 no existía el 15."
    today = by_key[(1, OCT16)]
    assert (today.new_followers, today.total_followers) == (1, 2)
    assert today.minutes_live == 120, "Sale de la sesión de Mongo (2 horas), no de las 4 fotos."
    assert today.avg_viewers == 11.5
    assert today.peak_viewers == 50, "El pico guardado en la sesión gana si es mayor."
    assert today.viewer_minutes == 46
    assert (today.chat_messages, today.unique_chatters) == (2, 2)
    assert by_key[(1, date(2026, 10, 15))].new_followers == 1
    assert by_key[(2, OCT16)].total_followers == 0

    assert [(h.hour, h.samples, h.chat_messages) for h in hourly] == [(20, 4, 2)]
    assert [(c.category_id, c.channels_live, c.viewer_minutes, c.peak_viewers) for c in category] == [(5, 1, 46, 13)]


def test_sin_sesiones_los_minutos_se_estiman_con_las_fotos():
    channels = [ChannelInfo(1, "owner1", "Canal Uno")]
    snapshots = [Snapshot(1, utc(2026, 10, 16, 23, m), 5) for m in range(30)]

    daily, _, _ = build_rows(channels, [], snapshots, [], [], [OCT16], CL, 60, utc(2026, 10, 17))

    assert daily[0].minutes_live == 30

from datetime import date, datetime, timedelta

import pytest

from analytics_service.config import Settings
from analytics_service.etl import EtlRunner
from analytics_service.transform import UTC, ChannelInfo, ChatEvent, Session, Snapshot

from .fakes import Clock, FakeMongo, FakeRepository

NOW = datetime(2026, 10, 16, 23, 0, tzinfo=UTC)   # 20:00 en Chile
TODAY = date(2026, 10, 16)


@pytest.fixture
def repo():
    repo = FakeRepository()
    repo.channels_data = [
        ChannelInfo(1, "owner1", "Canal Uno", category_id=5, is_live=True, viewer_count=12),
        ChannelInfo(2, "owner2", "Canal Dos", category_id=5, is_live=False, viewer_count=0),
    ]
    repo.follows_data = [("owner1", datetime(2026, 10, 16, 18)), ("owner1", datetime(2026, 10, 10, 9))]
    repo.categories_data = {5: "Videojuegos"}
    return repo


@pytest.fixture
def mongo():
    m = FakeMongo()
    m.chat = [ChatEvent(1, "u1", NOW - timedelta(minutes=5)), ChatEvent(1, "u2", NOW - timedelta(minutes=4))]
    m.sessions = [Session(1, NOW - timedelta(hours=1), None)]
    return m


@pytest.fixture
def clock():
    return Clock(NOW)


@pytest.fixture
def runner(repo, mongo, clock):
    return EtlRunner(repo, mongo, Settings(backfill_days=30, trend_days=7), clock)


def test_la_foto_solo_incluye_canales_en_vivo(runner, repo):
    assert runner.snapshot() == 1
    assert repo.snapshots == [Snapshot(1, NOW, 12, 5)]
    assert repo.runs[-1]["job"] == "snapshot" and repo.runs[-1]["ok"] is True


def test_la_primera_vez_rellena_30_dias_y_despues_solo_hoy_y_ayer(runner, repo, clock):
    runner.aggregate()
    assert repo.replaced_windows[-1] == (TODAY - timedelta(days=29), TODAY)

    clock.now += timedelta(minutes=5)
    runner.aggregate()
    assert repo.replaced_windows[-1] == (TODAY - timedelta(days=1), TODAY)


def test_el_resumen_de_hoy_junta_sql_y_mongo(runner, repo, clock):
    for minute in range(3):  # 3 fotos de 12 viewers
        clock.now = NOW - timedelta(minutes=minute)
        runner.snapshot()
    clock.now = NOW

    runner.aggregate()

    today = next(r for r in repo.daily if r.channel_id == 1 and r.day == TODAY)
    assert (today.new_followers, today.total_followers) == (1, 2)
    assert today.minutes_live == 60, "La sesión abierta lleva 1 hora."
    assert today.avg_viewers == 12.0
    assert (today.chat_messages, today.unique_chatters) == (2, 2)
    assert [c.category_id for c in repo.category if c.day == TODAY] == [5]
    assert {t.channel_id for t in repo.trend_rows} == {1, 2}


def test_si_mongo_esta_caido_el_etl_sigue_y_lo_anota(runner, repo, mongo):
    mongo.down = True

    rows = runner.aggregate()

    assert rows > 0, "Los resúmenes se escriben igual (sin datos del chat)."
    run = repo.runs[-1]
    assert run["ok"] is False
    assert "MongoDB no disponible" in run["error"]
    assert all(r.chat_messages == 0 for r in repo.daily)


def test_si_sql_falla_queda_anotado_y_se_propaga(runner, repo):
    def broken():
        raise RuntimeError("SQL Server apagado (simulado)")
    repo.channels = broken

    with pytest.raises(RuntimeError):
        runner.snapshot()
    assert repo.runs[-1]["ok"] is False
    assert "SQL Server apagado" in repo.runs[-1]["error"]


def test_la_limpieza_borra_las_fotos_viejas(runner, repo):
    repo.snapshots = [Snapshot(1, NOW - timedelta(days=100), 5), Snapshot(1, NOW - timedelta(days=10), 5)]

    assert runner.cleanup() == 1
    assert len(repo.snapshots) == 1


def test_sin_mongo_configurado_funciona_igual(repo, clock):
    runner = EtlRunner(repo, None, Settings(backfill_days=2), clock)
    assert runner.aggregate() > 0
    assert repo.runs[-1]["ok"] is True


# ── SQL Server caído en el bucle ───────────────────────────────────────

def test_sql_caido_se_avisa_una_vez_y_tambien_cuando_vuelve(repo, mongo, clock, caplog):
    from analytics_service.repository import SqlUnavailable

    runner = EtlRunner(repo, mongo, Settings(), clock)
    down = {"yes": True}

    def job():
        if down["yes"]:
            raise SqlUnavailable("No se pudo conectar a SQL Server: timeout")
        return 1

    with caplog.at_level("INFO", logger="analytics.etl"):
        for _ in range(3):
            runner.run_safely(job, 60)          # tres minutos sin SQL Server
        down["yes"] = False
        runner.run_safely(job, 60)
        runner.run_safely(job, 60)

    errors = [r for r in caplog.records if r.levelname == "ERROR"]
    assert len(errors) == 1, "Un solo aviso, no uno por minuto."
    assert "SQL Server" in errors[0].getMessage()
    assert sum("volvió a responder" in r.getMessage() for r in caplog.records) == 1


def test_otro_error_no_detiene_el_bucle(repo, mongo, clock):
    runner = EtlRunner(repo, mongo, Settings(), clock)

    def job():
        raise ValueError("dato raro")

    runner.run_safely(job, 60)  # no lanza


def test_si_sql_no_estaba_al_arrancar_el_esquema_se_crea_cuando_vuelve(repo, mongo, clock):
    from analytics_service.repository import SqlUnavailable

    repo.schema_failures = [SqlUnavailable("No se pudo conectar a SQL Server: apagado")]
    runner = EtlRunner(repo, mongo, Settings(), clock)

    runner.run_safely(runner.snapshot, 60)
    assert not runner.schema_ready
    assert "apagado" in runner.schema_error
    assert repo.snapshots == [], "Sin esquema no se intenta el trabajo."

    runner.run_safely(runner.snapshot, 60)
    assert runner.schema_ready and runner.schema_error is None
    assert len(repo.snapshots) == 1

    runner.run_safely(runner.snapshot, 60)
    assert repo.schema_calls == 2, "Una vez creado, no se vuelve a correr el script."

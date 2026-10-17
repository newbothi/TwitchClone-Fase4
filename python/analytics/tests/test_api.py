from datetime import date, datetime, timedelta

import pytest
from fastapi.testclient import TestClient

from analytics_service.api import create_app
from analytics_service.config import Settings
from analytics_service.repository import SqlUnavailable
from analytics_service.transform import UTC, ChannelInfo, ChatEvent, Session, Snapshot

from .fakes import Clock, FakeMongo, FakeRepository

NOW = datetime(2026, 10, 16, 23, 30, tzinfo=UTC)   # 20:30 en Chile


@pytest.fixture
def client():
    repo = FakeRepository()
    repo.channels_data = [
        ChannelInfo(1, "owner1", "Canal Uno", category_id=5, is_live=True, viewer_count=20),
        ChannelInfo(2, "owner2", "Canal Dos", category_id=6, is_live=True, viewer_count=4),
    ]
    repo.categories_data = {5: "Videojuegos", 6: "Música"}
    # Canal 1: 1 seguidor por día durante los últimos 10 días.
    repo.follows_data = [("owner1", datetime(2026, 10, 7 + i, 12)) for i in range(10)]
    # Una hora de fotos por minuto para cada canal, terminando ahora.
    for m in range(60):
        at = NOW - timedelta(minutes=m)
        repo.snapshots.append(Snapshot(1, at, 20 + (m % 3), 5))
        repo.snapshots.append(Snapshot(2, at, 4, 6))
    mongo = FakeMongo()
    mongo.chat = [ChatEvent(1, f"u{i % 4}", NOW - timedelta(minutes=i)) for i in range(30)]
    mongo.sessions = [Session(1, NOW - timedelta(hours=1), None, 22), Session(2, NOW - timedelta(hours=1), None, 4)]

    app = create_app(Settings(run_etl=False, backfill_days=30), repo=repo, mongo=mongo, clock=Clock(NOW))
    with TestClient(app) as c:
        assert c.post("/etl/run").status_code == 200
        yield c


def test_health(client):
    body = client.get("/health").json()
    assert body["status"] == "ok"
    assert body["timezone"] == "America/Santiago"
    assert {r["job"] for r in body["lastRuns"]} == {"snapshot", "aggregate"}


def test_resumen_del_canal(client):
    body = client.get("/channels/1/summary?days=7").json()

    assert body["name"] == "Canal Uno"
    assert (body["from"], body["to"]) == ("2026-10-10", "2026-10-16")
    assert body["followers"]["total"] == 10
    assert body["followers"]["new"] == 7
    assert body["followers"]["previousNew"] == 3
    assert body["followers"]["perDay"] == pytest.approx(1.0)
    assert body["followers"]["projected7d"] == 17
    assert body["viewers"]["peak"] == 22
    assert 20 <= body["viewers"]["average"] <= 22
    assert body["streaming"] == {"hoursLive": 1.0, "daysLive": 1}
    assert body["chat"]["messages"] == 30
    assert body["chat"]["messagesPerHourLive"] == 30.0
    assert body["bestHours"][0]["hour"] in (19, 20)


def test_serie_diaria_completa_los_dias_sin_datos(client):
    rows = client.get("/channels/1/daily?days=14").json()

    assert len(rows) == 14
    assert rows[0]["day"] == "2026-10-03"
    assert rows[-1]["day"] == "2026-10-16"
    assert [r["totalFollowers"] for r in rows[-3:]] == [8, 9, 10]


def test_historia_de_viewers_agrupada(client):
    points = client.get("/channels/1/viewers?hours=2&bucket=10").json()

    assert 6 <= len(points) <= 7
    assert all(set(p) == {"time", "avgViewers", "peakViewers"} for p in points)
    assert max(p["peakViewers"] for p in points) == 22


def test_horas_del_dia(client):
    hours = client.get("/channels/1/hours?days=7").json()

    assert [h["hour"] for h in hours] == list(range(24))
    assert sum(h["chatMessages"] for h in hours) == 30
    assert {h["hour"] for h in hours if h["daysWithData"]} <= {19, 20}


def test_tendencias_de_categorias(client):
    ranking = client.get("/trends/categories?days=7").json()

    assert [r["name"] for r in ranking] == ["Videojuegos", "Música"]
    assert ranking[0]["hoursWatched"] == pytest.approx(21.0, abs=0.5)


def test_canales_en_alza(client):
    ranking = client.get("/trends/channels?days=7&limit=5").json()

    assert ranking[0]["name"] == "Canal Uno"
    assert ranking[0]["followersPerDay"] == pytest.approx(1.0)


def test_canal_inexistente_es_404(client):
    assert client.get("/channels/999/summary").status_code == 404
    assert client.get("/channels/999/daily").status_code == 404


@pytest.mark.parametrize("url", ["/channels/1/summary?days=0", "/channels/1/viewers?hours=0", "/trends/channels?days=1", "/etl/run?days=999"])
def test_parametros_fuera_de_rango_son_422(client, url):
    method = client.post if url.startswith("/etl") else client.get
    assert method(url).status_code == 422


# ── SQL Server caído ───────────────────────────────────────────────────

class _SqlCaido:
    """Repositorio que falla como SqlRepository cuando SQL Server no responde."""
    def __getattr__(self, name):
        def fail(*args, **kwargs):
            raise SqlUnavailable("No se pudo conectar a SQL Server: el servidor no responde")
        return fail


def test_sql_server_caido_responde_503_con_el_motivo():
    app = create_app(Settings(run_etl=False), repo=_SqlCaido(), mongo=FakeMongo(), clock=Clock(NOW))
    with TestClient(app) as c:
        response = c.get("/channels/1/summary")
        assert response.status_code == 503
        assert "SQL Server" in response.json()["detail"]
        assert c.get("/health").json()["status"] == "degraded"


def test_sin_driver_o_sin_servidor_es_sql_unavailable():
    from analytics_service.repository import SqlRepository
    with pytest.raises(SqlUnavailable):
        SqlRepository("DRIVER={Driver que no existe};SERVER=nadie").has_daily_data()


def test_con_sql_server_inalcanzable_la_api_arranca_y_health_dice_por_que():
    settings = Settings(sql_connection="DRIVER={Driver que no existe};SERVER=nadie", run_etl=False,
                        mongo_uri="mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=200")
    with TestClient(create_app(settings)) as c:
        health = c.get("/health").json()
        assert health["status"] == "degraded"
        assert "SQL Server" in health["error"]
        assert c.get("/channels/1/summary").status_code == 503

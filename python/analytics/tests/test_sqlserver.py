"""Pruebas contra un SQL Server DE VERDAD (las demás usan dobles en memoria).

Crean una base de datos temporal, corren sql/fase4_analytics.sql, ejecutan el ETL y la API
contra ella, y la borran al final. NO tocan la base de la app.

Para correrlas (PowerShell, desde python/analytics):
    $env:ANALYTICS_TEST_SQL = "DRIVER={ODBC Driver 17 for SQL Server};SERVER=localhost;Trusted_Connection=yes;TrustServerCertificate=yes"
    python -m pytest -m sqlserver

Sin esa variable se omiten (así pasa en las pruebas normales).
"""

from __future__ import annotations

import os
import re
import uuid
from datetime import date, datetime, timedelta

import pytest

from analytics_service.config import DEFAULT_SCHEMA_SQL, Settings
from analytics_service.transform import UTC, ChannelDailyRow, ChatEvent, Session

from .fakes import Clock, FakeMongo

pytestmark = pytest.mark.sqlserver
ADMIN = os.environ.get("ANALYTICS_TEST_SQL", "").strip()
if not ADMIN:
    pytest.skip("Define ANALYTICS_TEST_SQL para probar contra SQL Server.", allow_module_level=True)

pyodbc = pytest.importorskip("pyodbc")

NOW = datetime(2026, 10, 16, 23, 0, tzinfo=UTC)
TODAY = date(2026, 10, 16)

# Lo mínimo de dbo.* que lee el servicio (mismos nombres y tipos que crea EF Core).
MINIMAL_DBO = """
CREATE TABLE dbo.Categories (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(100) NOT NULL);
CREATE TABLE dbo.Channels (
    Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY, UserId NVARCHAR(450) NOT NULL, Name NVARCHAR(100) NOT NULL,
    CategoryId INT NULL, CreatedAt DATETIME2 NOT NULL, IsLive BIT NOT NULL, ViewerCount INT NOT NULL);
CREATE TABLE dbo.Follows (
    Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY, FollowerId NVARCHAR(450) NOT NULL,
    FollowedId NVARCHAR(450) NOT NULL, CreatedAt DATETIME2 NOT NULL);
"""


def _with_database(connection: str, name: str) -> str:
    if re.search(r"(?i)(^|;)\s*(DATABASE|Initial Catalog)\s*=", connection):
        return re.sub(r"(?i)((?:^|;)\s*)(DATABASE|Initial Catalog)\s*=[^;]*", rf"\1DATABASE={name}", connection)
    return connection.rstrip(";") + f";DATABASE={name}"


@pytest.fixture(scope="module")
def connection_string():
    name = f"TwitchCloneAnalyticsPruebas_{uuid.uuid4().hex[:8]}"
    admin = pyodbc.connect(_with_database(ADMIN, "master"), autocommit=True)
    admin.execute(f"CREATE DATABASE [{name}]")
    try:
        target = _with_database(ADMIN, name)
        with pyodbc.connect(target, autocommit=True) as c:
            c.execute(MINIMAL_DBO)
            c.execute("INSERT INTO dbo.Categories (Id, Name) VALUES (5, N'Videojuegos'), (6, N'Música')")
            c.execute("""INSERT INTO dbo.Channels (UserId, Name, CategoryId, CreatedAt, IsLive, ViewerCount) VALUES
                         (N'owner1', N'Canal Uno', 5, '2026-01-01', 1, 15),
                         (N'owner2', N'Canal Dos', 6, '2026-01-01', 0, 0)""")
            for i in range(5):
                c.execute("INSERT INTO dbo.Follows (FollowerId, FollowedId, CreatedAt) VALUES (?, N'owner1', ?)",
                          f"fan{i}", datetime(2026, 10, 12 + i, 15, 30, 12, 123456))
        yield target
    finally:
        admin.execute(f"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE")
        admin.execute(f"DROP DATABASE [{name}]")
        admin.close()


@pytest.fixture(scope="module")
def repo(connection_string):
    from analytics_service.repository import SqlRepository
    repository = SqlRepository(connection_string)
    repository.ensure_schema(DEFAULT_SCHEMA_SQL)
    return repository


def test_el_script_del_esquema_se_puede_correr_varias_veces(repo, connection_string):
    repo.ensure_schema(DEFAULT_SCHEMA_SQL)  # segunda vez: no debe fallar

    with pyodbc.connect(connection_string) as c:
        tables = {r[0] for r in c.execute("SELECT name FROM sys.tables WHERE schema_id = SCHEMA_ID('analytics')")}
    assert tables == {"ViewerSnapshots", "ChannelDaily", "ChannelHourly", "CategoryDaily", "ChannelTrends", "EtlRuns"}


def test_lee_dbo_con_sus_tipos(repo):
    channels = {c.id: c for c in repo.channels()}
    assert channels[1].name == "Canal Uno" and channels[1].is_live and channels[1].viewer_count == 15
    assert repo.categories() == {5: "Videojuegos", 6: "Música"}
    follows = repo.follows()
    assert len(follows) == 5 and all(isinstance(d, datetime) for _, d in follows)


def test_etl_completo_y_api_contra_sql_server(repo):
    from fastapi.testclient import TestClient
    from analytics_service.api import create_app

    mongo = FakeMongo()
    mongo.chat = [ChatEvent(1, f"u{i % 3}", NOW - timedelta(minutes=i)) for i in range(12)]
    mongo.sessions = [Session(1, NOW - timedelta(hours=2), None, 30)]
    clock = Clock(NOW)
    app = create_app(Settings(run_etl=False, backfill_days=10, trend_days=5), repo=repo, mongo=mongo, clock=clock)

    with TestClient(app) as client:
        for m in range(3):
            clock.now = NOW - timedelta(minutes=m)
            client.post("/etl/run")
        clock.now = NOW

        summary = client.get("/channels/1/summary?days=7").json()
        assert summary["followers"]["total"] == 5
        assert summary["streaming"]["hoursLive"] == 2.0
        assert summary["chat"]["messages"] == 12
        assert summary["viewers"]["peak"] == 30

        daily = client.get("/channels/1/daily?days=7").json()
        assert daily[-1]["day"] == TODAY.isoformat()
        assert client.get("/trends/categories?days=7").json()[0]["name"] == "Videojuegos"
        assert {r["job"] for r in client.get("/health").json()["lastRuns"]} >= {"snapshot", "aggregate"}

    snapshots = repo.snapshots_between(NOW - timedelta(hours=1), NOW + timedelta(seconds=1), 1)
    assert [s.viewers for s in snapshots] == [15, 15, 15]
    assert snapshots[-1].captured_at_utc == NOW, "Se guarda y se lee en UTC."
    assert len(repo.trends()) == 2


def test_reemplazar_resumenes_es_todo_o_nada(repo):
    before = repo.channel_daily(TODAY, TODAY)
    assert before, "La prueba anterior dejó datos de hoy."
    duplicated = [ChannelDailyRow(1, TODAY, 0, 0, 0, 0.0, 0, 0, 0, 0, 0)] * 2   # viola la clave primaria

    with pytest.raises(pyodbc.Error):
        repo.replace_aggregates(TODAY, TODAY, duplicated, [], [], NOW)

    assert repo.channel_daily(TODAY, TODAY) == before, "El DELETE se deshizo: no quedó a medias."

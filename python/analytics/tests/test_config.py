import json

from analytics_service.config import Settings, odbc_from_ado, pick_driver, read_appsettings


def test_convierte_la_cadena_de_csharp_a_odbc():
    ado = "Server=MI-PC;Database=TwitchClonePorSeparadoConMongoMasSusurros;Trusted_Connection=True;TrustServerCertificate=True;"

    odbc = odbc_from_ado(ado, "ODBC Driver 18 for SQL Server")

    assert odbc == ("DRIVER={ODBC Driver 18 for SQL Server};SERVER=MI-PC;"
                    "DATABASE=TwitchClonePorSeparadoConMongoMasSusurros;Trusted_Connection=yes;TrustServerCertificate=yes")


def test_otras_formas_de_escribir_la_cadena():
    ado = "Data Source=localhost\\SQLEXPRESS;Initial Catalog=X;Integrated Security=SSPI;MultipleActiveResultSets=true"
    assert odbc_from_ado(ado, "SQL Server") == "DRIVER={SQL Server};SERVER=localhost\\SQLEXPRESS;DATABASE=X;Trusted_Connection=yes"


def test_usuario_y_contrasena():
    odbc = odbc_from_ado("Server=db;Database=X;User Id=sa;Password=Clave123!;Encrypt=False", "ODBC Driver 17 for SQL Server")
    assert odbc == "DRIVER={ODBC Driver 17 for SQL Server};SERVER=db;DATABASE=X;UID=sa;PWD=Clave123!;Encrypt=no"


def test_elige_el_driver_mas_nuevo_instalado():
    assert pick_driver(["SQL Server", "ODBC Driver 17 for SQL Server"]) == "ODBC Driver 17 for SQL Server"
    assert pick_driver(["SQL Server", "ODBC Driver 18 for SQL Server", "ODBC Driver 17 for SQL Server"]) == "ODBC Driver 18 for SQL Server"
    assert pick_driver(["SQL Server"]) == "SQL Server"
    assert pick_driver(["PostgreSQL Unicode"]) is None


def test_lee_el_appsettings_de_la_app_web_aunque_tenga_bom(tmp_path):
    path = tmp_path / "appsettings.json"
    path.write_text("﻿" + json.dumps({
        "ConnectionStrings": {"DefaultConnection": "Server=PC;Database=Db;Trusted_Connection=True;"},
        "MongoSettings": {"ConnectionString": "mongodb://otro:27017", "DatabaseName": "MiBase"},
    }), encoding="utf-8")

    assert read_appsettings(path) == {
        "sql": "Server=PC;Database=Db;Trusted_Connection=True;",
        "mongo_uri": "mongodb://otro:27017",
        "mongo_database": "MiBase",
    }


def test_appsettings_inexistente_no_rompe(tmp_path):
    assert read_appsettings(tmp_path / "no-existe.json") == {}


def test_from_env_usa_el_appsettings_y_las_variables_pisan(tmp_path, monkeypatch):
    path = tmp_path / "appsettings.json"
    path.write_text(json.dumps({
        "ConnectionStrings": {"DefaultConnection": "Server=PC;Database=Db;Trusted_Connection=True;"},
        "MongoSettings": {"ConnectionString": "mongodb://otro:27017", "DatabaseName": "MiBase"},
    }), encoding="utf-8")
    monkeypatch.setenv("ANALYTICS_TZ", "America/Bogota")
    monkeypatch.setenv("ANALYTICS_MONGO_DB", "OtraBase")
    monkeypatch.delenv("ANALYTICS_SQL", raising=False)

    settings = Settings.from_env(appsettings=path, available_drivers=["ODBC Driver 17 for SQL Server"])

    assert settings.sql_connection == "DRIVER={ODBC Driver 17 for SQL Server};SERVER=PC;DATABASE=Db;Trusted_Connection=yes"
    assert settings.mongo_uri == "mongodb://otro:27017"
    assert settings.mongo_database == "OtraBase"
    assert settings.timezone == "America/Bogota"
    assert "appsettings.json" in settings.sources["sql"]


def test_analytics_sql_tiene_prioridad(tmp_path, monkeypatch):
    monkeypatch.setenv("ANALYTICS_SQL", "DRIVER={X};SERVER=Y")
    settings = Settings.from_env(appsettings=tmp_path / "nada.json", available_drivers=[])
    assert settings.sql_connection == "DRIVER={X};SERVER=Y"
    assert settings.sources["sql"] == "ANALYTICS_SQL"


def test_sin_driver_odbc_lo_dice_claro(tmp_path, monkeypatch):
    path = tmp_path / "appsettings.json"
    path.write_text(json.dumps({"ConnectionStrings": {"DefaultConnection": "Server=PC;Database=Db;Trusted_Connection=True;"}}), encoding="utf-8")
    monkeypatch.delenv("ANALYTICS_SQL", raising=False)

    settings = Settings.from_env(appsettings=path, available_drivers=["PostgreSQL Unicode"])

    assert settings.sql_connection == ""
    assert "ODBC Driver 18 for SQL Server" in settings.sql_problem


def test_sin_appsettings_dice_donde_lo_busco(tmp_path, monkeypatch):
    monkeypatch.delenv("ANALYTICS_SQL", raising=False)
    missing = tmp_path / "no-existe.json"

    settings = Settings.from_env(appsettings=missing, available_drivers=["SQL Server"])

    assert settings.sql_connection == ""
    assert str(missing) in settings.sql_problem
    assert "ANALYTICS_SQL" in settings.sql_problem


def test_con_conexion_no_hay_problema(tmp_path, monkeypatch):
    monkeypatch.setenv("ANALYTICS_SQL", "DRIVER={X};SERVER=Y")
    assert Settings.from_env(appsettings=tmp_path / "nada.json", available_drivers=[]).sql_problem == ""

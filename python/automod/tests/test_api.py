import pytest
from fastapi.testclient import TestClient

from automod_service.api import create_app
from automod_service.config import Settings


@pytest.fixture
def client(settings):
    with TestClient(create_app(settings)) as c:  # "with" ejecuta el arranque (entrena el modelo)
        yield c


def test_health(client):
    body = client.get("/health").json()
    assert body["status"] == "ok"
    assert body["modelVersion"]


def test_moderate_respeta_el_contrato_que_usa_la_app_de_csharp(client):
    # HttpAutoModClassifier.cs lee "flagged", "category" y "reason".
    body = client.post("/moderate", json={"text": "eres un idiota", "channelId": 1}).json()

    assert set(body) == {"flagged", "category", "reason", "score", "modelVersion"}
    assert body["flagged"] is True
    assert body["category"] == "insulto"
    assert "insulto" in body["reason"]
    assert Settings().threshold <= body["score"] <= 1


def test_moderate_mensaje_normal(client):
    body = client.post("/moderate", json={"text": "hola a todos"}).json()
    assert body == {**body, "flagged": False, "category": "ninguna", "reason": ""}


def test_moderate_texto_vacio_o_sin_texto(client):
    assert client.post("/moderate", json={"text": ""}).json()["flagged"] is False
    assert client.post("/moderate", json={}).json()["flagged"] is False


def test_moderate_rechaza_textos_gigantes(client):
    assert client.post("/moderate", json={"text": "a" * 2001}).status_code == 422


def test_feedback_queda_guardado(client):
    response = client.post("/feedback", json={
        "text": "qué tonto ese jefe", "isToxic": False, "category": "insulto",
        "source": "automod_aprobado", "channelId": 1,
    })

    assert response.status_code == 202
    assert response.json() == {"stored": True, "category": "ninguna", "pendingRetrain": 1}
    assert client.get("/model").json()["feedback"]["total"] == 1


@pytest.mark.parametrize("body", [{}, {"text": "hola"}, {"isToxic": True}, {"text": "", "isToxic": True}])
def test_feedback_invalido_es_422(client, body):
    assert client.post("/feedback", json=body).status_code == 422


def test_model_muestra_version_umbral_y_categorias(client):
    info = client.get("/model").json()

    assert info["threshold"] == Settings().threshold
    assert "insulto" in info["categories"]
    assert info["seedSamples"] > 500
    assert info["retrainEvery"] == 3


def test_train_genera_una_version_nueva(client):
    import time
    before = client.get("/model").json()["version"]
    time.sleep(1.1)
    after = client.post("/train").json()["version"]
    assert after != before

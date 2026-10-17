from collections import Counter

import pytest

from automod_service.config import Settings
from automod_service.model import CATEGORIES, CLEAN, AutoModModel, Sample, evaluate, merge
from automod_service.text import normalize


# ── Los datos de la semilla ──────────────────────────────────────────────

def test_la_semilla_tiene_todas_las_categorias_y_sin_repetidos(seed):
    counts = Counter(s.category for s in seed)
    assert set(counts) == set(CATEGORIES)
    assert all(counts[c] >= 25 for c in CATEGORIES), counts
    normalized = [normalize(s.text) for s in seed]
    assert len(normalized) == len(set(normalized)), "Hay ejemplos repetidos en la semilla."


# ── Qué retiene y qué deja pasar ─────────────────────────────────────────
# Frases que NO están en la semilla: así se prueba que generaliza, no que memoriza.

@pytest.mark.parametrize(
    ("text", "category"),
    [
        ("eres un idiota y un inútil", "insulto"),
        ("IMBÉCIL!!!", "insulto"),
        ("eres un 1d10t4", "insulto"),
        ("sé dónde vives, cuídate", "amenaza"),
        ("te voy a romper la cara weón", "amenaza"),
        ("mándame fotos tuyas", "sexual"),
        ("sigue mi canal porfa", "spam"),
        ("vendo seguidores baratos", "spam"),
        ("los inmigrantes son una plaga", "odio"),
        ("nadie te quiere, cierra el canal", "acoso"),
    ],
)
def test_retiene_mensajes_toxicos_con_su_categoria(trained_model, text, category):
    prediction = trained_model.predict(text)
    assert prediction.flagged, (text, prediction)
    assert prediction.category == category, (text, prediction)
    assert prediction.reason.startswith("Probable")


@pytest.mark.parametrize(
    "text",
    [
        "hola chat, buenas noches",
        "gg wp, qué partida",
        "qué buen stream hoy",
        "puta que es difícil este jefe",          # garabato sin destinatario
        "jajaja weón qué buena",                   # "weón" amistoso
        "lo mataron de un tiro jaja",              # jerga de videojuegos
        "te voy a ganar en la revancha",
        "saludos a los peruanos del chat",          # mencionar un grupo no es odio
        "las mujeres gamers son increíbles",
        "gané 1000000 puntos",
    ],
)
def test_deja_pasar_mensajes_normales(trained_model, text):
    prediction = trained_model.predict(text)
    assert not prediction.flagged, (text, prediction)
    assert prediction.category == CLEAN
    assert prediction.reason == ""


def test_mensaje_vacio_no_se_retiene(trained_model):
    for text in ["", "   ", None]:
        assert not trained_model.predict(text).flagged


def test_un_umbral_mas_alto_retiene_menos(seed):
    texts = ["eres tonto", "qué fea", "sigue mi canal", "eres malo jugando", "idiota"]
    strict = AutoModModel.train(seed, threshold=0.3, evaluate_model=False)
    lax = AutoModModel(strict.pipeline, strict.info, threshold=0.9)

    assert sum(strict.predict(t).flagged for t in texts) >= sum(lax.predict(t).flagged for t in texts)
    for t in texts:  # misma probabilidad, distinto corte
        assert strict.predict(t).score == lax.predict(t).score


def test_las_probabilidades_suman_uno(trained_model):
    prediction = trained_model.predict("eres un tonto")
    assert set(prediction.probabilities) == set(CATEGORIES)
    assert sum(prediction.probabilities.values()) == pytest.approx(1.0, abs=1e-3)


def test_guardar_y_cargar_da_las_mismas_respuestas(trained_model, tmp_path):
    path = tmp_path / "modelo.joblib"
    trained_model.save(path)
    loaded = AutoModModel.load(path, trained_model.threshold)

    for text in ["hola", "eres un idiota", "sigue mi canal"]:
        assert loaded.predict(text) == trained_model.predict(text)


def test_no_entrena_con_una_sola_categoria():
    with pytest.raises(ValueError):
        AutoModModel.train([Sample("hola", CLEAN), Sample("chao", CLEAN)], threshold=0.6, evaluate_model=False)


# ── Mezcla con el feedback ───────────────────────────────────────────────

def test_el_feedback_reemplaza_al_ejemplo_de_la_semilla():
    seed = [Sample("Eres un TONTO", "insulto"), Sample("hola", CLEAN)]
    feedback = [Sample("eres un tonto", CLEAN, 3.0, "automod_aprobado")]

    merged = merge(seed, feedback)

    assert len(merged) == 2
    tonto = next(s for s in merged if normalize(s.text) == "eres un tonto")
    assert tonto.category == CLEAN and tonto.source == "automod_aprobado"


def test_gana_la_ultima_decision():
    feedback = [Sample("zorcotudo", CLEAN, 3.0, "automod_aprobado"), Sample("zorcotudo", "insulto", 3.0, "automod_rechazado")]
    assert merge([], feedback)[0].category == "insulto"


# ── Calidad mínima (si alguien edita la semilla y la empeora, esto avisa) ──

def test_calidad_minima_con_validacion_cruzada(seed):
    # Con el umbral que usa el servicio. Hoy da precisión ≈ 0,88, recall ≈ 0,74 y
    # categoría ≈ 0,88; los mínimos quedan un poco por debajo para avisar si algo empeora.
    metrics = evaluate(seed, threshold=Settings().threshold)

    assert metrics is not None
    assert metrics["precision"] >= 0.82, metrics
    assert metrics["recall"] >= 0.70, metrics
    assert metrics["f1"] >= 0.75, metrics
    assert metrics["categoryAccuracy"] >= 0.80, metrics
    assert metrics["falsePositives"] <= 40, metrics

import time

from automod_service.engine import AutoModEngine
from automod_service.feedback import FeedbackStore


def wait_until(condition, seconds=20.0):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if condition():
            return True
        time.sleep(0.05)
    return False


def test_al_arrancar_entrena_y_guarda_el_modelo(engine, settings):
    assert settings.model_file.exists()
    assert engine.model.info["seedSamples"] > 500
    assert engine.model.info["feedbackCount"] == 0


def test_al_reiniciar_carga_el_modelo_guardado_sin_reentrenar(engine, settings):
    second = AutoModEngine(settings)
    second.start()
    assert second.model.version == engine.model.version


def test_si_cambio_el_feedback_reentrena_al_arrancar(engine, settings):
    FeedbackStore(settings.feedback_db).add("algo nuevo", "ninguna", "automod_aprobado")
    time.sleep(1.1)  # la versión del modelo tiene resolución de segundos

    second = AutoModEngine(settings)
    second.start()

    assert second.model.info["feedbackCount"] == 1
    assert second.model.version != engine.model.version


def test_si_cambio_el_umbral_reentrena_al_arrancar(engine, settings):
    from dataclasses import replace

    time.sleep(1.1)
    second = AutoModEngine(replace(settings, threshold=0.5))
    second.start()

    assert second.model.info["threshold"] == 0.5
    assert second.model.version != engine.model.version


def test_sin_cambios_carga_el_modelo_guardado(engine, settings):
    time.sleep(1.1)
    second = AutoModEngine(settings)
    second.start()

    assert second.model.version == engine.model.version


def test_feedback_aprobado_queda_como_ninguna(engine):
    assert engine.add_feedback("qué tonto ese jefe", False, "insulto", "automod_aprobado", 1) == "ninguna"


def test_feedback_toxico_sin_categoria_usa_la_mas_probable(engine):
    assert engine.add_feedback("sé dónde vives", True, None, "mensaje_borrado", 1) == "amenaza"


def test_aprende_una_palabra_nueva_de_los_moderadores(engine):
    # "zorcotudo" no existe: al principio el modelo no tiene cómo saber que es un insulto.
    # (Ojo: "eres un zorcotudo" sí lo retiene desde el inicio, por el patrón "eres un ...").
    before = engine.classify("qué zorcotudo")
    assert not before.flagged, before

    for text in ["zorcotudo", "eres un zorcotudo", "qué zorcotudo eres", "cállate zorcotudo"]:
        engine.add_feedback(text, True, "insulto", "automod_rechazado", 1)
    engine.retrain()

    after = engine.classify("qué zorcotudo")
    assert after.flagged, after
    assert after.category == "insulto"
    assert after.score > before.score + 0.4
    assert not engine.classify("qué buen stream").flagged, "Lo demás no cambia."


def test_reentrena_solo_cada_n_decisiones(engine, settings):
    version = engine.model.version
    time.sleep(1.1)

    for i in range(settings.retrain_every):
        engine.add_feedback(f"mensaje de prueba {i}", False, None, "automod_aprobado", 1)

    assert wait_until(lambda: engine.model.version != version), "No se reentrenó en segundo plano."
    assert engine.pending == 0
    assert engine.model.info["feedbackCount"] == settings.retrain_every

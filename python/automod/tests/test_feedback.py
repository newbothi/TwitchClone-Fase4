from automod_service.feedback import SOURCE_WEIGHTS, FeedbackStore


def test_guarda_y_lee_en_orden(tmp_path):
    store = FeedbackStore(tmp_path / "f.sqlite3")
    store.add("hola", "ninguna", "automod_aprobado", 1)
    store.add("idiota", "insulto", "automod_rechazado", 1)
    store.add("compren", "spam", "mensaje_borrado", None)

    samples = store.samples()

    assert store.count() == 3
    assert [s.text for s in samples] == ["hola", "idiota", "compren"]
    assert samples[0].weight == SOURCE_WEIGHTS["automod_aprobado"]
    assert samples[2].weight == SOURCE_WEIGHTS["mensaje_borrado"]
    assert samples[1].weight > samples[2].weight, "Una decisión explícita pesa más que un borrado."


def test_fuente_desconocida_pesa_uno(tmp_path):
    store = FeedbackStore(tmp_path / "f.sqlite3")
    store.add("x", "ninguna", "otra_cosa")
    assert store.samples()[0].weight == 1.0


def test_estadisticas(tmp_path):
    store = FeedbackStore(tmp_path / "f.sqlite3")
    store.add("a", "insulto", "automod_rechazado")
    store.add("b", "insulto", "mensaje_borrado")
    store.add("c", "ninguna", "automod_aprobado")

    stats = store.stats()

    assert stats["total"] == 3
    assert stats["byCategory"] == {"insulto": 2, "ninguna": 1}
    assert stats["bySource"]["automod_rechazado"] == 1


def test_sobrevive_a_reabrir_el_archivo(tmp_path):
    FeedbackStore(tmp_path / "f.sqlite3").add("hola", "ninguna", "automod_aprobado")
    assert FeedbackStore(tmp_path / "f.sqlite3").count() == 1

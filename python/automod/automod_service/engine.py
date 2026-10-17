"""Mantiene el modelo vivo: lo carga o entrena al arrancar, clasifica, guarda las
decisiones de los moderadores y reentrena en segundo plano sin cortar el servicio."""

from __future__ import annotations

import logging
import threading

import sklearn

from .config import Settings
from .feedback import FeedbackStore
from .model import CLEAN, TOXIC, AutoModModel, Prediction, file_hash, load_seed, merge

log = logging.getLogger("automod")


class AutoModEngine:
    def __init__(self, settings: Settings):
        self.settings = settings
        self.store = FeedbackStore(settings.feedback_db)
        self._model: AutoModModel | None = None
        self._lock = threading.Lock()          # protege el cambio de modelo
        self._train_lock = threading.Lock()    # un solo entrenamiento a la vez
        self._pending = 0                      # decisiones nuevas desde el último entrenamiento

    # ── Ciclo de vida ────────────────────────────────────────────────────

    def start(self) -> None:
        """Carga el modelo guardado si sigue vigente; si no, entrena uno nuevo."""
        model_file = self.settings.model_file
        if model_file.exists():
            try:
                model = AutoModModel.load(model_file, self.settings.threshold)
                if self._is_current(model):
                    self._model = model
                    log.info("Modelo %s cargado desde %s", model.version, model_file)
                    return
                log.info("El modelo guardado está desactualizado; se reentrena.")
            except Exception:  # archivo dañado o de otra versión de scikit-learn
                log.exception("No se pudo cargar %s; se reentrena.", model_file)
        self.retrain()

    def _is_current(self, model: AutoModModel) -> bool:
        info = model.info
        # El umbral también cuenta: las métricas guardadas (precisión, recall) dependen de él.
        return (info.get("sklearnVersion") == sklearn.__version__
                and info.get("seedHash") == file_hash(self.settings.seed_file)
                and info.get("feedbackCount") == self.store.count()
                and info.get("threshold") == self.settings.threshold)

    @property
    def model(self) -> AutoModModel:
        if self._model is None:
            raise RuntimeError("El modelo todavía no está listo.")
        return self._model

    # ── Clasificar ───────────────────────────────────────────────────────

    def classify(self, text: str) -> Prediction:
        with self._lock:
            model = self.model
        return model.predict(text)

    # ── Aprender de los moderadores ──────────────────────────────────────

    def add_feedback(self, text: str, is_toxic: bool, category: str | None, source: str, channel_id: int | None) -> str:
        """Guarda una decisión y devuelve la categoría con la que quedó."""
        if not is_toxic:
            final = CLEAN
        elif category in TOXIC:
            final = category
        else:
            # Un mod borró el mensaje sin decir por qué: se usa la categoría tóxica más probable.
            final = self.classify(text).top_toxic

        self.store.add(text, final, source, channel_id)
        with self._lock:
            self._pending += 1
            should_retrain = self._pending >= self.settings.retrain_every
        if should_retrain:
            threading.Thread(target=self._retrain_quietly, name="automod-retrain", daemon=True).start()
        return final

    @property
    def pending(self) -> int:
        return self._pending

    def retrain(self) -> AutoModModel:
        with self._train_lock:
            seed = load_seed(self.settings.seed_file)
            feedback = self.store.samples()
            model = AutoModModel.train(
                merge(seed, feedback),
                self.settings.threshold,
                evaluate_model=self.settings.evaluate,
                extra_info={
                    "seedHash": file_hash(self.settings.seed_file),
                    "seedSamples": len(seed),
                    "feedbackCount": len(feedback),
                },
            )
            try:
                model.save(self.settings.model_file)
            except OSError:
                log.exception("No se pudo guardar el modelo en %s (sigue funcionando en memoria).", self.settings.model_file)
            with self._lock:
                self._model = model
                self._pending = 0
            log.info("Modelo %s entrenado con %d ejemplos. Métricas: %s", model.version, model.info["samples"], model.info["metrics"])
            return model

    def _retrain_quietly(self) -> None:
        try:
            self.retrain()
        except Exception:
            log.exception("Falló el reentrenamiento automático; se mantiene el modelo anterior.")

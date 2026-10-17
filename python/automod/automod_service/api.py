"""API HTTP (FastAPI). La app de C# la usa con AutoMod:Provider = "Http".

  POST /moderate   { "text": "...", "channelId": 1 }
                   -> { "flagged": true, "category": "insulto", "reason": "Probable insulto (87 %)",
                        "score": 0.87, "modelVersion": "20261016-153000" }
                   (es el contrato que ya entiende HttpAutoModClassifier.cs)

  POST /feedback   { "text": "...", "isToxic": false, "category": null,
                     "source": "automod_aprobado", "channelId": 1 }
                   -> 202 { "stored": true, "category": "ninguna", "pendingRetrain": 3 }

  POST /train      reentrena ahora y devuelve las métricas
  GET  /model      versión, ejemplos y métricas del modelo actual
  GET  /health     para saber si está arriba
"""

from __future__ import annotations

import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI, Request
from pydantic import BaseModel, Field

from . import __version__
from .config import Settings
from .engine import AutoModEngine
from .model import CATEGORIES

MAX_TEXT = 2000


class ModerateRequest(BaseModel):
    text: str = Field(default="", max_length=MAX_TEXT)
    channelId: int | None = None


class ModerateResponse(BaseModel):
    flagged: bool
    category: str
    reason: str
    score: float
    modelVersion: str


class FeedbackRequest(BaseModel):
    text: str = Field(min_length=1, max_length=MAX_TEXT)
    isToxic: bool
    category: str | None = None
    source: str = Field(default="moderador", max_length=40)
    channelId: int | None = None


class FeedbackResponse(BaseModel):
    stored: bool
    category: str
    pendingRetrain: int


def create_app(settings: Settings | None = None, engine: AutoModEngine | None = None) -> FastAPI:
    settings = settings or Settings.from_env()

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        app.state.engine = engine or AutoModEngine(settings)
        if app.state.engine._model is None:
            app.state.engine.start()
        yield

    app = FastAPI(title="TwitchClone AutoMod", version=__version__, lifespan=lifespan)

    def get_engine(request: Request) -> AutoModEngine:
        return request.app.state.engine

    @app.get("/health")
    def health(request: Request):
        engine = get_engine(request)
        return {"status": "ok", "modelVersion": engine.model.version}

    @app.post("/moderate", response_model=ModerateResponse)
    def moderate(body: ModerateRequest, request: Request):
        engine = get_engine(request)
        prediction = engine.classify(body.text)
        return ModerateResponse(
            flagged=prediction.flagged,
            category=prediction.category,
            reason=prediction.reason,
            score=prediction.score,
            modelVersion=engine.model.version,
        )

    @app.post("/feedback", response_model=FeedbackResponse, status_code=202)
    def feedback(body: FeedbackRequest, request: Request):
        engine = get_engine(request)
        category = (body.category or "").strip().lower() or None
        stored = engine.add_feedback(body.text, body.isToxic, category, body.source.strip() or "moderador", body.channelId)
        return FeedbackResponse(stored=True, category=stored, pendingRetrain=engine.pending)

    @app.post("/train")
    def train(request: Request):
        model = get_engine(request).retrain()
        return model.info

    @app.get("/model")
    def model_info(request: Request):
        engine = get_engine(request)
        return {
            **engine.model.info,
            "threshold": engine.settings.threshold,
            "categories": list(CATEGORIES),
            "pendingRetrain": engine.pending,
            "retrainEvery": engine.settings.retrain_every,
            "feedback": engine.store.stats(),
        }

    return app


def configure_logging() -> None:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")

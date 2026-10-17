"""El modelo: TF-IDF + regresión logística multinomial.

Cómo decide (resumen para el README):
  1. El texto se normaliza (text.normalize): minúsculas, sin tildes, "1d10t4" -> "idiota".
  2. Se convierte en números con TF-IDF de dos formas a la vez:
       - n-gramas de CARACTERES (2 a 5 letras): reconoce palabras mal escritas o cortadas.
       - n-gramas de PALABRAS (1 y 2 palabras): reconoce frases ("sé dónde vives").
  3. Una regresión logística (la misma idea de tu curso: pesos que se ajustan siguiendo
     el gradiente, aquí con el optimizador L-BFGS, y una función softmax al final) da una
     probabilidad para cada categoría: ninguna, insulto, acoso, odio, sexual, amenaza, spam.
  4. toxicidad = 1 - P(ninguna). Si toxicidad >= umbral, el mensaje se retiene y la
     categoría es la más probable entre las tóxicas.
"""

from __future__ import annotations

import csv
import hashlib
from collections import Counter
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

import joblib
import numpy as np
import sklearn
from sklearn.feature_extraction.text import TfidfVectorizer
from sklearn.linear_model import LogisticRegression
from sklearn.model_selection import StratifiedKFold
from sklearn.pipeline import FeatureUnion, Pipeline

from .text import normalize

CLEAN = "ninguna"
CATEGORIES = (CLEAN, "insulto", "acoso", "odio", "sexual", "amenaza", "spam")
TOXIC = CATEGORIES[1:]

_REASONS = {
    "insulto": "Probable insulto",
    "acoso": "Probable acoso",
    "odio": "Probable discurso de odio",
    "sexual": "Probable contenido sexual",
    "amenaza": "Probable amenaza",
    "spam": "Probable spam",
}


@dataclass(frozen=True)
class Sample:
    text: str
    category: str
    weight: float = 1.0
    source: str = "semilla"


@dataclass(frozen=True)
class Prediction:
    flagged: bool
    category: str          # "ninguna" si no se retiene
    score: float           # probabilidad de que sea tóxico (0 a 1)
    top_toxic: str         # categoría tóxica más probable (aunque no se retenga)
    probabilities: dict[str, float]

    @property
    def reason(self) -> str:
        if not self.flagged:
            return ""
        return f"{_REASONS.get(self.category, 'Mensaje dudoso')} ({round(self.score * 100)} %)"


# ── Datos ────────────────────────────────────────────────────────────────

def load_seed(path: Path) -> list[Sample]:
    samples: list[Sample] = []
    with open(path, encoding="utf-8", newline="") as f:
        for row in csv.DictReader(f):
            text = (row.get("texto") or "").strip()
            category = (row.get("categoria") or "").strip().lower()
            if not text:
                continue
            if category not in CATEGORIES:
                raise ValueError(f"Categoría desconocida en {path.name}: '{category}' (texto: {text!r})")
            samples.append(Sample(text, category))
    return samples


def file_hash(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()[:16]


def merge(seed: list[Sample], feedback: list[Sample]) -> list[Sample]:
    """Une la semilla con las decisiones de los moderadores.

    Si un mismo mensaje (ya normalizado) aparece varias veces, gana la ÚLTIMA decisión:
    así un moderador puede corregir un ejemplo de la semilla con el que no está de acuerdo.
    """
    by_text: dict[str, Sample] = {}
    for sample in [*seed, *feedback]:
        key = normalize(sample.text)
        if key:
            by_text[key] = sample
    return list(by_text.values())


# ── Modelo ───────────────────────────────────────────────────────────────

def build_pipeline() -> Pipeline:
    features = FeatureUnion([
        ("caracteres", TfidfVectorizer(analyzer="char_wb", ngram_range=(2, 5), sublinear_tf=True)),
        ("palabras", TfidfVectorizer(analyzer="word", ngram_range=(1, 2), sublinear_tf=True,
                                     token_pattern=r"(?u)\b\w+\b")),
    ])
    classifier = LogisticRegression(C=8.0, class_weight="balanced", max_iter=5000)
    return Pipeline([("features", features), ("clasificador", classifier)])


class AutoModModel:
    def __init__(self, pipeline: Pipeline, info: dict, threshold: float):
        self.pipeline = pipeline
        self.info = info
        self.threshold = threshold

    @property
    def version(self) -> str:
        return self.info["version"]

    @staticmethod
    def train(samples: list[Sample], threshold: float, evaluate_model: bool = True, extra_info: dict | None = None) -> "AutoModModel":
        if len({s.category for s in samples}) < 2:
            raise ValueError("Se necesitan ejemplos de al menos dos categorías para entrenar.")

        texts = [normalize(s.text) for s in samples]
        labels = [s.category for s in samples]
        weights = np.array([s.weight for s in samples], dtype=float)

        pipeline = build_pipeline()
        pipeline.fit(texts, labels, clasificador__sample_weight=weights)

        now = datetime.now(timezone.utc)
        info = {
            "version": now.strftime("%Y%m%d-%H%M%S"),
            "trainedAt": now.isoformat(timespec="seconds"),
            "samples": len(samples),
            "bySource": dict(Counter(s.source for s in samples)),
            "byCategory": dict(Counter(labels)),
            "threshold": threshold,
            "sklearnVersion": sklearn.__version__,
            "metrics": evaluate(samples, threshold) if evaluate_model else None,
            **(extra_info or {}),
        }
        return AutoModModel(pipeline, info, threshold)

    def predict(self, text: str) -> Prediction:
        normalized = normalize(text)
        if not normalized:
            return Prediction(False, CLEAN, 0.0, TOXIC[0], {})

        probabilities = dict(zip(self.pipeline.classes_, self.pipeline.predict_proba([normalized])[0]))
        score = float(1.0 - probabilities.get(CLEAN, 0.0))
        top_toxic = max((c for c in TOXIC if c in probabilities), key=lambda c: probabilities[c], default=TOXIC[0])
        flagged = score >= self.threshold
        return Prediction(
            flagged=flagged,
            category=top_toxic if flagged else CLEAN,
            score=round(score, 4),
            top_toxic=top_toxic,
            probabilities={k: round(float(v), 4) for k, v in probabilities.items()},
        )

    def save(self, path: Path) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        joblib.dump({"pipeline": self.pipeline, "info": self.info}, path)

    @staticmethod
    def load(path: Path, threshold: float) -> "AutoModModel":
        data = joblib.load(path)
        return AutoModModel(data["pipeline"], data["info"], threshold)


# ── Evaluación ───────────────────────────────────────────────────────────

def evaluate(samples: list[Sample], threshold: float, folds: int = 5) -> dict | None:
    """Validación cruzada: entrena con 4/5 de los ejemplos y prueba con el 1/5 restante,
    cinco veces, para medir cómo le iría con mensajes que nunca vio."""
    labels = np.array([s.category for s in samples])
    smallest = min(Counter(labels).values())
    folds = min(folds, smallest)
    if folds < 2:
        return None

    texts = np.array([normalize(s.text) for s in samples], dtype=object)
    weights = np.array([s.weight for s in samples], dtype=float)
    true_toxic = labels != CLEAN
    pred_toxic = np.zeros(len(samples), dtype=bool)
    pred_category = np.empty(len(samples), dtype=object)

    for train_idx, test_idx in StratifiedKFold(n_splits=folds, shuffle=True, random_state=7).split(texts, labels):
        pipeline = build_pipeline()
        pipeline.fit(list(texts[train_idx]), list(labels[train_idx]), clasificador__sample_weight=weights[train_idx])
        probabilities = pipeline.predict_proba(list(texts[test_idx]))
        clean_col = list(pipeline.classes_).index(CLEAN)
        pred_toxic[test_idx] = (1.0 - probabilities[:, clean_col]) >= threshold
        pred_category[test_idx] = pipeline.classes_[probabilities.argmax(axis=1)]

    tp = int(np.sum(pred_toxic & true_toxic))
    fp = int(np.sum(pred_toxic & ~true_toxic))
    fn = int(np.sum(~pred_toxic & true_toxic))
    precision = tp / (tp + fp) if tp + fp else 0.0
    recall = tp / (tp + fn) if tp + fn else 0.0
    f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    toxic_idx = np.where(true_toxic & pred_toxic)[0]
    category_accuracy = float(np.mean(pred_category[toxic_idx] == labels[toxic_idx])) if len(toxic_idx) else 0.0

    return {
        "folds": folds,
        # De los mensajes que retuvo, cuántos eran tóxicos de verdad.
        "precision": round(precision, 3),
        # De los mensajes tóxicos, cuántos alcanzó a retener.
        "recall": round(recall, 3),
        "f1": round(f1, 3),
        # Mensajes normales retenidos por error (lo que más molesta en un chat).
        "falsePositives": fp,
        "falseNegatives": fn,
        # Entre los tóxicos retenidos, cuántas veces acertó la categoría.
        "categoryAccuracy": round(category_accuracy, 3),
    }

import pytest

from automod_service.config import Settings
from automod_service.engine import AutoModEngine
from automod_service.model import AutoModModel, load_seed


@pytest.fixture
def settings(tmp_path) -> Settings:
    # Cada prueba tiene su propia carpeta: no toca el modelo ni el feedback reales.
    return Settings(data_dir=tmp_path / "datos", retrain_every=3, evaluate=False)


@pytest.fixture
def engine(settings) -> AutoModEngine:
    engine = AutoModEngine(settings)
    engine.start()
    return engine


@pytest.fixture(scope="session")
def seed():
    return load_seed(Settings().seed_file)


@pytest.fixture(scope="session")
def trained_model(seed) -> AutoModModel:
    # Un solo entrenamiento para todas las pruebas que solo predicen.
    return AutoModModel.train(seed, threshold=Settings().threshold, evaluate_model=False)

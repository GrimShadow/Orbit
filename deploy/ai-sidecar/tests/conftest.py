from __future__ import annotations

import os
from collections.abc import Iterator
from dataclasses import replace
from pathlib import Path

import pytest
from app.config import Settings
from app.main import create_app
from app.registry import Registry
from fastapi.testclient import TestClient


@pytest.fixture(scope="session")
def base_settings(tmp_path_factory: pytest.TempPathFactory) -> Settings:
    tmp = tmp_path_factory.mktemp("sidecar-tmp")
    return replace(Settings.from_env(), models_dir=Path(os.environ.get("AI_MODELS_DIR", "/models")), tmp_dir=tmp)


@pytest.fixture(scope="session")
def registry(base_settings: Settings) -> Registry:
    """Real models, loaded once for the whole run."""
    reg = Registry(base_settings)
    reg.start_warmup()
    assert reg.wait_warm(600), "model warm-up did not finish"
    return reg


@pytest.fixture(scope="session")
def client(base_settings: Settings, registry: Registry) -> Iterator[TestClient]:
    with TestClient(create_app(base_settings, registry)) as c:
        yield c


@pytest.fixture
def make_client(base_settings: Settings, registry: Registry):
    """Factory for clients with modified settings that share the already-loaded models."""
    opened: list[TestClient] = []

    def _make(**overrides: object) -> TestClient:
        c = TestClient(create_app(replace(base_settings, **overrides), registry), raise_server_exceptions=False)
        c.__enter__()
        opened.append(c)
        return c

    yield _make
    for c in opened:
        c.__exit__(None, None, None)

"""엔진 레지스트리: config.toml 의 [engines.*] 섹션을 읽어 어댑터를 지연 로딩한다."""

import importlib
import threading
import tomllib
from pathlib import Path

from engines.base import CnnEngine

CONFIG_PATH = Path(__file__).resolve().parent.parent / "config.toml"


class UnknownEngineError(ValueError):
    pass


class EngineRegistry:
    def __init__(self, config_path: Path = CONFIG_PATH):
        with open(config_path, "rb") as f:
            config = tomllib.load(f)
        self.default_engine: str = config["service"]["default_engine"]
        self.preload: list[str] = config["service"].get("preload", [])
        self._configs: dict[str, dict] = config.get("engines", {})
        self._engines: dict[str, CnnEngine] = {}
        self._lock = threading.Lock()

    @property
    def names(self) -> list[str]:
        return list(self._configs)

    def is_loaded(self, name: str) -> bool:
        return name in self._engines

    def get(self, name: str | None = None) -> CnnEngine:
        name = name or self.default_engine
        if name not in self._configs:
            raise UnknownEngineError(f"알 수 없는 엔진: {name} (사용 가능: {', '.join(self.names)})")
        if name not in self._engines:
            with self._lock:
                if name not in self._engines:
                    options = dict(self._configs[name])
                    module_name, class_name = options.pop("adapter").split(":")
                    cls = getattr(importlib.import_module(module_name), class_name)
                    self._engines[name] = cls(name=name, **options)
        return self._engines[name]

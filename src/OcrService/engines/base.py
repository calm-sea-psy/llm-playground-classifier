from abc import ABC, abstractmethod

import numpy as np

from contract import OcrResult


class OcrEngine(ABC):
    """엔진 어댑터 공통 인터페이스. 모든 엔진은 run(image) -> OcrResult 하나만 구현한다."""

    name: str
    model_version: str

    @abstractmethod
    def run(self, image: np.ndarray) -> OcrResult:
        """image: BGR uint8 (H, W, 3). 반환 bbox는 이 이미지의 픽셀 좌표."""

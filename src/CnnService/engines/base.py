import base64
import threading
import time
from abc import ABC, abstractmethod

import cv2
import numpy as np
import torch

from contract import CnnResult, Finding, Heatmap


class CnnEngine(ABC):
    """엔진 어댑터 공통 인터페이스. 모든 엔진은 run(image) -> CnnResult 하나만 구현한다."""

    name: str
    model_version: str
    labels: list[str]

    @abstractmethod
    def run(self, image: np.ndarray, heatmap: bool = True, target: str | None = None) -> CnnResult:
        """image: 흑백 uint8 (H, W). target: 히트맵을 그릴 소견 (생략 시 가장 강한 양성 소견)"""


class DenseNetEngine(CnnEngine):
    """DenseNet 계열 공통: 전처리 ➔ 추론 ➔ 소견 판정 ➔ Grad-CAM (마지막 합성곱 블록 model.features 기준)"""

    def __init__(self, name: str, model: torch.nn.Module, labels: list[str], thresholds: dict[str, float],
                 device: str):
        self.name = name
        self.device = torch.device(device if torch.cuda.is_available() or device == "cpu" else "cpu")
        self.model = model.to(self.device).eval()
        self.labels = labels
        self.thresholds = {label: float(thresholds.get(label, thresholds.get("default", 0.5))) for label in labels}
        # 한 번에 1건 (Grad-CAM 훅이 요청 간에 섞이지 않도록)
        self._lock = threading.Lock()

    @abstractmethod
    def preprocess(self, image: np.ndarray) -> tuple[torch.Tensor, list[int]]:
        """흑백 uint8 ➔ (입력 텐서 [1, C, H, W], 모델이 보는 원본 영역 [x0, y0, x1, y1])"""

    @abstractmethod
    def probabilities(self, output: torch.Tensor) -> torch.Tensor:
        """모델 출력 ➔ labels 순서의 확률 [1, len(labels)]"""

    def run(self, image: np.ndarray, heatmap: bool = True, target: str | None = None) -> CnnResult:
        if target is not None and target not in self.labels:
            raise ValueError(f"알 수 없는 소견: {target} (사용 가능: {', '.join(self.labels)})")
        height, width = image.shape[:2]
        x, region = self.preprocess(image)
        x = x.to(self.device)
        with self._lock:
            activations: dict[str, torch.Tensor] = {}
            hook = self.model.features.register_forward_hook(lambda _m, _i, out: activations.update(a=out))
            try:
                with torch.set_grad_enabled(heatmap):
                    started = time.perf_counter()
                    probs = self.probabilities(self.model(x))[0]
                    if self.device.type == "cuda":
                        torch.cuda.synchronize()
                    elapsed_ms = int((time.perf_counter() - started) * 1000)
                    values = probs.detach().float().cpu().numpy()
                    findings = sorted(
                        (Finding(label=label, probability=round(float(p), 4), threshold=self.thresholds[label],
                                 positive=bool(p >= self.thresholds[label]))
                         for label, p in zip(self.labels, values) if not np.isnan(p)),
                        key=lambda f: f.probability, reverse=True)
                    cam = None
                    if heatmap and findings:
                        label = target or self._strongest(findings)
                        cam = Heatmap(label=label, region=region,
                                      png_base64=self._grad_cam(probs[self.labels.index(label)], activations["a"]))
            finally:
                hook.remove()
        return CnnResult(engine=self.name, model_version=self.model_version, width=width, height=height,
                         findings=findings, heatmap=cam, elapsed_ms=elapsed_ms)

    @staticmethod
    def _strongest(findings: list[Finding]) -> str:
        # 기준값 대비 가장 많이 넘은 소견 (양성이 없으면 기준값에 가장 가까운 소견)
        return max(findings, key=lambda f: f.probability / max(f.threshold, 1e-6)).label

    @staticmethod
    def _grad_cam(score: torch.Tensor, activation: torch.Tensor) -> str:
        grads = torch.autograd.grad(score, activation)[0]  # [1, C, h, w]
        weights = grads.mean(dim=(2, 3), keepdim=True)
        cam = torch.relu((weights * activation).sum(dim=1))[0].detach().float().cpu().numpy()
        if cam.max() > 0:
            cam = cam / cam.max()
        cam = cv2.resize((cam * 255).astype(np.uint8), (224, 224), interpolation=cv2.INTER_CUBIC)
        ok, png = cv2.imencode(".png", cam)
        return base64.b64encode(png.tobytes()).decode("ascii")

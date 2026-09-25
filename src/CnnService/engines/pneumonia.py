import json
from pathlib import Path

import numpy as np
import torch
from PIL import Image
from torchvision import transforms as T
from torchvision.models import densenet121

from engines.base import DenseNetEngine
from imaging import center_square

SERVICE_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = SERVICE_DIR.parent.parent

# 학습(tools/train_pneumonia.py)과 같은 전처리: 짧은 변 256 ➔ 중앙 224 ➔ ImageNet 정규화
EVAL_TRANSFORM = T.Compose([
    T.Grayscale(3), T.Resize(256), T.CenterCrop(224), T.ToTensor(),
    T.Normalize([0.485, 0.456, 0.406], [0.229, 0.224, 0.225]),
])


def build_model() -> torch.nn.Module:
    model = densenet121(weights=None)
    model.classifier = torch.nn.Linear(model.classifier.in_features, 1)
    return model


class PneumoniaEngine(DenseNetEngine):
    """ImageNet DenseNet121 을 Kaggle 소아 흉부 X-ray(정상/폐렴)로 미세조정한 이진 분류.
    가중치: data/models/ (git 제외, tools/train_pneumonia.py 로 다시 만들 수 있음)"""

    def __init__(self, name: str, checkpoint: str, device: str = "cuda", thresholds: dict[str, float] | None = None):
        path = Path(checkpoint)
        path = path if path.is_absolute() else REPO_ROOT / path
        if not path.exists():
            raise FileNotFoundError(f"가중치가 없습니다: {path} (tools/train_pneumonia.py 로 학습)")
        model = build_model()
        model.load_state_dict(torch.load(path, map_location="cpu"))
        super().__init__(name, model, ["Pneumonia"], thresholds or {}, device)
        meta = path.with_suffix(".json")
        info = json.loads(meta.read_text(encoding="utf-8")) if meta.exists() else {}
        self.model_version = f"densenet121 kaggle-pneumonia {info.get('trained', '')}".strip()

    def preprocess(self, image: np.ndarray) -> tuple[torch.Tensor, list[int]]:
        x = EVAL_TRANSFORM(Image.fromarray(image))
        return x[None], center_square(image.shape[1], image.shape[0], 224 / 256)

    def probabilities(self, output: torch.Tensor) -> torch.Tensor:
        return torch.sigmoid(output)

import numpy as np
import torch
import torchxrayvision as xrv

from engines.base import DenseNetEngine
from imaging import center_square


class XrvEngine(DenseNetEngine):
    """TorchXRayVision DenseNet121: 성인 흉부 X-ray 여러 데이터셋(NIH·CheXpert·MIMIC·PadChest 등)으로 학습, 18개 소견.
    출력은 모델의 운영 기준점(op_threshs)이 0.5 가 되도록 변환된 값이라 기본 판정 기준은 0.5.
    가중치는 첫 실행 때 ~/.torchxrayvision 으로 내려받는다 (약 30MB)."""

    def __init__(self, name: str, weights: str = "densenet121-res224-all", device: str = "cuda",
                 thresholds: dict[str, float] | None = None):
        model = xrv.models.DenseNet(weights=weights)
        super().__init__(name, model, list(model.pathologies), thresholds or {}, device)
        self.model_version = f"torchxrayvision {xrv.__version__} {weights}"
        self._crop = xrv.datasets.XRayCenterCrop()
        # 학습 때와 같은 skimage(안티에일리어싱) 축소. cv2 로 바꾸면 폐렴 AUC 가 0.791 ➔ 0.640 으로 떨어짐 (2차-1 측정)
        self._resize = xrv.datasets.XRayResizer(224)

    def preprocess(self, image: np.ndarray) -> tuple[torch.Tensor, list[int]]:
        img = xrv.datasets.normalize(image.astype(np.float32), 255)[None, ...]  # [-1024, 1024]
        img = self._resize(self._crop(img))
        return torch.from_numpy(img)[None], center_square(image.shape[1], image.shape[0])

    def probabilities(self, output: torch.Tensor) -> torch.Tensor:
        return output

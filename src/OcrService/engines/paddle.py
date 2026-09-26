import threading
import time

import cv2
import numpy as np
from paddleocr import PaddleOCR

from contract import OcrLine, OcrResult, average_confidence
from engines.base import OcrEngine
from engines.gpu import release_cached_memory


class PaddleEngine(OcrEngine):
    def __init__(
        self,
        name: str = "paddleocr",
        lang: str = "korean",
        device: str = "gpu:0",
        use_textline_orientation: bool = True,
        use_doc_orientation_classify: bool = False,
        use_doc_unwarping: bool = False,
        max_pixels: int = 8_500_000,
        enable_mkldnn: bool | None = None,
    ):
        self.name = name
        self.max_pixels = max_pixels
        # CPU 에서 oneDNN(mkldnn) 경로가 paddle 3.4 에서 NotImplementedError (ConvertPirAttribute2RuntimeAttribute) ➔ CPU 판은 끔
        extra = {} if enable_mkldnn is None else {"enable_mkldnn": enable_mkldnn}
        self._ocr = PaddleOCR(
            lang=lang,
            device=device,
            use_textline_orientation=use_textline_orientation,
            use_doc_orientation_classify=use_doc_orientation_classify,
            use_doc_unwarping=use_doc_unwarping,
            **extra,
        )
        det = self._ocr.paddlex_pipeline.text_det_model.model_name
        rec = self._ocr.paddlex_pipeline.text_rec_model.model_name
        self.model_version = f"{det}+{rec}"
        # Paddle 예측기는 스레드 안전하지 않으므로 한 번에 1건만 처리
        self._lock = threading.Lock()

    def run(self, image: np.ndarray) -> OcrResult:
        height, width = image.shape[:2]
        # 약 9백만 화소를 넘으면 추론이 1초 ➔ 20초 이상으로 급격히 느려짐 (휴대폰 촬영 3024×4032 에서 확인)
        # ➔ 축소해서 인식하고 bbox 는 원본 좌표로 되돌림. 신뢰도 차이는 0.97 ➔ 0.97 수준
        scale = min(1.0, (self.max_pixels / (width * height)) ** 0.5)
        target = image if scale == 1.0 else cv2.resize(
            image, (int(width * scale), int(height * scale)), interpolation=cv2.INTER_AREA)
        with self._lock:
            started = time.perf_counter()
            res = self._ocr.predict(target)[0]
            elapsed_ms = int((time.perf_counter() - started) * 1000)
            release_cached_memory()

        lines = [
            OcrLine(
                text=text,
                confidence=round(float(score), 4),
                bbox=[[round(int(x) / scale), round(int(y) / scale)] for x, y in poly],
            )
            for text, score, poly in zip(res["rec_texts"], res["rec_scores"], res["rec_polys"])
            if text.strip()  # 검출됐지만 인식 결과가 빈 박스(score 0.0)는 제외
        ]
        return OcrResult(
            engine=self.name,
            model_version=self.model_version,
            width=width,
            height=height,
            lines=lines,
            avg_confidence=average_confidence(lines),
            elapsed_ms=elapsed_ms,
        )

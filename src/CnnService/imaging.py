from pathlib import Path

import cv2
import numpy as np


class ImageDecodeError(ValueError):
    pass


def decode_gray(data: bytes) -> np.ndarray:
    """바이트 ➔ 흑백 uint8. 16비트 PNG(DICOM 에서 변환한 X-ray)는 0~255 로 줄인다."""
    image = cv2.imdecode(np.frombuffer(data, np.uint8), cv2.IMREAD_UNCHANGED)
    if image is None:
        raise ImageDecodeError("이미지를 읽을 수 없습니다 (지원 형식: png, jpg, jpeg, bmp, tif, webp)")
    if image.ndim == 3:
        image = cv2.cvtColor(image, cv2.COLOR_BGRA2GRAY if image.shape[2] == 4 else cv2.COLOR_BGR2GRAY)
    if image.dtype != np.uint8:
        image = cv2.normalize(image, None, 0, 255, cv2.NORM_MINMAX).astype(np.uint8)
    return image


def read_gray(path: Path) -> np.ndarray:
    # cv2.imread 는 Windows 에서 한글 경로를 못 읽으므로 바이트로 읽어서 디코딩
    return decode_gray(path.read_bytes())


def center_square(width: int, height: int, fraction: float = 1.0) -> list[int]:
    """중앙 정사각형 자르기 영역 [x0, y0, x1, y1] (fraction: 짧은 변 대비 비율)"""
    side = int(min(width, height) * fraction)
    x0, y0 = (width - side) // 2, (height - side) // 2
    return [x0, y0, x0 + side, y0 + side]

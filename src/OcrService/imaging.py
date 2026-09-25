from pathlib import Path

import cv2
import numpy as np


class ImageDecodeError(ValueError):
    pass


def decode_image(data: bytes) -> np.ndarray:
    """바이트 ➔ BGR 이미지. JPEG EXIF 회전은 OpenCV가 적용해 준다 (휴대폰 촬영 영수증)."""
    image = cv2.imdecode(np.frombuffer(data, np.uint8), cv2.IMREAD_COLOR)
    if image is None:
        raise ImageDecodeError("이미지를 읽을 수 없습니다 (지원 형식: png, jpg, jpeg, bmp, tif, webp)")
    return image


def read_image(path: Path) -> np.ndarray:
    # cv2.imread 는 Windows 에서 한글 경로를 못 읽으므로 바이트로 읽어서 디코딩
    return decode_image(path.read_bytes())

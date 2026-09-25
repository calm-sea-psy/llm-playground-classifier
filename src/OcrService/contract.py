"""OCR 표준 응답 계약 (todo 1번 ㄹ)-c).

모든 엔진 어댑터는 이 형식으로만 결과를 돌려준다. 필드를 추가할 수는 있어도
기존 필드의 이름·의미는 바꾸지 않는다 (C# OcrResult DTO와 1:1 대응).
"""

from pydantic import BaseModel


class OcrLine(BaseModel):
    text: str
    # bbox·신뢰도를 주지 않는 엔진(예: DeepSeek-OCR)은 null
    confidence: float | None = None
    # 좌상단부터 시계 방향 4점 [[x, y], ...], 원본 이미지 픽셀 좌표
    bbox: list[list[int]] | None = None


class LayoutBlock(BaseModel):
    """문서 파싱 엔진(PP-StructureV3 등)의 레이아웃 블록. 읽기 순서대로 정렬"""
    label: str  # paragraph_title, text, table, header, footer, number, ...
    bbox: list[int]  # [x0, y0, x1, y1], 원본 이미지 픽셀 좌표
    content: str  # 표는 HTML, 나머지는 줄바꿈으로 이은 텍스트


class OcrResult(BaseModel):
    engine: str
    model_version: str
    page: int = 1
    width: int
    height: int
    lines: list[OcrLine]
    # 줄이 없으면 0.0, 줄은 있지만 엔진이 신뢰도를 주지 않으면 null
    avg_confidence: float | None
    elapsed_ms: int
    # 문서 파싱 엔진만 채움 (1번 ㄹ)-f: 기존 필드는 그대로 두고 선택 필드로 확장)
    blocks: list[LayoutBlock] | None = None
    # 읽기 순서로 복원한 Markdown (표는 HTML). 있으면 LLM 입력으로 우선 사용
    markdown: str | None = None


def average_confidence(lines: list[OcrLine]) -> float | None:
    if not lines:
        return 0.0
    scores = [line.confidence for line in lines if line.confidence is not None]
    if not scores:
        return None
    return round(sum(scores) / len(scores), 4)

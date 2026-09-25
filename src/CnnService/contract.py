"""CNN 표준 응답 계약 (2차 Step 2).

모든 엔진 어댑터는 이 형식으로만 결과를 돌려준다 (C# CnnResult DTO 와 1:1 대응).
OCR 계약과 같은 원칙: 필드를 추가할 수는 있어도 기존 필드의 이름·의미는 바꾸지 않는다.
"""

from pydantic import BaseModel


class Finding(BaseModel):
    label: str  # 영문 소견 이름 (예: Pneumonia, Consolidation, Cardiomegaly)
    probability: float  # 0~1, 엔진이 내는 값 그대로 (보정 여부는 엔진 설명 참고)
    threshold: float  # 이 소견의 판정 기준값 (config.toml)
    positive: bool  # probability >= threshold


class Heatmap(BaseModel):
    """Grad-CAM: 모델이 판단할 때 본 영역. 결과 화면에서 원본 위에 겹쳐 그린다."""
    label: str  # 어느 소견에 대한 히트맵인지
    png_base64: str  # 흑백 PNG (밝을수록 영향이 큼), 크기는 region 과 비율이 같음
    region: list[int]  # [x0, y0, x1, y1] 히트맵이 대응하는 원본 이미지 픽셀 영역 (중앙 자르기 영역)


class CnnResult(BaseModel):
    engine: str
    model_version: str
    width: int
    height: int
    findings: list[Finding]  # 확률 높은 순
    heatmap: Heatmap | None = None
    elapsed_ms: int  # 추론 시간 (전처리·히트맵 제외)

import re
import threading
import time

import cv2
import numpy as np
from paddleocr import PPStructureV3

from contract import LayoutBlock, OcrLine, OcrResult, average_confidence
from engines.base import OcrEngine
from engines.gpu import release_cached_memory

# Markdown 에서 뺄 블록 (쪽 번호 등). 머리말·꼬리말은 영수증 상호·주소가 들어가는 경우가 있어 유지
SKIP_LABELS = {"number"}
TITLE_LABELS = {"doc_title", "paragraph_title"}


class PaddleStructureEngine(OcrEngine):
    """
    PP-StructureV3 문서 파싱 (경로 B). 레이아웃 검출 ➔ 영역별 OCR ➔ 표 구조(HTML) ➔ 읽기 순서.

    PaddleX 기본 Markdown 은 블록 안의 줄을 구분자 없이 이어 붙여 영수증 숫자가 뭉개지므로
    ("1,380 3 4,140" ➔ "1,38034,140"), 블록 구조와 표 HTML 만 쓰고 텍스트 블록은 OCR 줄 좌표로 줄바꿈을 살려 직접 만든다.
    lines[] 는 PP-OCR 엔진과 같은 형식으로 채워 기존 계약을 유지한다.
    """

    def __init__(
        self,
        name: str = "ppstructure",
        lang: str = "korean",
        device: str = "gpu:0",
        use_textline_orientation: bool = True,
        use_table_recognition: bool = True,
        use_region_detection: bool = True,
        max_pixels: int = 8_500_000,
    ):
        self.name = name
        self.max_pixels = max_pixels
        self._pipeline = PPStructureV3(
            lang=lang,
            device=device,
            use_doc_orientation_classify=False,
            use_doc_unwarping=False,
            use_textline_orientation=use_textline_orientation,
            use_table_recognition=use_table_recognition,
            use_region_detection=use_region_detection,
            use_formula_recognition=False,
            use_chart_recognition=False,
            use_seal_recognition=False,
        )
        self.model_version = f"PP-StructureV3({lang})"
        self._lock = threading.Lock()

    def run(self, image: np.ndarray) -> OcrResult:
        height, width = image.shape[:2]
        scale = min(1.0, (self.max_pixels / (width * height)) ** 0.5)
        target = image if scale == 1.0 else cv2.resize(
            image, (int(width * scale), int(height * scale)), interpolation=cv2.INTER_AREA)
        with self._lock:
            started = time.perf_counter()
            res = self._pipeline.predict(target)[0]
            elapsed_ms = int((time.perf_counter() - started) * 1000)
            release_cached_memory()

        def restore(v: float) -> int:
            return round(float(v) / scale)

        ocr = res["overall_ocr_res"]
        lines = [
            OcrLine(text=text, confidence=round(float(score), 4),
                    bbox=[[restore(x), restore(y)] for x, y in poly])
            for text, score, poly in zip(ocr["rec_texts"], ocr["rec_scores"], ocr["rec_polys"])
            if text.strip()
        ]

        blocks: list[LayoutBlock] = []
        for block in res["parsing_res_list"]:
            if block.label in SKIP_LABELS:
                continue
            bbox = [restore(v) for v in block.bbox]
            if block.label == "table":
                content = _compact_html(block.content)
            else:
                content = _block_text(lines, bbox) or (block.content or "").strip()
            if content:
                blocks.append(LayoutBlock(label=block.label, bbox=bbox, content=content))

        return OcrResult(
            engine=self.name,
            model_version=self.model_version,
            width=width,
            height=height,
            lines=lines,
            avg_confidence=average_confidence(lines),
            elapsed_ms=elapsed_ms,
            blocks=blocks,
            markdown=_markdown(blocks),
        )


def _block_text(lines: list[OcrLine], bbox: list[int]) -> str:
    """블록 영역에 중심이 들어가는 OCR 줄을 위➔아래, 같은 줄은 왼➔오 순으로 (C# ReadingOrder 와 같은 규칙)"""
    x0, y0, x1, y1 = bbox
    inside = []
    for line in lines:
        xs = [p[0] for p in line.bbox]
        ys = [p[1] for p in line.bbox]
        cx, cy = (min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2
        if x0 <= cx <= x1 and y0 <= cy <= y1:
            inside.append((min(ys), max(ys), min(xs), line.text))
    inside.sort()
    rows: list[list] = []
    for top, bottom, left, text in inside:
        if rows and top < (rows[-1][0] + rows[-1][1]) / 2:
            rows[-1][2].append((left, text))
        else:
            rows.append([top, bottom, [(left, text)]])
    return "\n".join(" ".join(t for _, t in sorted(items)) for _, _, items in rows)


def _compact_html(html: str) -> str:
    """LLM 입력용으로 표 HTML 을 줄임 (스타일·html/body 래퍼 제거)"""
    html = re.sub(r"</?(html|body|div)[^>]*>", "", html or "")
    return re.sub(r"\s+", " ", html).strip()


def _markdown(blocks: list[LayoutBlock]) -> str:
    parts = []
    for block in blocks:
        if block.label in TITLE_LABELS:
            parts.append("## " + block.content.replace("\n", " "))
        else:
            parts.append(block.content)
    return "\n\n".join(parts)

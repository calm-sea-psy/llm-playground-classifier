"""OCR 엔진·설정 비교 실행: 같은 이미지를 설정별로 OCR 해서 원본 결과(JSON)를 저장한다. 채점은 score_ocr.py.

변형 (variant)
- paddle_ko        : 서비스 기본 (PP-OCRv5, lang=korean)
- paddle_en        : lang=en (상업송장 = 영어 문서용 후보)
- paddle_ko_unwarp : lang=korean + 문서 왜곡 펴기(use_doc_unwarping)
- paddle_ko_noorient : lang=korean + 줄 방향 보정(use_textline_orientation) 끔 — 똑바른 줄을 180° 뒤집는 경우 확인용
- easyocr_koen     : EasyOCR ['ko','en'] (별도 venv: torch 와 paddle 을 한 venv 에 두지 않음)

모든 변형은 서비스와 같게 8.5MP 초과 이미지를 축소해서 인식하고 bbox 는 원본 좌표로 되돌린다.
(왜곡 펴기는 이미지 자체를 바꾸므로 bbox 가 원본 좌표계와 달라짐 ➔ 채점은 텍스트 기준 지표만 사용)

예)
  src/OcrService/.venv/Scripts/python eval/ocr_variants.py --dataset korie --variant paddle_ko_unwarp
  <easyocr venv>/Scripts/python eval/ocr_variants.py --dataset invoice --variant easyocr_koen
결과: eval/results/ocr_{dataset}_{variant}/{image}.json
"""

import argparse
import csv
import json
import random
import sys
import time
from pathlib import Path

import cv2
import numpy as np

REPO = Path(__file__).resolve().parent.parent
MAX_PIXELS = 8_500_000
DATASETS = {
    "korie": REPO / "data" / "samples" / "korie" / "images",
    "invoice": REPO / "data" / "samples" / "aihub" / "commercial_invoice" / "images",
}


def images_for(dataset: str, limit: int) -> list[Path]:
    root = DATASETS[dataset]
    if dataset == "korie":
        # 필드 정답이 있는 150장
        names = sorted({r["image"] for r in csv.DictReader(open(
            REPO / "data" / "verify" / "korie_fields_labels.csv", encoding="utf-8-sig"))})
        files = {p.stem: p for p in root.iterdir()}
        return [files[n] for n in names if n in files][:limit]
    files = sorted(root.iterdir())
    return sorted(random.Random(42).sample(files, min(limit, len(files))))


def load(path: Path) -> np.ndarray:
    return cv2.imdecode(np.frombuffer(path.read_bytes(), np.uint8), cv2.IMREAD_COLOR)


def shrink(image: np.ndarray):
    h, w = image.shape[:2]
    scale = min(1.0, (MAX_PIXELS / (w * h)) ** 0.5)
    if scale == 1.0:
        return image, 1.0
    return cv2.resize(image, (int(w * scale), int(h * scale)), interpolation=cv2.INTER_AREA), scale


class Paddle:
    def __init__(self, lang: str, unwarp: bool, device: str, orient: bool = True):
        from paddleocr import PaddleOCR
        sys.path.insert(0, str(REPO / "src" / "OcrService"))
        from engines.gpu import release_cached_memory
        self._release = release_cached_memory
        self._ocr = PaddleOCR(lang=lang, device=device, use_textline_orientation=orient,
                              use_doc_orientation_classify=False, use_doc_unwarping=unwarp)
        det = self._ocr.paddlex_pipeline.text_det_model.model_name
        rec = self._ocr.paddlex_pipeline.text_rec_model.model_name
        self.model = f"{det}+{rec}" + ("+UVDoc" if unwarp else "") + ("" if orient else " (줄 방향 보정 끔)")

    def __call__(self, image):
        res = self._ocr.predict(image)[0]
        self._release()
        return [(t, float(s), [[int(x), int(y)] for x, y in p])
                for t, s, p in zip(res["rec_texts"], res["rec_scores"], res["rec_polys"]) if t.strip()]


class EasyOcr:
    def __init__(self, langs: list[str], device: str):
        import easyocr
        self._reader = easyocr.Reader(langs, gpu=device != "cpu")
        self.model = f"easyocr {easyocr.__version__} {'+'.join(langs)}"

    def __call__(self, image):
        return [(t, float(s), [[int(x), int(y)] for x, y in box])
                for box, t, s in self._reader.readtext(image) if t.strip()]


def make(variant: str, device: str):
    return {
        "paddle_ko": lambda: Paddle("korean", False, device),
        "paddle_en": lambda: Paddle("en", False, device),
        "paddle_ko_unwarp": lambda: Paddle("korean", True, device),
        "paddle_ko_noorient": lambda: Paddle("korean", False, device, orient=False),
        "easyocr_koen": lambda: EasyOcr(["ko", "en"], device),
    }[variant]()


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--dataset", choices=DATASETS, required=True)
    p.add_argument("--variant", required=True)
    p.add_argument("--limit", type=int, default=150)
    p.add_argument("--device", default="gpu:0", help="gpu:0 또는 cpu (동작 확인용)")
    p.add_argument("--out-suffix", default="", help="결과 폴더 이름 뒤에 붙일 문자열 (동작 확인용)")
    args = p.parse_args()

    out = REPO / "eval" / "results" / f"ocr_{args.dataset}_{args.variant}{args.out_suffix}"
    out.mkdir(parents=True, exist_ok=True)
    engine = make(args.variant, args.device)
    images = images_for(args.dataset, args.limit)
    engine(load(images[0]))  # 워밍업 (첫 추론의 커널 준비 시간을 제외)

    for i, path in enumerate(images, 1):
        target = out / f"{path.stem}.json"
        if target.exists():
            continue
        image = load(path)
        h, w = image.shape[:2]
        small, scale = shrink(image)
        started = time.perf_counter()
        lines = engine(small)
        elapsed = int((time.perf_counter() - started) * 1000)
        target.write_text(json.dumps({
            "image": path.stem, "variant": args.variant, "model": engine.model, "width": w, "height": h,
            "elapsed_ms": elapsed,
            "lines": [{"text": t, "confidence": round(s, 4), "bbox": [[round(x / scale), round(y / scale)] for x, y in b]}
                      for t, s, b in lines],
        }, ensure_ascii=False), encoding="utf-8")
        print(f"[{i}/{len(images)}] {path.stem} {w}x{h} {len(lines)}줄 {elapsed}ms", flush=True)


if __name__ == "__main__":
    main()

"""KORIE 헤더 필드 정답 만들기.

KORIE detection 라벨(YOLO: class cx cy w h, 텍스트 없음)의 필드 영역을 잘라 OCR 하고,
정규화한 값을 정답 열에 미리 채운 fields 엑셀을 만든다. 사람은 필드 이미지와 대조해 틀린 정답만 고친다.

예) src/OcrService/.venv/Scripts/python src/OcrService/tools/korie_fields.py --limit 150
"""

import argparse
import random
import re
import sys
from datetime import datetime
from pathlib import Path

SERVICE_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = SERVICE_DIR.parent.parent
sys.path.insert(0, str(SERVICE_DIR))

from engines import EngineRegistry  # noqa: E402
from imaging import read_image  # noqa: E402
from verify_excel import FieldVerifyWorkbook  # noqa: E402

# KORIE 클래스 ID ➔ 필드명. 713장에서 클래스별 영역을 OCR 해 내용으로 확인함 (2026-09-24)
#   0~4 = 품목 행(0 품목명, 1 수량, 2 금액, 3 단가, 4 행 전체) ➔ 품목 정답은 items.csv 사용
#   15 = 품목 영역 전체, 16 = 품목 코드, 11 = 전체 2건뿐이라 제외
# 소계(7)는 공급가액: 소계 + 세금 = 합계 (예: 11,817 + 1,183 = 13,000)
FIELD_CLASSES = {
    5: "store_name",
    9: "date",
    10: "time",
    13: "receipt_no",
    7: "subtotal",
    8: "tax",
    6: "total",
    12: "phone",
    14: "address",
}
AMOUNT_FIELDS = {"subtotal", "tax", "total"}


def normalize(field: str, raw: str) -> str:
    text = raw.strip().lstrip(":;").strip()
    if field in AMOUNT_FIELDS:
        # "16, 000" / "12.850" ➔ "16000" / "12850" (영수증 금액은 원 단위 정수)
        sign = "-" if text.startswith("-") else ""
        digits = re.sub(r"\D", "", text)
        return sign + digits if digits else ""
    if field == "date":
        m = re.search(r"(\d{2,4})\D{1,3}(\d{1,2})\D{1,3}(\d{1,2})", text)
        if m:
            y, mo, d = m.groups()
            y = f"20{y}" if len(y) == 2 else y
            return f"{y}-{int(mo):02d}-{int(d):02d}"
        return text
    if field == "time":
        # 초 구분자는 ':' 만 허용 ("21:21/21:21" 처럼 두 시각이 붙은 경우 앞 시각만)
        m = re.search(r"(\d{1,2})[:;.](\d{2})(?::(\d{2}))?", text)
        if m:
            h, mi, sec = m.groups()
            return f"{int(h):02d}:{mi}" + (f":{sec}" if sec else "")
        return text
    if field == "phone":
        return re.sub(r"\s", "", text)
    return text


def reading_order_text(lines) -> str:
    """크롭 안의 줄들을 위➔아래, 같은 줄은 왼쪽➔오른쪽으로 이어 붙임."""
    boxes = []
    for line in lines:
        ys = [p[1] for p in line.bbox]
        boxes.append((min(ys), max(ys), min(p[0] for p in line.bbox), line.text))
    boxes.sort()
    rows: list[list] = []
    for top, bottom, left, text in boxes:
        if rows and top < (rows[-1][0] + rows[-1][1]) / 2:  # 이전 줄의 세로 중앙보다 위에서 시작하면 같은 줄
            rows[-1][2].append((left, text))
        else:
            rows.append([top, bottom, [(left, text)]])
    return " ".join(t for _, _, items in rows for _, t in sorted(items))


def read_labels(path: Path, width: int, height: int) -> dict[str, list[list[int]]]:
    """필드별 bbox(4점). 같은 클래스 박스가 여러 개면 합친 영역."""
    rects: dict[str, list[float]] = {}
    for row in path.read_text().splitlines():
        if not row.strip():
            continue
        cls, cx, cy, w, h = row.split()
        field = FIELD_CLASSES.get(int(cls))
        if field is None:
            continue
        cx, cy, w, h = float(cx) * width, float(cy) * height, float(w) * width, float(h) * height
        x0, y0, x1, y1 = cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2
        if field in rects:
            r = rects[field]
            x0, y0, x1, y1 = min(r[0], x0), min(r[1], y0), max(r[2], x1), max(r[3], y1)
        rects[field] = [x0, y0, x1, y1]
    pad = 4
    result = {}
    for field, (x0, y0, x1, y1) in rects.items():
        x0, y0 = max(int(x0) - pad, 0), max(int(y0) - pad, 0)
        x1, y1 = min(int(x1) + pad, width), min(int(y1) + pad, height)
        result[field] = [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]
    return result


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--korie", type=Path, default=REPO_ROOT / "data" / "samples" / "korie")
    parser.add_argument("--out", type=Path,
                        default=REPO_ROOT / "data" / "verify" / f"korie_fields_{datetime.now():%Y%m%d}.xlsx")
    parser.add_argument("--engine", default=None)
    parser.add_argument("--limit", type=int, default=150, help="라벨링할 영수증 수 (무작위 추출)")
    parser.add_argument("--seed", type=int, default=42)
    args = parser.parse_args()

    images = {p.stem: p for p in (args.korie / "images").iterdir()}
    stems = sorted(s for s in images if (args.korie / "labels" / f"{s}.txt").exists())
    if args.limit < len(stems):
        stems = sorted(random.Random(args.seed).sample(stems, args.limit))

    engine = EngineRegistry().get(args.engine)
    print(f"engine={engine.name} ({engine.model_version}), 영수증 {len(stems)}장 ➔ {args.out}")

    book = FieldVerifyWorkbook(list(FIELD_CLASSES.values()))
    order = {name: i for i, name in enumerate(FIELD_CLASSES.values())}
    for i, stem in enumerate(stems, start=1):
        image = read_image(images[stem])
        height, width = image.shape[:2]
        fields = read_labels(args.korie / "labels" / f"{stem}.txt", width, height)
        for field, bbox in sorted(fields.items(), key=lambda kv: order[kv[0]]):
            (x0, y0), (x1, y1) = bbox[0], bbox[2]
            raw = reading_order_text(engine.run(image[y0:y1, x0:x1]).lines)
            book.add_field(stem, field, raw, normalize(field, raw), image, bbox)
        print(f"[{i}/{len(stems)}] {stem}: 필드 {len(fields)}개")

    args.out.parent.mkdir(parents=True, exist_ok=True)
    book.save(args.out)
    print(f"완료 ➔ {args.out}")


if __name__ == "__main__":
    main()

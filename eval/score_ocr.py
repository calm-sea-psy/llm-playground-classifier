"""OCR 변형 채점 (ocr_variants.py 결과). LLM 없이 OCR 텍스트만 본다.

상업송장 (AI Hub 라벨 = 단어 + bbox)
- 단어 정확도(위치)  : 라벨 단어의 중심을 포함하는 OCR 줄 텍스트 안에 그 단어가 그대로 있는 비율 (대소문자 무시, 공백 제거)
- 단어 재현율(전체)  : 라벨 단어가 문서 전체 OCR 텍스트 어딘가에 있는 비율 (좌표를 쓰지 않음 ➔ 왜곡 펴기에도 적용 가능)
- 문자 오류율(CER)   : 라벨 단어와 가장 비슷한 OCR 줄 부분 문자열의 편집 거리 합 ÷ 라벨 글자 수

KORIE 영수증 (doc/verify 필드 정답: 상호·날짜·시각·번호·금액·전화·주소)
- 필드 값 재현율     : 정답 값(글자·숫자만)이 OCR 텍스트(글자·숫자만)에 그대로 있는 비율
- 문자 오류율(CER)   : 정답 값과 가장 비슷한 OCR 줄(다음 줄과 이은 것 포함) 부분 문자열의 편집 거리 합 ÷ 정답 글자 수
- 스캔(png)과 휴대폰 촬영(jpeg)을 나눠서 표시

예) src/OcrService/.venv/Scripts/python eval/score_ocr.py --dataset korie --variants paddle_ko paddle_ko_unwarp easyocr_koen
"""

import argparse
import csv
import json
import re
import statistics
import sys
from collections import defaultdict
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
KORIE_IMAGES = REPO / "data" / "samples" / "korie" / "images"
INVOICE_LABELS = REPO / "data" / "samples" / "aihub" / "commercial_invoice" / "labels"
FIELD_GROUPS = {
    "금액": {"subtotal", "tax", "total"},
    "날짜·시각": {"date", "time"},
    "상호·주소": {"store_name", "address"},
    "번호·전화": {"receipt_no", "phone"},
}


def norm_word(s: str) -> str:
    return re.sub(r"\s+", "", s).lower()


def norm_alnum(s: str) -> str:
    return "".join(ch for ch in s.lower() if ch.isalnum())


def substring_distance(needle: str, hay: str) -> int:
    """needle 과 hay 의 부분 문자열 사이 최소 편집 거리 (시작·끝 위치는 자유)"""
    if not needle:
        return 0
    prev = [0] * (len(hay) + 1)
    for i, a in enumerate(needle, 1):
        cur = [i] + [0] * len(hay)
        for j, b in enumerate(hay, 1):
            cur[j] = min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (a != b))
        prev = cur
    return min(prev)


def best_distance(needle: str, candidates: list[str]) -> int:
    best = len(needle)
    for c in candidates:
        if best == 0:
            break
        # 길이 차이만으로도 best 이상이면 건너뜀 (후보가 짧을 때만 해당)
        if len(needle) - len(c) >= best:
            continue
        best = min(best, substring_distance(needle, c))
    return best


def load_runs(dataset: str, variant: str) -> dict[str, dict]:
    root = REPO / "eval" / "results" / f"ocr_{dataset}_{variant}"
    return {f.stem: json.loads(f.read_text(encoding="utf-8")) for f in sorted(root.glob("*.json"))}


def common_stats(runs: dict) -> dict:
    confs = [statistics.mean(l["confidence"] for l in r["lines"]) for r in runs.values() if r["lines"]]
    times = [r["elapsed_ms"] for r in runs.values()]
    return {
        "문서": len(runs),
        "평균 신뢰도": f"{statistics.mean(confs):.3f}" if confs else "-",
        "OCR 시간 중앙값": f"{statistics.median(times) / 1000:.2f}초" if times else "-",
        "p90": f"{sorted(times)[int(len(times) * 0.9)] / 1000:.2f}초" if times else "-",
    }


def score_invoice(runs: dict, use_position: bool) -> dict:
    located = recall = total = 0
    dist = chars = 0
    for image, run in runs.items():
        label = json.loads((INVOICE_LABELS / f"{image}.json").read_text(encoding="utf-8"))
        lines = [(norm_word(l["text"]), l["bbox"]) for l in run["lines"]]
        doc = "".join(t for t, _ in lines)
        for b in label["bbox"]:
            word = norm_word(b["data"])
            if len(word) < 2:
                continue
            total += 1
            recall += word in doc
            if use_position:
                cx, cy = sum(b["x"]) / 4, sum(b["y"]) / 4
                hits = [t for t, box in lines
                        if min(p[0] for p in box) - 3 <= cx <= max(p[0] for p in box) + 3
                        and min(p[1] for p in box) - 3 <= cy <= max(p[1] for p in box) + 3]
                located += any(word in t for t in hits)
                dist += best_distance(word, hits) if hits else len(word)
            else:
                dist += best_distance(word, [t for t, _ in lines])
            chars += len(word)
    return {
        "라벨 단어": total,
        "단어 정확도(위치)": f"{located / total:.1%}" if use_position else "해당 없음(좌표 변경)",
        "단어 재현율(전체)": f"{recall / total:.1%}",
        "CER": f"{dist / chars:.2%}",
    }


def korie_labels() -> dict[str, dict[str, str]]:
    labels = defaultdict(dict)
    for row in csv.DictReader(open(REPO / "data" / "verify" / "korie_fields_labels.csv", encoding="utf-8-sig")):
        if row["label"] and row["label"] != "#INVALID" and norm_alnum(row["label"]):
            labels[row["image"]][row["field"]] = row["label"]
    return labels


def score_korie(runs: dict, labels: dict, kind: str | None) -> dict:
    suffix = {p.stem: p.suffix.lower() for p in KORIE_IMAGES.iterdir()}
    found = defaultdict(lambda: [0, 0])
    dist = chars = 0
    for image, run in runs.items():
        if kind == "scan" and suffix.get(image) != ".png":
            continue
        if kind == "photo" and suffix.get(image) == ".png":
            continue
        texts = [norm_alnum(l["text"]) for l in run["lines"]]
        doc = "".join(texts)
        candidates = texts + [a + b for a, b in zip(texts, texts[1:])]
        for field, value in labels.get(image, {}).items():
            v = norm_alnum(value)
            group = next(g for g, fs in FIELD_GROUPS.items() if field in fs)
            for key in (group, "전체"):
                found[key][0] += v in doc
                found[key][1] += 1
            dist += best_distance(v, candidates)
            chars += len(v)
    row = {k: f"{c / n:.1%}" for k, (c, n) in found.items() if n}
    row["CER"] = f"{dist / chars:.2%}" if chars else "-"
    return row


def print_table(rows: list[dict]):
    keys = list(dict.fromkeys(k for r in rows for k in r))
    print("| " + " | ".join(keys) + " |")
    print("|" + "---|" * len(keys))
    for r in rows:
        print("| " + " | ".join(str(r.get(k, "")) for k in keys) + " |")


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--dataset", choices=["korie", "invoice"], required=True)
    p.add_argument("--variants", nargs="+", required=True)
    args = p.parse_args()

    all_runs = {v: load_runs(args.dataset, v) for v in args.variants}
    # 모든 변형에 결과가 있는 이미지만 비교
    shared = set.intersection(*(set(r) for r in all_runs.values()))
    all_runs = {v: {k: r[k] for k in sorted(shared)} for v, r in all_runs.items()}

    if args.dataset == "invoice":
        print_table([{"변형": v, **common_stats(r), **score_invoice(r, use_position="unwarp" not in v)}
                     for v, r in all_runs.items()])
        return

    labels = korie_labels()
    suffix = {p.stem: p.suffix.lower() for p in KORIE_IMAGES.iterdir()}
    for kind, title in ((None, "전체"), ("scan", "스캔 (png)"), ("photo", "휴대폰 촬영 (jpeg)")):
        rows = []
        for v, r in all_runs.items():
            subset = {k: x for k, x in r.items()
                      if kind is None or (kind == "scan") == (suffix.get(k) == ".png")}
            rows.append({"변형": v, **common_stats(subset), **score_korie(subset, labels, None)})
        print(f"\n### {title}\n")
        print_table(rows)


if __name__ == "__main__":
    main()

"""AI Hub 문서(보험 청구서·상업송장) 채점: run_eval.py --dataset insurance_claim|commercial_invoice 결과용.

AI Hub 라벨은 "양식에 기입된 값 + bbox" 뿐이고 필드 이름이 없으며, 양식도 보험사마다 달라(값 19~30개)
필드 단위 정답을 만들 수 없다. 그래서 필드 이름과 무관한 두 지표로 경로 A/B 를 비교한다 (두 경로 모두 같은 스키마).
- 값 재현율: 라벨 값 중 추출 결과(모든 필드 값을 이은 문자열)에 들어간 비율
- 근거율:   추출한 문자열 값 중 라벨 값(이은 문자열)에 들어 있는 비율 ➔ 문서에 없는 값을 지어내지 않았는지

예) src/OcrService/.venv/Scripts/python eval/score_aihub.py --run ic_pathA ic_pathB
"""

import argparse
import json
import re
import statistics
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent


def norm(value) -> str:
    return re.sub(r"[\s,.\-()/:원]", "", str(value)).lower()


def flatten(value) -> list[str]:
    if value is None:
        return []
    if isinstance(value, dict):
        return [s for v in value.values() for s in flatten(v)]
    if isinstance(value, list):
        return [s for v in value for s in flatten(v)]
    return [str(value)]


def meaningful(label: str) -> bool:
    """날짜 조각("5", "16") 같은 1~2자리 숫자는 우연히 들어가기 쉬워 제외"""
    n = norm(label)
    return len(n) >= 3 or (len(n) >= 2 and not n.isdigit())


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--run", nargs="+", required=True)
    args = p.parse_args()

    rows = []
    for run in args.run:
        root = REPO / "eval" / "results" / run
        dataset = json.loads((root / "run.json").read_text(encoding="utf-8"))["dataset"]
        labels_dir = REPO / "data" / "samples" / "aihub" / dataset / "labels"
        for model_dir in sorted((root / "raw").iterdir()):
            recall, grounding, fill, times, types = [], [], [], [], {}
            model = engine = None
            for f in sorted(model_dir.glob("*.json")):
                rec = json.loads(f.read_text(encoding="utf-8"))
                model = rec["model"]
                res = rec.get("result")
                if not res or not rec.get("ocr"):
                    continue
                engine = rec["ocr"]["engine"]
                types[res["documentType"]] = types.get(res["documentType"], 0) + 1
                label = json.loads((labels_dir / f"{rec['image']}.json").read_text(encoding="utf-8"))
                values = [b["data"] for b in label["bbox"] if meaningful(b["data"])]
                extracted = [v for v in flatten(res.get("fields")) if v.strip()]
                haystack = norm(" ".join(extracted))
                truth = norm(" ".join(b["data"] for b in label["bbox"]))
                if values:
                    recall.append(sum(norm(v) in haystack for v in values) / len(values))
                strings = [v for v in extracted if len(norm(v)) >= 2]
                if strings:
                    grounding.append(sum(norm(v) in truth for v in strings) / len(strings))
                schema_fields = [k for k in (res.get("fields") or {})]
                fill.append(sum(1 for k in schema_fields if flatten(res["fields"][k])) / max(1, len(schema_fields)))
                times.append(rec["ocr"]["elapsed_ms"] + res["llmElapsedMs"])
            if model is None:
                continue
            rows.append({
                "run": run, "dataset": dataset, "ocr": engine, "model": model, "docs": len(times),
                "classified": ", ".join(f"{k} {v}" for k, v in sorted(types.items())),
                "value_recall": f"{statistics.mean(recall):.1%}" if recall else "-",
                "grounding": f"{statistics.mean(grounding):.1%}" if grounding else "-",
                "fill_rate": f"{statistics.mean(fill):.1%}" if fill else "-",
                "ocr+llm_ms_median": round(statistics.median(times)) if times else "-",
            })

    keys = list(rows[0].keys())
    print("| " + " | ".join(keys) + " |")
    print("|" + "---|" * len(keys))
    for r in rows:
        print("| " + " | ".join(str(r[k]) for k in keys) + " |")


if __name__ == "__main__":
    main()

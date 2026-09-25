"""배치 평가 2단계: run_eval.py 결과를 KORIE 정답과 비교해 모델별 지표를 계산한다.

정답
- 헤더 필드: data/verify/korie_fields_labels.csv (#INVALID 는 제외)
- 품목: data/samples/korie/items.csv (수량·단가·금액)

출력: eval/results/{run}/fields.csv (문서×필드), items.csv (문서별 품목), summary.csv, summary.md

예) src/OcrService/.venv/Scripts/python eval/score_eval.py --run korie150
"""

import argparse
import csv
import json
import re
import statistics
import sys
from collections import Counter, defaultdict
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
LABELS = REPO / "data" / "verify" / "korie_fields_labels.csv"
ITEMS = REPO / "data" / "samples" / "korie" / "items.csv"

# 파이프라인 영수증 스키마와 KORIE 라벨이 모두 가진 필드 (phone·address 는 스키마에 없음)
FIELDS = ["store_name", "date", "time", "receipt_no", "subtotal", "tax", "total"]
AMOUNT_FIELDS = {"subtotal", "tax", "total"}


def norm_text(v) -> str:
    return re.sub(r"\s+", "", str(v)) if v is not None else ""


def to_amount(v):
    if v is None or v == "":
        return None
    try:
        return round(float(str(v).replace(",", "")))
    except ValueError:
        return None


def field_correct(field: str, label: str, pred) -> bool:
    if pred is None or pred == "":
        return False
    if field in AMOUNT_FIELDS:
        return to_amount(label) == to_amount(pred)
    if field == "time":
        # 인쇄된 정밀도(HH:MM 또는 HH:MM:SS)까지만 비교
        return norm_text(pred)[: len(label)] == label
    return norm_text(label) == norm_text(pred)


def similarity(a: str, b: str) -> float:
    """1 - 정규화 편집 거리 (텍스트 필드가 '얼마나 가까운지')"""
    a, b = norm_text(a), norm_text(b)
    if not a and not b:
        return 1.0
    prev = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        cur = [i]
        for j, cb in enumerate(b, 1):
            cur.append(min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (ca != cb)))
        prev = cur
    return 1 - prev[-1] / max(len(a), len(b))


def item_scores(truth: list[dict], pred: list[dict]):
    """금액 기준 다중집합 매칭 ➔ precision/recall, 매칭된 품목의 수량 정확도"""
    t_amounts = Counter(to_amount(t["Total Price"]) for t in truth)
    p_items = [(to_amount(p.get("amount")), p.get("qty")) for p in pred]
    matched = 0
    remaining = t_amounts.copy()
    for amount, _ in p_items:
        if amount is not None and remaining[amount] > 0:
            remaining[amount] -= 1
            matched += 1
    # 수량: 같은 금액을 가진 정답 품목의 수량과 비교
    t_qty = defaultdict(list)
    for t in truth:
        t_qty[to_amount(t["Total Price"])].append(to_amount(t["Number of units"]))
    qty_ok = qty_n = 0
    for amount, qty in p_items:
        if t_qty.get(amount):
            qty_n += 1
            qty_ok += to_amount(qty) in t_qty[amount]
    return matched, len(pred), len(truth), qty_ok, qty_n


def pct(n, d):
    return f"{n / d:.1%}" if d else "-"


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--run", required=True)
    args = p.parse_args()
    root = REPO / "eval" / "results" / args.run

    labels = defaultdict(dict)
    for row in csv.DictReader(open(LABELS, encoding="utf-8-sig")):
        if row["label"] and row["label"] != "#INVALID":
            labels[row["image"]][row["field"]] = row["label"]
    items_truth = defaultdict(list)
    for row in csv.DictReader(open(ITEMS, encoding="utf-8-sig")):
        items_truth[row["image_name"]].append(row)

    field_rows, item_rows, summary = [], [], []
    for model_dir in sorted((root / "raw").iterdir()):
        records = [json.loads(f.read_text(encoding="utf-8")) for f in sorted(model_dir.glob("*.json"))]
        if not records:  # 아직 실행 중인 모델
            continue
        model = records[0]["model"]
        s = defaultdict(float)
        per_field = defaultdict(lambda: [0, 0, 0.0])  # 정답 수, 비교 수, 유사도 합
        times = defaultdict(list)
        for rec in records:
            img = rec["image"]
            s["docs"] += 1
            res = rec.get("result")
            if rec["job"]["status"] != "Completed" or not res:
                s["job_failed"] += 1
                continue
            attempts = res.get("attempts") or []
            text_attempt = next((a for a in attempts if a["source"] == "text"), None)
            s["classified_receipt"] += res["documentType"] == "receipt"
            s["schema_valid"] += bool(text_attempt and text_attempt["schemaValid"])
            s["validation_passed"] += bool(res.get("validationPassed"))
            s["fallback"] += bool(res.get("fallbackUsed"))
            s["final_vlm"] += res.get("finalSource") == "vlm"
            times["llm_ms"].append(res["llmElapsedMs"])
            times["classify_ms"].append(res["classifyMs"])
            times["ocr_ms"].append(rec["ocr"]["elapsed_ms"])
            if text_attempt:
                times["extract_ms"].append(text_attempt["elapsedMs"])
            job = rec["job"]
            if job.get("startedAt") and job.get("completedAt"):
                from datetime import datetime
                t = lambda x: datetime.fromisoformat(x.replace("Z", "+00:00"))
                times["job_ms"].append((t(job["completedAt"]) - t(job["startedAt"])).total_seconds() * 1000)

            fields = res.get("fields") or {}
            # 오류가 났다면 텍스트 추출 결과로도 채점해 폴백 효과를 따로 봄
            text_fields = (text_attempt or {}).get("fields") or {}
            for field in FIELDS:
                if field not in labels[img]:
                    continue
                label = labels[img][field]
                pred = fields.get(field)
                ok = field_correct(field, label, pred)
                ok_text = field_correct(field, label, text_fields.get(field))
                sim = similarity(label, pred) if field not in AMOUNT_FIELDS else float(ok)
                per_field[field][0] += ok
                per_field[field][1] += 1
                per_field[field][2] += sim
                s["field_ok"] += ok
                s["field_ok_text_only"] += ok_text
                s["field_n"] += 1
                if field in AMOUNT_FIELDS:
                    s["amount_ok"] += ok
                    s["amount_n"] += 1
                field_rows.append({"model": model, "image": img, "field": field, "label": label,
                                   "pred": "" if pred is None else pred, "correct": int(ok),
                                   "correct_text_only": int(ok_text), "similarity": round(sim, 3),
                                   "final_source": res.get("finalSource"), "job_id": job["jobId"]})
            if img in items_truth:
                m, np_, nt, qok, qn = item_scores(items_truth[img], fields.get("items") or [])
                s["item_matched"] += m
                s["item_pred"] += np_
                s["item_truth"] += nt
                s["qty_ok"] += qok
                s["qty_n"] += qn
                item_rows.append({"model": model, "image": img, "truth_items": nt, "pred_items": np_,
                                  "matched_by_amount": m, "qty_ok": qok, "qty_compared": qn})

        done = s["docs"] - s["job_failed"]
        precision = s["item_matched"] / s["item_pred"] if s["item_pred"] else 0
        recall = s["item_matched"] / s["item_truth"] if s["item_truth"] else 0
        row = {
            "model": model,
            "docs": int(s["docs"]),
            "job_failed": int(s["job_failed"]),
            "field_exact": pct(s["field_ok"], s["field_n"]),
            "field_exact_text_only": pct(s["field_ok_text_only"], s["field_n"]),
            "amount_exact": pct(s["amount_ok"], s["amount_n"]),
            **{f"{f}": pct(per_field[f][0], per_field[f][1]) for f in FIELDS},
            "store_name_similarity": f"{per_field['store_name'][2] / per_field['store_name'][1]:.3f}" if per_field["store_name"][1] else "-",
            "item_precision": f"{precision:.1%}",
            "item_recall": f"{recall:.1%}",
            "item_f1": f"{2 * precision * recall / (precision + recall):.1%}" if precision + recall else "-",
            "item_qty_exact": pct(s["qty_ok"], s["qty_n"]),
            "classified_receipt": pct(s["classified_receipt"], done),
            "schema_valid": pct(s["schema_valid"], done),
            "validation_passed": pct(s["validation_passed"], done),
            "fallback_rate": pct(s["fallback"], done),
            "final_from_vlm": pct(s["final_vlm"], done),
            **{f"{k}_median": round(statistics.median(v)) for k, v in times.items() if v},
            **{f"{k}_p90": round(statistics.quantiles(v, n=10)[-1]) for k, v in times.items() if len(v) >= 10},
        }
        summary.append(row)

    def write(name, rows):
        with open(root / name, "w", newline="", encoding="utf-8-sig") as f:
            w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
            w.writeheader()
            w.writerows(rows)

    write("fields.csv", field_rows)
    write("items.csv", item_rows)
    write("summary.csv", summary)

    keys = list(summary[0].keys())[1:]
    md = ["| 지표 | " + " | ".join(r["model"] for r in summary) + " |", "|---|" + "---|" * len(summary)]
    md += [f"| {k} | " + " | ".join(str(r.get(k, "-")) for r in summary) + " |" for k in keys]
    (root / "summary.md").write_text("\n".join(md) + "\n", encoding="utf-8")
    print("\n".join(md))


if __name__ == "__main__":
    main()

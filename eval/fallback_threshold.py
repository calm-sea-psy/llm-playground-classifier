"""VLM 폴백 신뢰도 기준값 분석: 이미 돌린 KORIE 실행 결과(텍스트 시도 + VLM 시도)로 기준값별 결과를 재현한다.

폴백 조건 = 검증 Error 가 있거나 OCR 평균 신뢰도 < 기준값. 실행 당시 기준(0.6)보다 낮은 기준값은
"폴백한 문서의 부분집합"이라 이미 있는 VLM 시도로 정확히 재현된다. 더 높은 기준값은 VLM 시도가 없는
문서가 생기므로 해당 문서 수만 센다 (필요하면 그 문서들만 추가로 실행).

최종 결과 선택은 API(TextJobHandler.ChooseFinal)와 같게: VLM 통과 ➔ VLM, 텍스트 통과 ➔ 텍스트,
둘 다 실패면 오류가 적은 쪽(같으면 텍스트).

예) src/OcrService/.venv/Scripts/python eval/fallback_threshold.py --run pathA_korie
"""

import argparse
import csv
import json
import statistics
import sys
from collections import defaultdict

from score_eval import FIELDS, LABELS, REPO, field_correct

THRESHOLDS = [0.0, 0.5, 0.55, 0.6, 0.65, 0.7, 0.75, 0.8, 0.85, 0.9]


def errors(attempt) -> int:
    return sum(1 for i in attempt.get("issues") or [] if i["severity"] == "Error")


def choose(text, vlm):
    if vlm is None:
        return text
    if errors(vlm) == 0:
        return vlm
    if errors(text) == 0:
        return text
    return vlm if errors(vlm) < errors(text) else text


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--run", required=True)
    p.add_argument("--used", type=float, default=0.6, help="실행 당시 폴백 기준값")
    args = p.parse_args()

    labels = defaultdict(dict)
    for row in csv.DictReader(open(LABELS, encoding="utf-8-sig")):
        if row["label"] and row["label"] != "#INVALID" and row["field"] in FIELDS:
            labels[row["image"]][row["field"]] = row["label"]

    def score(img, attempt):
        truth = labels[img]
        return sum(field_correct(f, v, (attempt.get("fields") or {}).get(f)) for f, v in truth.items()), len(truth)

    docs = []
    for model_dir in sorted((REPO / "eval" / "results" / args.run / "raw").iterdir()):
        for f in sorted(model_dir.glob("*.json")):
            rec = json.loads(f.read_text(encoding="utf-8"))
            res = rec.get("result")
            if not res or rec["image"] not in labels:
                continue
            attempts = res.get("attempts") or []
            text = next((a for a in attempts if a["source"] == "text"), None)
            vlm = next((a for a in attempts if a["source"] == "vlm"), None)
            if text is None:
                continue
            vlm_ms = vlm["elapsedMs"] if vlm else None
            docs.append({
                "model": rec["model"], "img": rec["image"], "conf": rec["ocr"]["avg_confidence"],
                "text": text, "vlm": vlm, "text_err": errors(text), "vlm_ms": vlm_ms,
            })

    for model in sorted({d["model"] for d in docs}):
        ds = [d for d in docs if d["model"] == model]
        confs = sorted(d["conf"] for d in ds)
        print(f"\n## {args.run} · {model} · 문서 {len(ds)}장")
        print(f"OCR 평균 신뢰도: 최소 {confs[0]:.3f} · p10 {confs[len(confs) // 10]:.3f} · "
              f"중앙값 {statistics.median(confs):.3f} · 최대 {confs[-1]:.3f}")

        # 신뢰도 구간별: 텍스트 결과 정확도, 검증 오류 비율
        print("\n| 신뢰도 구간 | 문서 | 텍스트 결과 필드 정확도 | 텍스트 검증 오류 있음 | VLM 시도 있음 | 최종(실제) 정확도 |")
        print("|---|---|---|---|---|---|")
        bins = [(0, 0.8), (0.8, 0.85), (0.85, 0.9), (0.9, 0.95), (0.95, 1.01)]
        for lo, hi in bins:
            b = [d for d in ds if lo <= d["conf"] < hi]
            if not b:
                continue
            tc = sum(score(d["img"], d["text"])[0] for d in b)
            fc = sum(score(d["img"], choose(d["text"], d["vlm"]))[0] for d in b)
            n = sum(score(d["img"], d["text"])[1] for d in b)
            print(f"| {lo:.2f}~{min(hi, 1):.2f} | {len(b)} | {tc / n:.1%} | {sum(d['text_err'] > 0 for d in b)} | "
                  f"{sum(d['vlm'] is not None for d in b)} | {fc / n:.1%} |")

        print("\n| 기준값 | 폴백 문서 | 신뢰도 때문에만 폴백 | VLM 시도가 없어 재현 불가 | 필드 정확도 | VLM 추가 시간(합) |")
        print("|---|---|---|---|---|---|")
        for t in THRESHOLDS:
            fallback = [d for d in ds if d["text_err"] > 0 or d["conf"] < t]
            conf_only = [d for d in fallback if d["text_err"] == 0]
            missing = [d for d in fallback if d["vlm"] is None]
            if missing:
                acc = "—"
            else:
                correct = total = 0
                for d in ds:
                    final = choose(d["text"], d["vlm"]) if d in fallback else d["text"]
                    c, n = score(d["img"], final)
                    correct, total = correct + c, total + n
                acc = f"{correct / total:.1%}"
            vlm_sec = sum(d["vlm_ms"] or 0 for d in fallback) / 1000
            mark = " (실행 당시)" if abs(t - args.used) < 1e-9 else ""
            print(f"| {t:.2f}{mark} | {len(fallback)} | {len(conf_only)} | {len(missing)} | {acc} | "
                  f"{vlm_sec:.0f}초{'+' if missing else ''} |")

        # 기준값을 올릴 때 새로 폴백 대상이 되는 문서 (검증 통과인데 신뢰도가 낮은 문서)
        extra = sorted((d for d in ds if d["text_err"] == 0 and d["vlm"] is None and d["conf"] < 0.9),
                       key=lambda d: d["conf"])
        if extra:
            print("\n신뢰도 0.9 미만이지만 검증을 통과해 폴백하지 않은 문서 (기준값을 올리면 새로 폴백):")
            for d in extra:
                c, n = score(d["img"], d["text"])
                print(f"  {d['img']}  신뢰도 {d['conf']:.3f}  텍스트 결과 {c}/{n}")


if __name__ == "__main__":
    main()

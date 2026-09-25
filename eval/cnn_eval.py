"""2차-1 CNN 모델 선정·판정 기준값 보정 (CNN 서비스 /classify 를 실제로 호출).

데이터 (src/CnnService/tools/splits.py, 환자 기준 분리)
- val     : Kaggle train 의 환자 10% (미세조정 모델의 검증 세트)
- calib   : Kaggle test 환자 절반 ➔ 기준값 보정용
- holdout : Kaggle test 나머지 절반 ➔ 최종 평가 (기준값을 정할 때 한 번도 보지 않음)

기준값을 정하는 방법 비교 (모두 holdout 에서 평가)
- 고정 0.5
- val 기준  : val 에서 민감도 95% 이상을 지키는 가장 높은 기준값 (학습 분포)
- calib 기준: calib 에서 민감도 95% 이상을 지키는 가장 높은 기준값 (테스트 분포)
- calib 균형: calib 에서 민감도 + 특이도가 최대인 기준값 (Youden)
의료 선별 용도라 "놓치지 않기(민감도)"를 우선으로 둠

예) src/CnnService/.venv/Scripts/python eval/cnn_eval.py            # 호출 + 채점
    src/CnnService/.venv/Scripts/python eval/cnn_eval.py --score    # 저장된 확률로 채점만
"""

import argparse
import json
import sys
import urllib.request
import uuid
from pathlib import Path

import numpy as np
from sklearn.metrics import roc_auc_score

REPO = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO / "src" / "CnnService"))
from tools.splits import calib_holdout, train_val  # noqa: E402

OUT = REPO / "eval" / "results" / "cnn_eval"
TARGET_SENSITIVITY = 0.95
# 엔진별로 폐렴 판정에 쓸 수 있는 소견 (xrv 는 폐렴 자체보다 경화·음영이 더 잘 맞았음 — 2차-0)
SCORES = {"pneumonia": ["Pneumonia"], "xrv": ["Pneumonia", "Consolidation", "Lung Opacity", "Infiltration"]}


def classify(path: Path, engine: str, api: str) -> dict:
    boundary = uuid.uuid4().hex
    body = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{path.name}\"\r\n"
            f"Content-Type: image/jpeg\r\n\r\n").encode() + path.read_bytes() + f"\r\n--{boundary}--\r\n".encode()
    req = urllib.request.Request(f"{api}/classify?engine={engine}&heatmap=false", data=body,
                                 headers={"Content-Type": f"multipart/form-data; boundary={boundary}"})
    with urllib.request.urlopen(req, timeout=60) as res:
        return json.loads(res.read())


def collect(engine: str, api: str):
    splits = {"val": train_val()[1], **dict(zip(("calib", "holdout"), calib_holdout()))}
    records = []
    for name, items in splits.items():
        for i, (path, label) in enumerate(items, 1):
            r = classify(path, engine, api)
            records.append({"split": name, "image": path.name, "label": label, "elapsed_ms": r["elapsed_ms"],
                            "probs": {f["label"]: f["probability"] for f in r["findings"]}})
            if i % 100 == 0:
                print(f"  {engine} {name} {i}/{len(items)}", flush=True)
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / f"{engine}.json").write_text(json.dumps(records, ensure_ascii=False), encoding="utf-8")


def rates(y: np.ndarray, p: np.ndarray, t: float):
    pred = p >= t
    return (pred & (y == 1)).sum() / (y == 1).sum(), (~pred & (y == 0)).sum() / (y == 0).sum()


def threshold_for_sensitivity(y, p, target):
    # 민감도 target 이상을 지키는 가장 높은 기준값 (특이도를 최대한 살림)
    candidates = [t for t in np.unique(p) if rates(y, p, t)[0] >= target]
    return float(max(candidates)) if candidates else 0.0


def youden(y, p):
    return float(max(np.unique(p), key=lambda t: sum(rates(y, p, t))))


def score(engine: str):
    records = json.loads((OUT / f"{engine}.json").read_text(encoding="utf-8"))
    get = lambda split, label: (np.array([r["label"] for r in records if r["split"] == split]),  # noqa: E731
                                np.array([r["probs"].get(label, np.nan) for r in records if r["split"] == split]))
    times = [r["elapsed_ms"] for r in records]
    print(f"\n## {engine} · 추론 중앙값 {np.median(times):.0f}ms · val {sum(r['split'] == 'val' for r in records)}"
          f" / calib {sum(r['split'] == 'calib' for r in records)} / holdout {sum(r['split'] == 'holdout' for r in records)}장")
    print("\n| 소견 | val AUC | test AUC (calib+holdout) | holdout AUC |")
    print("|---|---|---|---|")
    for label in SCORES[engine]:
        (vy, vp), (cy, cp), (hy, hp) = get("val", label), get("calib", label), get("holdout", label)
        print(f"| {label} | {roc_auc_score(vy, vp):.3f} | {roc_auc_score(np.r_[cy, hy], np.r_[cp, hp]):.3f} | "
              f"{roc_auc_score(hy, hp):.3f} |")
    print("\n| 소견 | 기준값 방법 | 기준값 | holdout 민감도 | holdout 특이도 |")
    print("|---|---|---|---|---|")
    for label in SCORES[engine]:
        (vy, vp), (cy, cp), (hy, hp) = get("val", label), get("calib", label), get("holdout", label)
        for method, t in (("고정 0.5", 0.5),
                          (f"val 민감도≥{TARGET_SENSITIVITY:.0%}", threshold_for_sensitivity(vy, vp, TARGET_SENSITIVITY)),
                          (f"calib 민감도≥{TARGET_SENSITIVITY:.0%}", threshold_for_sensitivity(cy, cp, TARGET_SENSITIVITY)),
                          ("calib 균형(Youden)", youden(cy, cp))):
            sens, spec = rates(hy, hp, t)
            print(f"| {label} | {method} | {t:.4f} | {sens:.1%} | {spec:.1%} |")


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--engines", nargs="+", default=["pneumonia", "xrv"])
    p.add_argument("--api", default="http://127.0.0.1:8002")
    p.add_argument("--score", action="store_true", help="호출 없이 저장된 확률로 채점만")
    args = p.parse_args()
    for engine in args.engines:
        if not args.score:
            collect(engine, args.api)
        score(engine)


if __name__ == "__main__":
    main()

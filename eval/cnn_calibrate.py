"""xrv 18소견 기준값을 IU X-Ray 성인 데이터로 소견별로 다시 잡는다 (3차 리뷰: 0.5 일괄 기준이면 정상 영상에도 양성이 많이 붙음).

xrv 출력은 원래 학습 데이터의 운영 기준(op_threshs)이 0.5 가 되도록 바뀐 값인데, IU 에서는 양성 판정의 절반 이상이 0.50~0.52 에 몰림.

단계
  run   보정 세트(2차-5 평가 120건·X-ray 실험 영상을 뺀 IU 정면 영상)에 xrv 를 돌려 18소견 확률 저장
  fit   소견별 기준값: 양성 30건 이상이면 Youden J(민감도+특이도-1) 최대, 적으면 정상 영상의 98% 가 음성이 되는 값.
        둘 다 0.5 아래로는 내리지 않음 (목적이 잡음 줄이기)
  eval  2차-5 의 120건(보정에 안 씀)에서 전후 비교: 소견별 민감도·특이도, 정상 영상의 양성 수, 소견서 vs CNN 불일치

사용 (CNN 서비스 가상 환경):
  src/CnnService/.venv/Scripts/python eval/cnn_calibrate.py run --limit 1500
  src/CnnService/.venv/Scripts/python eval/cnn_calibrate.py fit
  src/CnnService/.venv/Scripts/python eval/cnn_calibrate.py eval
결과: eval/results/cnn_calib/ (probs.csv, thresholds.json, eval.md)
"""
import argparse
import csv
import json
import random
import sys
from pathlib import Path

import numpy as np

sys.stdout.reconfigure(encoding="utf-8")
REPO = Path(__file__).resolve().parent.parent
IU = REPO / "data" / "samples" / "iu-xray"
OUT = REPO / "eval" / "results" / "cnn_calib"

# IU MeSH(Problems) ➔ xrv 소견. 경화(Consolidation)는 성인 폐렴 신호로 쓰므로 폐렴성 음영 전체를 정답으로
MESH = {
    "Atelectasis": {"Pulmonary Atelectasis"},
    "Cardiomegaly": {"Cardiomegaly"},
    "Effusion": {"Pleural Effusion"},
    "Edema": {"Pulmonary Edema"},
    "Emphysema": {"Emphysema", "Pulmonary Emphysema"},
    "Pneumonia": {"Pneumonia"},
    "Consolidation": {"Consolidation", "Pneumonia", "Airspace Disease", "Infiltrate"},
    "Infiltration": {"Infiltrate"},
    "Nodule": {"Nodule"},
    "Mass": {"Mass"},
    "Lung Lesion": {"Nodule", "Mass"},
    "Fracture": {"Fractures, Bone"},
    "Hernia": {"Hernia, Hiatal"},
    "Pneumothorax": {"Pneumothorax"},
    "Fibrosis": {"Fibrosis", "Pulmonary Fibrosis"},
    "Pleural_Thickening": {"Pleural Thickening", "Thickening"},
    "Lung Opacity": {"Opacity", "Density", "Airspace Disease", "Infiltrate", "Consolidation"},
    "Enlarged Cardiomediastinum": {"Cardiomegaly", "Mediastinum"},
}
MIN_POSITIVES = 30
NORMAL_SPECIFICITY = 0.98
# 소견서 요약(2차-5)의 소견 ➔ CNN 소견 (폐렴은 성인 = 경화)
CONCORDANCE = {"pneumonia": "Consolidation", "cardiomegaly": "Cardiomegaly", "effusion": "Effusion",
               "atelectasis": "Atelectasis", "edema": "Edema"}


def reports():
    rows = {r["uid"]: r for r in csv.DictReader(open(IU / "indiana_reports.csv", encoding="utf-8"))}
    front = {}
    for r in csv.DictReader(open(IU / "indiana_projections.csv", encoding="utf-8")):
        if r["projection"] == "Frontal":
            front.setdefault(r["uid"], r["filename"])
    return rows, front


def problems(row) -> set[str]:
    return {p.strip() for p in row["Problems"].split(";")}


def excluded_uids() -> set[str]:
    """평가·실험에 쓴 영상: 2차-5 120건 + X-ray 실험(성인 IU) 영상"""
    used = {f.stem for f in (REPO / "eval" / "results" / "mm_iu" / "raw" / "gemma4_12b").glob("*.json")}
    for lst in (REPO / "eval" / "bench").glob("*iu*.txt"):
        used |= {Path(line.strip()).name.split("_")[0] for line in lst.read_text(encoding="utf-8").splitlines() if line.strip()}
    return used


def cmd_run(limit: int):
    sys.path.insert(0, str(REPO / "src" / "CnnService"))
    from engines.xrv_engine import XrvEngine
    from imaging import read_gray

    rows, front = reports()
    skip = excluded_uids() | experiment_uids()
    uids = sorted(u for u in front if u not in skip and rows.get(u) and rows[u]["impression"].strip())
    uids = sorted(random.Random(11).sample(uids, min(limit, len(uids))))
    engine = XrvEngine("xrv", device="cuda")
    OUT.mkdir(parents=True, exist_ok=True)
    with open(OUT / "probs.csv", "w", newline="", encoding="utf-8") as f:
        w = None
        for i, uid in enumerate(uids):
            image = read_gray(IU / "images" / "images_normalized" / front[uid])
            result = engine.run(image, heatmap=False)
            probs = {x.label: x.probability for x in result.findings}
            if w is None:
                w = csv.DictWriter(f, fieldnames=["uid", *sorted(probs)])
                w.writeheader()
            w.writerow({"uid": uid, **probs})
            if (i + 1) % 100 == 0:
                print(f"{i + 1}/{len(uids)}", flush=True)
    print(f"보정 세트 {len(uids)}장 (제외 {len(skip)}장) ➔ {OUT / 'probs.csv'}")


def experiment_uids() -> set[str]:
    """API 로 돌린 X-ray 실험의 IU 영상 (파일 이름 = uid_...)"""
    import urllib.request
    try:
        exps = json.load(urllib.request.urlopen("http://127.0.0.1:5000/api/image/experiments"))
        uids = set()
        for e in exps:
            d = json.load(urllib.request.urlopen(f"http://127.0.0.1:5000/api/image/experiments/{e['id']}"))
            uids |= {doc["fileName"].split("_")[0] for doc in d["docs"]}
        return uids
    except Exception:  # API 가 꺼져 있으면 2차-5 목록만
        return set()


def load_probs():
    rows, _ = reports()
    data = list(csv.DictReader(open(OUT / "probs.csv", encoding="utf-8")))
    labels = [k for k in data[0] if k != "uid"]
    probs = {lab: np.array([float(r[lab]) for r in data]) for lab in labels}
    truth = [problems(rows[r["uid"]]) for r in data]
    return labels, probs, truth


def youden(scores, positive):
    best, best_t = -1.0, 0.5
    for t in np.unique(np.round(scores, 3)):
        pred = scores >= t
        sens = (pred & positive).sum() / max(1, positive.sum())
        spec = (~pred & ~positive).sum() / max(1, (~positive).sum())
        if sens + spec - 1 > best:
            best, best_t = sens + spec - 1, float(t)
    return best_t


def cmd_fit():
    labels, probs, truth = load_probs()
    normal = np.array([t == {"normal"} for t in truth])
    thresholds, lines = {}, ["| 소견 | 양성 수 | 방법 | 기준값 | 보정 세트 민감도·특이도 (0.5 ➔ 새 기준) |", "|---|---|---|---|---|"]
    for lab in sorted(labels):
        s = probs[lab]
        pos = np.array([bool(t & MESH.get(lab, set())) for t in truth])
        if pos.sum() >= MIN_POSITIVES:
            t, how = youden(s, pos), "Youden"
        else:
            t, how = float(np.quantile(s[normal], NORMAL_SPECIFICITY)), f"정상 {NORMAL_SPECIFICITY:.0%} 음성"
        t = round(max(0.5, t), 3)
        thresholds[lab] = t

        def rates(th):
            pred = s >= th
            sens = (pred & pos).sum() / pos.sum() if pos.sum() else float("nan")
            spec = (~pred & ~pos).sum() / (~pos).sum()
            return f"{sens:.0%}/{spec:.0%}" if pos.sum() else f"—/{spec:.0%}"
        lines.append(f"| {lab} | {int(pos.sum())} | {how} | {t} | {rates(0.5)} ➔ {rates(t)} |")
    (OUT / "thresholds.json").write_text(json.dumps(thresholds, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"보정 세트 {len(truth)}장 (정상 {int(normal.sum())})\n" + "\n".join(lines))
    print("\nconfig.toml 용:\nthresholds = { default = 0.5, " + ", ".join(
        f'"{k}" = {v}' for k, v in thresholds.items() if v != 0.5) + " }")


def cmd_eval():
    thresholds = json.loads((OUT / "thresholds.json").read_text(encoding="utf-8"))
    rows, _ = reports()
    recs = [json.loads(f.read_text(encoding="utf-8")) for f in (REPO / "eval" / "results" / "mm_iu" / "raw" / "gemma4_12b").glob("*.json")]
    out = [f"# xrv 소견별 기준값 전후 비교 (2차-5 IU 성인 {len(recs)}건, 보정에 쓰지 않은 세트)\n"]

    def positives(rec, new):
        return {f["label"] for f in rec["image"]["findings"]["findings"]
                if f["probability"] >= (thresholds.get(f["label"], 0.5) if new else 0.5)}

    # 1) 정상 영상의 양성 소견
    normals = [r for r in recs if r["truth"]["normal"]]
    out.append("## 정상 영상의 양성 소견\n\n| | 0.5 일괄 | 소견별 기준 |\n|---|---|---|")
    for name, fn in [("양성 소견이 1개 이상인 정상 영상", lambda n: sum(1 for r in normals if positives(r, n))),
                     ("정상 영상당 양성 소견 수 (평균)", lambda n: round(np.mean([len(positives(r, n)) for r in normals]), 2))]:
        out.append(f"| {name} (정상 {len(normals)}장) | {fn(False)} | {fn(True)} |")

    # 2) 소견별 민감도·특이도 (MeSH 정답)
    out.append("\n## 소견별 민감도 / 특이도 (MeSH 정답이 있는 소견)\n\n| 소견 | 양성 | 0.5 일괄 | 소견별 기준 |\n|---|---|---|---|")
    for lab in sorted(MESH):
        pos = [bool(problems(rows[r["uid"]]) & MESH[lab]) for r in recs]
        if sum(pos) == 0:
            continue

        def rate(new):
            pred = [lab in positives(r, new) for r in recs]
            tp = sum(p and t for p, t in zip(pred, pos))
            tn = sum(not p and not t for p, t in zip(pred, pos))
            return f"{tp / sum(pos):.0%} / {tn / (len(pos) - sum(pos)):.0%}"
        out.append(f"| {lab} | {sum(pos)} | {rate(False)} | {rate(True)} |")

    # 3) 소견서 요약 vs CNN 불일치 (2차-5 의 LLM 요약 그대로, CNN 판정만 새 기준으로)
    out.append("\n## 소견서 vs CNN 불일치 (소견서 = 2차-5 LLM 요약)\n")
    summary = {}
    for new in (False, True):
        jobs_with, total, real = 0, 0, 0
        for r in recs:
            ms = (r["mm"].get("reportSummary") or {}).get("mentions")
            if not ms:
                continue
            pos = positives(r, new)
            n = 0
            for key, lab in CONCORDANCE.items():
                if bool(ms[key]) != (lab in pos):
                    n += 1
                    # 불일치가 실제 CNN 오답인지 (MeSH 정답 기준)
                    truth_key = bool(r["truth"][key])
                    real += (lab in pos) != truth_key
            total += n
            jobs_with += n > 0
        summary[new] = (jobs_with, total, real)
    n_jobs = sum(1 for r in recs if (r["mm"].get("reportSummary") or {}).get("mentions"))
    out.append(f"| | 0.5 일괄 | 소견별 기준 |\n|---|---|---|")
    out.append(f"| 불일치가 1개 이상인 작업 ({n_jobs}건) | {summary[False][0]} ({summary[False][0] / n_jobs:.0%}) | {summary[True][0]} ({summary[True][0] / n_jobs:.0%}) |")
    out.append(f"| 불일치 소견 수 | {summary[False][1]} | {summary[True][1]} |")
    out.append(f"| 그중 실제 CNN 오답 (MeSH 기준) | {summary[False][2]} | {summary[True][2]} |")

    # 4) 성인 폐렴 신호 (= 경화)
    pos = [bool(r["truth"]["pneumonia"]) for r in recs]
    out.append("\n## 성인 폐렴 신호 (경화 소견)\n\n| | 0.5 일괄 | 소견별 기준 |\n|---|---|---|")
    for name, new in [("민감도 / 특이도", None)]:
        cells = []
        for n in (False, True):
            pred = ["Consolidation" in positives(r, n) for r in recs]
            tp = sum(p and t for p, t in zip(pred, pos))
            tn = sum(not p and not t for p, t in zip(pred, pos))
            cells.append(f"{tp / sum(pos):.0%} / {tn / (len(pos) - sum(pos)):.0%}")
        out.append(f"| {name} | {cells[0]} | {cells[1]} |")
    text = "\n".join(out)
    (OUT / "eval.md").write_text(text, encoding="utf-8")
    print(text)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("step", choices=["run", "fit", "eval"])
    ap.add_argument("--limit", type=int, default=1500)
    a = ap.parse_args()
    {"run": lambda: cmd_run(a.limit), "fit": cmd_fit, "eval": cmd_eval}[a.step]()

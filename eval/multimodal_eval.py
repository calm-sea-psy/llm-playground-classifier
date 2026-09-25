"""2차-5 IU X-Ray 통합 평가: X-ray + 소견서를 통합 API(/api/multimodal/jobs)로 실제 처리하고 IU 라벨(MeSH Problems)로 채점.

표본 (seed 42): 정상 40명 + 대상 소견(심장비대·흉수·무기폐·폐부종·폐렴성 음영) 중 하나 이상 있는 80명, 환자당 정면 1장.
소견서 = INDICATION / COMPARISON / FINDINGS / IMPRESSION 원문 (OCR 로 읽었다고 가정한 텍스트)

보는 것
1) 소견서 요약(LLM): 소견별 언급(mentions) vs MeSH ➔ 정밀도·재현율·F1, 정상 판단 정확도
2) CNN: 소견별 AUC·민감도·특이도 (폐렴은 성인 폐렴 신호 = xrv 경화)
3) VLM(독립 판독): 폐렴 의심·이상 소견 판단의 민감도·특이도
4) 통합: 이상 소견을 CNN·VLM 중 한쪽만 잡은 경우, 둘 중 하나라도 잡은 비율, CNN 폐렴 오답을 불일치로 적발한 비율

예) src/OcrService/.venv/Scripts/python eval/multimodal_eval.py --run mm_iu --models gemma4:12b qwen3-vl:8b-instruct
    src/OcrService/.venv/Scripts/python eval/multimodal_eval.py --run mm_iu --score
결과: eval/results/{run}/raw/{모델}/{uid}.json (git 제외)
"""

import argparse
import csv
import json
import random
import statistics
import sys
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
IU = REPO / "data" / "samples" / "iu-xray"
API = "http://127.0.0.1:5000"
TARGETS = {
    "pneumonia": {"Pneumonia", "Airspace Disease", "Infiltrate"},
    "cardiomegaly": {"Cardiomegaly"},
    "effusion": {"Pleural Effusion"},
    "atelectasis": {"Pulmonary Atelectasis"},
    "edema": {"Pulmonary Edema"},
}
CNN_LABEL = {"cardiomegaly": "Cardiomegaly", "effusion": "Effusion", "atelectasis": "Atelectasis", "edema": "Edema"}


def sample(normal: int, abnormal: int, seed: int):
    reports = {r["uid"]: r for r in csv.DictReader(open(IU / "indiana_reports.csv", encoding="utf-8"))}
    front = {}
    for r in csv.DictReader(open(IU / "indiana_projections.csv", encoding="utf-8")):
        if r["projection"] == "Frontal":
            front.setdefault(r["uid"], r["filename"])
    normals, positives = [], []
    for uid, r in reports.items():
        if uid not in front or not r["impression"].strip():
            continue
        problems = {p.strip() for p in r["Problems"].split(";")}
        truth = {k: int(bool(problems & v)) for k, v in TARGETS.items()}
        truth["normal"] = int(problems == {"normal"})
        item = {"uid": uid, "image": str(IU / "images" / "images_normalized" / front[uid]), "truth": truth,
                "report": f"INDICATION: {r['indication']}\nCOMPARISON: {r['comparison']}\n"
                          f"FINDINGS: {r['findings']}\nIMPRESSION: {r['impression']}"}
        if truth["normal"]:
            normals.append(item)
        elif any(truth[k] for k in TARGETS):
            positives.append(item)
    rng = random.Random(seed)
    return rng.sample(normals, normal) + rng.sample(positives, abnormal)


def post_job(item, model: str) -> str:
    boundary = uuid.uuid4().hex
    parts = []
    image = Path(item["image"])
    parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="xray"; filename="{image.name}"\r\n'
                 f"Content-Type: image/png\r\n\r\n".encode() + image.read_bytes() + b"\r\n")
    for name, value in (("reportText", item["report"]), ("population", "adult"), ("model", model)):
        parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode())
    body = b"".join(parts) + f"--{boundary}--\r\n".encode()
    req = urllib.request.Request(f"{API}/api/multimodal/jobs", data=body,
                                 headers={"Content-Type": f"multipart/form-data; boundary={boundary}"})
    with urllib.request.urlopen(req, timeout=60) as res:
        return json.loads(res.read())["jobId"]


def get(path: str):
    with urllib.request.urlopen(f"{API}{path}", timeout=60) as res:
        return json.loads(res.read())


def run(run_name: str, models: list[str], items):
    for model in models:
        out = REPO / "eval" / "results" / run_name / "raw" / model.replace(":", "_")
        out.mkdir(parents=True, exist_ok=True)
        pending = {}
        for item in items:
            if not (out / f"{item['uid']}.json").exists():
                pending[post_job(item, model)] = item
        print(f"{model}: {len(pending)}건 제출", flush=True)
        started = time.time()
        while pending:
            time.sleep(5)
            for job_id, item in list(pending.items()):
                job = get(f"/api/jobs/{job_id}")
                if job["status"] not in ("Completed", "Failed"):
                    continue
                record = {"uid": item["uid"], "truth": item["truth"], "report": item["report"], "job": job}
                if job["status"] == "Completed":
                    record["mm"] = get(f"/api/multimodal/jobs/{job_id}/result")
                    record["image"] = get(f"/api/image/jobs/{job_id}/result")
                (out / f"{item['uid']}.json").write_text(json.dumps(record, ensure_ascii=False), encoding="utf-8")
                del pending[job_id]
            print(f"  남은 {len(pending)}건 ({time.time() - started:.0f}초)", flush=True)


# ---- 채점 ----

def auc(pairs):
    pos = [p for p, y in pairs if y]
    neg = [p for p, y in pairs if not y]
    if not pos or not neg:
        return None
    wins = sum(1 if p > n else 0.5 if p == n else 0 for p in pos for n in neg)
    return wins / (len(pos) * len(neg))


def rates(pairs):
    """pairs = [(예측 bool, 정답 bool)] ➔ 민감도, 특이도, 정밀도"""
    tp = sum(p and y for p, y in pairs)
    fn = sum(not p and y for p, y in pairs)
    tn = sum(not p and not y for p, y in pairs)
    fp = sum(p and not y for p, y in pairs)
    div = lambda a, b: a / b if b else None  # noqa: E731
    return div(tp, tp + fn), div(tn, tn + fp), div(tp, tp + fp)


def pct(v):
    return "—" if v is None else f"{v:.0%}"


def f1(p, r):
    return None if p is None or r is None or p + r == 0 else 2 * p * r / (p + r)


def score(run_name: str):
    root = REPO / "eval" / "results" / run_name / "raw"
    for model_dir in sorted(root.iterdir()):
        recs = [json.loads(f.read_text(encoding="utf-8")) for f in sorted(model_dir.glob("*.json"))]
        ok = [r for r in recs if r["job"]["status"] == "Completed"]
        print(f"\n## {model_dir.name} · {len(recs)}명 (완료 {len(ok)}, 실패 {len(recs) - len(ok)})")

        # 1) 소견서 요약
        summaries = [r for r in ok if r["mm"].get("reportSummary")]
        print(f"\n### 소견서 요약 (LLM) — 형식 성공 {len(summaries)}/{len(ok)}")
        print("| 소견 | 양성 | 정밀도 | 재현율 | F1 |")
        print("|---|---|---|---|---|")
        for k in TARGETS:
            pairs = [(bool(r["mm"]["reportSummary"]["mentions"].get(k)), bool(r["truth"][k])) for r in summaries]
            sens, _, prec = rates(pairs)
            print(f"| {k} | {sum(y for _, y in pairs)} | {pct(prec)} | {pct(sens)} | {pct(f1(prec, sens))} |")
        normal_acc = statistics.mean(r["mm"]["reportSummary"]["normal"] == bool(r["truth"]["normal"]) for r in summaries)
        print(f"정상 판단 정확도 {normal_acc:.0%}")

        # 2) CNN
        print("\n### CNN (성인: 폐렴 = xrv 경화, 나머지 = xrv 소견)")
        print("| 소견 | AUC | 민감도 | 특이도 |")
        print("|---|---|---|---|")
        for k in TARGETS:
            probs, preds = [], []
            for r in ok:
                img = r["image"]
                if k == "pneumonia":
                    p, pos = img.get("pneumoniaProbability"), img.get("pneumoniaPositive")
                else:
                    f = next((x for x in img["findings"]["findings"] if x["label"] == CNN_LABEL[k]), None)
                    p, pos = (f["probability"], f["positive"]) if f else (None, None)
                if p is not None:
                    probs.append((p, bool(r["truth"][k])))
                    preds.append((bool(pos), bool(r["truth"][k])))
            sens, spec, _ = rates(preds)
            print(f"| {k} | {auc(probs):.3f} | {pct(sens)} | {pct(spec)} |")

        # 3) VLM
        vlm = [r for r in ok if r["image"].get("report")]
        pn = rates([(bool(r["image"]["report"]["pneumonia_suspected"]), bool(r["truth"]["pneumonia"])) for r in vlm])
        ab = rates([(not r["image"]["report"]["normal"], not r["truth"]["normal"]) for r in vlm])
        print(f"\n### VLM 독립 판독 — 성공 {len(vlm)}/{len(ok)}")
        print(f"폐렴 의심: 민감도 {pct(pn[0])} · 특이도 {pct(pn[1])} / 이상 소견: 민감도 {pct(ab[0])} · 특이도 {pct(ab[1])}")

        # 4) 통합: 이상 소견(정상 아님)을 누가 잡았나
        both = only_cnn = only_vlm = none = 0
        for r in vlm:
            if r["truth"]["normal"]:
                continue
            cnn_hit = any(x["positive"] for x in r["image"]["findings"]["findings"]
                          if x["label"] in CNN_LABEL.values()) or bool(r["image"].get("pneumoniaPositive"))
            vlm_hit = not r["image"]["report"]["normal"]
            both += cnn_hit and vlm_hit
            only_cnn += cnn_hit and not vlm_hit
            only_vlm += vlm_hit and not cnn_hit
            none += not cnn_hit and not vlm_hit
        abnormal = both + only_cnn + only_vlm + none
        print(f"\n### 통합: 이상 소견 {abnormal}명을 누가 잡았나 (CNN = 대상 소견 5개 중 양성)")
        print(f"둘 다 {both} · CNN 만 {only_cnn} · VLM 만 {only_vlm} · 둘 다 놓침 {none} ➔ 한쪽이라도 잡음 {(abnormal - none) / abnormal:.0%}"
              f" (CNN 단독 {(both + only_cnn) / abnormal:.0%}, VLM 단독 {(both + only_vlm) / abnormal:.0%})")
        wrong = [r for r in vlm if bool(r["image"].get("pneumoniaPositive")) != bool(r["truth"]["pneumonia"])]
        flagged = sum(bool(r["image"]["report"]["pneumonia_suspected"]) != bool(r["image"].get("pneumoniaPositive")) for r in wrong)
        print(f"CNN 폐렴 오답 {len(wrong)}건 중 VLM 불일치로 적발 {flagged}건")
        needs = sum(r["mm"]["needsReview"] for r in ok)
        print(f"사람 확인 필요로 표시 {needs}/{len(ok)}")
        secs = [(__import__('datetime').datetime.fromisoformat(r["job"]["completedAt"]) -
                 __import__('datetime').datetime.fromisoformat(r["job"]["startedAt"])).total_seconds() for r in ok]
        print(f"작업 시간 중앙값 {statistics.median(secs):.1f}초 · LLM 합 중앙값 {statistics.median(r['mm']['llmElapsedMs'] for r in ok) / 1000:.1f}초")


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--run", default="mm_iu")
    p.add_argument("--models", nargs="+", default=["gemma4:12b"])
    p.add_argument("--normal", type=int, default=40)
    p.add_argument("--abnormal", type=int, default=80)
    p.add_argument("--seed", type=int, default=42)
    p.add_argument("--score", action="store_true", help="채점만")
    args = p.parse_args()
    if not args.score:
        run(args.run, args.models, sample(args.normal, args.abnormal, args.seed))
    score(args.run)


if __name__ == "__main__":
    main()

"""2차-6: 소견서 요약 오답이 RAG(용어 정의 제공)로 고쳐지는지, 예시(규칙 시연)로 고쳐지는지 측정.

API 를 거치지 않고 Ollama /api/chat 를 직접 호출한다 (API 와 같은 system 프롬프트·스키마·옵션: temperature 0,
think off, num_ctx 16384, num_predict 4096). 영상은 쓰지 않고 소견서 요약 단계만 돌린다.

변형
  base     현재 프롬프트 (src/Api/Modules/Multimodal/Pipeline/Prompts/summarize.system.md)
  rag      + 소견서에 나온 용어의 정의를 용어집에서 찾아 붙임 (키워드 검색 = 가장 단순한 RAG)
  fewshot  + 부정·의심 표현을 어떻게 판단하는지 보여 주는 예시 4개 (용어 정의는 넣지 않음)
  both     rag + fewshot

세트
  dev   2차-5 에서 쓴 120건 (eval/results/mm_iu/raw/gemma4_12b) ➔ 용어집은 이 세트의 오답을 보고 만들었음
  test  dev 와 겹치지 않는 새 120건: 정상 30 + 폐부종 20 + 울혈만(부종 없음) 10 + 그 밖의 이상 60 (seed 7)

사용: src/OcrService/.venv/Scripts/python eval/summary_variants.py --sets dev test --variants base rag fewshot both
      (--score 만 주면 채점만)
      용어집 새 버전: 프롬프트 화면에서 v1 을 적용한 뒤 --variants rag@v1 (API 의 적용 버전을 읽음, 번호가 다르면 중단)
      같은 설정 반복(흔들림 측정): 꼬리표 "#r2" 를 붙임 (예: rag#r2, rag@v2#r2). temperature 0 이어도 Ollama 결과가 호출마다 달라질 수 있음
"""
import argparse
import csv
import json
import random
import re
import statistics
import sys
import time
import urllib.request
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")

REPO = Path(__file__).resolve().parent.parent
IU = REPO / "data" / "samples" / "iu-xray"
PROMPTS = REPO / "src" / "Api" / "Modules" / "Multimodal" / "Pipeline" / "Prompts"
OUT = REPO / "eval" / "results" / "summary_variants"
OLLAMA = "http://127.0.0.1:11434"
KEYS = ["pneumonia", "cardiomegaly", "effusion", "atelectasis", "edema"]
TARGETS = {
    "pneumonia": {"Pneumonia", "Airspace Disease", "Infiltrate"},
    "cardiomegaly": {"Cardiomegaly"},
    "effusion": {"Pleural Effusion"},
    "atelectasis": {"Pulmonary Atelectasis"},
    "edema": {"Pulmonary Edema"},
}

SCHEMA = {
    "type": "object",
    "properties": {
        "indication": {"type": ["string", "null"]},
        "key_symptoms": {"type": "array", "items": {"type": "string"}},
        "findings": {"type": "array", "items": {"type": "string"}},
        "final_diagnosis": {"type": "string"},
        "normal": {"type": "boolean"},
        "mentions": {
            "type": "object",
            "properties": {k: {"type": "boolean"} for k in KEYS},
            "required": KEYS,
        },
    },
    "required": ["indication", "key_symptoms", "findings", "final_diagnosis", "normal", "mentions"],
}

# 용어집 (키워드 트리거): 정규식 ➔ 정의. 소견서에 키워드가 있으면 그 항목만 프롬프트에 붙인다
# 기본 = 저장소 파일(v0). 변형 이름에 "@vN" 을 붙이면 API 에서 지금 적용 중인 버전을 읽고, 그 번호가 N 인지 확인 (운영과 같은 조건)
API = "http://127.0.0.1:5000"


def parse_glossary(content: str):
    return [(e["pattern"], e["text"]) for e in json.loads(content)["entries"]]


GLOSSARY = parse_glossary((PROMPTS / "summarize.glossary.json").read_text(encoding="utf-8"))


def use_api_glossary(tag: str):
    """tag = "v1" ➔ API 의 적용 버전이 v1 인지 확인하고 그 내용으로 바꿈"""
    global GLOSSARY
    d = json.load(urllib.request.urlopen(f"{API}/api/prompts/multimodal/summarize.glossary"))
    active = d["info"].get("activeVersion")
    if f"v{active}" != tag:
        sys.exit(f"API 용어집 적용 버전이 v{active} 입니다 (요청: {tag}). 프롬프트 화면에서 {tag} 를 적용한 뒤 다시 실행하세요")
    GLOSSARY = parse_glossary(d["content"])
    print(f"용어집: API 적용 버전 {tag} ({len(GLOSSARY)}항목)")

FEWSHOT = """
예시 (판단 방식만 참고, 내용은 실제 소견서를 따를 것)
1) "No overt edema. Mild cardiomegaly." ➔ edema=false (부정), cardiomegaly=true
2) "No focal consolidation, pleural effusion, or pneumothorax." ➔ pneumonia=false, effusion=false (나열 전체가 부정)
3) "Left basilar opacity, atelectasis versus pneumonia." ➔ atelectasis=true, pneumonia=true (둘 다 가능성으로 언급)
4) "Previously seen right effusion has resolved." ➔ effusion=false (현재는 없음)
"""


def retrieve(report: str) -> list[str]:
    text = report.lower()
    return [d for pattern, d in GLOSSARY if re.search(pattern, text, re.IGNORECASE)]


def system_prompt(variant: str, report: str) -> tuple[str, int]:
    base = (PROMPTS / "summarize.system.md").read_text(encoding="utf-8").strip()
    variant = variant.split("#")[0].split("@")[0]  # "rag@v2#r2" ➔ "rag"
    hits = retrieve(report) if variant in ("rag", "both") else []
    parts = [base]
    if hits:
        parts.append("참고 용어 정의 (소견서에 나온 용어만 검색해 붙임)\n" + "\n".join(f"- {h}" for h in hits))
    if variant in ("fewshot", "both"):
        parts.append(FEWSHOT.strip())
    return "\n\n".join(parts), len(hits)


def user_prompt(report: str) -> str:
    return (PROMPTS / "summarize.user.md").read_text(encoding="utf-8").replace("{{$report}}", report)


def build_item(uid, r):
    problems = {p.strip() for p in r["Problems"].split(";")}
    truth = {k: int(bool(problems & v)) for k, v in TARGETS.items()}
    truth["normal"] = int(problems == {"normal"})
    return {"uid": uid, "truth": truth, "problems": sorted(problems),
            "report": f"INDICATION: {r['indication']}\nCOMPARISON: {r['comparison']}\n"
                      f"FINDINGS: {r['findings']}\nIMPRESSION: {r['impression']}"}


def load_sets():
    dev = [json.loads(f.read_text(encoding="utf-8"))
           for f in sorted((REPO / "eval" / "results" / "mm_iu" / "raw" / "gemma4_12b").glob("*.json"))]
    dev = [{"uid": d["uid"], "truth": d["truth"], "report": d["report"]} for d in dev]
    used = {d["uid"] for d in dev}
    front = {r["uid"] for r in csv.DictReader(open(IU / "indiana_projections.csv", encoding="utf-8"))
             if r["projection"] == "Frontal"}
    pools = {"normal": [], "edema": [], "congestion": [], "other": []}
    for r in csv.DictReader(open(IU / "indiana_reports.csv", encoding="utf-8")):
        uid = r["uid"]
        if uid in used or uid not in front or not r["impression"].strip():
            continue
        item = build_item(uid, r)
        t = item["truth"]
        if t["normal"]:
            pools["normal"].append(item)
        elif t["edema"]:
            pools["edema"].append(item)
        elif "Pulmonary Congestion" in item["problems"]:
            pools["congestion"].append(item)
        elif any(t[k] for k in KEYS):
            pools["other"].append(item)
    rng = random.Random(7)
    test = (rng.sample(pools["normal"], 30) + rng.sample(pools["edema"], 20)
            + rng.sample(pools["congestion"], min(10, len(pools["congestion"]))) + rng.sample(pools["other"], 60))
    return {"dev": dev, "test": test}


def call(model: str, system: str, user: str) -> dict:
    body = {"model": model, "stream": False, "think": False, "format": SCHEMA, "keep_alive": "10m",
            "messages": [{"role": "system", "content": system}, {"role": "user", "content": user}],
            "options": {"temperature": 0, "num_ctx": 16384, "num_predict": 4096}}
    req = urllib.request.Request(f"{OLLAMA}/api/chat", data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json"})
    t = time.time()
    with urllib.request.urlopen(req, timeout=600) as resp:
        data = json.load(resp)
    return {"raw": data["message"]["content"], "doneReason": data.get("done_reason"),
            "evalCount": data.get("eval_count"), "elapsedMs": int((time.time() - t) * 1000)}


def run(sets, variants, model):
    all_sets = load_sets()
    for s in sets:
        items = all_sets[s]
        (OUT / s).mkdir(parents=True, exist_ok=True)
        (OUT / s / "items.json").write_text(json.dumps(items, ensure_ascii=False, indent=1), encoding="utf-8")
        for v in variants:
            if "@" in v:
                use_api_glossary(v.split("#")[0].split("@")[1])
            path = OUT / s / f"{model.replace(':', '_')}__{v}.jsonl"
            done = {json.loads(line)["uid"] for line in open(path, encoding="utf-8")} if path.exists() else set()
            with open(path, "a", encoding="utf-8") as out:
                for i, item in enumerate(items):
                    if item["uid"] in done:
                        continue
                    system, hits = system_prompt(v, item["report"])
                    res = call(model, system, user_prompt(item["report"]))
                    try:
                        parsed = json.loads(res["raw"])
                    except json.JSONDecodeError:
                        parsed = None
                    out.write(json.dumps({"uid": item["uid"], "hits": hits, "result": parsed, **res},
                                         ensure_ascii=False) + "\n")
                    out.flush()
                    print(f"[{s}/{v}] {i + 1}/{len(items)} uid {item['uid']} {res['elapsedMs']}ms"
                          f"{' 형식오류' if parsed is None else ''}", flush=True)


def f1(pairs):
    tp = sum(p and t for p, t in pairs)
    fp = sum(p and not t for p, t in pairs)
    fn = sum(t and not p for p, t in pairs)
    prec = tp / (tp + fp) if tp + fp else float("nan")
    rec = tp / (tp + fn) if tp + fn else float("nan")
    f = 2 * tp / (2 * tp + fp + fn) if tp else 0.0
    return prec, rec, f, fp, fn, tp + fn


def score(sets, variants, model):
    lines = []
    for s in sets:
        items = {i["uid"]: i for i in json.loads((OUT / s / "items.json").read_text(encoding="utf-8"))}
        lines.append(f"\n## {s} ({len(items)}건, {model})\n")
        lines.append("| 변형 | 형식 성공 | " + " | ".join(f"{k} F1 (FP/FN)" for k in KEYS) + " | 정상 정확도 | 오답 합 | 시간 중앙값 |")
        lines.append("|---" * (len(KEYS) + 5) + "|")
        for v in variants:
            path = OUT / s / f"{model.replace(':', '_')}__{v}.jsonl"
            if not path.exists():
                continue
            recs = [json.loads(line) for line in open(path, encoding="utf-8")]
            ok = [r for r in recs if r["result"]]
            cells, errors = [], 0
            for k in KEYS:
                pairs = [(bool(r["result"]["mentions"][k]), bool(items[r["uid"]]["truth"][k])) for r in ok]
                p, rc, f, fp, fn, npos = f1(pairs)
                errors += fp + fn
                cells.append(f"{f:.0%} ({fp}/{fn})")
            normal = statistics.mean(r["result"]["normal"] == bool(items[r["uid"]]["truth"]["normal"]) for r in ok)
            med = statistics.median(r["elapsedMs"] for r in recs) / 1000
            lines.append(f"| {v} | {len(ok)}/{len(recs)} | " + " | ".join(cells)
                         + f" | {normal:.0%} | {errors} | {med:.1f}초 |")
        npos = {k: sum(i["truth"][k] for i in items.values()) for k in KEYS}
        lines.append("\n양성 수: " + ", ".join(f"{k} {n}" for k, n in npos.items()))
    text = "\n".join(lines)
    print(text)
    (OUT / f"summary_{model.replace(':', '_')}.md").write_text(text, encoding="utf-8")


def diff(s, a, b, model, key=None):
    """두 변형 사이에 바뀐 판단 (고쳐진 것/새로 틀린 것) 출력"""
    items = {i["uid"]: i for i in json.loads((OUT / s / "items.json").read_text(encoding="utf-8"))}
    load = lambda v: {r["uid"]: r for r in map(json.loads, open(OUT / s / f"{model.replace(':', '_')}__{v}.jsonl", encoding="utf-8"))}
    ra, rb = load(a), load(b)
    for uid, x in ra.items():
        y = rb.get(uid)
        if not (x["result"] and y and y["result"]):
            continue
        for k in [key] if key else KEYS:
            t = bool(items[uid]["truth"][k])
            pa, pb = bool(x["result"]["mentions"][k]), bool(y["result"]["mentions"][k])
            if pa != pb:
                tag = "고침" if pb == t else "새 오답"
                imp = items[uid]["report"].split("IMPRESSION:")[1].strip()[:120]
                print(f"{tag} {k} uid {uid} 정답 {int(t)} {a}={int(pa)} ➔ {b}={int(pb)} | {imp}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--sets", nargs="+", default=["dev", "test"])
    ap.add_argument("--variants", nargs="+", default=["base", "rag", "fewshot", "both"])
    ap.add_argument("--model", default="gemma4:12b")
    ap.add_argument("--score", action="store_true")
    ap.add_argument("--diff", nargs=3, metavar=("SET", "A", "B"))
    ap.add_argument("--key")
    a = ap.parse_args()
    if a.diff:
        diff(*a.diff, a.model, a.key)
    else:
        if not a.score:
            run(a.sets, a.variants, a.model)
        score(a.sets, a.variants, a.model)

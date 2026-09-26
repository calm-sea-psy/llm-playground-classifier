"""4차 0단계: 문서 전산화 측정 채점 (문서 종류 팩의 필드 타입만 보고 채점, 종류별 코드 없음).

입력: eval/results/digitizer/{run}/texts.jsonl (원문) · extract.jsonl (필드 추출 · 검증), 정답 data/digitizer/{type}/{split}/{id}/truth.json
출력: eval/results/digitizer/{run}/summary.md

지표
- 원문 보존율: 정답 값이 추출된 원문(PDF 텍스트 층 · DOCX · OCR)에 있는 비율 ➔ 이 단계에서 잃은 값은 LLM 이 되살릴 수 없음
- 단일 필드 정확도: 단일 필드 + 맞춘 목록 항목의 하위 필드 (longtext 제외). 정답 · 추출 중 하나라도 값이 있는 칸만 셈
- 목록 재현율 · 정밀도: 목록 항목을 key 필드로 짝지음 (재현율이 낮으면 빠뜨림)
- 지어낸 값: 추출 값이 원문에 없음 (Engine 검증과 같은 기준) · 수집 금지 값 누출
- 사람 확인 비율 · 검증 통과했지만 틀림 · 틀린 문서 중 검증이 잡은 비율

예) src/OcrService/.venv/Scripts/python eval/digitizer_eval.py --run dev-text --split dev
"""

import argparse
import json
import re
import statistics
import sys
from collections import defaultdict
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent


# ── 정규화 (Engine Validator 와 같은 기준) ─────────────────────────────────
def compact(s) -> str:
    return re.sub(r"[^\w@.]", "", str(s), flags=re.UNICODE).lower() if s is not None else ""


def digits(s) -> str:
    return re.sub(r"\D", "", str(s)) if s is not None else ""


def norm(value, ftype):
    if value is None or (isinstance(value, str) and not value.strip()):
        return None
    v = str(value).strip()
    if ftype == "phone":
        return digits(v)
    if ftype == "email":
        return v.lower()
    if ftype in ("month", "month_or_present"):
        if v.lower() in ("present", "현재", "재직 중", "재직중"):
            return "present"
        m = re.match(r"(\d{4})\D+(\d{1,2})", v)
        return f"{m.group(1)}-{int(m.group(2)):02d}" if m else v
    if ftype == "date":
        m = re.match(r"(\d{4})\D+(\d{1,2})\D+(\d{1,2})", v)
        return f"{m.group(1)}-{int(m.group(2)):02d}-{int(m.group(3)):02d}" if m else v
    return compact(v)


class Haystack:
    def __init__(self, source: str):
        self.source = source
        self.compact = compact(source)
        lines = [digits(l) for l in source.split("\n")]
        lines = [l for l in lines if l]
        self.digit_lines = lines + [a + b for a, b in zip(lines, lines[1:])]

    def has_digits(self, d):
        return bool(d) and any(d in l for l in self.digit_lines)

    def grounded(self, value, ftype) -> bool:
        if value is None:
            return True
        v = str(value)
        if ftype == "phone":
            return self.has_digits(digits(v))
        if ftype == "date":
            n = norm(v, "date")
            if not re.match(r"\d{4}-\d{2}-\d{2}$", n or ""):
                return False
            y, m, d = n.split("-")
            return any(self.has_digits(c) for c in (y + m + d, y + str(int(m)) + str(int(d)), y[2:] + m + d))
        if ftype in ("month", "month_or_present"):
            n = norm(v, ftype)
            if n == "present":
                return bool(re.search(r"현재|재직\s*중|present", self.source, re.I))
            if not re.match(r"\d{4}-\d{2}$", n or ""):
                return False
            return self.has_digits(n[:4] + n[5:7]) or self.has_digits(n[:4] + str(int(n[5:7])))
        if ftype == "longtext":
            words = [w for w in re.findall(r"\w+", v) if len(w) >= 2]
            return not words or sum(compact(w) in self.compact for w in words) * 2 >= len(words)
        c = compact(v)
        return bool(c) and c in self.compact


# ── 채점 ─────────────────────────────────────────────────────────────────
def grade(type_def, truth, pred, source, needs_review):
    fields = type_def["fields"]
    hay = Haystack(source)
    tf, pf = truth["fields"], pred or {}
    cells = []          # (경로, 맞음 여부)
    fabricated = []     # 원문에 없는 추출 값
    lists = {}          # 목록별 (정답 수, 추출 수, 맞춘 수)

    def check(path, ftype, t, p):
        if isinstance(p, str) and not p.strip():
            p = None  # 빈 문자열은 값 없음 (Engine Validator.Text 와 같은 기준)
        tn, pn = norm(t, ftype), norm(p, ftype)
        if p is not None and not hay.grounded(p, ftype):
            # 원문에 연속으로 없음. 정답과 같으면 "섞인 원문을 올바르게 이어 붙임" (예: 표 칸 줄바꿈으로 OCR 순서가 섞인 주소)
            fabricated.append((f"{path}={p}", tn is not None and tn == pn))
        if ftype == "longtext" or (tn is None and pn is None):
            return
        cells.append((path, tn == pn, t, p))

    for f in fields:
        name, ftype = f["name"], f["type"]
        if ftype == "list":
            key = f["key"]
            key_type = next(i["type"] for i in f["items"] if i["name"] == key)
            titems = tf.get(name) or []
            pitems = [x for x in (pf.get(name) or []) if isinstance(x, dict)]
            used, matched = set(), 0
            for ti, t in enumerate(titems):
                k = norm(t.get(key), key_type)
                # 짝짓기: 이름이 한쪽에 포함되면 같은 항목 ("새봄대학교" ⊂ "새봄대학교 대학원"). 이름 칸 자체는 아래 check 에서 틀림으로 셈
                # ➔ "항목을 빠뜨림"(재현율) 과 "이름을 덜 적음"(필드 정확도) 을 구분 (개발 세트 v0.2 에서 정함, 모든 모델에 같이 적용)
                pi = next((j for j, p in enumerate(pitems) if j not in used and norm(p.get(key), key_type) == k), None)
                if pi is None and k:
                    pi = next((j for j, p in enumerate(pitems) if j not in used and (pk := norm(p.get(key), key_type)) and (pk in k or k in pk)), None)
                if pi is None:
                    continue
                used.add(pi)
                matched += 1
                for sub in f["items"]:
                    check(f"{name}[{ti}].{sub['name']}", sub["type"], t.get(sub["name"]), pitems[pi].get(sub["name"]))
            for j, p in enumerate(pitems):  # 짝이 없는 추출 항목도 근거 확인
                if j not in used:
                    for sub in f["items"]:
                        if p.get(sub["name"]) is not None and not hay.grounded(p.get(sub["name"]), sub["type"]):
                            fabricated.append((f"{name}[+{j}].{sub['name']}={p.get(sub['name'])}", False))
            lists[name] = (len(titems), len(pitems), matched)
        elif ftype == "string_list":
            tset = {compact(x) for x in tf.get(name) or []}
            pset = {compact(x) for x in pf.get(name) or [] if isinstance(x, str)}
            lists[name] = (len(tset), len(pset), len(tset & pset))
            for x in pf.get(name) or []:
                if isinstance(x, str) and not hay.grounded(x, "text"):
                    fabricated.append((f"{name}={x}", compact(x) in tset))
        else:
            check(name, ftype, tf.get(name), pf.get(name))

    # 수집 금지 값: 추출 결과에 나오면 누출 (정답 필드에도 있는 값은 제외: 예 본적 = 주소의 시 · 도)
    truth_text = json.dumps(tf, ensure_ascii=False)
    pred_text = json.dumps(pf, ensure_ascii=False)
    leaks = []
    for k, v in (truth.get("forbidden_present") or {}).items():
        values = [x[1] for x in v] if k == "family" else [v]
        for val in values:
            if val and str(val) in pred_text and str(val) not in truth_text:
                leaks.append(f"{k}={val}")

    wrong = [c for c in cells if not c[1]]
    missed = sum(t - m for t, _, m in lists.values())
    return {
        "cells": len(cells), "correct": len(cells) - len(wrong), "wrong": wrong,
        "lists": lists, "fabricated": fabricated, "leaks": leaks,
        "doc_wrong": bool(wrong) or missed > 0 or any(not ok for _, ok in fabricated) or bool(leaks),
        "needs_review": needs_review,
    }


def value_recall(type_def, truth, source):
    """정답 값이 원문에 있는 비율 (원문 추출 단계 품질)"""
    hay = Haystack(source)
    total = found = 0
    lost = []
    for f in type_def["fields"]:
        v = truth["fields"].get(f["name"])
        if f["type"] == "list":
            for i, item in enumerate(v or []):
                for sub in f["items"]:
                    if sub["type"] != "longtext" and item.get(sub["name"]) is not None:
                        total += 1
                        ok = hay.grounded(item[sub["name"]], sub["type"])
                        found += ok
                        if not ok:
                            lost.append(f"{f['name']}[{i}].{sub['name']}={item[sub['name']]}")
        elif f["type"] == "string_list":
            for x in v or []:
                total += 1
                found += hay.grounded(x, "text")
        elif v is not None:
            total += 1
            ok = hay.grounded(v, f["type"])
            found += ok
            if not ok:
                lost.append(f"{f['name']}={v}")
    return total, found, lost


def pct(n, d):
    return f"{n / d * 100:.1f}% ({n}/{d})" if d else "-"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", required=True)
    ap.add_argument("--split", required=True)
    ap.add_argument("--type", default="resume")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    type_def = json.loads((REPO / "packs" / args.type / "type.json").read_text(encoding="utf-8"))
    run_dir = REPO / "eval" / "results" / "digitizer" / args.run
    data_dir = REPO / "data" / "digitizer" / args.type / args.split
    truths = {d.name: json.loads((d / "truth.json").read_text(encoding="utf-8")) for d in data_dir.iterdir() if d.is_dir()}
    texts = [json.loads(l) for l in (run_dir / "texts.jsonl").read_text(encoding="utf-8").splitlines() if l.strip()]
    text_by = {(t["id"], t["input"]): t for t in texts}

    lines = [f"# 문서 전산화 측정: {args.type} · {args.run} ({args.split})", ""]

    # 1) 원문 보존율
    lines += ["## 원문 보존율 (정답 값이 추출된 원문에 있는 비율)", "", "| 입력 | 문서 | 보존율 | 원문 추출 시간 중앙값 |", "|---|---|---|---|"]
    lost_all = defaultdict(list)
    by_input = defaultdict(list)
    for t in texts:
        if t["id"] in truths and not t.get("error"):
            by_input[t["input"]].append(t)
    for inp, ts in by_input.items():
        tot = fnd = 0
        for t in ts:
            a, b, lost = value_recall(type_def, truths[t["id"]], t["text"])
            tot += a
            fnd += b
            lost_all[inp] += [f"{t['id']}: {x}" for x in lost]
        ms = statistics.median(t["ms"] for t in ts)
        lines.append(f"| {inp} | {len(ts)} | {pct(fnd, tot)} | {ms / 1000:.1f}초 |")
    for inp, lost in lost_all.items():
        if lost:
            lines += ["", f"- {inp} 에서 잃은 값 (앞 10개): " + "; ".join(lost[:10])]

    # 2) 필드 추출
    ext_path = run_dir / "extract.jsonl"
    if ext_path.exists():
        rows = [json.loads(l) for l in ext_path.read_text(encoding="utf-8").splitlines() if l.strip()]
        groups = defaultdict(list)
        for r in rows:
            if r["id"] not in truths:
                continue
            src = text_by.get((r["id"], r["input"]), {}).get("text", "")
            g = grade(type_def, truths[r["id"]], r.get("fields"), src, r["needs_review"])
            g["row"] = r
            groups[(r["model"] + (" (CPU)" if r.get("cpu") else ""), r["input"])].append(g)

        lines += ["", "## 필드 추출", "",
                  "| 모델 | 입력 | 기록 | 단일 필드 정확도 | 목록 재현율 | 목록 정밀도 | 원문에 없는 값 (문서) · 그중 정답과도 다름 | 금지 값 누출 | 사람 확인 | 통과했지만 틀림 | 틀린 문서 중 검증이 잡음 | LLM 시간 중앙값 | 최대 |",
                  "|---|---|---|---|---|---|---|---|---|---|---|---|---|"]
        for (model, inp), gs in sorted(groups.items()):
            cells = sum(g["cells"] for g in gs)
            correct = sum(g["correct"] for g in gs)
            lt = sum(t for g in gs for t, _, _ in g["lists"].values())
            lp = sum(p for g in gs for _, p, _ in g["lists"].values())
            lm = sum(m for g in gs for _, _, m in g["lists"].values())
            fab = sum(1 for g in gs if g["fabricated"])
            fab_wrong = sum(1 for g in gs if any(not ok for _, ok in g["fabricated"]))
            leak = sum(1 for g in gs if g["leaks"])
            review = sum(1 for g in gs if g["needs_review"])
            passed_wrong = sum(1 for g in gs if g["doc_wrong"] and not g["needs_review"])
            wrong_docs = [g for g in gs if g["doc_wrong"]]
            caught = sum(1 for g in wrong_docs if g["needs_review"])
            ms = [g["row"]["llm_ms"] for g in gs]
            lines.append(f"| {model} | {inp} | {len(gs)} | {pct(correct, cells)} | {pct(lm, lt)} | {pct(lm, lp)} | {fab} · **{fab_wrong}** | {leak} | {pct(review, len(gs))} | "
                         f"{passed_wrong} | {pct(caught, len(wrong_docs))} | {statistics.median(ms) / 1000:.1f}초 | {max(ms) / 1000:.1f}초 |")

        # 반복 일치 (같은 모델 · 입력 · 문서의 회차 간 추출 결과가 같은지)
        reps = defaultdict(list)
        for r in rows:
            reps[(r["model"], r.get("cpu"), r["input"], r["id"])].append(json.dumps(r.get("fields"), sort_keys=True, ensure_ascii=False))
        multi = {k: v for k, v in reps.items() if len(v) > 1}
        if multi:
            same = defaultdict(lambda: [0, 0])
            for (m, cpu, inp, _), v in multi.items():
                key = (m + (" (CPU)" if cpu else ""), inp)
                same[key][1] += 1
                same[key][0] += all(x == v[0] for x in v)
            lines += ["", "## 반복 일치 (회차 간 추출 결과가 완전히 같은 문서)", ""]
            lines += [f"- {m} · {inp}: {pct(a, b)}" for (m, inp), (a, b) in sorted(same.items())]

        # 상세: 틀린 칸 · 지어낸 값 · 누출
        lines += ["", "## 상세 (문서별 문제)", ""]
        for (model, inp), gs in sorted(groups.items()):
            lines.append(f"### {model} · {inp}")
            for g in sorted(gs, key=lambda g: (g["row"]["id"], g["row"]["rep"])):
                missed = {k: f"{m}/{t}" for k, (t, p, m) in g["lists"].items() if m < t or p > m}
                if not (g["wrong"] or missed or g["fabricated"] or g["leaks"]):
                    continue
                parts = []
                if g["wrong"]:
                    parts.append("틀림 " + "; ".join(f"{c[0]}: {c[2]} ➔ {c[3]}" for c in g["wrong"][:6]))
                if missed:
                    parts.append(f"목록 {missed}")
                if g["fabricated"]:
                    parts.append("원문에 없음 " + "; ".join(v + ("(정답과 같음)" if ok else "(**지어냄**)") for v, ok in g["fabricated"][:4]))
                if g["leaks"]:
                    parts.append(f"누출 {g['leaks']}")
                flag = "검수" if g["needs_review"] else "**통과**"
                lines.append(f"- {g['row']['id']} r{g['row']['rep']} [{flag}] " + " | ".join(parts))
            lines.append("")

    out = run_dir / "summary.md"
    out.write_text("\n".join(lines), encoding="utf-8")
    print("\n".join(l for l in lines if not l.startswith("- ") and not l.startswith("### "))[:6000])
    print(f"\n➔ {out.relative_to(REPO)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

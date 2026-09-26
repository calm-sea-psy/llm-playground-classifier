"""3차 스파이크 3단계: 실험 분석 에이전트 기록(JSONL)을 과제 정답(eval/agent/tasks.json)으로 채점한다.

입력: spikes/AgentSpike `run` 이 쌓은 eval/results/agent/{run}.jsonl (설정 × 과제 × 회차)
출력: eval/results/agent/{run}.summary.md (설정별 지표, 과제별 성공 횟수, 근거 없는 숫자 목록)

지표
- 성공: 필요한 도구를 불렀고(required_tools), 답에 정답 표현이 있고(answer_all), 금지 표현이 없음(answer_none), no_tools 면 도구 0회,
  추론을 답으로 대체하지 않음
- 근거 없는 숫자: 답의 숫자가 질문·도구 결과 어디에도 없음 (비율 ×100, 반올림 허용, 10 미만 정수는 제외) ➔ 목록은 사람이 확인
- 반복 호출: 같은 도구를 같은 인자로 2번 이상 부른 횟수
- 반복 일치: 과제마다 회차 간 성공 여부 · 도구 호출 순서가 모두 같은 비율

예) src/OcrService/.venv/Scripts/python eval/agent_eval.py --run spike1
"""

import argparse
import json
import re
import statistics
import sys
from collections import defaultdict
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
TASKS = REPO / "eval" / "agent" / "tasks.json"  # --tasks 로 보류 세트(tasks_holdout.json) 지정
RESULTS = REPO / "eval" / "results" / "agent"

NUMBER = re.compile(r"\d[\d,]*(?:\.\d+)?")


def numbers(text: str) -> list[float]:
    out = []
    for token in NUMBER.findall(text or ""):
        try:
            out.append(float(token.replace(",", "")))
        except ValueError:
            pass
    return out


def grounded(x: float, pool: list[float]) -> bool:
    for v in pool:
        if abs(x - v) <= 0.0051 * max(1, abs(v)):
            return True
        # 0.172 ➔ 17.2%, 0.078 ➔ 7.8%p
        if abs(v) <= 1 and (abs(x - v * 100) <= 0.051 or (x == int(x) and abs(x - v * 100) <= 0.5)):
            return True
    return False


def any_match(patterns: list[str], text: str) -> bool:
    return any(re.search(p, text, re.IGNORECASE | re.MULTILINE) for p in patterns)


def grade(task: dict, trace: dict) -> dict:
    answer = trace.get("Answer") or ""
    steps = trace.get("Steps") or []
    called = [s["Name"] for s in steps]
    problems = []

    if trace.get("Error"):
        problems.append("실행 오류")
    if not answer.strip():
        problems.append("빈 답")
    # 추론이 폭주해 문맥 한도에 걸리면 추론 텍스트가 답 자리에 옴 ➔ 정답 표현이 우연히 들어 있어도 답이 아님
    # (spike1: qwen3-vl:8b N04 의 영어 추론이 "text"·"image" 로 성공 판정됐던 것)
    if trace.get("ReasoningFallbacks", 0) > 0:
        problems.append("추론을 답으로 대체")
    for group in task.get("required_tools", []):
        if not any(name in called for name in group):
            problems.append(f"도구 누락({'/'.join(group)})")
    if task.get("no_tools") and called:
        problems.append("불필요한 도구 호출")
    for group in task.get("answer_all", []):
        if not any_match(group, answer):
            problems.append(f"정답 표현 없음({group[0]})")
    for pattern in task.get("answer_none", []):
        if re.search(pattern, answer, re.IGNORECASE | re.MULTILINE):
            problems.append(f"금지 표현({pattern})")

    pool = numbers(task["question"]) + [n for s in steps for n in numbers(s.get("Result", ""))]
    pool += [n for s in steps for v in (s.get("Arguments") or {}).values() for n in numbers(str(v))]
    ungrounded = [] if task.get("allow_derived") else sorted(
        {x for x in numbers(answer) if not (x < 10 and x == int(x)) and not grounded(x, pool)})

    keys = [(s["Name"], json.dumps(s.get("Arguments") or {}, sort_keys=True, ensure_ascii=False)) for s in steps]
    repeats = len(keys) - len(set(keys))
    tool_errors = sum(1 for s in steps if s.get("Result", "").startswith('{"error"'))

    return {
        "success": not problems,
        "problems": problems,
        "ungrounded": ungrounded,
        "repeats": repeats,
        "tool_errors": tool_errors,
        "sequence": called,
    }


def pct(n: int, d: int) -> str:
    return f"{n / d * 100:.0f}% ({n}/{d})" if d else "-"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", required=True, help="eval/results/agent/{run}.jsonl")
    ap.add_argument("--tasks", default=str(TASKS), help="과제 파일 (기본 개발 세트)")
    args = ap.parse_args()

    tasks = {t["id"]: t for t in json.loads(Path(args.tasks).read_text(encoding="utf-8"))["tasks"]}
    path = RESULTS / f"{args.run}.jsonl"
    rows = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]

    graded = defaultdict(list)  # config ➔ [(task_id, rep, trace, grade)]
    for r in rows:
        task = tasks.get(r["task_id"])
        if task is None:
            continue
        graded[r["config"]].append((r["task_id"], r["rep"], r["trace"], grade(task, r["trace"])))

    types = ["single", "multi", "none", "error"]
    lines = [f"# 에이전트 측정 {args.run}", "", f"과제 {len(tasks)}개, 기록 {len(rows)}건", ""]

    header = "| 지표 | " + " | ".join(graded) + " |"
    lines += [header, "|---|" + "---|" * len(graded)]

    def row(label, fn):
        lines.append(f"| {label} | " + " | ".join(fn(v) for v in graded.values()) + " |")

    row("**성공률 (전체)**", lambda v: pct(sum(g["success"] for *_, g in v), len(v)))
    for t in types:
        row(f"성공 · {t}", lambda v, t=t: pct(sum(g["success"] for tid, _, _, g in v if tasks[tid]["type"] == t),
                                           sum(1 for tid, *_ in v if tasks[tid]["type"] == t)))
    row("single+multi 성공", lambda v: pct(sum(g["success"] for tid, _, _, g in v if tasks[tid]["type"] in ("single", "multi")),
                                         sum(1 for tid, *_ in v if tasks[tid]["type"] in ("single", "multi"))))
    row("**근거 없는 숫자가 있는 답**", lambda v: str(sum(1 for *_, g in v if g["ungrounded"])))
    row("같은 호출 반복 (건)", lambda v: str(sum(1 for *_, g in v if g["repeats"])))
    row("도구 오류 응답 (error 과제 제외)", lambda v: str(sum(g["tool_errors"] for tid, _, _, g in v if tasks[tid]["type"] != "error")))
    row("빈 답", lambda v: str(sum(1 for *_, g in v if "빈 답" in g["problems"])))
    row("길이 잘림 / 추론→답 대체", lambda v: f"{sum(t.get('LengthCutoffs', 0) for _, _, t, _ in v)} / {sum(t.get('ReasoningFallbacks', 0) for _, _, t, _ in v)}")
    row("실행 오류", lambda v: str(sum(1 for _, _, t, _ in v if t.get("Error"))))
    row("도구 호출 수 (평균)", lambda v: f"{statistics.mean(len(t['Steps']) for _, _, t, _ in v):.1f}")
    row("시간 중앙값 (초)", lambda v: f"{statistics.median(t['ElapsedSec'] for _, _, t, _ in v):.1f}")
    row("시간 최대 (초)", lambda v: f"{max(t['ElapsedSec'] for _, _, t, _ in v):.1f}")
    row("출력 토큰 중앙값", lambda v: f"{statistics.median((t.get('OutputTokens') or 0) for _, _, t, _ in v):.0f}")

    def consistency(v, key):
        by_task = defaultdict(list)
        for tid, _, _, g in v:
            by_task[tid].append(key(g))
        multi = [xs for xs in by_task.values() if len(xs) > 1]
        same = sum(1 for xs in multi if all(x == xs[0] for x in xs))
        return pct(same, len(multi)) if multi else "- (1회)"

    row("**반복 일치 (성공 여부)**", lambda v: consistency(v, lambda g: g["success"]))
    row("반복 일치 (도구 순서)", lambda v: consistency(v, lambda g: g["sequence"]))

    # 과제별 성공 횟수
    lines += ["", "## 과제별 성공 (성공 회차 / 전체 회차)", "", "| 과제 | 유형 | " + " | ".join(graded) + " |",
              "|---|---|" + "---|" * len(graded)]
    for tid, task in tasks.items():
        cells = []
        for v in graded.values():
            gs = [g for t, _, _, g in v if t == tid]
            cells.append(f"{sum(g['success'] for g in gs)}/{len(gs)}" if gs else "-")
        lines.append(f"| {tid} | {task['type']} | " + " | ".join(cells) + " |")

    # 실패와 근거 없는 숫자 목록 (사람이 확인)
    lines += ["", "## 실패 · 근거 없는 숫자 상세", ""]
    for config, v in graded.items():
        lines.append(f"### {config}")
        for tid, rep, trace, g in sorted(v, key=lambda x: (x[0], x[1])):
            if g["success"] and not g["ungrounded"]:
                continue
            answer = (trace.get("Answer") or "").replace("\n", " ")[:160]
            flags = "; ".join(g["problems"]) + (f"; 근거 없는 숫자 {g['ungrounded']}" if g["ungrounded"] else "")
            lines.append(f"- {tid} r{rep} [{' → '.join(g['sequence'])}] {flags} — {answer}")
        lines.append("")

    out = RESULTS / f"{args.run}.summary.md"
    out.write_text("\n".join(lines), encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    print("\n".join(lines[: 6 + 18]))
    print(f"\n➔ {out.relative_to(REPO)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

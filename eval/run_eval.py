"""배치 평가 1단계: 라벨이 있는 KORIE 영수증을 API 로 모델별 처리하고 원본 결과를 저장한다.

API(src/Api)와 OcrService 가 떠 있어야 한다. 모델마다 전체를 한 번씩 돌린다 (Ollama 모델 교체 최소화).
결과는 eval/results/{run}/raw/{model}/{image}.json ➔ score_eval.py 로 채점.

예) src/OcrService/.venv/Scripts/python eval/run_eval.py --run korie150 --models gemma4:12b qwen3-vl:8b
"""

import argparse
import csv
import json
import random
import sys
import time
from pathlib import Path

import httpx

REPO = Path(__file__).resolve().parent.parent
LABELS = REPO / "data" / "verify" / "korie_fields_labels.csv"
IMAGES = REPO / "data" / "samples" / "korie" / "images"
IN_FLIGHT = 3  # 큐에 미리 넣어 둘 작업 수 (GPU 는 워커가 1건씩 사용)
JOB_TIMEOUT_S = 600


def labeled_images() -> list[Path]:
    names = sorted({row["image"] for row in csv.DictReader(open(LABELS, encoding="utf-8-sig"))})
    by_stem = {p.stem: p for p in IMAGES.iterdir()}
    return [by_stem[n] for n in names]


def dataset_images(dataset: str, limit: int | None, seed: int) -> list[Path]:
    """korie: 필드 라벨이 있는 150장 / insurance_claim·commercial_invoice: AI Hub 에서 무작위 limit 장"""
    if dataset == "korie":
        return labeled_images()[:limit]
    images = sorted((REPO / "data" / "samples" / "aihub" / dataset / "images").iterdir())
    return sorted(random.Random(seed).sample(images, limit or 30))


def safe(model: str) -> str:
    return model.replace(":", "_").replace("/", "_")  # Windows 파일명에 ':' 불가


def submit(client: httpx.Client, image: Path, model: str) -> str:
    with open(image, "rb") as f:
        r = client.post("/api/text/jobs", files={"file": (image.name, f)}, data={"model": model})
    r.raise_for_status()
    return r.json()["jobId"]


def collect(client: httpx.Client, job_id: str) -> dict:
    job = client.get(f"/api/jobs/{job_id}").json()
    record = {"job": job, "ocr": None, "result": None}
    if job["status"] == "Completed":
        record["ocr"] = client.get(f"/api/text/jobs/{job_id}/ocr").json()
        r = client.get(f"/api/text/jobs/{job_id}/result")
        record["result"] = r.json() if r.status_code == 200 else None
    return record


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--run", required=True, help="결과 폴더 이름 (eval/results/{run})")
    p.add_argument("--models", nargs="+", required=True)
    p.add_argument("--api", default="http://127.0.0.1:5000")
    p.add_argument("--dataset", default="korie", choices=["korie", "insurance_claim", "commercial_invoice"])
    p.add_argument("--limit", type=int, default=None, help="korie: 앞에서부터, AI Hub: 무작위 (기본 30)")
    p.add_argument("--seed", type=int, default=42)
    p.add_argument("--resume", action="store_true", help="이미 저장된 결과는 건너뜀")
    p.add_argument("--image-list", type=Path, help="처리할 이미지 경로 목록 파일 (한 줄에 하나, --dataset 은 채점용)")
    args = p.parse_args()

    images = ([REPO / line.strip() for line in args.image_list.read_text(encoding="utf-8").splitlines() if line.strip()]
              if args.image_list else dataset_images(args.dataset, args.limit, args.seed))
    root = REPO / "eval" / "results" / args.run
    client = httpx.Client(base_url=args.api, timeout=60)
    (root / "run.json").parent.mkdir(parents=True, exist_ok=True)
    (root / "run.json").write_text(json.dumps({
        "run": args.run, "dataset": args.dataset, "models": args.models, "images": len(images),
        "started": time.strftime("%Y-%m-%d %H:%M:%S"),
        "config": client.get("/api/text/models").json(),
    }, ensure_ascii=False, indent=2), encoding="utf-8")

    for model in args.models:
        out = root / "raw" / safe(model)
        out.mkdir(parents=True, exist_ok=True)
        todo = [img for img in images if not (args.resume and (out / f"{img.stem}.json").exists())]
        print(f"== {model}: {len(todo)}/{len(images)}건", flush=True)

        pending: dict[str, tuple[Path, float]] = {}
        queue = list(todo)
        done = 0
        started = time.time()
        while queue or pending:
            while queue and len(pending) < IN_FLIGHT:
                img = queue.pop(0)
                pending[submit(client, img, model)] = (img, time.time())
            time.sleep(1)
            for job_id, (img, t0) in list(pending.items()):
                status = client.get(f"/api/jobs/{job_id}").json()["status"]
                timed_out = time.time() - t0 > JOB_TIMEOUT_S
                if status not in ("Completed", "Failed") and not timed_out:
                    continue
                record = collect(client, job_id)
                record["image"] = img.stem
                record["model"] = model
                record["timed_out"] = timed_out
                (out / f"{img.stem}.json").write_text(json.dumps(record, ensure_ascii=False), encoding="utf-8")
                del pending[job_id]
                done += 1
                r = record["result"] or {}
                eta = (time.time() - started) / done * (len(todo) - done)
                print(f"[{done}/{len(todo)}] {img.stem} {record['job']['status']} "
                      f"passed={r.get('validationPassed')} fallback={r.get('fallbackUsed')} "
                      f"llm={r.get('llmElapsedMs')}ms  (남은 시간 약 {eta / 60:.0f}분)", flush=True)
    print("완료", root)


if __name__ == "__main__":
    main()

"""Kaggle 폐렴 데이터 분할 (학습·평가 스크립트가 같은 분할을 쓰도록 한 곳에 둠).

- Kaggle 공식 val 은 16장뿐이라 쓰지 않고, train 에서 환자 10% 를 검증용으로 다시 나눈다
- 공식 test 624장은 환자 기준으로 반씩 나눠 "기준값 보정용(calib)"과 "최종 평가용(holdout)"으로 쓴다
  (테스트 분포가 학습과 달라 검증 세트로 정한 기준값이 맞지 않기 때문 — 2차-0 스파이크)
- 같은 환자의 사진이 양쪽에 섞이지 않도록 파일명에서 환자 번호를 뽑아 환자 단위로 나눈다
"""

import random
import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent.parent
DATA = REPO_ROOT / "data" / "samples" / "pneumonia" / "chest_xray"
SEED = 42


def patient_id(path: Path) -> str:
    # person1000_bacteria_2931.jpeg ➔ person1000 / IM-0115-0001.jpeg ➔ IM-0115 / NORMAL2-IM-0666-0001.jpeg ➔ NORMAL2-IM-0666
    name = path.stem
    if name.startswith("person"):
        return name.split("_")[0]
    match = re.match(r"(.*IM-\d+)", name)
    return match.group(1) if match else name


def folder(name: str) -> list[tuple[Path, int]]:
    items = []
    for label, cls in ((0, "NORMAL"), (1, "PNEUMONIA")):
        items += [(p, label) for p in sorted((DATA / name / cls).glob("*.jpeg"))]
    return items


def by_patient(items: list[tuple[Path, int]], fraction: float, seed: int = SEED):
    """(나머지, fraction 만큼의 환자) 로 나눔"""
    patients = sorted({patient_id(p) for p, _ in items})
    picked = set(random.Random(seed).sample(patients, round(len(patients) * fraction)))
    rest = [x for x in items if patient_id(x[0]) not in picked]
    part = [x for x in items if patient_id(x[0]) in picked]
    return rest, part


def train_val():
    return by_patient(folder("train"), 0.1)


def calib_holdout():
    holdout, calib = by_patient(folder("test"), 0.5)
    return calib, holdout

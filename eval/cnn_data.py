"""2차-1b 평가 데이터 정의 (cnn_models.py 와 cnn_models_mm.py 가 같이 사용 — MMPretrain 가상 환경에는 xrv 가 없어서 분리)"""

import csv
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO / "src" / "CnnService"))
from tools.splits import calib_holdout  # noqa: E402

IU = REPO / "data" / "samples" / "iu-xray"

# IU 정답: 소견서 Problems 에 이 이름이 있으면 양성 (환자 단위)
IU_TARGETS = {
    "Cardiomegaly": {"Cardiomegaly"},
    "Effusion": {"Pleural Effusion"},
    "Atelectasis": {"Pulmonary Atelectasis"},
    "Opacity": {"Opacity"},
    "Edema": {"Pulmonary Edema"},
    # 폐렴 자체는 36명뿐 ➔ 폐렴성 음영(공기공간 질환·침윤)을 묶어 평가
    "PneumoniaLike": {"Pneumonia", "Airspace Disease", "Infiltrate"},
}


def kaggle_items():
    _, holdout = calib_holdout()
    return [(p, {"Pneumonia": y}) for p, y in holdout]


def iu_items():
    reports = {r["uid"]: r for r in csv.DictReader(open(IU / "indiana_reports.csv", encoding="utf-8"))}
    seen, items = set(), []
    for row in csv.DictReader(open(IU / "indiana_projections.csv", encoding="utf-8")):
        if row["projection"] != "Frontal" or row["uid"] in seen:
            continue
        seen.add(row["uid"])
        problems = {p.strip() for p in reports[row["uid"]]["Problems"].split(";")}
        items.append((IU / "images" / "images_normalized" / row["filename"],
                      {t: int(bool(problems & names)) for t, names in IU_TARGETS.items()}))
    return items

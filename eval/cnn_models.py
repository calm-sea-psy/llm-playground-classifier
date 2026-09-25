"""2차-1b CNN 후보 확장 비교: 사전학습 흉부 X-ray 모델들 + 미세조정 모델들을 같은 데이터로 평가.

데이터
- kaggle : Kaggle 소아 폐렴 test 의 holdout 절반 318장 (src/CnnService/tools/splits.py, 기준값 보정에 안 쓴 쪽)
- iu     : IU X-Ray 성인, 환자당 정면 1장 (3,689장). 정답 = 소견서 Problems(MeSH) 라벨

후보
- 사전학습 (학습 없음): TorchXRayVision DenseNet121 가중치별(all · nih(=CheXNet 계열) · chex(=CheXpert) · rsna(폐렴) ·
  mimic_ch · mimic_nb · pc), ResNet50-res512-all, JF Healthcare CheXpert 모델, CheXNet 재구현(arnoweng)
- 미세조정 (Kaggle 학습 세트): torchvision DenseNet121 (서비스의 pneumonia 엔진), MONAI DenseNet121 (tools/train_monai.py)
  MMPretrain 은 별도 가상 환경이라 eval/cnn_models_mm.py 가 같은 형식으로 결과를 추가

예) src/CnnService/.venv/Scripts/python eval/cnn_models.py              # 추론 + 채점
    src/CnnService/.venv/Scripts/python eval/cnn_models.py --score      # 저장된 확률로 채점만
결과: eval/results/cnn_models/{dataset}.json (이미지별 정답 + 모델별 소견 확률)
"""

import argparse
import json
import re
import sys
import time
from collections import defaultdict
from pathlib import Path

import numpy as np
import torch
import torchxrayvision as xrv
from PIL import Image
from sklearn.metrics import roc_auc_score
from torchvision import transforms as T
from torchvision.models import densenet121

REPO = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO / "src" / "CnnService"))
from engines.pneumonia import EVAL_TRANSFORM, build_model  # noqa: E402
from imaging import read_gray  # noqa: E402
from cnn_data import IU_TARGETS, iu_items, kaggle_items  # noqa: E402

OUT = REPO / "eval" / "results" / "cnn_models"
MODELS_DIR = REPO / "data" / "models"
CHEXNET_LABELS = ["Atelectasis", "Cardiomegaly", "Effusion", "Infiltration", "Mass", "Nodule", "Pneumonia",
                  "Pneumothorax", "Consolidation", "Edema", "Emphysema", "Fibrosis", "Pleural_Thickening", "Hernia"]

# 정답 이름 ➔ 모델 소견 이름 (모델에 그 소견이 없으면 제외)
MODEL_LABEL = {
    "Pneumonia": ["Pneumonia", "Consolidation"],
    "PneumoniaLike": ["Pneumonia", "Consolidation"],
    "Cardiomegaly": ["Cardiomegaly"],
    "Effusion": ["Effusion"],
    "Atelectasis": ["Atelectasis"],
    "Opacity": ["Lung Opacity"],
    "Edema": ["Edema"],
}


# ---- 전처리 (모델 계열별로 한 번씩) ----

_crop = xrv.datasets.XRayCenterCrop()
_resize = {224: xrv.datasets.XRayResizer(224), 512: xrv.datasets.XRayResizer(512)}
MONAI_SIZE = 224


def xrv_input(gray: np.ndarray, size: int) -> torch.Tensor:
    img = xrv.datasets.normalize(gray.astype(np.float32), 255)[None, ...]
    return torch.from_numpy(_resize[size](_crop(img)))[None]


def imagenet_input(gray: np.ndarray) -> torch.Tensor:
    return EVAL_TRANSFORM(Image.fromarray(gray))[None]


def monai_input(gray: np.ndarray) -> torch.Tensor:
    # tools/train_monai.py 와 같은 전처리: 0~1 스케일 ➔ 224×224 (자르지 않고 전체) ➔ 3채널
    img = torch.from_numpy(gray.astype(np.float32) / 255.0)[None, None]
    img = torch.nn.functional.interpolate(img, size=(MONAI_SIZE, MONAI_SIZE), mode="area")
    return img.repeat(1, 3, 1, 1)


# ---- 모델 ----

class Candidate:
    def __init__(self, name: str, family: str, model: torch.nn.Module, labels: list[str], sigmoid: bool = False):
        self.name, self.family, self.labels, self.sigmoid = name, family, labels, sigmoid
        self.model = model.cuda().eval()

    def __call__(self, x: torch.Tensor) -> dict[str, float]:
        out = self.model(x.cuda())
        out = torch.sigmoid(out) if self.sigmoid else out
        values = out[0].float().cpu().numpy()
        return {label: round(float(v), 5) for label, v in zip(self.labels, values) if label and not np.isnan(v)}


def load_chexnet() -> torch.nn.Module:
    """arnoweng/CheXNet 재구현 가중치 (NIH ChestX-ray14, 14개 소견). 옛 torchvision 키 이름(norm.1)을 새 이름(norm1)으로"""
    ck = torch.load(MODELS_DIR / "chexnet_arnoweng.pth.tar", map_location="cpu", weights_only=False)["state_dict"]
    state = {}
    for key, value in ck.items():
        key = key.replace("module.densenet121.", "")
        key = re.sub(r"\.(norm|relu|conv)\.(\d)", r".\1\2", key)
        state[key.replace("classifier.0.", "classifier.")] = value
    model = densenet121(weights=None)
    model.classifier = torch.nn.Linear(1024, 14)
    model.load_state_dict(state)
    return model


def load_candidates() -> list[Candidate]:
    cands = []
    for w in ["all", "nih", "chex", "rsna", "mimic_ch", "mimic_nb", "pc"]:
        m = xrv.models.DenseNet(weights=f"densenet121-res224-{w}")
        cands.append(Candidate(f"xrv-{w}", "xrv224", m, list(m.pathologies)))
    m = xrv.models.ResNet(weights="resnet50-res512-all")
    cands.append(Candidate("xrv-resnet50-512", "xrv512", m, list(m.pathologies)))
    m = xrv.baseline_models.jfhealthcare.DenseNet()
    cands.append(Candidate("jf-chexpert", "xrv512", m, list(m.pathologies)))
    cands.append(Candidate("chexnet-arnoweng", "imagenet224", load_chexnet(), CHEXNET_LABELS, sigmoid=True))
    tuned = build_model()
    tuned.load_state_dict(torch.load(MODELS_DIR / "pneumonia_densenet121.pt", map_location="cpu"))
    cands.append(Candidate("tuned-torchvision", "imagenet224", tuned, ["Pneumonia"], sigmoid=True))
    monai_path = MODELS_DIR / "pneumonia_monai_densenet121.pt"
    if monai_path.exists():
        from monai.networks.nets import DenseNet121
        net = DenseNet121(spatial_dims=2, in_channels=3, out_channels=1)
        net.load_state_dict(torch.load(monai_path, map_location="cpu"))
        cands.append(Candidate("tuned-monai", "monai224", net, ["Pneumonia"], sigmoid=True))
    return cands


def collect(dataset: str, only: set[str] | None):
    items = kaggle_items() if dataset == "kaggle" else iu_items()
    cands = [c for c in load_candidates() if not only or c.name in only]
    target = OUT / f"{dataset}.json"
    records = json.loads(target.read_text(encoding="utf-8")) if target.exists() else []
    by_image = {r["image"]: r for r in records}
    times = defaultdict(list)
    print(f"{dataset}: {len(items)}장 × 모델 {len(cands)}개")
    with torch.no_grad():
        for i, (path, truth) in enumerate(items, 1):
            gray = read_gray(path)
            inputs = {}
            rec = by_image.setdefault(path.name, {"image": path.name, "truth": truth, "probs": {}})
            for c in cands:
                if c.family not in inputs:
                    inputs[c.family] = (xrv_input(gray, 224) if c.family == "xrv224"
                                        else xrv_input(gray, 512) if c.family == "xrv512"
                                        else monai_input(gray) if c.family == "monai224"
                                        else imagenet_input(gray))
                torch.cuda.synchronize()
                t = time.perf_counter()
                rec["probs"][c.name] = c(inputs[c.family])
                torch.cuda.synchronize()
                times[c.name].append(time.perf_counter() - t)
            if i % 500 == 0:
                print(f"  {i}/{len(items)}", flush=True)
    OUT.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(list(by_image.values()), ensure_ascii=False), encoding="utf-8")
    speed = OUT / "speed.json"
    saved = json.loads(speed.read_text(encoding="utf-8")) if speed.exists() else {}
    saved.update({name: round(float(np.median(v[1:])) * 1000, 1) for name, v in times.items()})
    speed.write_text(json.dumps(saved, indent=2), encoding="utf-8")


def auc(records, model: str, truth_key: str, label: str):
    ys, ps = [], []
    for r in records:
        p = r["probs"].get(model, {}).get(label)
        if p is None:
            return None
        ys.append(r["truth"][truth_key])
        ps.append(p)
    return roc_auc_score(ys, ps) if len(set(ys)) == 2 else None


def score():
    speed = json.loads((OUT / "speed.json").read_text(encoding="utf-8")) if (OUT / "speed.json").exists() else {}
    for dataset, targets in (("kaggle", ["Pneumonia"]), ("iu", list(IU_TARGETS))):
        path = OUT / f"{dataset}.json"
        if not path.exists():
            continue
        records = json.loads(path.read_text(encoding="utf-8"))
        models = sorted({m for r in records for m in r["probs"]})
        pos = {t: sum(r["truth"][t] for r in records) for t in targets}
        cols = [(t, label) for t in targets for label in MODEL_LABEL[t]]
        print(f"\n### {dataset} ({len(records)}장) · 양성 수: " + ", ".join(f"{t} {n}" for t, n in pos.items()))
        print("\n| 모델 | " + " | ".join(f"{t}←{label}" for t, label in cols) + " | 추론 ms |")
        print("|---|" + "---|" * (len(cols) + 1))
        for m in models:
            vals = [auc(records, m, t, label) for t, label in cols]
            print(f"| {m} | " + " | ".join("—" if v is None else f"{v:.3f}" for v in vals) + f" | {speed.get(m, '—')} |")


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--datasets", nargs="+", default=["kaggle", "iu"])
    p.add_argument("--models", nargs="*", help="이 모델만 (다시) 추론")
    p.add_argument("--score", action="store_true")
    args = p.parse_args()
    if not args.score:
        for d in args.datasets:
            collect(d, set(args.models) if args.models else None)
    score()


if __name__ == "__main__":
    main()

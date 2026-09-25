"""2차-1b: MMPretrain(구 MMClassification) 모델 zoo 의 ConvNeXt-Tiny(ImageNet) 를 폐렴 이진 분류로 미세조정·평가.

MMPretrain 은 흉부 X-ray 사전학습 모델이 없어 "다른 구조(ConvNeXt)의 ImageNet 가중치를 가져오는 도구"로 비교한다.
torchvision 미세조정(src/CnnService/tools/train_pneumonia.py)과 같은 분할·epoch·학습률·전처리, 네트워크만 다름.
OpenMMLab 은 torch 2.11 과 한 환경에 두기 위해 별도 가상 환경 (mmcv-lite: 분류에는 CUDA 연산자 불필요)

설치 (별도 venv):
  uv venv --python 3.11 mm-venv
  uv pip install torch==2.11.0 torchvision==0.26.0 --index-url https://download.pytorch.org/whl/cu128
  uv pip install mmpretrain mmcv-lite opencv-python-headless scikit-learn pillow
예) <mm-venv>/Scripts/python eval/cnn_models_mm.py --train
결과: data/models/pneumonia_mmpretrain_convnext_t.pt, eval/results/cnn_models/{kaggle,iu}.json 에 "tuned-mmpretrain-convnext-t" 추가
"""

import argparse
import json
import sys
import time
from pathlib import Path

import numpy as np
import torch
from PIL import Image
from sklearn.metrics import roc_auc_score
from torchvision import transforms as T

from cnn_data import REPO, iu_items, kaggle_items

sys.path.insert(0, str(REPO / "src" / "CnnService"))
from imaging import read_gray  # noqa: E402
from tools.splits import SEED, train_val  # noqa: E402

NAME = "tuned-mmpretrain-convnext-t"
CHECKPOINT = REPO / "data" / "models" / "pneumonia_mmpretrain_convnext_t.pt"
OUT = REPO / "eval" / "results" / "cnn_models"
NORM = T.Normalize([0.485, 0.456, 0.406], [0.229, 0.224, 0.225])
TRAIN = T.Compose([T.Grayscale(3), T.RandomResizedCrop(224, scale=(0.8, 1.0)), T.RandomRotation(7), T.ToTensor(), NORM])
EVAL = T.Compose([T.Grayscale(3), T.Resize(256), T.CenterCrop(224), T.ToTensor(), NORM])


class Net(torch.nn.Module):
    """MMPretrain ConvNeXt-T 백본 + GAP 넥 (768) ➔ 폐렴 로짓 1개"""

    def __init__(self, pretrained: bool):
        super().__init__()
        from mmpretrain import get_model
        self.body = get_model("convnext-tiny_32xb128_in1k", pretrained=pretrained)
        self.head = torch.nn.Linear(768, 1)

    def forward(self, x):
        feat = self.body.extract_feat(x)
        feat = feat[-1] if isinstance(feat, (tuple, list)) else feat
        return self.head(feat)


class Data(torch.utils.data.Dataset):
    def __init__(self, items, transform):
        self.items, self.transform = items, transform

    def __len__(self):
        return len(self.items)

    def __getitem__(self, i):
        path, label = self.items[i]
        return self.transform(Image.fromarray(read_gray(path))), torch.tensor(label, dtype=torch.float32)


def train(epochs: int):
    torch.manual_seed(SEED)
    tr, va = train_val()
    pos = sum(y for _, y in tr)
    print(f"학습 {len(tr)}장 · 검증 {len(va)}장 (torchvision 미세조정과 같은 분할)")
    model = Net(pretrained=True).cuda()
    loss_fn = torch.nn.BCEWithLogitsLoss(pos_weight=torch.tensor((len(tr) - pos) / pos).cuda())
    opt = torch.optim.AdamW(model.parameters(), lr=1e-4, weight_decay=1e-4)
    loader = torch.utils.data.DataLoader(Data(tr, TRAIN), batch_size=32, shuffle=True)
    vloader = torch.utils.data.DataLoader(Data(va, EVAL), batch_size=64)
    best, history = 0.0, []
    CHECKPOINT.parent.mkdir(parents=True, exist_ok=True)
    for epoch in range(1, epochs + 1):
        started = time.perf_counter()
        model.train()
        for x, y in loader:
            opt.zero_grad()
            with torch.autocast("cuda", dtype=torch.bfloat16):
                loss = loss_fn(model(x.cuda()).squeeze(1), y.cuda())
            loss.backward()
            opt.step()
        model.eval()
        ps, ys = [], []
        with torch.no_grad():
            for x, y in vloader:
                ps += torch.sigmoid(model(x.cuda()).squeeze(1)).float().cpu().tolist()
                ys += y.tolist()
        auc = roc_auc_score(ys, ps)
        history.append({"epoch": epoch, "val_auc": round(auc, 4), "seconds": round(time.perf_counter() - started)})
        print(f"epoch {epoch}: 검증 AUC {auc:.4f} ({history[-1]['seconds']}초)")
        if auc > best:
            best = auc
            torch.save(model.state_dict(), CHECKPOINT)
    CHECKPOINT.with_suffix(".json").write_text(json.dumps(
        {"framework": "MMPretrain 1.2 (mmcv-lite)", "base": "convnext-tiny_32xb128_in1k", "best_val_auc": round(best, 4),
         "history": history}, ensure_ascii=False, indent=2), encoding="utf-8")


def evaluate():
    model = Net(pretrained=False)
    model.load_state_dict(torch.load(CHECKPOINT, map_location="cpu"))
    model = model.cuda().eval()
    times = []
    for dataset, items in (("kaggle", kaggle_items()), ("iu", iu_items())):
        path = OUT / f"{dataset}.json"
        records = {r["image"]: r for r in json.loads(path.read_text(encoding="utf-8"))}
        with torch.no_grad():
            for p, truth in items:
                x = EVAL(Image.fromarray(read_gray(p)))[None].cuda()
                torch.cuda.synchronize()
                t = time.perf_counter()
                prob = float(torch.sigmoid(model(x))[0, 0])
                torch.cuda.synchronize()
                times.append(time.perf_counter() - t)
                records.setdefault(p.name, {"image": p.name, "truth": truth, "probs": {}})["probs"][NAME] = {
                    "Pneumonia": round(prob, 5)}
        path.write_text(json.dumps(list(records.values()), ensure_ascii=False), encoding="utf-8")
        print(f"{dataset}: {len(items)}장 추가")
    speed = OUT / "speed.json"
    saved = json.loads(speed.read_text(encoding="utf-8")) if speed.exists() else {}
    saved[NAME] = round(float(np.median(times[1:])) * 1000, 1)
    speed.write_text(json.dumps(saved, indent=2), encoding="utf-8")


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--train", action="store_true")
    p.add_argument("--epochs", type=int, default=3)
    args = p.parse_args()
    if args.train:
        train(args.epochs)
    evaluate()


if __name__ == "__main__":
    main()

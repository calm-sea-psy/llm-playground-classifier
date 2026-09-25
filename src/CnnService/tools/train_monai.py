"""MONAI 로 폐렴 이진 분류 미세조정 ➔ data/models/pneumonia_monai_densenet121.pt (2차-1b 프레임워크 비교용)

torchvision 미세조정(tools/train_pneumonia.py)과 같은 데이터 분할·epoch·학습률에서, 파이프라인만 MONAI 방식으로 바꿈:
- 네트워크: monai.networks.nets.DenseNet121 (ImageNet 사전학습 가중치)
- 전처리: 0~1 스케일, 자르지 않고 224×224 로 축소 (의료 영상 관례: 가장자리 병변을 잘라내지 않음)
- 증강: MONAI RandRotate · RandZoom · RandGaussianNoise (의료 영상용 변환)

예) src/CnnService/.venv/Scripts/python src/CnnService/tools/train_monai.py
"""

import argparse
import json
import sys
import time
from datetime import datetime
from pathlib import Path

import numpy as np
import torch
from monai import transforms as M
from monai.networks.nets import DenseNet121
from sklearn.metrics import roc_auc_score

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from imaging import read_gray  # noqa: E402
from tools.splits import REPO_ROOT, SEED, train_val  # noqa: E402

SIZE = 224

TRAIN = M.Compose([
    M.Resize((SIZE, SIZE), mode="area"),
    M.RandRotate(range_x=np.deg2rad(7), prob=0.8, padding_mode="zeros"),
    M.RandZoom(min_zoom=0.9, max_zoom=1.1, prob=0.5),
    M.RandGaussianNoise(prob=0.2, std=0.02),
])
EVAL = M.Resize((SIZE, SIZE), mode="area")


class Data(torch.utils.data.Dataset):
    def __init__(self, items, transform):
        self.items, self.transform = items, transform

    def __len__(self):
        return len(self.items)

    def __getitem__(self, i):
        path, label = self.items[i]
        img = torch.from_numpy(read_gray(path).astype(np.float32) / 255.0)[None]  # [1, H, W], 0~1
        img = torch.as_tensor(self.transform(img)).repeat(3, 1, 1)  # ImageNet 가중치라 3채널
        return img, torch.tensor(label, dtype=torch.float32)


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--epochs", type=int, default=3)
    p.add_argument("--out", type=Path, default=REPO_ROOT / "data" / "models" / "pneumonia_monai_densenet121.pt")
    args = p.parse_args()
    torch.manual_seed(SEED)

    train, val = train_val()
    pos = sum(y for _, y in train)
    print(f"학습 {len(train)}장 (폐렴 {pos}) · 검증 {len(val)}장 — 환자 기준 분리 (torchvision 미세조정과 같은 분할)")

    model = DenseNet121(spatial_dims=2, in_channels=3, out_channels=1, pretrained=True).cuda()
    loss_fn = torch.nn.BCEWithLogitsLoss(pos_weight=torch.tensor((len(train) - pos) / pos).cuda())
    opt = torch.optim.AdamW(model.parameters(), lr=1e-4, weight_decay=1e-4)
    loader = torch.utils.data.DataLoader(Data(train, TRAIN), batch_size=32, shuffle=True)
    vloader = torch.utils.data.DataLoader(Data(val, EVAL), batch_size=64)

    best, history = 0.0, []
    args.out.parent.mkdir(parents=True, exist_ok=True)
    for epoch in range(1, args.epochs + 1):
        started = time.perf_counter()
        model.train()
        for x, y in loader:
            opt.zero_grad()
            with torch.autocast("cuda", dtype=torch.bfloat16):
                loss = loss_fn(model(x.cuda()).squeeze(1), y.cuda())
            loss.backward()
            opt.step()
        model.eval()
        probs, labels = [], []
        with torch.no_grad():
            for x, y in vloader:
                probs += torch.sigmoid(model(x.cuda()).squeeze(1)).float().cpu().tolist()
                labels += y.tolist()
        auc = roc_auc_score(labels, probs)
        history.append({"epoch": epoch, "val_auc": round(auc, 4), "seconds": round(time.perf_counter() - started)})
        print(f"epoch {epoch}: 검증 AUC {auc:.4f} ({history[-1]['seconds']}초)")
        if auc > best:
            best = auc
            torch.save(model.state_dict(), args.out)

    args.out.with_suffix(".json").write_text(json.dumps({
        "trained": f"{datetime.now():%Y-%m-%d}", "framework": "MONAI", "base": "monai DenseNet121 (ImageNet)",
        "data": "Kaggle Chest X-Ray Images (Pneumonia) train, 환자 10% 검증 분리 (seed 42)",
        "best_val_auc": round(best, 4), "history": history,
    }, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"저장 ➔ {args.out} (검증 AUC {best:.4f})")


if __name__ == "__main__":
    main()

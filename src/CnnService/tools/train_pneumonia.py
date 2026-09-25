"""Kaggle 소아 흉부 X-ray(정상/폐렴)로 ImageNet DenseNet121 미세조정 ➔ data/models/pneumonia_densenet121.pt

2차-0 스파이크 설정 그대로: 3 epoch, AdamW 1e-4, 배치 32, bfloat16 autocast, 폐렴:정상 불균형은 pos_weight 로 보정.
검증 AUC 가 가장 높은 epoch 를 저장하고, 옆에 학습 정보(.json)를 남긴다.

예) src/CnnService/.venv/Scripts/python src/CnnService/tools/train_pneumonia.py
"""

import argparse
import json
import sys
import time
from datetime import datetime
from pathlib import Path

import torch
from PIL import Image
from sklearn.metrics import roc_auc_score
from torchvision import transforms as T
from torchvision.models import DenseNet121_Weights, densenet121

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from engines.pneumonia import EVAL_TRANSFORM  # noqa: E402
from tools.splits import REPO_ROOT, SEED, train_val  # noqa: E402

TRAIN_TRANSFORM = T.Compose([
    T.Grayscale(3), T.RandomResizedCrop(224, scale=(0.8, 1.0)), T.RandomRotation(7), T.ToTensor(),
    T.Normalize([0.485, 0.456, 0.406], [0.229, 0.224, 0.225]),
])


class Data(torch.utils.data.Dataset):
    def __init__(self, items, transform):
        self.items, self.transform = items, transform

    def __len__(self):
        return len(self.items)

    def __getitem__(self, i):
        path, label = self.items[i]
        return self.transform(Image.open(path).convert("L")), torch.tensor(label, dtype=torch.float32)


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--epochs", type=int, default=3)
    p.add_argument("--out", type=Path, default=REPO_ROOT / "data" / "models" / "pneumonia_densenet121.pt")
    args = p.parse_args()
    torch.manual_seed(SEED)

    train, val = train_val()
    pos = sum(y for _, y in train)
    print(f"학습 {len(train)}장 (폐렴 {pos}) · 검증 {len(val)}장 — 환자 기준 분리")

    model = densenet121(weights=DenseNet121_Weights.IMAGENET1K_V1)
    model.classifier = torch.nn.Linear(model.classifier.in_features, 1)
    model = model.cuda()
    loss_fn = torch.nn.BCEWithLogitsLoss(pos_weight=torch.tensor((len(train) - pos) / pos).cuda())
    opt = torch.optim.AdamW(model.parameters(), lr=1e-4, weight_decay=1e-4)
    loader = torch.utils.data.DataLoader(Data(train, TRAIN_TRANSFORM), batch_size=32, shuffle=True)
    vloader = torch.utils.data.DataLoader(Data(val, EVAL_TRANSFORM), batch_size=64)

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
        "trained": f"{datetime.now():%Y-%m-%d}", "base": "torchvision densenet121 IMAGENET1K_V1",
        "data": "Kaggle Chest X-Ray Images (Pneumonia) train, 환자 10% 검증 분리 (seed 42)",
        "train_images": len(train), "val_images": len(val), "best_val_auc": round(best, 4), "history": history,
    }, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"저장 ➔ {args.out} (검증 AUC {best:.4f})")


if __name__ == "__main__":
    main()

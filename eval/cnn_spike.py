"""2차-0 스파이크: 흉부 X-ray CNN 후보를 Kaggle 폐렴 테스트 세트(정상 234 · 폐렴 390)로 빠르게 확인한다.

후보
- xrv  : TorchXRayVision DenseNet121 (densenet121-res224-all, 성인 흉부 X-ray 여러 데이터셋으로 학습, 18개 소견)
         ➔ 학습 없이 "Pneumonia" 등 소견 확률을 그대로 사용
- tuned: ImageNet 사전학습 모델을 Kaggle 학습 세트로 미세조정 (--train 으로 학습, 체크포인트 저장)

보는 것: AUC, 기준값 0.5 에서의 민감도·특이도, 장당 추론 시간, 최대 VRAM

예) src/CnnService/.venv/Scripts/python eval/cnn_spike.py xrv
    src/CnnService/.venv/Scripts/python eval/cnn_spike.py tuned --train --epochs 3
"""

import argparse
import random
import sys
import time
from pathlib import Path

import numpy as np
import torch
from PIL import Image
from sklearn.metrics import roc_auc_score

REPO = Path(__file__).resolve().parent.parent
DATA = REPO / "data" / "samples" / "pneumonia" / "chest_xray"
CHECKPOINT = REPO / "eval" / "results" / "cnn_spike" / "tuned.pt"


def split(name: str) -> list[tuple[Path, int]]:
    items = []
    for label, cls in ((0, "NORMAL"), (1, "PNEUMONIA")):
        items += [(p, label) for p in sorted((DATA / name / cls).glob("*.jpeg"))]
    return items


def report(name: str, labels: list[int], probs: list[float], times: list[float]):
    y, p = np.array(labels), np.array(probs)
    pred = p >= 0.5
    sens = (pred & (y == 1)).sum() / (y == 1).sum()
    spec = (~pred & (y == 0)).sum() / (y == 0).sum()
    # 민감도와 특이도가 같아지는 기준값 (분포가 0.5 근처에 있지 않은 모델 비교용)
    order = np.linspace(0, 1, 201)
    balanced = min(order, key=lambda t: abs(((p >= t) & (y == 1)).sum() / (y == 1).sum()
                                          - ((p < t) & (y == 0)).sum() / (y == 0).sum()))
    b_sens = ((p >= balanced) & (y == 1)).sum() / (y == 1).sum()
    print(f"\n[{name}] 테스트 {len(y)}장 (폐렴 {(y == 1).sum()} · 정상 {(y == 0).sum()})")
    print(f"  AUC {roc_auc_score(y, p):.3f}")
    print(f"  기준 0.5: 민감도 {sens:.1%} · 특이도 {spec:.1%}")
    print(f"  균형 기준 {balanced:.3f}: 민감도=특이도 약 {b_sens:.1%}")
    print(f"  장당 추론 {np.median(times) * 1000:.1f}ms (중앙값, 전처리 제외) · 최대 VRAM {torch.cuda.max_memory_allocated() / 2**20:.0f}MB")


def run_xrv(test):
    import torchxrayvision as xrv
    model = xrv.models.DenseNet(weights="densenet121-res224-all").cuda().eval()
    idx = model.pathologies.index("Pneumonia")
    transform = xrv.datasets.XRayCenterCrop()
    resize = xrv.datasets.XRayResizer(224)
    probs, times, labels = [], [], []
    others = {k: [] for k in ("Lung Opacity", "Consolidation", "Infiltration")}
    with torch.no_grad():
        for path, label in test:
            img = np.array(Image.open(path).convert("L")).astype(np.float32)
            img = xrv.datasets.normalize(img, 255)[None, ...]
            img = resize(transform(img))
            x = torch.from_numpy(img)[None].cuda()
            torch.cuda.synchronize()
            t = time.perf_counter()
            out = model(x)[0].cpu().numpy()
            torch.cuda.synchronize()
            times.append(time.perf_counter() - t)
            probs.append(float(out[idx]))
            for k in others:
                others[k].append(float(out[model.pathologies.index(k)]))
            labels.append(label)
    report("xrv densenet121-res224-all · Pneumonia", labels, probs, times[1:])
    y = np.array(labels)
    for k, v in others.items():
        print(f"  참고: {k} AUC {roc_auc_score(y, v):.3f}")


def tuned_model():
    from torchvision.models import DenseNet121_Weights, densenet121
    model = densenet121(weights=DenseNet121_Weights.IMAGENET1K_V1)
    model.classifier = torch.nn.Linear(model.classifier.in_features, 1)
    return model


def tuned_transform(train: bool):
    from torchvision import transforms as T
    steps = [T.Grayscale(3), T.Resize(256), T.CenterCrop(224)]
    if train:
        steps = [T.Grayscale(3), T.RandomResizedCrop(224, scale=(0.8, 1.0)), T.RandomRotation(7)]
    return T.Compose(steps + [T.ToTensor(), T.Normalize([0.485, 0.456, 0.406], [0.229, 0.224, 0.225])])


def train_tuned(epochs: int):
    # Kaggle val 은 16장뿐 ➔ 학습 세트에서 환자 구분 없이 10% 를 검증용으로 다시 나눔 (파일명의 person 번호로 나눠 누수 방지)
    train = split("train")
    persons = sorted({p.name.split("_")[0] for p, _ in train})
    rng = random.Random(42)
    val_persons = set(rng.sample(persons, len(persons) // 10))
    tr = [x for x in train if x[0].name.split("_")[0] not in val_persons]
    va = [x for x in train if x[0].name.split("_")[0] in val_persons]
    print(f"학습 {len(tr)}장 · 검증 {len(va)}장 (환자 번호 기준 분리)")

    class Data(torch.utils.data.Dataset):
        def __init__(self, items, train):
            self.items, self.t = items, tuned_transform(train)

        def __len__(self):
            return len(self.items)

        def __getitem__(self, i):
            p, y = self.items[i]
            return self.t(Image.open(p).convert("RGB")), torch.tensor(y, dtype=torch.float32)

    model = tuned_model().cuda()
    pos = sum(y for _, y in tr)
    loss_fn = torch.nn.BCEWithLogitsLoss(pos_weight=torch.tensor((len(tr) - pos) / pos).cuda())  # 폐렴 3:1 불균형 보정
    opt = torch.optim.AdamW(model.parameters(), lr=1e-4, weight_decay=1e-4)
    loader = torch.utils.data.DataLoader(Data(tr, True), batch_size=32, shuffle=True, num_workers=0)
    vloader = torch.utils.data.DataLoader(Data(va, False), batch_size=64, num_workers=0)
    best = 0.0
    for epoch in range(1, epochs + 1):
        model.train()
        t = time.perf_counter()
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
        print(f"epoch {epoch}: 검증 AUC {auc:.3f} ({time.perf_counter() - t:.0f}초)")
        if auc > best:
            best = auc
            CHECKPOINT.parent.mkdir(parents=True, exist_ok=True)
            torch.save(model.state_dict(), CHECKPOINT)


def run_tuned(test):
    model = tuned_model()
    model.load_state_dict(torch.load(CHECKPOINT))
    model = model.cuda().eval()
    t_eval = tuned_transform(False)
    probs, times, labels = [], [], []
    with torch.no_grad():
        for path, label in test:
            x = t_eval(Image.open(path).convert("RGB"))[None].cuda()
            torch.cuda.synchronize()
            t = time.perf_counter()
            probs.append(float(torch.sigmoid(model(x))[0, 0]))
            torch.cuda.synchronize()
            times.append(time.perf_counter() - t)
            labels.append(label)
    report("tuned densenet121 (ImageNet ➔ Kaggle 학습)", labels, probs, times[1:])


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("candidate", choices=["xrv", "tuned"])
    p.add_argument("--train", action="store_true")
    p.add_argument("--epochs", type=int, default=3)
    args = p.parse_args()
    torch.manual_seed(42)
    test = split("test")
    if args.candidate == "xrv":
        run_xrv(test)
        return
    if args.train:
        train_tuned(args.epochs)
    torch.cuda.reset_peak_memory_stats()
    run_tuned(test)


if __name__ == "__main__":
    main()

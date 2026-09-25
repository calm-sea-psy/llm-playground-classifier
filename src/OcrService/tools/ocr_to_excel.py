"""샘플 이미지를 OCR 해서 검증용 엑셀을 만든다 (서버 없이 엔진 어댑터를 직접 호출).

예) src/OcrService/.venv/Scripts/python src/OcrService/tools/ocr_to_excel.py --input data/samples/korie/images --limit 150
"""

import argparse
import random
import sys
import time
from datetime import datetime
from pathlib import Path

SERVICE_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = SERVICE_DIR.parent.parent
sys.path.insert(0, str(SERVICE_DIR))

from engines import EngineRegistry  # noqa: E402
from imaging import ImageDecodeError, read_image  # noqa: E402
from verify_excel import VerifyWorkbook  # noqa: E402

IMAGE_EXTS = {".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp"}


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--input", type=Path, default=REPO_ROOT / "data" / "samples",
                        help="이미지 폴더 (하위 폴더 포함) 또는 이미지 파일 1개")
    parser.add_argument("--out", type=Path,
                        default=REPO_ROOT / "data" / "verify" / f"ocr_verify_{datetime.now():%Y%m%d}.xlsx")
    parser.add_argument("--engine", default=None, help="생략 시 config.toml 의 default_engine")
    parser.add_argument("--limit", type=int, default=None, help="최대 문서 수")
    parser.add_argument("--sample", action="store_true", help="--limit 개를 무작위로 뽑음 (기본: 파일명 순 앞에서부터)")
    parser.add_argument("--seed", type=int, default=42)
    args = parser.parse_args()

    root = args.input if args.input.is_dir() else args.input.parent
    paths = ([args.input] if args.input.is_file()
             else sorted(p for p in args.input.rglob("*") if p.suffix.lower() in IMAGE_EXTS))
    if args.limit is not None and len(paths) > args.limit:
        paths = random.Random(args.seed).sample(paths, args.limit) if args.sample else paths[:args.limit]
        paths.sort()
    if not paths:
        sys.exit(f"이미지가 없습니다: {args.input}")

    engine = EngineRegistry().get(args.engine)
    print(f"engine={engine.name} ({engine.model_version}), 문서 {len(paths)}건 ➔ {args.out}")

    book = VerifyWorkbook()
    failed = []
    started = time.perf_counter()
    for i, path in enumerate(paths, start=1):
        name = path.relative_to(root).as_posix()
        try:
            image = read_image(path)
        except ImageDecodeError as e:
            failed.append(name)
            print(f"[{i}/{len(paths)}] {name}: 건너뜀 ({e})")
            continue
        result = engine.run(image)
        book.add_document(name, image, result)
        print(f"[{i}/{len(paths)}] {name}: {len(result.lines)}줄, "
              f"평균 신뢰도 {result.avg_confidence}, {result.elapsed_ms}ms")

    args.out.parent.mkdir(parents=True, exist_ok=True)
    book.save(args.out)
    print(f"완료: {len(paths) - len(failed)}건, 실패 {len(failed)}건, "
          f"{time.perf_counter() - started:.1f}초 ➔ {args.out}")


if __name__ == "__main__":
    main()

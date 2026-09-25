#!/usr/bin/env bash
# GPU 경합 대책 비교: Ollama 모델 유지(Never) vs 항상 내리기(Always) vs 큰 이미지만(LargeImages), 같은 문서로 실제 파이프라인 측정
set -u
cd "$(dirname "$0")/../.."
PY=src/OcrService/.venv/Scripts/python.exe
LOG=eval/bench/bench.log

stop_api() { powershell -NoProfile -Command "\$c = Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1; if (\$c) { Stop-Process -Id \$c.OwningProcess -Force }"; sleep 2; }
start_api() {  # $1 = OCR 엔진, $2 = UnloadBeforeOcr
  stop_api
  (cd src/Api && DOTNET_CLI_TELEMETRY_OPTOUT=1 Ocr__Engine=$1 Ocr__TimeoutSeconds=180 Llm__UnloadBeforeOcr=$2 \
     dotnet run --no-build --launch-profile http > ../../eval/bench/api_$1_$2.log 2>&1 &)
  for i in $(seq 1 60); do curl -s --max-time 2 http://127.0.0.1:5000/health/live >/dev/null && return; sleep 1; done
  echo "API 시작 실패" >> $LOG; exit 1
}

for mode in Never Always LargeImages; do
  unload=$mode
  start_api paddleocr $unload
  $PY -u eval/run_eval.py --run bench_${mode}_receipts --dataset korie --image-list eval/bench/receipts.txt --models gemma4:12b >> $LOG 2>&1
  start_api ppstructure $unload
  $PY -u eval/run_eval.py --run bench_${mode}_claims --dataset insurance_claim --image-list eval/bench/claims.txt --models gemma4:12b >> $LOG 2>&1
done
stop_api
start_api paddleocr LargeImages   # 기본 설정으로 복구
echo "BENCH DONE" >> $LOG

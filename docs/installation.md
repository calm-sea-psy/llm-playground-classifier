# 설치와 실행

[README](../README.md) · [구조](architecture.md) · [설치와 실행](installation.md) · [화면 사용법](user-guide.md) · [평가 가이드](evaluation-guide.md)

## 요구 사항

- Windows 11 (다른 OS에서는 모니터링 설정의 실행 경로 `.venv/Scripts/...` 와 `cmd /c` 를 바꿔야 함)
- NVIDIA GPU + 드라이버. VRAM 16GB 권장 (OCR과 12B LLM을 함께 올림). RTX 50 시리즈 기준으로 paddlepaddle-gpu는 CUDA 12.9, torch는 CUDA 12.8 빌드를 씁니다.
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/) 22 이상 (개발 환경: 24)
- Python 3.11 + [uv](https://docs.astral.sh/uv/)
- [Ollama](https://ollama.com/) 0.34 이상

## 설치

### 1. 환경 변수와 DB 계정

```bash
cp .env.example .env
cp src/Api/appsettings.Development.example.json src/Api/appsettings.Development.json
cp src/Monitor/appsettings.Development.example.json src/Monitor/appsettings.Development.json
```

`.env` 의 `POSTGRES_PASSWORD` 를 바꿨다면 두 `appsettings.Development.json` 의 연결 문자열 비밀번호도 같게 맞춥니다. 세 파일은 모두 git에서 제외됩니다.

### 2. PostgreSQL

```bash
docker compose up -d postgres
```

테이블은 API가 처음 시작할 때 마이그레이션으로 만듭니다. 데이터를 모두 지우려면 `docker compose down -v` 를 실행합니다.

### 3. OCR 서비스

```bash
cd src/OcrService
uv venv --python 3.11
uv pip install -r requirements.txt
```

paddlepaddle-gpu는 `requirements.txt` 에 적힌 Paddle 인덱스(cu129)에서 받습니다. OCR 모델 파일은 처음 실행할 때 자동으로 내려받습니다. 엔진 설정은 `src/OcrService/config.toml` 에 있습니다.

### 4. CNN 서비스 (흉부 X-ray 기능을 쓸 때)

torch와 paddle이 충돌하지 않도록 OCR 서비스와 **별도 가상 환경**을 씁니다.

```bash
cd src/CnnService
uv venv --python 3.11
uv pip install -r requirements.txt
```

- 성인 18소견 모델(TorchXRayVision `densenet121-res224-all`)은 처음 실행할 때 자동으로 내려받습니다.
- 소아 폐렴 모델은 직접 학습합니다. [Kaggle Chest X-Ray Images (Pneumonia)](https://www.kaggle.com/datasets/paultimothymooney/chest-xray-pneumonia) 를 `data/samples/pneumonia/chest_xray/` 에 풀고 아래를 실행하면 `data/models/pneumonia_densenet121.pt` 가 생깁니다 (3 epoch, RTX 5080 기준 약 4분).

```bash
src/CnnService/.venv/Scripts/python src/CnnService/tools/train_pneumonia.py
```

엔진과 판정 기준은 `src/CnnService/config.toml` 에 있습니다. 폐렴 기준값 0.989는 Kaggle test 절반으로 민감도 95%에 맞춘 값입니다.

### 5. Ollama 모델

```bash
ollama pull gemma4:12b
```

```bash
ollama pull qwen3-vl:8b-instruct
```

`gemma4:12b` 가 기본 모델이고 나머지는 비교용입니다. 추론형 `qwen3-vl:8b` 는 이미지 입력에서 추론을 반복하는 경우가 있어, X-ray 비교에는 instruct 판을 권장합니다. 평가할 모델 목록은 `src/Api/appsettings.json` 의 `Llm:Models` 에서 바꿉니다. Ollama에 받아 둔 모델이면 무엇이든 추가해 비교할 수 있습니다.

### 6. API와 웹

```bash
cd src/Api
dotnet build
```

```bash
cd src/Web
npm install
```

### 7. (선택) 정답 데이터

정답이 있으면 실험 점수에 **정확도**가 들어갑니다. 없으면 검증 통과율과 속도로만 비교합니다. `data/` 아래 데이터는 라이선스 때문에 저장소에 포함하지 않습니다.

| 기능 | 파일 | 짝 맞추는 방법 |
|---|---|---|
| 문서 필드 정확도 | `data/verify/korie_fields_labels.csv` (KORIE 영수증 헤더 필드 정답) | 파일명(확장자 제외, 예: `IMG00007`) |
| X-ray 폐렴 정답 (소아) | 없음 (Kaggle 파일명이 라벨) | `person…_bacteria/virus` = 폐렴, `IM-…` = 정상 |
| X-ray 정답 (성인) | `data/samples/iu-xray/indiana_reports.csv` ([Chest X-rays (Indiana University)](https://www.kaggle.com/datasets/raddar/chest-xrays-indiana-university)) | 파일명 앞의 uid ➔ 소견서 MeSH |

**자기 회사 문서로 평가할 때**: 같은 형식의 CSV(파일명 + 필드별 정답)를 만들어 `data/verify/` 에 두거나, 정답 없이 검증 통과율과 사람 확인 비율로 먼저 비교합니다. 새 문서 종류는 `src/Api/Modules/Text/Pipeline/` 의 스키마·검증 규칙과 `Prompts/extract.{종류}.md` 를 추가합니다.

## 실행

### 한 번에 실행 (모니터링 서버)

```bash
cd src/Monitor
dotnet run
```

모니터링 서버가 꺼져 있는 서비스를 PostgreSQL ➔ OCR·CNN ➔ Ollama ➔ API ➔ 웹 순서로 시작하고, 이후 죽은 서비스를 자동으로 다시 시작합니다. 감시 대상은 `src/Monitor/monitor.json` 에 있고, `EnabledSteps` 에 없는 기능이 쓰는 서비스(예: `image` 를 빼면 CNN)는 띄우지 않습니다.

- API는 `--no-build` 로 실행하므로 **코드를 바꾼 뒤에는 `src/Api` 에서 `dotnet build` 를 먼저** 합니다.
- API를 다시 빌드할 때는 모니터링 서버를 먼저 멈춥니다. 켜 둔 채로 API만 멈추면 곧바로 다시 띄워서 빌드 파일이 잠깁니다.
- Ollama는 Windows 설치판의 트레이 앱이 관리하므로 감시만 합니다.

### 따로 실행

```bash
docker compose up -d postgres
```

```bash
cd src/OcrService
.venv/Scripts/uvicorn.exe main:app --host 127.0.0.1 --port 8001
```

```bash
cd src/CnnService
.venv/Scripts/uvicorn.exe main:app --host 127.0.0.1 --port 8002
```

```bash
cd src/Api
dotnet run --launch-profile http
```

```bash
cd src/Web
npm run dev
```

브라우저에서 `http://localhost:5173` 을 엽니다.

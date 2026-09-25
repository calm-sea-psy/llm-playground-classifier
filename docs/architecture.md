# 구조

[README](../README.md) · [구조](architecture.md) · [설치와 실행](installation.md) · [화면 사용법](user-guide.md) · [평가 가이드](evaluation-guide.md)

## 전체 구조

```mermaid
flowchart TB
    User(["브라우저"])

    subgraph Stack["애플리케이션"]
        direction TB
        subgraph Web["웹 · React + Vite :5173"]
            Pages["문서 텍스트 추출 · 흉부 X-ray 판독<br/>X-ray + 소견서 통합 · 프롬프트"]
        end

        subgraph Api["API · ASP.NET Core :5000"]
            Endpoints["REST 엔드포인트<br/>업로드 · 설정 · 실험 · 프롬프트 · 기기 사양"]
            Queue[["작업 큐 (Channel)"]]
            Worker["백그라운드 워커<br/>모듈: Text · Image · Multimodal<br/>Semantic Kernel + C# 규칙 검증"]
            Prompts["프롬프트 저장소<br/>파일 기본값 + DB 버전"]
            Hub["SignalR 허브 /hubs/jobs"]
        end

        subgraph Gpu["GPU 1장 공유"]
            Ocr["OCR 서비스 · FastAPI :8001<br/>PaddleOCR (경로 A)<br/>PP-StructureV3 (경로 B)"]
            Cnn["CNN 서비스 · FastAPI :8002<br/>TorchXRayVision (18소견)<br/>폐렴 미세조정 DenseNet · Grad-CAM"]
            Ollama["Ollama :11434<br/>gemma4:12b · qwen3-vl:8b(-instruct)"]
        end

        Db[("PostgreSQL :5432<br/>jobs · 결과 테이블 · experiments<br/>pipeline_settings · prompt_versions")]
        Files[/"data/uploads/{jobId}/<br/>원본 · OCR · 히트맵 · LLM 기록"/]
    end

    subgraph Monitor["모니터링 서버 :5100"]
        Checker["헬스 체크 · 자동 재시작<br/>상태 페이지 · 이벤트 기록"]
    end
    Webhook(["Discord / Slack 웹훅"])

    User --> Pages
    Pages -- "/api (Vite 프록시)" --> Endpoints
    Endpoints --> Queue --> Worker
    Worker --> Prompts
    Worker -- "진행 상황" --> Hub
    Hub -. "실시간 푸시" .-> Pages
    Worker -- "문서 텍스트 + 위치" --> Ocr
    Worker -- "X-ray 소견 확률 + 히트맵" --> Cnn
    Worker -- "분류 · 추출 · 판독 · 요약" --> Ollama
    Endpoints --> Db
    Worker --> Db
    Worker --> Files
    Checker -. "감시 · 재시작" .-> Stack
    Checker -- "상태 변경 알림" --> Webhook
```

## 처리 흐름

**문서 텍스트 추출** (영수증, 상업송장, 보험 청구서)

```mermaid
flowchart LR
    Upload["업로드<br/>(설정 스냅숏)"] --> OcrStep["OCR<br/>텍스트 + 위치 + 신뢰도"]
    OcrStep --> Classify["문서 분류<br/>(LLM)"]
    Classify --> Extract["필드 추출<br/>(LLM, JSON 스키마)"]
    Extract --> Validate{"규칙 검증<br/>합계 · 수량×단가 · 날짜"}
    Validate -- "통과" --> Save["저장"]
    Validate -- "오류 또는 저신뢰" --> Vlm["VLM 폴백<br/>(이미지 + OCR 텍스트)"]
    Vlm --> Revalidate["재검증 후<br/>더 나은 결과"] --> Save
```

**흉부 X-ray 판독**과 **X-ray + 소견서 통합**

```mermaid
flowchart LR
    Xray["X-ray"] --> CnnStep["CNN<br/>폐렴 신호 + 18소견 + Grad-CAM"]
    Xray --> VlmStep["VLM 독립 판독<br/>(CNN 결과를 보지 않음)"]
    CnnStep --> Cross{"교차 검증<br/>폐렴 판단이 다르면 사람 확인"}
    VlmStep --> Cross
    Report["소견서<br/>(텍스트 또는 이미지 ➔ OCR)"] --> Summary["소견서 요약 (LLM)<br/>+ 용어집 RAG"]
    Cross --> Compare{"소견별 일치 비교<br/>소견서 vs CNN vs VLM"}
    Summary --> Compare
    Compare --> Final["환자 종합 보고서 (LLM)<br/>주어진 정보만, 불일치는 명시"]
```

설계 원칙은 다음과 같습니다.
- **LLM 출력은 코드로 검증합니다.** 숫자를 지어낼 수 있는 VLM은 폴백이나 독립 의견으로만 씁니다.
- **어긋나면 사람에게 넘깁니다.** 검증 오류, 출처 간 불일치, 형식 오류(1회 재시도 후)는 "사람 확인 필요"로 표시합니다.
- **모든 작업에 처리 조건을 남깁니다.** 설정 스냅숏, 사용한 프롬프트 버전, 기기 사양이 기록되어 결과를 재현하고 비교할 수 있습니다.

## 구성

| 서비스 | 위치 | 주소 |
|---|---|---|
| PostgreSQL 17 (Docker) | `docker-compose.yml` | `127.0.0.1:5432` |
| OCR 서비스 (Python, FastAPI + PaddleOCR) | `src/OcrService` | `http://127.0.0.1:8001` |
| CNN 서비스 (Python, FastAPI + PyTorch) | `src/CnnService` | `http://127.0.0.1:8002` |
| Ollama (LLM) | 별도 설치 | `http://127.0.0.1:11434` |
| API (.NET 10) | `src/Api` | `http://127.0.0.1:5000` |
| 웹 (React + Vite) | `src/Web` | **`http://localhost:5173`** |
| 모니터링 서버 (.NET 10) | `src/Monitor` | `http://127.0.0.1:5100` |
| 평가 스크립트 (Python) | `eval/` | — |

기능은 모듈 단위로 켜고 끕니다(`src/Api/appsettings.json` 의 `Features`). 예를 들어 문서 평가만 하려면 `Text` 만 켜면 되고, 그러면 CNN 서비스는 필요 없습니다.

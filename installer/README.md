# 설치 파일 만들기

```powershell
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

결과: `installer\out\DocumentDigitizer-setup-<버전>.exe` (약 70MB). 필요한 것: .NET 10 SDK, Node.js(화면 빌드), [Inno Setup 6](https://jrsoftware.org/isdl.php).

| 파일 | 역할 |
|---|---|
| `build.ps1` | 게시(자체 포함 win-x64) ➔ OCR 서비스 중 필요한 파일만 복사 ➔ GPU · CPU 설정 두 벌 ➔ Inno Setup |
| `digitizer.iss` | 설치 스크립트. 관리자 권한 없이 `%LocalAppData%\Programs\DocumentDigitizer` |
| `setup-helper.ps1` | 설치 뒤 실행되는 설치 도우미 (사양 점검 · Ollama · 모델 · OCR · 설정 · 자동 시작). 시작 메뉴에서 다시 실행 가능 |
| `ocr-constraints.txt` | 측정에 쓴 OCR 환경의 패키지 버전 전부. 설치판도 같은 버전으로 설치 |
| `info-before.txt` | 설치 전 안내 (받는 양 · SmartScreen · 개인정보) |
| `release-notes.ps1` | 릴리스 노트 · SHA-256 만들기 (배포 워크플로가 부름) |
| `install-innosetup.ps1` | CI 에서 Inno Setup 설치 (버전 고정, SHA-256 · 서명 확인) |

- OCR 코드는 `src/OcrService` 의 파일을 그대로 복사하고 설정(`config.toml` 의 엔진 섹션)도 글자 그대로 옮깁니다. 측정한 코드와 배포하는 코드가 같게 하기 위해서입니다.
- `src/OcrService/.venv` 가 있으면 `ocr-constraints.txt` 와 대조해 다르면 멈춥니다. OCR 환경을 바꿨다면 재측정한 뒤 `build.ps1 -UpdateConstraints` 로 갱신합니다.
- 설치 도우미 점검만: `setup-helper.ps1 -Check` (아무것도 바꾸지 않음), 특정 단계만: `-Only ocr`, 구성 지정: `-Mode gpu|gpu-small|cpu`.
- GPU 점검: nvidia-smi 로 GPU · 메모리 · 드라이버 · 드라이버가 지원하는 최대 CUDA 를 읽고, GPU 판 OCR 에 필요한 CUDA(requirements-gpu.txt 의 `cu129` ➔ 12.9)보다 낮거나 NVIDIA 장치는 있는데 nvidia-smi 가 안 되면 알린 뒤 고르게 함 (드라이버 업데이트 후 다시 · CPU 로 설치 · 그래도 GPU). 드라이버 설치는 관리자 권한이 필요해 설치 도우미가 하지 않음

## 배포 (GitHub Releases)

1. `src/Digitizer.App/Digitizer.App.csproj` 의 `<Version>` 을 올리고 커밋 · 푸시합니다.
2. 같은 버전으로 태그를 올립니다.

```bash
git tag digitizer-v0.1.0
```

```bash
git push origin digitizer-v0.1.0
```

3. `.github/workflows/release-digitizer.yml` 이 Windows 러너에서 태그 버전 = `<Version>` 확인 ➔ 테스트 · 화면 린트 ➔ Inno Setup 설치(`install-innosetup.ps1`: 버전 고정 · SHA-256 · 서명 확인) ➔ `build.ps1` ➔ `release-notes.ps1` ➔ Releases 에 설치 파일 · `.sha256` · 릴리스 노트를 올립니다.

- 릴리스 노트: 포함한 문서 종류(팩 버전 · 판정 · 조건 · 측정한 모델 · 측정 보고서 링크), 설치 때 받는 것과 크기, OCR 버전, SmartScreen 안내, SHA-256. 로컬에서 미리 보기: `powershell -File installer\release-notes.ps1` ➔ `installer\out\release-notes.md`
- 모델 · OCR 은 설치 파일에 넣지 않습니다 (설치 도우미가 설치 때 내려받음).
- 평소 CI(`ci.yml`)도 설치 파일까지 빌드해, 설치 스크립트가 깨지면 바로 알 수 있습니다.

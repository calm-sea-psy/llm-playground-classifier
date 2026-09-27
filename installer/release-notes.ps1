<#
  문서 전산화 릴리스 노트 만들기 (4차-exe 7-2). build.ps1 로 만든 out\ 을 읽어 Markdown 으로 씀
    포함한 문서 종류(팩 id · 버전 · 합격 상태 · 조건 · 측정 보고서) · 측정한 모델 · OCR 버전 · 설치 때 받는 것 · SmartScreen 안내 · SHA-256
  사용: powershell -File installer\release-notes.ps1 [-Out installer\out] [-Repo https://github.com/calm-sea-psy/llm-playground-classifier] [-Ref <태그>]
#>
param(
    [string]$Out = (Join-Path $PSScriptRoot 'out'),
    [string]$Repo = 'https://github.com/calm-sea-psy/llm-playground-classifier',
    [string]$Ref = 'main'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$app = Join-Path $Out 'app'
$setup = Get-ChildItem $Out -Filter 'DocumentDigitizer-setup-*.exe' | Select-Object -First 1
if (-not $setup) { throw "설치 파일이 없습니다: $Out (build.ps1 을 먼저)" }
$version = $setup.BaseName -replace '^DocumentDigitizer-setup-', ''
$sha = (Get-FileHash $setup.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$sizeMB = [math]::Round($setup.Length / 1MB)

# 팩별 측정 모델 (src/Digitizer.App/AppSettings.cs 의 MeasuredModels 와 같게)
$measured = @{ resume = 'gemma4:12b · gemma-4-E4B'; receipt = 'gemma4:12b' }
$status = @{ passed = '합격'; conditional = '**조건부 합격**' }

$lines = @(
    "# 문서 전산화 $version", '',
    '평가 도구에서 측정해 합격한 문서 종류만 담은 Windows 설치 파일입니다. 문서를 이 PC 안에서만 읽어 필드를 추출하고, 사람이 검수한 뒤 엑셀로 내보냅니다.', '',
    '## 포함한 문서 종류', '',
    '| 종류 | 팩 버전 | 판정 | 조건 | 측정한 모델 | 측정 보고서 |',
    '|---|---|---|---|---|---|'
)
foreach ($dir in Get-ChildItem (Join-Path $app 'packs') -Directory | Sort-Object Name) {
    $pack = Get-Content (Join-Path $dir.FullName 'type.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $conditions = if ($pack.release.conditions) { ($pack.release.conditions -join ' · ') } else { '-' }
    $report = "$Repo/blob/$Ref/$($pack.release.report)"
    $lines += "| $($pack.display_name) (``$($pack.id)``) | $($pack.version) | $($status[$pack.release.status]) | $conditions | $($measured[$pack.id]) | [보고서]($report) |"
}

$constraints = Get-Content (Join-Path $root 'installer\ocr-constraints.txt') -Encoding UTF8
$ocr = ($constraints | Where-Object { $_ -match '^(paddlepaddle-gpu|paddleocr|paddlex)==' }) -join ' · '
$lines += @(
    '', '## 설치', '',
    "1. 아래 ``$($setup.Name)`` ($sizeMB MB) 를 받아 실행합니다. 관리자 권한은 필요 없습니다.",
    '2. **"Windows의 PC 보호" 창이 뜨면 [추가 정보] ➔ [실행]**. 코드 서명을 하지 않은 설치 파일이라 뜹니다. 이 페이지가 아닌 곳에서 받은 파일은 실행하지 마세요.',
    '3. 설치 도우미가 사양을 보고 Ollama · LLM 모델 · OCR 을 내려받습니다 (**모델과 OCR 은 설치 파일에 들어 있지 않음**).', '',
    '| 설치 때 받는 것 | 크기 |', '|---|---|',
    '| Ollama (없을 때만, 설치 전에 물어봄) | 약 1GB |',
    '| LLM 모델: NVIDIA GPU 8GB 이상 gemma4:12b / 그 밖 gemma-4-E4B | 7.6GB / 6.0GB |',
    '| OCR (Python 3.11 + PaddleOCR): NVIDIA GPU / GPU 없음 | 약 3GB / 약 0.6GB |',
    '| OCR 모델 | 약 0.1GB |', '',
    "OCR 은 측정에 쓴 버전 그대로 설치합니다: $ocr",
    '', "사양 · 사용법 · 개인정보 · 문제 해결: [docs/digitizer.md]($Repo/blob/$Ref/docs/digitizer.md)",
    '', '## 파일 확인 (SHA-256)', '', '```', "$sha  $($setup.Name)", '```', '',
    ('PowerShell 에서 확인: `Get-FileHash .\' + $setup.Name + ' -Algorithm SHA256`')
)
$notes = Join-Path $Out 'release-notes.md'
[IO.File]::WriteAllLines($notes, [string[]]$lines, (New-Object Text.UTF8Encoding $false))
[IO.File]::WriteAllText("$($setup.FullName).sha256", "$sha  $($setup.Name)`n", (New-Object Text.UTF8Encoding $false))
Write-Host "릴리스 노트: $notes"
Write-Host "체크섬: $($setup.FullName).sha256"

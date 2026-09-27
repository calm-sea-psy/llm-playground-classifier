<#
  문서 전산화 설치 도우미 (4차-exe 5-3). 설치 프로그램이 끝에 실행하고, 시작 메뉴 "문서 전산화 설치 도우미" 로 다시 실행할 수 있음.
    0) 사양 점검: NVIDIA GPU · 메모리 · 디스크 ➔ 구성 선택 (GPU 8GB 이상: gemma4:12b + GPU OCR / 8GB 미만: E4B + GPU OCR / 없음: E4B + CPU)
    1) Ollama   : 없으면 공식 설치 파일을 받아 설치 (동의 받음)
    2) 모델     : ollama pull
    3) OCR      : uv 로 Python 3.11 + OCR 의존성 (설치 폴더 안에만) + OCR 모델 미리 받기
    4) 설정     : %LocalAppData%\Digitizer\settings.json 에 모델 · OCR 엔진 · CPU 여부 (다른 설정은 그대로)
    5) 자동 시작: 로그인하면 알림 영역에서 실행 (설정 화면에서 끌 수 있음)
  단계마다 실패하면 다시 시도 · 건너뛰기 · 끝내기. 이미 된 단계는 확인만 하고 넘어감 (다시 실행해도 안전)
  인자: -Check (점검만, 아무것도 바꾸지 않음) · -Only ocr,settings (그 단계만) · -Mode gpu|gpu-small|cpu (사양 판정 대신) · -Yes (묻지 않음, 조용한 설치)
        -Pause (끝나면 엔터를 기다림: 창이 바로 닫혀 결과를 못 보는 것 방지)
#>
param(
    [string]$AppDir = (Split-Path $PSScriptRoot -Parent),
    [switch]$Check,
    [string[]]$Only = @(),
    [ValidateSet('auto', 'gpu', 'gpu-small', 'cpu')][string]$Mode = 'auto',
    [switch]$Yes,
    [switch]$Pause
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$AppDir = (Resolve-Path $AppDir).Path
$dataDir = Join-Path $env:LOCALAPPDATA 'Digitizer'
$logDir = New-Item -ItemType Directory -Force (Join-Path $dataDir 'logs')
$log = Join-Path $logDir ("setup-{0:yyyyMMdd}.log" -f (Get-Date))
$ocrDir = Join-Path $AppDir 'ocr'
$venvPython = Join-Path $ocrDir '.venv\Scripts\python.exe'
$ollamaUrl = 'http://127.0.0.1:11434'

$Models = @{
    large = @{ Tag = 'gemma4:12b'; GB = 7.6 }
    small = @{ Tag = 'hf.co/unsloth/gemma-4-E4B-it-GGUF:Q4_K_M'; GB = 6.0 }
}

function Say([string]$text, [string]$color = 'Gray') {
    Write-Host $text -ForegroundColor $color
    Add-Content $log ("{0:HH:mm:ss} {1}" -f (Get-Date), $text) -Encoding UTF8
}

# 외부 프로그램(uv · ollama · curl · python) 실행: 출력(오류 포함)을 화면과 기록 파일에 같이 남김.
# Windows PowerShell 5.1 은 오류 출력(stderr)을 받으면 ErrorActionPreference=Stop 에서 예외로 바꾸므로 이 안에서만 Continue
function Invoke-Native([string]$exe, [string[]]$arguments) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $exe @arguments 2>&1 | ForEach-Object {
            # stderr 줄은 ErrorRecord 로 옴 (빈 줄이면 "RemoteException" 이라고만 찍힘) ➔ 메시지만
            $line = if ($_ -is [Management.Automation.ErrorRecord]) { $_.Exception.Message } else { "$_" }
            if ($line -eq 'System.Management.Automation.RemoteException') { $line = '' }
            if ($line.Trim()) { Write-Host "  $line"; Add-Content $log "  $line" -Encoding UTF8 }
        }
        return $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $old
    }
}

function Ask([string]$question) {
    if ($Yes) { return $true }
    while ($true) {
        $a = Read-Host "$question [Y/N]"
        if ($a -match '^[yYㅛ]') { return $true }
        if ($a -match '^[nNㅜ]') { return $false }
    }
}

# ---------------------------------------------------------------- 0) 사양
$DriverUrl = 'https://www.nvidia.com/Download/index.aspx'

# GPU 판 OCR 이 요구하는 CUDA 버전: requirements-gpu.txt 의 Paddle 인덱스 주소 (…/cu129/ ➔ 12.9). 측정 환경을 바꾸면 같이 따라감
function Get-RequiredCuda {
    $req = Join-Path $ocrDir 'requirements-gpu.txt'
    if ((Test-Path $req) -and ((Get-Content $req -Raw) -match '/cu(\d{2})(\d)/')) { return [version]"$($Matches[1]).$($Matches[2])" }
    return $null
}

# nvidia-smi 표준 출력 줄만 (없거나 실패면 빈 배열). 한 번 GPU 를 못 찾았다고 CPU 구성으로 가지 않게 3번까지 다시.
# 2>$null 을 쓰면 PowerShell 5.1 이 stderr 한 줄(경고 등)을 Stop 에서 예외로 바꿔 "GPU 없음" 으로 잘못 판정했음 ➔ stderr 는 버림
function Invoke-NvidiaSmi([string[]]$arguments) {
    for ($try = 1; $try -le 3; $try++) {
        $old = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $lines = @(& nvidia-smi @arguments 2>&1 | Where-Object { $_ -isnot [Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
            $code = $LASTEXITCODE
        } catch {
            $lines = @(); $code = -1  # nvidia-smi 가 없음
        } finally {
            $ErrorActionPreference = $old
        }
        if ($code -eq 0 -and $lines.Count -gt 0) { return $lines }
        if ($code -eq -1) { return @() }
        Start-Sleep -Seconds 1
    }
    return @()
}

function Get-Spec {
    $gpu = $null
    $unverified = $null
    $line = Invoke-NvidiaSmi @('--query-gpu=name,memory.total,driver_version', '--format=csv,noheader,nounits') | Select-Object -First 1
    if ($line) {
        $parts = $line.Split(',') | ForEach-Object { $_.Trim() }
        # 드라이버가 지원하는 최대 CUDA 버전은 nvidia-smi 머리글에만 나옴 ("CUDA Version: 13.1")
        $cuda = $null
        if (((Invoke-NvidiaSmi @()) -join "`n") -match 'CUDA Version:\s*(\d+\.\d+)') { $cuda = [version]$Matches[1] }
        $gpu = @{ Name = $parts[0]; VramGB = [math]::Round([double]$parts[1] / 1024, 1); Driver = $parts[2]; Cuda = $cuda }
    }
    if (-not $gpu) {
        # nvidia-smi 가 안 되는데 NVIDIA 장치는 보이면 드라이버 문제일 수 있음 ➔ CPU 구성으로 조용히 바꾸지 않도록 알림
        $nvidia = Get-CimInstance Win32_VideoController | Where-Object { $_.Name -match 'NVIDIA' } | Select-Object -First 1
        if ($nvidia) { $unverified = $nvidia.Name }
    }
    $ramGB = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)
    $drive = (Get-Item $AppDir).PSDrive
    $freeGB = [math]::Round($drive.Free / 1GB, 1)
    return @{ Gpu = $gpu; Unverified = $unverified; RamGB = $ramGB; FreeGB = $freeGB; Drive = $drive.Name }
}

function Get-Plan($spec) {
    $p = $Mode
    if ($p -eq 'auto') {
        $p = if (-not $spec.Gpu) { 'cpu' } elseif ($spec.Gpu.VramGB -ge 8) { 'gpu' } else { 'gpu-small' }
    }
    switch ($p) {
        'gpu' { @{ Name = $p; Model = $Models.large; Ocr = 'gpu'; OcrEngine = 'paddleocr'; CpuOnly = $false; OcrGB = 3.0; Label = 'NVIDIA GPU 8GB 이상: gemma4:12b + GPU OCR (측정한 기본 구성)' } }
        'gpu-small' { @{ Name = $p; Model = $Models.small; Ocr = 'gpu'; OcrEngine = 'paddleocr'; CpuOnly = $false; OcrGB = 3.0; Label = 'NVIDIA GPU 8GB 미만: gemma-4-E4B + GPU OCR' } }
        'cpu' { @{ Name = $p; Model = $Models.small; Ocr = 'cpu'; OcrEngine = 'paddleocr_cpu'; CpuOnly = $true; OcrGB = 0.6; Label = 'GPU 없음: gemma-4-E4B + CPU OCR (문서당 1~2분, 측정 중앙 64초)' } }
    }
}

# ---------------------------------------------------------------- 1) Ollama
function Find-Ollama {
    $cmd = Get-Command ollama.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $default = Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe'
    if (Test-Path $default) { return $default }
    return $null
}

function Test-OllamaUp {
    try { Invoke-RestMethod "$ollamaUrl/api/version" -TimeoutSec 3 | Out-Null; return $true } catch { return $false }
}

function Start-Ollama {
    if (Test-OllamaUp) { return $true }
    $app = Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama app.exe'
    if (Test-Path $app) { Start-Process $app } else { Start-Process (Find-Ollama) -ArgumentList 'serve' -WindowStyle Hidden }
    for ($i = 0; $i -lt 60; $i++) { if (Test-OllamaUp) { return $true }; Start-Sleep 1 }
    return $false
}

function Step-Ollama {
    $exe = Find-Ollama
    if ($exe) {
        Say "Ollama 있음: $exe"
    } else {
        Say 'Ollama 가 없습니다. 로컬 LLM 을 실행하는 무료 프로그램입니다 (ollama.com, 설치 파일 약 1GB).' 'Yellow'
        if (-not (Ask 'Ollama 공식 설치 파일을 내려받아 설치할까요?')) { throw '사용자가 Ollama 설치를 건너뜀' }
        $setup = Join-Path $env:TEMP 'OllamaSetup.exe'
        Say '내려받는 중: https://ollama.com/download/OllamaSetup.exe'
        & curl.exe -L --fail --progress-bar -o $setup 'https://ollama.com/download/OllamaSetup.exe'  # 진행 막대는 화면에만
        if ($LASTEXITCODE -ne 0) { throw "Ollama 설치 파일을 내려받지 못했습니다 (curl 종료 코드 $LASTEXITCODE)" }
        Say 'Ollama 설치 중 (창이 잠깐 뜰 수 있음)'
        Start-Process $setup -ArgumentList '/SILENT', '/NORESTART' -Wait
        Remove-Item $setup -ErrorAction SilentlyContinue
        if (-not (Find-Ollama)) { throw 'Ollama 설치 뒤에도 ollama.exe 를 찾지 못했습니다' }
    }
    if (-not (Start-Ollama)) { throw 'Ollama 가 60초 안에 응답하지 않습니다 (알림 영역의 Ollama 아이콘 확인)' }
    Say 'Ollama 실행 중' 'Green'
}

# ---------------------------------------------------------------- 2) 모델
function Step-Model($plan) {
    if (-not (Start-Ollama)) { throw 'Ollama 가 실행되지 않아 모델을 확인할 수 없습니다' }
    $tag = $plan.Model.Tag
    $names = (Invoke-RestMethod "$ollamaUrl/api/tags").models.name
    if ($names -contains $tag -or $names -contains "$tag`:latest") { Say "모델 있음: $tag" 'Green'; return }
    Say ("모델 받는 중: {0} (약 {1}GB, 인터넷 속도에 따라 수십 분)" -f $tag, $plan.Model.GB) 'Yellow'
    & (Find-Ollama) pull $tag  # 진행 막대는 화면에만 (기록 파일에 수천 줄이 쌓이지 않게)
    if ($LASTEXITCODE -ne 0) { throw "모델을 받지 못했습니다 (ollama pull 종료 코드 $LASTEXITCODE). 인터넷 연결을 확인하고 다시 시도하세요" }
    Say "모델 준비됨: $tag" 'Green'
}

# ---------------------------------------------------------------- 3) OCR
function Get-Uv {
    $uv = Join-Path $AppDir 'tools\uv.exe'
    if (Test-Path $uv) { return $uv }
    New-Item -ItemType Directory -Force (Split-Path $uv) | Out-Null
    $zip = Join-Path $env:TEMP 'uv-x86_64-pc-windows-msvc.zip'
    Say 'Python 설치 도구(uv, 약 20MB) 받는 중: github.com/astral-sh/uv'
    & curl.exe -L --fail --progress-bar -o $zip 'https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip'
    if ($LASTEXITCODE -ne 0) { throw 'uv 를 내려받지 못했습니다' }
    Expand-Archive $zip -DestinationPath (Split-Path $uv) -Force
    Remove-Item $zip -ErrorAction SilentlyContinue
    if (-not (Test-Path $uv)) { throw 'uv.exe 를 풀지 못했습니다' }
    return $uv
}

# Python 코드에 큰따옴표를 쓰지 말 것: Windows PowerShell 5.1 은 외부 프로그램 인자의 큰따옴표를 지움
function Invoke-Ocr([string]$code) {
    $env:PADDLE_PDX_CACHE_HOME = Join-Path $ocrDir 'models'
    $env:PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK = 'True'
    $env:PYTHONIOENCODING = 'utf-8'
    Push-Location $ocrDir
    try { return (Invoke-Native $venvPython @('-c', $code)) -eq 0 } finally { Pop-Location }
}

function Step-Ocr($plan) {
    # Python 은 설치 폴더 안에만 (제거하면 같이 지워짐, 다른 Python 과 섞이지 않음)
    $env:UV_PYTHON_INSTALL_DIR = Join-Path $AppDir 'python'
    $env:UV_PYTHON_PREFERENCE = 'only-managed'
    $req = Join-Path $ocrDir "requirements-$($plan.Ocr).txt"
    $marker = Join-Path $ocrDir '.venv\installed.txt'
    # 있어도 실제로 실행될 때만 그대로 씀 (uv 의 Python 연결 폴더를 못 거치면 os error 448 ➔ 다시 만듦. digitizer.iss RedirectionGuard=no 참고)
    $usable = (Test-Path $venvPython) -and (Test-Path $marker) -and ((Get-Content $marker -Raw).Trim() -eq $plan.Ocr) -and
        ((Invoke-Native $venvPython @('-c', 'pass')) -eq 0)
    if ($usable) {
        Say "OCR 환경 있음 ($($plan.Ocr))"
    } else {
        $uv = Get-Uv
        Say ("OCR 설치 중 ({0}, 받는 양 약 {1}GB): Python 3.11 + PaddleOCR" -f $plan.Ocr, $plan.OcrGB) 'Yellow'
        if ((Invoke-Native $uv @('venv', (Join-Path $ocrDir '.venv'), '--python', '3.11', '--clear')) -ne 0) { throw 'Python 3.11 환경을 만들지 못했습니다 (위 출력 확인)' }
        # 측정 환경과 같은 버전 (constraints.txt). Paddle 인덱스(GPU 판)에도 paddleocr 옛 버전이 있어, uv 기본값(첫 인덱스만)이면
        # 3.7.0 을 못 찾음 ➔ 모든 인덱스에서 고름 (버전은 constraints 로 모두 고정돼 있어 다른 버전이 들어올 수 없음)
        if ((Invoke-Native $uv @('pip', 'install', '-r', $req, '-c', (Join-Path $ocrDir 'constraints.txt'), '--index-strategy', 'unsafe-best-match', '--python', $venvPython)) -ne 0) {
            throw 'OCR 의존성을 설치하지 못했습니다 (위 출력 확인, 인터넷 연결 확인 후 다시 시도)'
        }
        Set-Content $marker $plan.Ocr -Encoding ASCII
    }
    Copy-Item (Join-Path $ocrDir "config.$($plan.Ocr).toml") (Join-Path $ocrDir 'config.toml') -Force
    New-Item -ItemType Directory -Force (Join-Path $ocrDir 'models') | Out-Null

    if ($plan.Ocr -eq 'gpu' -and -not (Invoke-Ocr 'import paddle,sys; sys.exit(0 if paddle.device.cuda.device_count() > 0 else 1)')) {
        $need = Get-RequiredCuda
        throw "OCR 이 GPU 를 쓰지 못합니다. NVIDIA 드라이버가 CUDA $need 이상을 지원해야 합니다 (업데이트: $DriverUrl, 관리자 권한 필요). GPU 없이 쓰려면 시작 메뉴의 설치 도우미를 -Mode cpu 로 다시 실행하세요"
    }
    Say 'OCR 모델 받는 중 (약 100MB, 처음 한 번)'
    if (-not (Invoke-Ocr "from engines import EngineRegistry; r = EngineRegistry(); r.get(); print('OCR engine ready:', r.default_engine)")) {
        throw 'OCR 엔진을 읽지 못했습니다 (로그의 오류 확인)'
    }
    Say 'OCR 준비됨' 'Green'
}

# ---------------------------------------------------------------- 4) 설정 · 5) 자동 시작
function Step-Settings($plan) {
    New-Item -ItemType Directory -Force $dataDir | Out-Null
    $file = Join-Path $dataDir 'settings.json'
    $s = if (Test-Path $file) { Get-Content $file -Raw -Encoding UTF8 | ConvertFrom-Json } else { [pscustomobject]@{} }
    foreach ($kv in @{ Model = $plan.Model.Tag; OcrEngine = $plan.OcrEngine; CpuOnly = $plan.CpuOnly }.GetEnumerator()) {
        $s | Add-Member -NotePropertyName $kv.Key -NotePropertyValue $kv.Value -Force
    }
    [IO.File]::WriteAllText($file, ($s | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
    Say "설정 저장: 모델 $($plan.Model.Tag) · OCR $($plan.OcrEngine) · CPU 전용 $($plan.CpuOnly) ($file)" 'Green'
}

function Step-AutoStart {
    $exe = Join-Path $AppDir 'Digitizer.exe'
    Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'DocumentDigitizer' -Value "`"$exe`" --background"
    Say '로그인하면 자동으로 시작 (끄려면 프로그램 설정 화면에서)' 'Green'
}

function Finish([int]$code) {
    if ($Pause -and -not $Yes) { Read-Host '엔터를 누르면 창이 닫힙니다' | Out-Null }
    exit $code
}

# ---------------------------------------------------------------- 실행
Say "문서 전산화 설치 도우미 ($AppDir)" 'Cyan'
$spec = Get-Spec
$plan = Get-Plan $spec
$gpuText = if ($spec.Gpu) { "$($spec.Gpu.Name) ($($spec.Gpu.VramGB)GB, 드라이버 $($spec.Gpu.Driver), CUDA $($spec.Gpu.Cuda) 까지)" } else { '없음 (NVIDIA GPU 를 찾지 못함)' }
Say "GPU: $gpuText · 메모리: $($spec.RamGB)GB · $($spec.Drive): 남은 공간 $($spec.FreeGB)GB"

# 드라이버 점검: GPU 판 OCR(Paddle CUDA 빌드)이 요구하는 CUDA 보다 드라이버가 낮으면 GPU OCR 이 동작하지 않을 수 있음.
# 드라이버 업데이트는 관리자 권한이 필요해 설치 도우미가 하지 않음 ➔ 알리고 고르게 함 (조용한 설치는 GPU 로 시도, 실패하면 OCR 단계에서 알림)
# NVIDIA 장치는 보이는데 nvidia-smi 가 안 됨 (드라이버 없음 · 고장) ➔ 조용히 CPU 로 가지 않게 알리고 고르게 함
if ($spec.Unverified) {
    Say "NVIDIA 장치($($spec.Unverified))가 있지만 nvidia-smi 로 확인하지 못했습니다 ➔ NVIDIA 드라이버가 없거나 고장일 수 있습니다" 'Yellow'
    Say "  드라이버 설치 · 업데이트(관리자 권한 필요, 회사 PC 는 IT 담당자에게): $DriverUrl" 'Yellow'
    if (-not $Yes -and -not $Check -and $Mode -eq 'auto') {
        while ($true) {
            $a = Read-Host '[U] 드라이버를 설치한 뒤 다시 실행 (끝내기) · [C] GPU 없이(CPU) 설치 · [G] 그래도 GPU 로 시도 (작은 모델)'
            if ($a -match '^[uUㅕ]') { Say "드라이버를 설치한 뒤 시작 메뉴 `"문서 전산화 설치 도우미`" 를 다시 실행하세요"; Finish 1 }
            if ($a -match '^[cCㅊ]') { break }
            if ($a -match '^[gGㅎ]') { $plan = Get-Plan @{ Gpu = @{ VramGB = 0 } }; break }
        }
    }
}

$needCuda = Get-RequiredCuda
$driverOld = $plan.Ocr -eq 'gpu' -and $needCuda -and $spec.Gpu.Cuda -and $spec.Gpu.Cuda -lt $needCuda
if ($driverOld) {
    Say ("NVIDIA 드라이버가 오래됐습니다: 이 드라이버({0})는 CUDA {1} 까지, GPU OCR 은 CUDA {2} 이상이 필요합니다" -f $spec.Gpu.Driver, $spec.Gpu.Cuda, $needCuda) 'Yellow'
    Say "  드라이버 업데이트(관리자 권한 필요, 회사 PC 는 IT 담당자에게): $DriverUrl" 'Yellow'
    if (-not $Yes -and -not $Check -and $Mode -eq 'auto') {
        while ($true) {
            $a = Read-Host '[U] 드라이버를 업데이트한 뒤 다시 실행 (끝내기) · [C] GPU 없이(CPU) 설치 · [G] 그래도 GPU 로 시도'
            if ($a -match '^[uUㅕ]') { Say "드라이버를 업데이트한 뒤 시작 메뉴 `"문서 전산화 설치 도우미`" 를 다시 실행하세요"; Finish 1 }
            if ($a -match '^[cCㅊ]') { $plan = Get-Plan @{ Gpu = $null }; break }
            if ($a -match '^[gGㅎ]') { break }
        }
    }
}
Say "구성: $($plan.Label)" 'Cyan'
$downloadGB = $plan.Model.GB + $plan.OcrGB + 0.1
$needGB = [math]::Ceiling($plan.Model.GB + $plan.OcrGB * 1.8 + 1)
Say ("받는 양 최대 약 {0:N1}GB (모델 {1}GB · OCR {2}GB · OCR 모델 0.1GB, 이미 있는 것은 건너뜀). 100Mbps 에서 약 {3:N0}분, 필요한 공간 약 {4}GB" -f `
    $downloadGB, $plan.Model.GB, $plan.OcrGB, ($downloadGB * 8 * 1024 / 100 / 60), $needGB)
if ($spec.RamGB -lt 16) { Say "메모리가 16GB 미만이라 처리가 느리거나 다른 프로그램이 느려질 수 있습니다" 'Yellow' }
if ($spec.FreeGB -lt $needGB) { Say "디스크 공간이 부족할 수 있습니다 (필요 약 $needGB GB, 남은 $($spec.FreeGB)GB)" 'Yellow' }
if ($plan.Name -eq 'gpu-small') { Say '이 구성(8GB 미만 GPU)은 따로 측정하지 않았습니다. 느리면 설정 화면에서 CPU 로 바꿀 수 있습니다' 'Yellow' }

$steps = [ordered]@{
    ollama    = @{ Title = 'Ollama'; Run = { Step-Ollama } }
    model     = @{ Title = '모델'; Run = { Step-Model $plan } }
    ocr       = @{ Title = 'OCR'; Run = { Step-Ocr $plan } }
    settings  = @{ Title = '설정'; Run = { Step-Settings $plan } }
    autostart = @{ Title = '자동 시작'; Run = { Step-AutoStart } }
}
if ($Check) {
    Say "점검만 (-Check): 바꾸지 않음. Ollama: $(if (Find-Ollama) { '있음' } else { '없음' }) · OCR 환경: $(if (Test-Path $venvPython) { '있음' } else { '없음' })"
    return
}
if (-not $Yes -and -not (Ask '이대로 설치를 계속할까요?')) { Say '취소했습니다. 시작 메뉴 "문서 전산화 설치 도우미" 로 다시 실행할 수 있습니다'; Finish 1 }

$results = [ordered]@{}
foreach ($key in $steps.Keys) {
    if ($Only.Count -gt 0 -and $Only -notcontains $key) { continue }
    $step = $steps[$key]
    while ($true) {
        Say "`n== $($step.Title) ==" 'Cyan'
        try {
            & $step.Run
            $results[$key] = '완료'
            break
        } catch {
            Say "실패: $($_.Exception.Message)" 'Red'
            if ($Yes) { $results[$key] = '실패'; break }
            $a = Read-Host '[R] 다시 시도 · [S] 건너뛰기 · [Q] 끝내기'
            if ($a -match '^[sSㄴ]') { $results[$key] = '건너뜀'; break }
            if ($a -match '^[qQㅂ]') { $results[$key] = '실패'; Say '끝냈습니다. 시작 메뉴 "문서 전산화 설치 도우미" 로 다시 실행하세요'; Finish 1 }
        }
    }
}

Say "`n== 결과 ==" 'Cyan'
foreach ($r in $results.GetEnumerator()) { Say ("{0}: {1}" -f $steps[$r.Key].Title, $r.Value) $(if ($r.Value -eq '완료') { 'Green' } else { 'Yellow' }) }
Say "기록: $log"
if ($results.Values -contains '실패' -or $results.Values -contains '건너뜀') {
    Say '끝나지 않은 단계가 있습니다. 프로그램의 상태 점검 화면에서 확인하고, 시작 메뉴 "문서 전산화 설치 도우미" 로 다시 실행하세요' 'Yellow'
    Finish 2
}
Say '설치를 마쳤습니다' 'Green'
Finish 0

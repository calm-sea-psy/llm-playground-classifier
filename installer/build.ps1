<#
  문서 전산화 설치 파일 만들기 (4차-exe 5단계)
    1) dotnet publish (자체 포함 win-x64, 화면 · 합격한 팩 포함)            ➔ out\app
    2) OCR 서비스 중 exe 에 필요한 것만 복사 (경로 A paddleocr, 문서 파싱 제외) ➔ out\app\ocr
       코드는 측정한 파일 그대로, 설정만 GPU · CPU 두 벌 (config.gpu.toml · config.cpu.toml) · 의존성 두 벌 (requirements-gpu/cpu.txt)
       ➔ 저장소 config.toml · requirements.txt 에서 만들어 엔진 설정 · 버전이 어긋나지 않게
    3) 설치 도우미 (setup-helper.ps1) ➔ out\app\setup
    4) Inno Setup 으로 out\DocumentDigitizer-setup-<버전>.exe
  사용: powershell -ExecutionPolicy Bypass -File installer\build.ps1 [-SkipInstaller]
#>
param(
    [string]$Out = (Join-Path $PSScriptRoot 'out'),
    [switch]$SkipInstaller,
    [switch]$UpdateConstraints
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$csproj = Join-Path $repo 'src\Digitizer.App\Digitizer.App.csproj'
$version = ([xml](Get-Content $csproj -Raw -Encoding UTF8)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "Digitizer.App.csproj 에 <Version> 이 없습니다" }
$app = Join-Path $Out 'app'
Write-Host "문서 전산화 $version ➔ $Out"

# 1) 프로그램
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
dotnet publish $csproj -c Release -r win-x64 --self-contained -o $app
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 실패" }
foreach ($must in 'Digitizer.exe', 'wwwroot\index.html', 'wwwroot\pdfjs\cmaps', 'packs\resume\type.json', 'packs\receipt\type.json') {
    if (-not (Test-Path (Join-Path $app $must))) { throw "게시 결과에 $must 가 없습니다" }
}
if (Test-Path (Join-Path $app 'package.json')) { throw '화면 소스(package.json)가 게시 결과에 섞였습니다' }
$packs = Get-ChildItem (Join-Path $app 'packs') -Directory | ForEach-Object Name
Write-Host "포함한 문서 종류: $($packs -join ', ')"

# 2) OCR 서비스 (측정한 파일 그대로)
$src = Join-Path $repo 'src\OcrService'
$ocr = Join-Path $app 'ocr'
New-Item -ItemType Directory -Force (Join-Path $ocr 'engines') | Out-Null
foreach ($f in 'main.py', 'contract.py', 'imaging.py', 'verify_excel.py', 'engines\__init__.py', 'engines\base.py', 'engines\gpu.py', 'engines\paddle.py') {
    Copy-Item (Join-Path $src $f) (Join-Path $ocr $f)
}

# config.toml ➔ [service] + 엔진 섹션 중 필요한 것만 (섹션 내용은 한 글자도 바꾸지 않음)
$sections = [ordered]@{}
$name = '_head'
foreach ($line in Get-Content (Join-Path $src 'config.toml') -Encoding UTF8) {
    if ($line -match '^\[(.+)\]\s*$') { $name = $Matches[1] }
    $sections[$name] += @($line)
}
function Write-OcrConfig([string]$file, [string]$engine, [string[]]$engines, [string]$note) {
    $lines = @("# 문서 전산화 설치판 OCR 설정 ($note). installer\build.ps1 이 src\OcrService\config.toml 에서 만듦 ➔ 직접 고치지 말 것", '',
        '[service]', "default_engine = `"$engine`"", "preload = [`"$engine`"]", '')
    foreach ($e in $engines) {
        if (-not $sections.Contains("engines.$e")) { throw "config.toml 에 [engines.$e] 가 없습니다" }
        $lines += $sections["engines.$e"]
        $lines += ''
    }
    [IO.File]::WriteAllLines((Join-Path $ocr $file), [string[]]$lines, (New-Object Text.UTF8Encoding $false))
}
Write-OcrConfig 'config.gpu.toml' 'paddleocr' @('paddleocr', 'paddleocr_cpu') 'NVIDIA GPU'
Write-OcrConfig 'config.cpu.toml' 'paddleocr_cpu' @('paddleocr_cpu') 'GPU 없음'

# requirements.txt ➔ 문서 파싱(paddlex[ocr]) 제외, CPU 판은 paddlepaddle (Paddle CPU 인덱스: PyPI 에는 3.3.1 까지라 측정한 3.4.0 이 없음)
$req = Get-Content (Join-Path $src 'requirements.txt') -Encoding UTF8 | Where-Object { $_ -notmatch '^paddlex\[ocr\]' -and $_ -notmatch '^# \[ocr\]' }
$gpuReq = @('# 문서 전산화 설치판 OCR 의존성 (NVIDIA GPU). installer\build.ps1 이 src\OcrService\requirements.txt 에서 만듦') + $req
$cpuReq = @('# 문서 전산화 설치판 OCR 의존성 (GPU 없음). installer\build.ps1 이 src\OcrService\requirements.txt 에서 만듦') +
    ($req | Where-Object { $_ -notmatch '^# paddlepaddle-gpu' } | ForEach-Object { $_ -replace '^paddlepaddle-gpu==', 'paddlepaddle==' -replace '^(--extra-index-url https://www\.paddlepaddle\.org\.cn/packages/stable/)cu\d+/', '$1cpu/' })
[IO.File]::WriteAllLines((Join-Path $ocr 'requirements-gpu.txt'), [string[]]$gpuReq, (New-Object Text.UTF8Encoding $false))
[IO.File]::WriteAllLines((Join-Path $ocr 'requirements-cpu.txt'), [string[]]$cpuReq, (New-Object Text.UTF8Encoding $false))

# constraints.txt: 측정에 쓴 OCR 환경(src\OcrService\.venv)의 실제 버전 전부 ➔ 설치판도 paddlex 등 간접 의존성까지 같은 버전.
# 저장소에 installer\ocr-constraints.txt 로 둠 (CI · 다른 PC 에서도 같은 설치 파일). 측정 환경이 있으면 대조해서 다르면 멈춤
# (측정 환경을 바꿨으면 재측정 후 -UpdateConstraints 로 갱신)
$constraintsFile = Join-Path $PSScriptRoot 'ocr-constraints.txt'
$devPython = Join-Path $src '.venv\Scripts\python.exe'
if (Test-Path $devPython) {
    $uv = (Get-Command uv.exe -ErrorAction SilentlyContinue).Source
    $frozen = if ($uv) { & $uv pip freeze --python $devPython } else { & $devPython -m pip freeze }
    if ($LASTEXITCODE -ne 0 -or -not $frozen) { throw "측정용 OCR 환경의 패키지 목록을 읽지 못했습니다" }
    $current = @('# 측정에 쓴 OCR 환경의 패키지 버전 (installer\build.ps1 -UpdateConstraints 가 src\OcrService\.venv 에서 만듦). 설치 도우미가 -c 로 씀') +
        ($frozen | Where-Object { $_ -match '^[A-Za-z0-9_.\-]+==' })
    if ($UpdateConstraints -or -not (Test-Path $constraintsFile)) {
        [IO.File]::WriteAllLines($constraintsFile, [string[]]$current, (New-Object Text.UTF8Encoding $false))
        Write-Host "ocr-constraints.txt 갱신 ($($current.Count - 1)개)"
    } elseif (Compare-Object (Get-Content $constraintsFile -Encoding UTF8 | Select-Object -Skip 1) ($current | Select-Object -Skip 1)) {
        throw "측정용 OCR 환경의 패키지 버전이 ocr-constraints.txt 와 다릅니다. 측정 환경을 바꿨다면 재측정 후 -UpdateConstraints"
    }
}
if (-not (Test-Path $constraintsFile)) { throw "installer\ocr-constraints.txt 가 없습니다 (측정 환경에서 -UpdateConstraints 로 만드세요)" }
$constraints = Get-Content $constraintsFile -Encoding UTF8
Copy-Item $constraintsFile (Join-Path $ocr 'constraints.txt')
foreach ($pin in 'paddlepaddle-gpu==', 'paddleocr==', 'paddlex==') {
    if (-not ($constraints | Where-Object { $_ -like "$pin*" })) { throw "constraints.txt 에 $pin 가 없습니다" }
}
Write-Host ("OCR 버전 고정: {0}" -f (($constraints | Where-Object { $_ -match '^(paddlepaddle-gpu|paddleocr|paddlex)==' }) -join ', '))

# 3) 설치 도우미
New-Item -ItemType Directory -Force (Join-Path $app 'setup') | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'setup-helper.ps1') (Join-Path $app 'setup\setup-helper.ps1')
Copy-Item (Join-Path $PSScriptRoot 'setup-helper.cmd') (Join-Path $app 'setup\setup-helper.cmd')

$size = (Get-ChildItem $app -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("프로그램 파일 {0:N0}MB" -f $size)

# 4) 설치 파일
if ($SkipInstaller) { return }
$iscc = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) 이 없습니다. https://jrsoftware.org/isdl.php 에서 설치하거나 -SkipInstaller 로 파일만 만드세요" }
& $iscc "/DAppVersion=$version" "/DAppDir=$app" "/O$Out" (Join-Path $PSScriptRoot 'digitizer.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup 컴파일 실패" }
Get-ChildItem $Out -Filter '*.exe' | ForEach-Object { Write-Host ("설치 파일: {0} ({1:N0}MB)" -f $_.FullName, ($_.Length / 1MB)) }

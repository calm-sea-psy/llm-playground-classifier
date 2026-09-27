<#
  CI(Windows 러너)에서 Inno Setup 6.7.3 설치. 공식 배포(GitHub jrsoftware/issrc Releases) 파일을 받아
  SHA-256(고정값)과 디지털 서명(Pyrsys B.V.)이 모두 맞을 때만 설치 ➔ 받은 파일이 바뀌었으면 멈춤.
  버전을 올릴 때는 새 파일의 SHA-256 을 확인해 아래 값을 같이 바꿈
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$version = '6.7.3'
$sha256 = '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732'
$url = "https://github.com/jrsoftware/issrc/releases/download/is-$($version -replace '\.', '_')/innosetup-$version.exe"
$file = Join-Path $env:RUNNER_TEMP "innosetup-$version.exe"
if (-not $env:RUNNER_TEMP) { $file = Join-Path $env:TEMP "innosetup-$version.exe" }

Invoke-WebRequest $url -OutFile $file
$actual = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $sha256) { throw "Inno Setup 설치 파일의 SHA-256 이 다릅니다: $actual (기대 $sha256)" }
$signature = Get-AuthenticodeSignature $file
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Pyrsys B\.V\.') {
    throw "Inno Setup 설치 파일의 서명이 올바르지 않습니다: $($signature.Status) $($signature.SignerCertificate.Subject)"
}
Start-Process $file -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER' -Wait
$iscc = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
if (-not (Test-Path $iscc)) { throw "Inno Setup 설치 뒤에도 ISCC.exe 가 없습니다: $iscc" }
Write-Host "Inno Setup $version 설치: $iscc"

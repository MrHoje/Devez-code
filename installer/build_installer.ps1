# DevezCode 인스톨러 빌드 스크립트
# 실행: PowerShell 에서 installer 폴더로 이동 후 .\build_installer.ps1

$ErrorActionPreference = "Stop"

$root    = Split-Path $PSScriptRoot
# setup.iss 의 [Files] Source 경로(..\bin\win-x64\publish)와 반드시 일치해야 함
$publish = Join-Path $root "bin\win-x64\publish"
$iscc    = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Error "Inno Setup 6가 설치되어 있지 않습니다.`nhttps://jrsoftware.org/isdl.php 에서 설치하세요."
    exit 1
}

# 실행 중인 앱 종료(파일 잠금 방지)
taskkill /IM DevezCode.exe /F 2>$null

# 1. Publish (single-file, framework-dependent, win-x64)
Write-Host "`n[1/2] dotnet publish ..." -ForegroundColor Cyan
dotnet publish "$root\DevezCode.csproj" `
    -c Release -r win-x64 `
    --no-self-contained `
    -p:PublishSingleFile=true `
    -p:DebugType=none `
    -o "$publish"

if ($LASTEXITCODE -ne 0) { Write-Error "publish 실패"; exit 1 }

# pdb 제거 (배포에 불필요)
Remove-Item "$publish\*.pdb" -ErrorAction SilentlyContinue

# 2. Inno Setup 컴파일
Write-Host "`n[2/2] Inno Setup 컴파일 ..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path "$PSScriptRoot\Output" -Force | Out-Null
& $iscc "$PSScriptRoot\setup.iss"

if ($LASTEXITCODE -ne 0) { Write-Error "Inno Setup 컴파일 실패"; exit 1 }

Write-Host "`n완료! 인스톨러: $PSScriptRoot\Output\" -ForegroundColor Green

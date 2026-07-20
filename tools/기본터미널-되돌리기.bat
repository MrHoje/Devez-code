@echo off
chcp 65001 >nul
setlocal
:: 기본터미널-conhost-적용.bat 로 바꾼 설정을 되돌린다.
:: 백업(console-backup.reg)이 있으면 원래대로 복원, 없으면 "Windows가 결정"(기본값)으로 초기화.

set "KEY=HKCU\Console\%%%%Startup"
set "DEFAULT={00000000-0000-0000-0000-000000000000}"

if exist "%~dp0console-backup.reg" (
    echo 백업에서 원래 설정 복원...
    reg import "%~dp0console-backup.reg" >nul 2>&1
    if errorlevel 1 goto :zero
    goto :show
)

:zero
echo 백업 없음 - "Windows가 결정"으로 초기화...
reg add "%KEY%" /v DelegationConsole  /t REG_SZ /d "%DEFAULT%" /f >nul
reg add "%KEY%" /v DelegationTerminal /t REG_SZ /d "%DEFAULT%" /f >nul

:show
echo.
echo == 현재 값 ==
reg query "%KEY%" /v DelegationConsole
reg query "%KEY%" /v DelegationTerminal
echo.
echo 완료. DevezCode 재실행해서 확인하세요.
pause

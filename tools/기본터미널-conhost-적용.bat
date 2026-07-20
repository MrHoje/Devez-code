@echo off
chcp 65001 >nul
setlocal
:: DevezCode 훅 팝업(빈 검은 wt 창) 해결용.
:: Windows "기본 터미널 앱"을 Windows 콘솔 호스트(conhost)로 강제한다.
:: 관리자 권한 불필요(HKCU). 되돌리려면 기본터미널-되돌리기.bat 실행.

set "KEY=HKCU\Console\%%%%Startup"
set "CONHOST={B23D10C0-E52E-411E-9D5B-C09FDF709C7D}"

echo == 현재 값 ==
reg query "%KEY%" /v DelegationConsole  2>nul
reg query "%KEY%" /v DelegationTerminal 2>nul
echo.

echo 현재 Console 설정 백업... (console-backup.reg)
reg export "HKCU\Console" "%~dp0console-backup.reg" /y >nul 2>&1

echo conhost 로 강제 설정...
reg add "%KEY%" /v DelegationConsole  /t REG_SZ /d "%CONHOST%" /f >nul
reg add "%KEY%" /v DelegationTerminal /t REG_SZ /d "%CONHOST%" /f >nul
echo.

echo == 변경 후 ==
reg query "%KEY%" /v DelegationConsole
reg query "%KEY%" /v DelegationTerminal
echo.

echo 완료. DevezCode 를 완전히 종료했다가 다시 실행해서 확인하세요.
echo (이미 실행 중인 프로세스에는 적용 안 됨 - 재실행 필요)
pause

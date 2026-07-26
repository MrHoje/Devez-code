@echo off
setlocal DisableDelayedExpansion
rem Lightweight Kimi state hook. Arg1 = working | waiting-on | waiting-off.
rem Keep this file ASCII-only so cmd.exe parses it consistently on every codepage.
rem PreToolUse/PostToolUse -> working (busy keepalive), PermissionRequest -> waiting-on,
rem PermissionResult -> waiting-off. No turn matching needed (Kimi has dedicated events).
if "%DEVEZCODE_ROOM_ID%"=="" (
  "%SystemRoot%\System32\more.com" >nul 2>nul
  exit /b 0
)
if /i not "%DEVEZCODE_TRACKING_AGENT%"=="kimi" (
  "%SystemRoot%\System32\more.com" >nul 2>nul
  exit /b 0
)

set "base=%APPDATA%\DevezCode\kimi"
set "room=%DEVEZCODE_ROOM_ID%"
if not exist "%base%" mkdir "%base%" >nul 2>&1
set "payload=%base%\payload-%RANDOM%%RANDOM%.tmp"
"%SystemRoot%\System32\more.com" >"%payload%" 2>nul
set "root="
set "active="
if exist "%base%\sessions\%room%.root.txt" set /p "root="<"%base%\sessions\%room%.root.txt"
if exist "%base%\active\%room%.txt" set /p "active="<"%base%\active\%room%.txt"
if "%root%"=="" goto :cleanup
if /i not "%active%"=="%root%" goto :cleanup
"%SystemRoot%\System32\findstr.exe" /i /r /c:"\"session_id\"[ ]*:[ ]*\"%root%\"" /c:"\"sessionId\"[ ]*:[ ]*\"%root%\"" "%payload%" >nul 2>nul
if errorlevel 1 goto :cleanup

if /i "%~1"=="working" (
  rem Keepalive while tools/subagents run. Do not invent a "main turn" fence here ?
  rem Stop is the sole idle authority. Late SubagentStart after a real Stop can still
  rem re-arm running briefly; next Stop/StopFailure clears it (same as PreToolUse).
  call :write "%base%\busy\%room%.txt" "running"
  goto :cleanup
)
if /i "%~1"=="waiting-on" (
  call :write "%base%\waiting\%room%.txt" "waiting"
  goto :cleanup
)
if /i "%~1"=="waiting-off" (
  call :write "%base%\waiting\%room%.txt" "idle"
  goto :cleanup
)
goto :cleanup

:cleanup
if exist "%payload%" del /f /q "%payload%" >nul 2>&1
exit /b 0

:write
rem Atomic temp+move: a killed hook never leaves a zero-byte state file.
set "target=%~1"
set "dir=%~dp1"
if not exist "%dir%" mkdir "%dir%" >nul 2>&1
set "tmp=%target%.%RANDOM%%RANDOM%.tmp"
>"%tmp%" echo %~2
move /y "%tmp%" "%target%" >nul 2>&1
if exist "%tmp%" del /f /q "%tmp%" >nul 2>&1
exit /b 0

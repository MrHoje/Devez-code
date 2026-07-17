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

set "base=%APPDATA%\DevezCode\kimi"
set "room=%DEVEZCODE_ROOM_ID%"
if not exist "%base%" mkdir "%base%" >nul 2>&1
rem Drain stdin so the hook runner's write completes cleanly.
"%SystemRoot%\System32\more.com" >nul 2>nul

if /i "%~1"=="working" (
  call :write "%base%\busy\%room%.txt" "running"
  goto :eof
)
if /i "%~1"=="waiting-on" (
  call :write "%base%\waiting\%room%.txt" "waiting"
  goto :eof
)
if /i "%~1"=="waiting-off" (
  call :write "%base%\waiting\%room%.txt" "idle"
  goto :eof
)
goto :eof

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

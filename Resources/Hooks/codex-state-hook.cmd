@echo off
setlocal
rem Lightweight Codex state hook. Arg1 = waiting|working.
rem Keep this file ASCII-only so cmd.exe parses it consistently on every codepage.
rem Codex writes the hook payload to stdin. Drain it before returning even though this
rem hook only needs the event-specific argument; otherwise the hook runner can hit a
rem broken pipe while this process exits early.
"%SystemRoot%\System32\more.com" >nul 2>nul
if "%DEVEZCODE_ROOM_ID%"=="" exit /b 0

set "base=%APPDATA%\DevezCode\codex"
set "room=%DEVEZCODE_ROOM_ID%"
if not exist "%base%\busy" mkdir "%base%\busy" >nul 2>&1
if not exist "%base%\waiting" mkdir "%base%\waiting" >nul 2>&1

if /i "%~1"=="waiting" (
  call :write "%base%\waiting\%room%.txt" "waiting"
  exit /b 0
)
if /i "%~1"=="working" (
  call :write "%base%\busy\%room%.txt" "running"
  call :write "%base%\waiting\%room%.txt" "idle"
)
exit /b 0

:write
rem Atomic temp+move: a killed hook never leaves a zero-byte state file.
set "tmp=%~1.%RANDOM%%RANDOM%.tmp"
>"%tmp%" echo %~2
move /y "%tmp%" "%~1" >nul 2>&1
if exist "%tmp%" del /f /q "%tmp%" >nul 2>&1
exit /b 0

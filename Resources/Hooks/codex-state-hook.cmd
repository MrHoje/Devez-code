@echo off
setlocal DisableDelayedExpansion
rem Lightweight Codex state hook. Arg1 = waiting|working.
rem Keep this file ASCII-only so cmd.exe parses it consistently on every codepage.
rem Tool hooks may arrive after Stop or from an internal/subagent turn. Save stdin and
rem accept it only when its turn_id matches the active root turn written by hook.ps1.
if "%DEVEZCODE_ROOM_ID%"=="" (
  "%SystemRoot%\System32\more.com" >nul 2>nul
  exit /b 0
)

set "base=%APPDATA%\DevezCode\codex"
set "room=%DEVEZCODE_ROOM_ID%"
set "payload=%base%\payload-%RANDOM%%RANDOM%.tmp"
if not exist "%base%" mkdir "%base%" >nul 2>&1
"%SystemRoot%\System32\more.com" >"%payload%" 2>nul
set "active="
if exist "%base%\active\%room%.txt" set /p "active="<"%base%\active\%room%.txt"
if "%active%"=="" goto :cleanup
"%SystemRoot%\System32\findstr.exe" /i /r /c:"\"turn_id\"[ ]*:[ ]*\"%active%\"" "%payload%" >nul 2>nul
if errorlevel 1 goto :cleanup

if not exist "%base%\waiting" mkdir "%base%\waiting" >nul 2>&1

if /i "%~1"=="waiting" (
  call :write "%base%\waiting\%room%.txt" "waiting"
  call :validate_active
  goto :cleanup
)
if /i "%~1"=="working" (
  call :write "%base%\waiting\%room%.txt" "idle"
  call :validate_active
)
:cleanup
if exist "%payload%" del /f /q "%payload%" >nul 2>&1
exit /b 0

:validate_active
rem Close the read/write race with Stop or a newer prompt. If this payload's turn no longer
rem owns the room, converge waiting back to idle. A concurrent Stop also writes idle last.
set "current="
if exist "%base%\active\%room%.txt" set /p "current="<"%base%\active\%room%.txt"
if not "%current%"=="%active%" call :write "%base%\waiting\%room%.txt" "idle"
exit /b 0

:write
rem Atomic temp+move: a killed hook never leaves a zero-byte state file.
set "tmp=%~1.%RANDOM%%RANDOM%.tmp"
>"%tmp%" echo %~2
move /y "%tmp%" "%~1" >nul 2>&1
if exist "%tmp%" del /f /q "%tmp%" >nul 2>&1
exit /b 0

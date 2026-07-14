@echo off
setlocal DisableDelayedExpansion
rem Lightweight Grok tool-state hook. Arg1 = PreToolUse|PostToolUse|PostToolUseFailure.
rem Keep this file ASCII-only so cmd.exe parses it consistently on every codepage.
if "%DEVEZCODE_ROOM_ID%"=="" (
  "%SystemRoot%\System32\more.com" >nul 2>nul
  exit /b 0
)

set "base=%APPDATA%\DevezCode\grok"
set "room=%DEVEZCODE_ROOM_ID%"
if not exist "%base%\busy" mkdir "%base%\busy" >nul 2>&1
if not exist "%base%\waiting" mkdir "%base%\waiting" >nul 2>&1

if /i "%~1"=="PreToolUse" goto :pre_tool

rem Post events only need to drain stdin and restore the normal working state.
"%SystemRoot%\System32\more.com" >nul 2>nul
call :write "%base%\busy\%room%.txt" "running"
call :write "%base%\waiting\%room%.txt" "idle"
exit /b 0

:pre_tool
set "payload=%base%\payload-%RANDOM%%RANDOM%.tmp"
"%SystemRoot%\System32\more.com" >"%payload%" 2>nul
call :write "%base%\busy\%room%.txt" "running"
"%SystemRoot%\System32\findstr.exe" /i /r /c:"\"toolName\"[ ]*:[ ]*\"ask_user_question\"" /c:"\"toolName\"[ ]*:[ ]*\"askUserQuestion\"" /c:"\"tool_name\"[ ]*:[ ]*\"ask_user_question\"" "%payload%" >nul 2>nul
if errorlevel 1 (
  call :write "%base%\waiting\%room%.txt" "idle"
) else (
  call :write "%base%\waiting\%room%.txt" "waiting"
)
del /f /q "%payload%" >nul 2>&1
exit /b 0

:write
rem Atomic temp+move: a cancelled hook never leaves a zero-byte state file.
set "tmp=%~1.%RANDOM%%RANDOM%.tmp"
>"%tmp%" echo %~2
move /y "%tmp%" "%~1" >nul 2>&1
if exist "%tmp%" del /f /q "%tmp%" >nul 2>&1
exit /b 0

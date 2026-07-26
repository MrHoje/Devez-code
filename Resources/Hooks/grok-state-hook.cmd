@echo off
setlocal DisableDelayedExpansion
rem Lightweight Grok tool-state hook. Arg1 = PreToolUse|PostToolUse|PostToolUseFailure.
rem Keep this file ASCII-only so cmd.exe parses it consistently on every codepage.
if "%DEVEZCODE_ROOM_ID%"=="" (
  "%SystemRoot%\System32\more.com" >nul 2>nul
  exit /b 0
)
if /i not "%DEVEZCODE_TRACKING_AGENT%"=="grok" (
  "%SystemRoot%\System32\more.com" >nul 2>nul
  exit /b 0
)

set "base=%APPDATA%\DevezCode\grok"
set "room=%DEVEZCODE_ROOM_ID%"
set "sid=%GROK_SESSION_ID%"
set "tracked="
set "root="
if not "%sid%"=="" if exist "%base%\sessions\%room%.txt" set /p "tracked="<"%base%\sessions\%room%.txt"
if not "%sid%"=="" if exist "%base%\sessions\%room%.root.txt" set /p "root="<"%base%\sessions\%room%.root.txt"
if "%sid%"=="" goto :drain
if /i not "%tracked%"=="%sid%" goto :drain
if /i not "%root%"=="%sid%" goto :drain
if not exist "%base%\waiting" mkdir "%base%\waiting" >nul 2>&1
if not exist "%base%\busy" mkdir "%base%\busy" >nul 2>&1
if not exist "%base%\completed" mkdir "%base%\completed" >nul 2>&1

if /i "%~1"=="PreToolUse" goto :pre_tool

rem Post: drain stdin, re-arm busy (long-tool recovery after premature Stop), clear waiting.
rem App-side events.jsonl poller converges to idle after turn_ended so late posts cannot stick-ON.
"%SystemRoot%\System32\more.com" >nul 2>nul
if exist "%base%\completed\%room%.flag" del /f /q "%base%\completed\%room%.flag" >nul 2>&1
call :write "%base%\busy\%room%.txt" "running"
call :write "%base%\waiting\%room%.txt" "idle"
exit /b 0

:pre_tool
set "payload=%base%\payload-%RANDOM%%RANDOM%.tmp"
"%SystemRoot%\System32\more.com" >"%payload%" 2>nul
rem Premature Stop/Notification idle must not permanently kill the spinner mid-turn.
if exist "%base%\completed\%room%.flag" del /f /q "%base%\completed\%room%.flag" >nul 2>&1
call :write "%base%\busy\%room%.txt" "running"
"%SystemRoot%\System32\findstr.exe" /i /r /c:"\"toolName\"[ ]*:[ ]*\"ask_user_question\"" /c:"\"toolName\"[ ]*:[ ]*\"askUserQuestion\"" /c:"\"tool_name\"[ ]*:[ ]*\"ask_user_question\"" "%payload%" >nul 2>nul
if errorlevel 1 (
  call :write "%base%\waiting\%room%.txt" "idle"
) else (
  call :write "%base%\waiting\%room%.txt" "waiting"
)
del /f /q "%payload%" >nul 2>&1
exit /b 0

:drain
rem Every Grok hook receives GROK_SESSION_ID. Fail closed for nested/inherited sessions so
rem their tool events cannot mutate the tracked root room's waiting state.
"%SystemRoot%\System32\more.com" >nul 2>nul
exit /b 0

:write
rem Atomic temp+move: a cancelled hook never leaves a zero-byte state file.
set "tmp=%~1.%RANDOM%%RANDOM%.tmp"
>"%tmp%" echo %~2
move /y "%tmp%" "%~1" >nul 2>&1
if exist "%tmp%" del /f /q "%tmp%" >nul 2>&1
exit /b 0

@echo off
setlocal
rem DevezCode antigravity(agy) hook. Event name arrives as %1 (registered per-event in hooks.json).
rem MUST stay ASCII-only: cmd parses batch in the OEM codepage (CP949 on ko-KR), so UTF-8
rem comments corrupt line parsing. Korean docs live in AntigravityHookInstaller.cs instead.
rem Field notes (agy 1.1.1, 2026-07-13):
rem  - agy keeps quotes when tokenizing hook commands and treats & as a separator
rem    -> only quote-free, space-free paths are safe (installer uses 8.3 short path if needed).
rem  - stdin JSON has no event name (only camelCase payload like conversationId) -> use %1.
rem  - SessionStart/Stop hook processes can be cancelled right after spawn (seen in --print)
rem    -> fast-starting batch instead of powershell; missed idle transitions are covered by
rem       the app-side stale failsafe in AntigravityHookService.
rem  - Room comes from %DEVEZCODE_ROOM_ID% (set by the app launch batch),
rem    conversation from %ANTIGRAVITY_CONVERSATION_ID%.
if "%DEVEZCODE_ROOM_ID%"=="" exit /b 0
set "base=%APPDATA%\DevezCode\antigravity"
set "room=%DEVEZCODE_ROOM_ID%"
if not exist "%base%\sessions" mkdir "%base%\sessions" >nul 2>&1
if not exist "%base%\busy" mkdir "%base%\busy" >nul 2>&1
rem PreToolUse writes "running-tool" (a tool is in flight) so the app-side transcript poller
rem never declares idle while a long tool is still running; PostToolUse downgrades to "running".
rem Anything starting with "running" counts as busy on the app side.
if not "%ANTIGRAVITY_CONVERSATION_ID%"=="" call :write "%base%\sessions\%room%.txt" "%ANTIGRAVITY_CONVERSATION_ID%"
if /i "%~1"=="PreToolUse"  call :write "%base%\busy\%room%.txt" "running-tool"
if /i "%~1"=="PostToolUse" call :write "%base%\busy\%room%.txt" "running"
if /i "%~1"=="Stop"        call :write "%base%\busy\%room%.txt" "idle"
if /i "%~1"=="SessionEnd"  call :write "%base%\busy\%room%.txt" "idle"
exit /b 0

:write
rem Atomic write: temp + move, so a mid-write kill never leaves a 0-byte state file.
set "tmp=%~1.%RANDOM%%RANDOM%.tmp"
>"%tmp%" echo %~2
move /y "%tmp%" "%~1" >nul 2>&1
if exist "%tmp%" del /f /q "%tmp%" >nul 2>&1
exit /b 0

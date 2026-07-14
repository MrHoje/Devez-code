@echo off
setlocal DisableDelayedExpansion
rem DevezCode Antigravity hook. Event name arrives as %1.
rem Keep this file ASCII-only: cmd.exe parses it with the active OEM codepage.
rem Every branch owns stdin until EOF so the hook runner never writes to a closed pipe.
rem Antigravity requires JSON stdout even for passive hooks. Stop needs a decision;
rem an empty value allows the normal stop without changing agent behavior.
if /i "%~1"=="Stop" (
  echo {"decision":""}
) else (
  echo {}
)
if "%DEVEZCODE_ROOM_ID%"=="" (
  "%SystemRoot%\System32\more.com" >nul 2>nul
  exit /b 0
)

set "base=%APPDATA%\DevezCode\antigravity"
set "room=%DEVEZCODE_ROOM_ID%"
set "busy=%base%\busy\%room%.txt"
set "waiting=%base%\waiting\%room%.txt"
set "completed=%base%\completed\%room%.flag"
if not exist "%base%\sessions" mkdir "%base%\sessions" >nul 2>&1
if not exist "%base%\busy" mkdir "%base%\busy" >nul 2>&1
if not exist "%base%\waiting" mkdir "%base%\waiting" >nul 2>&1
if not exist "%base%\completed" mkdir "%base%\completed" >nul 2>&1

rem Nested agy processes inherit the room ID. Accept a new conversation only after
rem SessionEnd of the tracked root, and ignore every state event from a nested ID.
if /i "%~1"=="SessionStart" (
  call :trackstart
) else if /i "%~1"=="SessionEnd" (
  call :markend
) else (
  call :trackcurrent
)
if errorlevel 1 (
  "%SystemRoot%\System32\more.com" >nul 2>nul
  exit /b 0
)

if /i "%~1"=="Stop" goto :stop

"%SystemRoot%\System32\more.com" >nul 2>nul
if /i "%~1"=="PreInvocation" (
  del /f /q "%completed%" >nul 2>&1
  call :write "%busy%" "running"
  call :write "%waiting%" "idle"
  exit /b 0
)
if /i "%~1"=="PostInvocation" (
  if not exist "%completed%" call :write "%busy%" "running"
  exit /b 0
)
if /i "%~1"=="PostToolUse" (
  if not exist "%completed%" call :write "%busy%" "running"
  call :write "%waiting%" "idle"
  exit /b 0
)
if /i "%~1"=="SessionEnd" (
  call :write "%completed%" "done"
  call :write "%busy%" "idle"
  call :write "%waiting%" "idle"
  exit /b 0
)
if /i "%~1"=="SessionStart" (
  del /f /q "%completed%" >nul 2>&1
  call :write "%busy%" "idle"
  call :write "%waiting%" "idle"
)
exit /b 0

:stop
set "payload=%base%\payload-%RANDOM%%RANDOM%.tmp"
"%SystemRoot%\System32\more.com" >"%payload%" 2>nul
"%SystemRoot%\System32\findstr.exe" /i /r /c:"fullyIdle.*false" /c:"fully_idle.*false" "%payload%" >nul 2>nul
if not errorlevel 1 (
  del /f /q "%completed%" >nul 2>&1
  call :write "%busy%" "running"
  call :write "%waiting%" "idle"
  del /f /q "%payload%" >nul 2>&1
  exit /b 0
)
rem Every Stop except the explicit fullyIdle=false continuation is terminal.
rem Older Antigravity payloads omit fullyIdle, so treating omission as done also
rem prevents their late PostToolUse from rearming a completed spinner.
call :write "%completed%" "done"
call :write "%busy%" "idle"
call :write "%waiting%" "idle"
del /f /q "%payload%" >nul 2>&1
exit /b 0

:trackstart
if "%ANTIGRAVITY_CONVERSATION_ID%"=="" exit /b 0
set "sid=%ANTIGRAVITY_CONVERSATION_ID%"
set "current="
if exist "%base%\sessions\%room%.txt" set /p "current="<"%base%\sessions\%room%.txt"
if "%current%"=="" goto trackwrite
if /i "%current%"=="%sid%" goto trackwrite
set "ended="
if exist "%base%\sessions\%room%.ended.txt" set /p "ended="<"%base%\sessions\%room%.ended.txt"
if /i not "%ended%"=="%current%" exit /b 1
call :write "%base%\sessions\%room%.prev.txt" "%current%"
:trackwrite
call :write "%base%\sessions\%room%.root.txt" "%sid%"
call :write "%base%\sessions\%room%.txt" "%sid%"
if exist "%base%\sessions\%room%.ended.txt" del /f /q "%base%\sessions\%room%.ended.txt" >nul 2>&1
exit /b 0

:trackcurrent
if "%ANTIGRAVITY_CONVERSATION_ID%"=="" exit /b 0
set "sid=%ANTIGRAVITY_CONVERSATION_ID%"
set "current="
if exist "%base%\sessions\%room%.txt" set /p "current="<"%base%\sessions\%room%.txt"
if not "%current%"=="" if /i not "%current%"=="%sid%" exit /b 1
call :write "%base%\sessions\%room%.txt" "%sid%"
exit /b 0

:markend
if "%ANTIGRAVITY_CONVERSATION_ID%"=="" exit /b 0
set "sid=%ANTIGRAVITY_CONVERSATION_ID%"
set "current="
if exist "%base%\sessions\%room%.txt" set /p "current="<"%base%\sessions\%room%.txt"
if not "%current%"=="" if /i not "%current%"=="%sid%" exit /b 1
if /i "%current%"=="%sid%" call :write "%base%\sessions\%room%.ended.txt" "%current%"
exit /b 0

:write
rem Atomic temp+move: a cancelled hook never leaves a zero-byte state file.
set "tmp=%~1.%RANDOM%%RANDOM%.tmp"
>"%tmp%" echo %~2
move /y "%tmp%" "%~1" >nul 2>&1
if exist "%tmp%" del /f /q "%tmp%" >nul 2>&1
exit /b 0

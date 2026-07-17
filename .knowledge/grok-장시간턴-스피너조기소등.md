# Grok 장시간 턴 스피너 조기 소등

## 증상
Grok 세션이 **대부분 정상이지만**, 동작이 길어지면(멀티 툴/멀티 루프) 좌측 스피너가 **중간에 꺼진 채 복구되지 않음**. 작업 자체는 계속 진행.

## 원인
1. Grok 한 user 턴은 `events.jsonl` 기준 **`turn_started` ~ `turn_ended`** 이고, 그 안에 `loop_started`/도구가 **수십 회** 반복된다(실측 6분+ 턴, loop 50+).
2. busy 는 훅이 `UserPromptSubmit→running`, `Stop/Notification(프롬프트 복귀)→idle + completed` 로 쓴다.
3. 2026-07-15 `0427e1d` 이후 **Pre/PostToolUse 가 busy 를 재무장하지 않음**(`completed` 면 무시). 조기 idle 이 한 번 오면 같은 턴 안에서는 스피너가 영구 소등.
4. 조기 idle 후보: 조기 `Stop`/`StopFailure`, Notification `"type your message|enter send|…"`, UI 단독 ESC(`InterruptRequested` → 파일은 running 인데 `IsBusy=false`만 끔).

## 수정 (2026-07-17)
- **훅** (`grok-hook.ps1` / `grok-state-hook.cmd`): PreToolUse·PostToolUse 가 `completed` 를 지우고 `busy=running` 재무장. 세션 fence(`Test-CurrentRoomSession`)는 유지.
- **앱** (`GrokHookService`): `~/.grok/sessions/.../events.jsonl` 2초 폴링.
  - 열린 턴(`turn_started` > `turn_ended`) 또는 미완료 도구 → `running` (+ completed 삭제)
  - `turn_ended` 정착(2s) 후 → `idle`
  - 하드킬 잔재: 활동 30분 초과 열린 턴은 idle
- 훅 스크립트는 앱 시작 시 `GrokHookInstaller.EnsureInstalled` 가 `%LOCALAPPDATA%\DevezCode\grok\` 에 덮어씀 → **재시작 필요**.

## 진단
- `%APPDATA%\DevezCode\grok\busy\<room>.txt` / `completed\<room>.flag`
- 해당 sid 의 `~/.grok/sessions/**/<sid>/events.jsonl` 에서 `turn_started`/`turn_ended`/`tool_*`
- `C:\devezLog\diag.log` 의 `busy[room] grok events 정정: idle→running (…)`

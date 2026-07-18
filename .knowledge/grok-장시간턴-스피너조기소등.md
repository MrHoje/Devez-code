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

## 수정 (2026-07-18) — 긴 턴 turn_started 유실 / 서브에이전트 대기
### 추가 원인
1. 한 턴 이벤트 스팬이 **90~250KB+**(phase_changed 폭주). 예전 **96KB 꼬리 스캔**은 턴 중반에 `turn_started` 를 놓침.
2. 꼬리만 보면 tool depth=0 + 활동 공백(서브에이전트/`run_terminal_command` 대기) → 가짜 idle.
3. Notification `"type your message|…"` 가 턴 중에도 idle+completed 를 써서 스피너를 끔.

### 추가 수정
- **GrokHookService**: events.jsonl **증분 커서**(방별 `InOpenTurn`/`ToolDepth` 유지). 폴링 1초.
- **fresh 창** 45s → 3분. turn_ended 이후에도 활성 phase 가 이어지면 유지.
- **훅**: 프롬프트 복귀 Notification 으로 busy idle/completed 쓰지 않음(Stop·turn_ended 권위).
- **MainWindow**: Grok 완료 정착 1.2s + `IsRoomBusy` 재확인(opencode/claude 와 동일).

### 추가 수정 (2026-07-18 종합 점검)
- 증분 파서가 **개행 없는 미완 줄**을 소비하지 않게 바이트 `\n` 경계로 커서 이동(이벤트 유실 방지).
- Stop 훅이 `completed` 플래그를 남기지 않음(조기 Stop 뒤 툴/폴러 재무장 방해 제거).
- `phase_changed` 단독 lastType 으로는 활성 판정하지 않음(phase 문자열만).

## 진단
- `%APPDATA%\DevezCode\grok\busy\<room>.txt` / `completed\<room>.flag`
- 해당 sid 의 `~/.grok/sessions/**/<sid>/events.jsonl` 에서 `turn_started`/`turn_ended`/`tool_*`
- `C:\devezLog\diag.log` 의 `busy[room] grok events 정정: idle→running (…)`

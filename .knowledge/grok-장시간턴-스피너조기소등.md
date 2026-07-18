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

### 추가 수정 (2026-07-18 재검수)
- **ToolDepth>0 분기에도 StaleRunningCap(30분) 적용** — 하드킬로 tool_completed/turn_ended 가
  영영 안 오면 이 분기가 busy 를 무한 재무장했다(앱 재시작해도 파일 재파싱으로 재현 = 영구 스피너).
  도구 실행 중 이벤트 공백 실측 최대 ~10분(558s)이라 30분 캡은 3배 여유.
- **turn_ended 가 `LastPhase` 를 초기화** — 턴 마지막 phase(streaming_text 등)가 남아 종료 후
  stray 이벤트(yolo_toggled 등)와 결합하면 post_end 가 최대 3분 재점등 + 중복 완료 카드.
- **훅 Notification permission ❗에 busy=running 게이트** — Stop 이 completed 를 안 남기게 된 뒤
  Test-Completed 가드가 무력화돼 턴 종료 후 늦은 permission 알림이 ❗를 다음 턴까지 박았다.

### 추가 수정 (2026-07-19) — in-process /new·rewind 후 스피너 사망 (root fence 잠금)
- **증상**: grok 에서 /new(또는 rewind fork)로 새 세션이 생기면 그 뒤로 스피너·lastmsg·완료기록 전부 죽음.
- **원인(실측)**: grok 은 in-process 세션 전환 때 추적 중인 옛 sid 로 **SessionEnd 를 발화하지 않는다**
  → `.ended.txt` 마커가 영영 없음 → root fence 가 옛 세션에 고정 → 새 sid 의 모든 훅이
  `Test-CurrentRoomSession` 에서 차단 + 폴러는 옛 events.jsonl(turn_ended)만 봐서 idle 확정.
  (프로세스 재시작 `-r <sid>` 재개는 sid 가 유지돼 문제없음. 자식 grok 차단용 fence 의 설계 가정
  "전환 전엔 SessionEnd 가 온다"가 실제와 달랐던 것.)
- **수정**: 훅이 방 root grok 의 **owner PID**(`sessions\<room>.owner.txt`)를 기록(SessionStart 갱신,
  없으면 백필). 새 sid 의 UserPromptSubmit 이 **같은 grok 프로세스**(부모 체인에서 grok.exe PID 탐색)
  에서 왔으면 정당한 전환으로 수락(prev 기록 후 root/cur 갱신). 자식 grok 은 PID 가 달라 기존처럼
  차단(fail-closed). 이름이 grok(.exe) 아닌 배포에선 탐색 실패 → 수락 없이 기존 동작.
- 진단법: 방 `sessions\<room>.txt` 의 sid 와 실제 작업 중인 `~/.grok/sessions/<cwd>/<sid>/` 최신
  디렉터리가 다르면 이 케이스. 복구 = root/cur 를 라이브 sid 로, owner 를 grok PID 로 원자적 재기록.

## 진단
- `%APPDATA%\DevezCode\grok\busy\<room>.txt` / `completed\<room>.flag`
- 해당 sid 의 `~/.grok/sessions/**/<sid>/events.jsonl` 에서 `turn_started`/`turn_ended`/`tool_*`
- `C:\devezLog\diag.log` 의 `busy[room] grok events 정정: idle→running (…)`
- 추적 sid ≠ 실제 작업 세션 디렉터리 → root fence 잠금(위 2026-07-19 항목)

# Claude GUI(SDK 브리지) 종료 시 세션/대화 유실

> 2026-08-06 수정 (`3980d5a`, `5e7f2a0`). 관련 파일: `Resources/ClaudeSdk/bridge.mjs`,
> `Services/ClaudeSdk/ClaudeSdkSessionManager.cs`, `MainWindow.xaml.cs`(OnClosing), `App.xaml.cs`(OnExit).

## 전제: GUI 세션도 transcript 는 claude CLI 가 쓴다

GUI(SDK) 세션은 ConPTY 를 쓰지 않을 뿐, 실제로는 브리지(node) → `@anthropic-ai/claude-agent-sdk`
→ **진짜 `claude` CLI 자식 프로세스** 구조다. 대화 기록(`~/.claude/projects/<slug>/<sid>.jsonl`)을
쓰는 주체는 그 CLI 자식이고, resume 도 그 파일에 의존한다.

따라서 **브리지를 먼저 죽이면 CLI 가 flush 할 틈이 없어 마지막 턴이 디스크에 안 남는다.**
터미널 경로(`GracefulShutdownAllAsync`: perGrace 2.5s + `WaitForHookFlushAsync` cap 5s)에
해당하는 보호 장치가 GUI 경로에는 없었다.

## 증상 (실측 대조)

응답 스트리밍 도중 앱을 종료한 뒤 jsonl 내용 비교:

| | jsonl 항목 종류 | 브리지 종료까지 |
|---|---|---|
| 수정 전(`3927dce`) | `queue-operation, attachment, user, last-prompt` | 74ms |
| 수정 후 | 위 + **`assistant`** + `[Request interrupted by user]` | 1.5s |

수정 전에는 assistant 블록이 **디스크에 아예 존재하지 않았다.** 재실행해 resume 하면 그 턴이 통째로 사라진 상태로 복원된다.

## 원인 4가지

1. **브리지가 50ms 만에 하드 종료.** `shutdown` 이 `prompts.close()` → `interrupt()` →
   `setTimeout(() => process.exit(0), 50)` 이었다. SDK 쿼리 루프(`for await`) 완료를 안 기다려서
   CLI 자식이 stdin EOF 를 받고 정리할 시간이 없었다. `process.exit()` 는 동기 종료라 SDK 의
   자식 teardown 도 안 돈다.
2. **line 핸들러가 async rejection 을 못 잡음.** `try { void handle(JSON.parse(line)) } catch {}` 는
   동기 예외만 잡는다. `interrupt()` 등이 reject 하면 unhandled rejection 으로 node 가 즉사 →
   flush 는커녕 `setTimeout` 조차 안 돈다.
3. **MainWindow.OnClosing 이 GUI 세션을 세지 않았다.**
   `if (!TerminalSessionManager.Instance.HasSessionsToClose()) return;` — GUI 세션은 ConPTY 를
   안 만들므로 "닫을 세션 없음"으로 판정돼 **안전 종료 단계(오버레이 + graceful)를 통째로 건너뛰고**
   창이 닫혔다. `App.OnExit` 폴백만 남았고 그마저 순차 대기(터미널 10s → SDK 7s)였다.
4. **transcript 조회 실패 시 세션 ID 를 영구 삭제.** `EnsureStartedAsync` 가
   `FindClaudeTranscriptPath == null` 이면 `RemoveClaudeCodeRoomSession` 을 호출했다. 폴더 권한·
   동기화 지연 같은 일시적 실패에도 방↔대화 연결이 끊긴다. `SessionExporter.FromClaude` 도 이 ID 를
   쓰므로 GUI 복원 목록까지 영구히 빈다.

## 수정

**`bridge.mjs`**

- `shutdown()` 을 정상 배수로 교체: 안전망 타이머(6s) 를 **제일 먼저** 걸고 → `prompts.close()` →
  (턴 중이면) 상한 1.5s 짜리 `interrupt()` → **`await sessionLoop`**(쿼리 루프 완료) → 자연 종료.
- `turnActive` 추적(`prompt` 에서 true, `result`/루프 종료에서 false). 진행 중 턴이 없으면 interrupt 를
  아예 안 보낸다 — idle 세션 종료가 3.1s → 2.3s.
- 마무리는 `process.exit()` 대신 `process.exitCode = 0` + `input.close()` + unref 타이머(1s).
  stdout 이 파이프일 때 `process.exit` 가 남은 버퍼를 잘라먹어 마지막 이벤트가 유실되기 때문.
- `handle(command).catch(...)` 로 async rejection 봉합. `input.on("close")` 도 같은 `shutdown()` 을 타고
  `shuttingDown` 플래그로 중복 진입 차단.

**`ClaudeSdkSessionManager.cs`**

- `DisposeAsync` 대기 5s → 8s(브리지 안전망 6s 보다 넉넉해야 정상 배수를 중간에 안 끊는다).
  타임아웃 시 `Kill(entireProcessTree: true)` — 기존엔 `Dispose()` 만 해서 node·claude 가 고아로 남았다.
- transcript 없을 때 세션 ID **삭제하지 않고 이번 기동만 resume skip**. 새 세션이 뜨면 `session`
  이벤트가 어차피 같은 키를 덮어쓰므로 자가 치유된다.
- `HasSessions` 추가.

**`MainWindow.xaml.cs` / `App.xaml.cs`**

- OnClosing 의 "닫을 세션" 판정에 `ClaudeSdkSessionManager.Instance.HasSessions` 포함.
- 터미널 graceful 과 SDK 배수를 `Task.WhenAll` 로 병렬(순차면 대기가 합산). OnExit 폴백도 동일, 상한 12s.

## 검증 방법 (재현 가능)

`bridge.mjs` 를 설치 폴더(`%LocalAppData%\DevezCode\ClaudeSdk\`)에 **다른 이름으로 복사**하면
그 옆 `node_modules` 로 import 가 해결되므로, 앱이 쓰는 `bridge.mjs` 를 건드리지 않고 단독 실행할 수 있다.
C# `ClaudeSdkBridgeProcess` 와 같은 JSON-lines 프로토콜로 말하는 드라이버를 붙여
`start` → (`prompt`) → `shutdown` → stdin close 를 재현하고, 종료 후 jsonl 을 파싱해 대조한다.

확인해야 할 케이스:

| 케이스 | 기대 |
|---|---|
| 새 세션 + 무프롬프트 종료 | **jsonl 미생성.** 세션 ID 는 저장됨(start 0.9s 뒤 `session` 이벤트). 재시작 시 resume skip → 새 세션, 오류 없음 |
| 턴 진행 중 종료 | jsonl 에 `assistant` + `[Request interrupted by user]` 기록 |
| resume | 오류 이벤트 0건, 같은 jsonl 이 이어서 커짐 |
| `shutdown` 명령 없이 stdin 만 close | 위와 동일하게 배수(`input.on("close")` 경로) |

## 함정

- **프롬프트를 한 번도 안 보낸 세션은 jsonl 이 만들어지지 않는다.** 그런데 세션 ID 는 저장된다.
  그 ID 로 그냥 resume 하면 `result isError=true` 가 떠서 GUI 에 오류 카드가 뜬다 →
  `EnsureStartedAsync` 의 transcript 존재 확인 가드는 **반드시 있어야 한다.**
- **`bridge.mjs` 수정은 앱 재시작해야 반영.** `EnsureInstalledAsync` 가 `bin\Resources\ClaudeSdk\` 에서
  `%LocalAppData%\DevezCode\ClaudeSdk\` 로 복사하는 구조라 빌드만으론 부족하다.
- 종료가 2~3초 걸리는 건 정상이다. claude CLI 가 stdin EOF 후 스스로 정리하는 시간이고,
  **그게 곧 flush 창**이다. 줄이면 유실이 돌아온다.
- 앱 재시작 후 GUI 대화 복원은 `SessionExporter.LoadClaudeConversation` 이 담당하는데
  **user/assistant 평문만** 복원한다(도구 호출·thinking·첨부·권한 카드는 화면에서 사라짐).
  `ClaudeSdkSessionManager._retainedEvents` 는 메모리 전용이라 프로세스 수명까지만 유효 — 설계상 한계.

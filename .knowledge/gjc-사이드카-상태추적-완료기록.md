# gjc 상태 추적 = 런타임 사이드카 + JSONL 하이브리드 / 세션 완료기록 스펙

> 2026-07-17 전면 개편. 관련 파일: `Services/GajaeLastMessageService.cs`,
> `MainWindow.xaml.cs` (`NotifyIfSessionFinished` / `AddGoalCompletionRecord`).

## 왜 개편했나 — gjc 0.11 의 transcript 지연 flush (실측)

- gjc 0.11 은 세션 transcript(`%APPDATA%\DevezCode\gajae\sessions\<roomId>\<ts>_<id>.jsonl`)를
  **수 분~수 시간 지연해 몰아서 flush** 한다. 실측: 12:43 이후 5시간 동안 EOF 불변 → 17:43 에 600줄 burst.
  (FileStream 실제 EOF 로 두 시점 측정 — mtime stale 문제가 아니라 진짜 디스크 미기록.)
- 그 동안 JSONL 폴링 기반 busy/idle/lastmsg 는 전부 몇 시간 stale → 스피너 안 뜨고 **완료기록 0건** 증상.
- 부분 flush 함정: burst 는 여러 청크로 나뉘어 도착할 수 있고, 청크 경계가 턴 중간(toolResult)이면
  jsonl 판정이 "이미 끝난 턴"을 busy 로 오판한다.

## 대체 신호원 탐사 결과 (0.11.1 실기 검증)

| 후보 | 결과 |
|---|---|
| `--hook <file>` | 0.10과 동일하게 **미파싱** — 값이 초기 프롬프트로 들어감. 전달 금지 |
| `~/.gjc/extensions/` 자동 발견 확장 | **격리(quarantine)됨** — `main.ts` 가 `disableExtensionDiscovery=true` 강제, sdk/session.ts 주석 "Filesystem extension paths remain ignored". 프로브 확장으로 factory 미실행 실측 |
| notifications WebSocket (`ws://127.0.0.1:<port>`) | 실존하나 토큰·포트 발견·프로토콜 통합 비용 큼 — 보류 |
| **런타임 사이드카** `runtime-state.json` | ✅ 채택. gjc 가 이벤트 즉시 잠금+원자적으로 직접 기록 |

## 채택: gjc 런타임 사이드카

- 경로: `<방 workingDir>\.gjc\_session-<sessionId>\runtime\runtime-state.json`
  (writer: gjc `src/gjc-runtime/session-state-sidecar.ts`)
- 스키마(요지): `session_id`, `state`, `ready_for_input`, `updated_at`(ISO),
  `event`(agent_start/turn_start/agent_end/process_exit…), `session_file`(← **roomId 매핑 근거**),
  완료 시 `final_response.text`(마지막 assistant text).
- state 전이: `agent_start`/`turn_start`→`running`(턴마다 updated_at 갱신),
  `agent_end`→`completed`/`errored`. 프로세스 정상 종료·시그널은 **postmortem 파이널라이저**가
  completed/errored 로 마감 → stale `running` 잔재는 사실상 하드킬뿐.
- 유효 상태 집합: `booting|ready_for_input|running|needs_user_input|completed|errored|stale|unknown`.

### GajaeLastMessageService 병합 규칙

1. 방별 후보 세션 ID = 최신 .jsonl 파일명 + orphan 새 세션 디렉터리명 + settings 저장값.
   각 후보의 사이드카를 읽어 `session_id` 일치 + `session_file` 부모 == 방 session-dir 인 것 중
   updated_at 최신을 채택. (서브에이전트 사이드카는 session_file 이 한 단계 깊어 자동 배제.)
2. `needs_user_input` → busy+❗ / `running`(age<15분) → busy / `running` stale → jsonl 폴백(하드킬 봉인)
   / idle 상태(completed·errored·ready_for_input) → **jsonl 이 busy 를 재점화할 수 없다**(부분 flush 차단).
   booting·stale·unknown·사이드카 없음 → 기존 jsonl 단독 판정(+idle 정착 900ms).
3. 헤더 lastmsg 는 여전히 transcript 원본 → flush 지연 시 늦게 갱신될 수 있음(수용).
4. 시그니처 스킵(sig)에 사이드카 서명 포함 — idle 방도 사이드카 전이가 재평가를 트리거.

## 세션 완료기록 스펙 (2026-07-17 확정)

- **프롬프트(=gjc agent 루프) 완료 1건당 카드 1장이 쌓인다**(최대 30장, `MaxSessionDoneRecords`).
- busy 재진입 시 **기존 카드를 삭제하지 않는다.** 10e6034 가 넣었던 `RemoveSessionCompletionRecords`
  (busy 재진입 시 그 세션 카드 전부 삭제)가 "완료기록이 안 쌓이는" 회귀의 절반이었고 제거됨.
  플랩(가짜 idle) 방지는 발행 "전"의 정착 디바운스(1.2s)+`isStillActive` 재확인이 담당:
  claude 는 훅 파일 재확인, gjc 는 `IsRoomBusy`(사이드카 병합값) 재확인.
- gjc goal 모드: 골 하나 = user 주입 프롬프트 1개 = agent_end 1회 → 골마다 카드 1장(의도된 동작).
  `turn_end`(내부 도구 사이클)는 완료 신호로 쓰지 않는다 — 매 턴 카드가 쌓이는 오동작 방지.
- **idle 근거 이원화 (2026-07-18)**: 사이드카 `state=completed`(agent_end)가 만든 idle 은
  `IsIdleAuthoritative`=true → MainWindow 가 **디바운스 없이 즉시** 카드 발행(골 체이닝이 1.2s 내
  재시작해도 카드 안 삼킴). 그 외(jsonl 폴백·stale 폴백) idle 은 settle 1.2s + `IsRoomBusy` 재확인 —
  이때 `IsRoomBusy` 는 그 방을 **강제 재평가(ScanRoom)** 한다. (기존엔 in-memory 사전만 봤는데,
  idle 발행 직후 폴 주기가 IdleInterval 2.5s 로 늘어 settle 1.2s 안에 갱신이 없어 재확인이 no-op —
  auto-retry 재무장을 못 잡는 타이밍 구멍이었다.) errored 는 auto-retry 직전일 수 있어 확정으로
  치지 않는다(최종 에러면 재확인이 idle 로 통과해 카드는 나온다).
- goal 백필: transcript 의 `custom/goal-completed`(objective·timestamp 포함)를 파싱해
  **사이드카 미가동 방에서만** 카드 추가(`GoalCompleted` 이벤트, 토스트 없음).
  파일 최초 스캔(과거 내역)은 재발행하지 않는다(`TranscriptCursor.Primed`).

## 함정 모음

- transcript 의 `timestamp` 는 **UTC** — KST 와 9시간 차이. 시간 대조 시 주의.
- gjc 는 mode_change/custom/custom_message 등 비-message 엔트리를 다수 기록 — busy 파서는
  `type=="message"` 만 상태에 반영해야 한다.
- 방 삭제 후 재사용 시 사이드카는 workingDir(.gjc) 소속이라 정리 대상이 아니다 —
  방별 세션 ID 후보가 방 디렉터리에서 나오므로 오염 없음.

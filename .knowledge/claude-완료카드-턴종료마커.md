# claude 완료카드 중복(서브 드레인 flap) — 턴종료 마커 게이트

> 2026-07-22 수정. 관련 파일: `Services/Terminal/TerminalSessionManager.cs`(busyScript),
> `Services/SessionBusyService.cs`, `MainWindow.xaml.cs`(`NotifyIfSessionFinished`).

## 증상 (실측)

- claude 세션 하나가 **같은 프롬프트 1개로 완료카드 12장**(settings.json `SessionHistoryRecords`)을
  3~6분 간격으로 발행. `LastMessage` 가 마지막 유저 프롬프트에 고정 = 턴이 안 바뀌었는데 카드만 쌓임.
- 가짜 "응답 완료" 토스트/작업표시줄 알림도 동반. 스피너는 서브 사이 공백마다 껐다 켜짐(flap).

## 원인

- claude busy 진실 = `main_<room>.flag 존재 OR 살아있는 subruns\<room>\*.run > 0` (`ComputeBusyTruth`).
- 문제 방은 **main 플래그가 유실**된 채 Task 서브에이전트(135개 순차)만 busy 를 지탱.
  서브 하나 끝나고 다음 substart 전 공백이 1.2s(FinishSettleMs) 를 넘으면
  substop 재평가가 `idle` 을 쓰고 → 디바운스 재확인(`IsRoomActive`)도 main 없고 서브 0이라 통과 →
  `EmitSessionFinished` → **Task 사이마다 카드 1장**.
- 결정적 대조: 같은 시각 main 플래그 있던 다른 세션은 서브 잔뜩 돌려도 카드 정상 1장.
- main 플래그 유실 근본 원인은 미확정(앱 재시작 wipe 와 장수 턴 겹침 추정). 방어를 신호 층에 넣음.

## 수정: 턴종료 마커 (done\<room>.txt)

훅(busyScript)의 idle 쓰기 경로가 원래 둘이었음 — 이를 구분하는 마커 도입:

| idle 을 쓰는 경로 | 의미 | 마커 |
|---|---|---|
| Stop/SessionEnd 분기 | **진짜 메인 턴 종료** | `%APPDATA%\DevezCode\claude\done\<room>.txt` 생성(Write-State, busy idle 보다 먼저) |
| substop 드레인 재평가 | 서브 공백(턴 진행 중일 수 있음) | 마커 없음 |

- C# `NotifyIfSessionFinished` 에 `isRealFinish` 게이트 추가(옵셔널 — claude 만 전달, 타 에이전트 무영향).
  디바운스 통과 후 `SessionBusyService.ConsumeTurnEndMarker(room)`(존재확인+삭제=소비) 가 false 면
  카드/토스트/알림 전부 스킵 + diag.log `완료카드 스킵: 턴종료 마커 없음`.
- 훅 `running`(UserPromptSubmit) 분기가 **미소비 마커를 삭제** — 옛 마커가 다음 턴 flap 에 오발행되는 것 방지.
  (부작용: Stop 후 1.2s 내 재프롬프트 시 카드 없음 — 기존 디바운스 취소와 동일 semantics, 회귀 아님.)
- **보조 경로** `SessionBusyService.TurnEndMarker`(done 디렉터리 FSW): main 유실 방은 진짜 Stop 시점에
  busy 가 이미 idle 이라 전이가 없어 카드가 0장이 될 뻔 → 마커 생성 이벤트에서
  `!IsBusy && !IsRoomActive && Consume` 이면 즉시 발행(마커=Stop 실발화라 정착 디바운스 불필요, gjc
  `IsIdleAuthoritative` 와 같은 사상). 일반 방은 이 시점 IsBusy=true 라 스킵 → 전이 경로가 소비.
- 이중 발행 없음: 소비 호출부(디바운스 타이머·TurnEndMarker 핸들러) 모두 **UI 스레드 직렬화** + 소비=삭제 1회.
- stale 방지: 앱 시작 시 `SessionBusyService.Start()` 가 done 디렉터리 wipe(busy 파일과 같은 정책).
- Stop 시 살아있는 서브가 남아 busy 가 running 유지되는 케이스: 마커는 남고, 드레인 완료 idle 에서 소비 → 카드 1장(시점만 늦음).
- `HookAssetsHealthy()` 에 `claude\done` 토큰 추가 — 구버전 훅 감지.

## 함정 / 잔여

- **훅 스크립트는 앱 재시작해야 재배포**(`EnsureSessionHookAssets` 가 WriteAllText). 빌드만으론 미반영.
- reconcile 이 정정한 idle(Stop 훅 자체가 유실된 극히 드문 경우)은 마커가 없어 카드 누락 — 의도된 트레이드오프
  (그 상황은 스피너도 이미 고장). diag.log 스킵 라인으로 판별 가능.
- **스피너 flap 자체는 안 고침**(카드/알림만 차단). substart 에서 main 플래그 재생성으로 막을 수 있으나
  백그라운드 에이전트가 턴 밖에서 substart 를 쏘면 stuck-ON 위험 — 건드리지 말 것.
- 완료기록 spec 원문은 `.knowledge/gjc-사이드카-상태추적-완료기록.md` §세션 완료기록 스펙 참고
  (프롬프트 완료 1건당 카드 1장, busy 재진입 시 기존 카드 삭제 금지).

## 진단 팁

- 카드 중복 의심 시: `settings.json` `SessionHistoryRecords` 에서 같은 `SessionId`+`LastMessage` 반복 확인.
- 그 방의 `claude\busy\_state\main_<room>.flag` 부재 + `claude\subruns\<room>\*.done` 다수면 이 케이스.
- diag.log 에 `완료카드 스킵: 턴종료 마커 없음` 라인 = 게이트가 flap 을 실제로 막는 중.

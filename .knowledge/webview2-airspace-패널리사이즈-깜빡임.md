# WebView2(터미널) airspace — 패널 리사이즈/오버레이 시 깜빡임 원인과 해법

## 한 줄 요약
터미널은 **WebView2 = HwndHost(네이티브 HWND)** 라 WPF 요소로 그 위를 덮을 수 없다(airspace).
그래서 오버레이/패널 전환 시 터미널을 정지시켜야 한다. 정지 방식은 두 가지다:
1. **스냅샷 + `Collapsed`**(HWND 숨김) — 오버레이가 위로 덮거나 패널이 숨겨지는 경로용.
2. **웹 레이어 커버(`#xfer-cover`) + HWND 유지** — 터미널을 **리사이즈**하는 경로(좌/우/사용량/세션완료기록 토글, 분할 펼침/접힘)용.
   HWND 를 숨기지 않고 WebView **자기 DOM 안**에 캡처 이미지를 덮은 채 애니메이션 → 최종 폭에서 fit 후
   커버→라이브 크로스페이드. **HWND 전환 0회 = 무플래시**. (2026-07-03, 리사이즈 경로 reveal 깜빡임 완전 제거.)

> 과거에는 리사이즈 경로의 reveal 1~2 프레임 깜빡임을 "근본 해결 불가"로 봤으나(아래 시도 1·2),
> **웹 레이어 in-DOM 커버**(시도 3, 성공)로 해결했다. 아래 "결론" 갱신분 참조.

## airspace 핵심 (제일 중요)
- WebView2 의 렌더는 별도 HWND 에서 합성된다 → **항상 모든 WPF 콘텐츠 위**에 그려진다.
  WPF `Image`/`Border` 를 ZIndex 로 아무리 올려도 라이브 WebView 를 가리지 못한다.
- 따라서 "터미널 위에 무언가를 띄우는" 유일한 WPF 방법은 **별도 최상위 Window**(topmost + `SWP_NOACTIVATE`,
  예: `NotificationPopup`)뿐이다. WPF 오버레이(`ShutdownOverlay` 등)는 WebView 를 먼저 숨겨야만 보인다.
  (과거 예시였던 `ProjectTargetPickerWindow` 는 제거됨 — 분할패널 문서 "제거된 구 UI" 참조.)
- **단, WebView 자신의 DOM 안 요소는 예외** — 같은 swap chain 이라 라이브 콘텐츠(reflow repaint 포함) 위에
  그려진다. 이게 시도 3(웹 레이어 커버)의 핵심이다.

## 정지 메커니즘 (`WorkspacePaneView`)
- `CapturePngAsync()` = `CoreWebView2.CapturePreviewAsync(PNG)` → 현재 화면 비트맵.
- `SuspendTerminalOnlyAsync(anchorTopLeft, webCover)` :
  - `webCover=false`(기본) — 스냅샷을 `TerminalSnapshot`(Image)에 깔고 `TerminalHostContainer.Visibility=Collapsed`.
  - `webCover=true` — HWND 를 **숨기지 않고** 웹 레이어 `#xfer-cover` 에 캡처 이미지를 덮는다(아래 webCover 경로).
- `ResumeTerminalOnly(webCover)` :
  - `webCover=false` — 컨테이너 다시 `Visible`, WPF 스냅샷 숨김.
  - `webCover=true` — `UpdateLayout()` 로 최종 폭 확정 후 `RevealAfterTransition(id, kick, expectWidth)` 로
    JS 에서 최종 폭 fit + 커버→라이브 크로스페이드.
- `SuspendTerminalWithSnapshotAsync(blankCurtain)` — 설정/MCP 창·종료용(스냅샷 또는 단색 커튼 + Collapsed).
- `PrepareShutdownSnapshotAsync()` / `CommitShutdownHide()` — 종료용 2단계(아래 적용 현황 참조).

### webCover 경로 (리사이즈 무플래시) — 시도 3의 실제 구현
- **suspend**: `CapturePngAsync()` → `CoverForTransitionImage(png, cw, ch)` → JS `xferCover` 가
  `#xfer-cover`(z-index 45) 에 캡처 이미지를 **좌상단 고정 + 캡처 크기(px)** 로 깔고 `opacity=1`.
  동시에 `_fitSuppressed=true` 로 전환 중 중간(전체) 폭 fit 을 억제. 캡처 실패 시 `CoverForTransition()`(단색) 폴백.
  커버가 올라온 뒤 `WaitForFramesAsync(2)` 로 첫 리플로우 프레임을 가린 뒤 애니메이션 시작.
- **resume**: `RevealAfterTransition` → JS `xferReveal` 가 `clientWidth` 가 `expectWidth`(C# 확정 최종 폭)에
  근접할 때까지 대기(전체→절반 전환의 중간 전체폭은 건너뜀) → `_fitSuppressed` 해제 → **최종 폭에서 한 번만 fit** +
  ConPTY 재동기 → `opacity 0.14s` 크로스페이드로 커버 걷음.
- **안전장치**: reveal 이 안 와도 2s 뒤 자동 커버 해제. `_xferGen`(세대) 검사로 빠른 재전환 시 낡은 타이머 폐기.
- **핵심**: 리플로우(re-fit)가 **커버 아래**서 일어나 안 보이고, HWND 를 애초에 숨기지 않으니 Collapsed→Visible
  재합성 플래시도 없다. → 리사이즈 경로 특유의 reveal 깜빡임이 사라진다.

### ⚠️ `Collapsed` 여야 한다 (`Hidden` 금지) — 스냅샷+Collapsed 경로 한정
HwndHost 는 `Visibility.Hidden` 에서 **레이아웃 슬롯을 남기며 네이티브 HWND 를 계속 보여준다**.
즉 `Hidden` 으로 숨기면 애니메이션 내내 라이브 터미널이 그대로 비쳐 심하게 깜빡인다.
`Collapsed`(슬롯 0)여야 HwndHost 가 HWND 를 실제로 숨긴다. (2026-06-26, Hidden 실험으로 회귀 확인 후 복원.)
(webCover 경로는 애초에 HWND 를 숨기지 않으므로 이 규칙과 무관.)

### `anchorTopLeft` vs `stretch` — 커버 크기 정책은 전환 종류로 고른다
스냅샷 Image(또는 웹 커버 이미지)가 `Stretch=Fill`/`backgroundSize:auto` 면 패널이 커질 때 같이 쭉 늘어난다.
`anchorTopLeft`(WPF) / `imgW·imgH`(웹 커버) 로 캡처 시점 크기로 **좌상단 고정** → 패널이 잘라낼 뿐
늘어나지 않는다(실제 터미널 reflow 와 비슷한 인상).
- **단, 좌상단 고정은 "패널" 리사이즈용이다.** 전체화면 토글처럼 **창 전체가 한 번에 크게 커지는**
  전환에서 px 고정을 쓰면 캡처 밖 영역(오른쪽·아래 넓은 띠)이 배경색만 남아 **"비어" 보인다**.
  이 경우 `stretch`(`xferCover` 의 `msg.stretch` → `backgroundSize:100% 100%`)로 커버를 뷰포트에
  맞춰 늘린다 — OS 최대화 애니메이션과 같은 인상. 체인: `CoverForTransitionImage(..., stretch)` ←
  `SuspendTerminalOnlyAsync(stretchCover:)` ← `FreezeWorkspaceTerminalsAsync(stretchCover:)`.
- 요약: 이웃 패널이 남는 공간을 채우는 **패널 리사이즈 = 좌상단 고정**, 창 전체가 점프하는
  **윈도우 리사이즈 = stretch**.

### 흰색 클리어 방지
리사이즈 중 WebView2 가 흰색으로 클리어했다 다시 그린다 →
`webView.DefaultBackgroundColor = #0C0C0C`(터미널 배경)로 흰 플래시 차단(`TerminalHostView` 초기화).

## 왜 어떤 경로는 안 깜빡이고 어떤 건 깜빡였나
| 경로 | 터미널 리사이즈? | 정지 방식 | 결과 |
|---|---|---|---|
| 설정/MCP 창, 우측 드로어 오버레이 | ✗ (오버레이가 위에 슬라이드) | 스냅샷+Collapsed | resume 크기 == 캡처 크기 → reflow 없음 → **매끄러움** |
| 분할 접힘 시 숨겨지는 PaneB | ✓ 이지만 숨겨짐 | 스냅샷+Collapsed | 어차피 사라지므로 collapse 경로 유지 |
| 좌/우/사용량/세션완료기록 토글, 분할 펼침, 분할 접힘 후 넓어지는 PaneA | ✓ (중앙 `*` 컬럼 변함) | **webCover** | 리플로우가 커버 아래·HWND 전환 0회 → **무플래시** |

과거(2026-06-26): 리사이즈 경로는 스냅샷+Collapsed 만 썼고, reveal 직후 xterm 이 0→최종폭 re-fit 하는
1~2 프레임이 HWND 위로 비쳐 깜빡였다. → 시도 3(webCover)으로 해결.

## reveal 깜빡임 해법 시도 기록

### ❌ 시도 1 — RenderTargetBitmap 정적 최상위 커버 창 (2026-06-26, 실패·롤백)
reveal 직전 `CenterSplit` 을 `RenderTargetBitmap` 으로 캡처해 picker 패턴의 borderless 창(`SnapshotCoverWindow`)으로
덮고, 그 아래서 WebView 를 되살린 뒤(150ms settle) 닫는 방식. **실패**:
- 깜빡임이 그대로였고(= 캡처/덮기 타이밍이 실제 reflow 순간을 못 가림),
- 커버 창을 150ms 띄웠다 닫는 과정에서 "재개 시 더 이상한 동작"(정적 비트맵이 잠깐 떴다 튀는 인상)이 생김.

### ❌ 시도 2 — 커버 "올라온 것 확인 후"(await Shown) reveal (2026-06-26, 실패·롤백)
시도 1 의 타이밍 버그(Show 직후 곧장 reveal)를 고쳐, `SnapshotCoverWindow.Shown`(=`OnContentRendered`)
을 await 한 뒤에만 resume 했다. 커버가 확실히 위에 올라온 뒤 reflow 가 일어나는데도 **깜빡임이 그대로**였다.
→ 정적 **최상위 WPF 커버 창**으로는 못 막는다는 결론(아래 참조). `SnapshotCoverWindow` 는 롤백·제거됨.

### ✅ 시도 3 — 웹 레이어 in-DOM 커버 (2026-07-03, 성공·현행)
커버를 **최상위 WPF 창이 아니라 WebView 자신의 DOM(`#xfer-cover`)** 에 둔다. 같은 swap chain 이라
라이브 repaint(reflow) 위에 그려지므로 시도 1·2 가 못 막던 그 프레임을 가린다. 게다가 **HWND 를 숨기지
않으므로**(Collapsed→Visible 재합성 플래시 없음) resume 도 깔끔하다. 위 "webCover 경로" 구현 참조.
- 정지: `CoverForTransitionImage(png)` 로 캡처 이미지 커버 + `_fitSuppressed`.
- 재개: `RevealAfterTransition(expectWidth)` 로 최종 폭 대기 → 1회 fit → 크로스페이드.
- 좌우 동시(synced) 재개: 각 패널이 `RevealPreparedSynced`(폭 안정·fit·재동기까지만) 를 보고하면 셸이
  양쪽 준비를 모아 `FadeNow`(`fadeNow`)로 동시에 커버를 걷는다 → 좌우가 정확히 같은 순간에 뜬다.

### 결론 — 정적 최상위 WPF 커버로는 왜 못 막았나 (windowed WebView2 swap chain)
windowed 모드 WebView2 는 **자체 GPU swap chain 으로 화면에 직접 합성**한다(DWM 의 창 z-order 합성을
부분적으로 우회). 그래서:
- **정적**일 때는 picker 같은 최상위 창이 위에 보인다(WebView 가 다시 안 그림).
- 그러나 **reflow 로 다시 그리는 순간**엔 그 repaint 가 최상위 WPF 커버 창을 뚫고 비친다 → 시도 1·2 실패.
- **하지만 WebView 자신의 DOM 요소는 같은 swap chain 안**이므로 그 repaint 위에 그려진다 → 시도 3 성공.

즉 "터미널 위를 정적으로 가리는 것"은 최상위 WPF 로도 되지만, "**활성 reflow repaint 를 가리는 것**"은
**WebView DOM 안(웹 레이어)** 에서만 된다. 이것이 리사이즈 경로 무플래시의 열쇠였다.

> 핵심 교훈: airspace 는 최상위 WPF 커버가 "정적 가림"만 해결하게 만든다. 활성 repaint(reflow)는 swap chain 으로
> 뚫고 나오므로 최상위 창으론 못 가린다. **그 repaint 를 가리려면 커버가 같은 swap chain(=WebView DOM) 안에
> 있어야 한다.** windowless 호스팅 없이도 in-DOM 커버로 근본 해결됨(시도 3).

## 적용 현황 (2026-07-03)
- **좌/우/사용량/세션완료기록 토글**: `FreezeWorkspaceTerminalsAsync()`(각 패널 `SuspendTerminalOnlyAsync(anchorTopLeft:true, webCover:true)`)
  → 애니메이션 → `UnfreezeWorkspaceTerminals()`(`ResumeTerminalOnly(webCover:true)`). **무플래시 크로스페이드.**
  (사용량 = `_usageAnimCancel`, 세션완료기록 = `_sessionHistoryAnimCancel`/`SessionHistoryCol`.)
- **분할 펼침**(`AnimateSplitOpenAsync`): PaneA·PaneB 둘 다 `webCover:true` suspend → 애니메이션 → `webCover:true` resume. 무플래시.
- **분할 접힘**(`AnimateSplitCloseAsync`): 넓어져 살아남는 PaneA 는 `webCover:true`(무플래시 크로스페이드),
  사라지는 PaneB 는 스냅샷+Collapsed(`webCover` 없음) — 어차피 hide 되므로 collapse 경로 유지.
- **전체화면 토글**(2026-07-08, `RunFullScreenTransitionCovered`): `EnterFullScreen`/`ExitFullScreen` 은
  창 전체를 `SetBoundsInstant` 로 즉시 리사이즈하는 **가장 큰 리플로우**인데 커버 없이 수행돼 터미널이
  클리어→재fit→TUI 비동기 재렌더 동안 비어 보였다. → `FreezeWorkspaceTerminalsAsync(stretchCover:true)`
  → 전환 → **Background 우선순위 대기**(EnterFullScreen 이 Maximized 경유 시 최종 bounds 를 Background
  에서 한 번 더 적용하므로, 그 뒤에 reveal 해야 expectWidth 가 최종값) → `UnfreezeWorkspaceTerminals()`.
  적용: MaxBtn/캡션 더블클릭 토글, 시스템 최대화 요청(`OnStateChangedForFullScreen`). 토글 조건은
  await 뒤 change 시점에 재확인, 연타는 `_fsCoverBusy` 로 무시. **미적용(의도)**: 시작 복원(터미널
  미생성), 전체화면 캡션 드래그 축소(커버 대기가 드래그 반응성을 해침 — 여기 잔여 플래시는 허용).
- **설정/MCP 창, 우측 드로어**: `SuspendTerminalWithSnapshotAsync(blankCurtain:false)`(스냅샷+Collapsed). 리사이즈 없어 원래부터 매끄러움.
- **종료("세션 닫는 중" 오버레이)**: 2단계 배치(`PrepareShutdownSnapshotAsync` → 모든 패널 스냅샷 present 대기(`WaitForFramesAsync`)
  → `CommitShutdownHide` 로 **모든 HWND 를 같은 프레임에 일괄 숨김**) → 렌더 프레임 flush → `ShutdownOverlay` 표시.
  (패널별 순차 캡처→hide 는 HWND 가 서로 다른 프레임에 사라져 팝이 여러 번 어긋났다 — 그래서 준비/커밋 분리.)

## 주의
- 새 오버레이/패널을 터미널 위에 띄울 땐:
  - 터미널을 **리사이즈하지 않는** 경로(위로 덮기/숨기기) → 스냅샷+`Collapsed`(`SuspendTerminalWithSnapshotAsync` 또는 `webCover:false`).
  - 터미널을 **리사이즈하는** 경로 → **`webCover:true`** 경로를 써야 reveal 깜빡임이 없다. (스냅샷+Collapsed 만 쓰면 옛 1~2 프레임 깜빡임이 되살아난다.)
    창/윈도우 단위 리사이즈(전체화면 등 새 창 크기 전환)면 여기에 **`stretchCover:true`** 까지 — 좌상단 고정 커버는 커지는 쪽이 비어 보인다.
- `Hidden` 으로 숨기는 코드를 발견하면 `Collapsed` 로 고칠 것(HwndHost HWND 가 안 숨겨짐).
- **suspend 중 표시 복원 우회 금지**: 에이전트 훅(lastmsg 갱신)이 `NotifySessionStateChanged` →
  `UpdateEmptyState` 를 타고 `TerminalHostContainer.Visibility=Visible` 을 복원하면, 스냅샷+Collapsed 로
  가려둔 라이브 HWND 가 airspace 로 스냅샷 위에 되살아난다("설정창 열어두면 조금 뒤 codex 터미널이 비침" —
  전 에이전트 공통, 활성 세션의 상태 이벤트가 오면 발생). `_overlaySuspended` 플래그
  (`SuspendTerminalWithSnapshotAsync`/`PrepareShutdownSnapshotAsync` 에서 set, `ResumeTerminal` 에서 해제)가
  `UpdateEmptyState` 의 세션/파일/브라우저 세 분기 표시 복원을 차단한다. 표시를 복원하는 새 경로를 추가하면
  이 플래그를 반드시 확인할 것.
- 커버를 올리는(`xferCover`) 경로를 추가하면 **어떤 종결 경로로 끝나든 `_fitSuppressed` 해제·needCreate 처리**가
  보장되는지 확인할 것 — `fadeNow` 가 이를 빠뜨려 패널 fit 이 영구 잠겼던 버그는
  `분할패널-탭격리-파트너-포커스.md` §6.5 참조.
- webCover 경로는 `expectWidth`(최종 폭)를 정확히 넘겨야 중간 전체폭 plateau 를 건너뛴다 — resume 전 `UpdateLayout()` 로 폭을 확정할 것.
- 좌우를 각자 뜨게 두면 시점이 어긋나 보인다 — 동시 표시가 필요하면 synced(`RevealPreparedSynced` + `FadeNow`) 경로를 쓸 것.

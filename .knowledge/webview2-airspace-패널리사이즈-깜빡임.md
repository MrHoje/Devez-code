# WebView2(터미널) airspace — 패널 리사이즈/오버레이 시 깜빡임 원인과 해법

## 한 줄 요약
터미널은 **WebView2 = HwndHost(네이티브 HWND)** 라 WPF 요소로 그 위를 덮을 수 없다(airspace).
그래서 오버레이/패널 전환 시 터미널을 **스냅샷 Image 로 대체하고 WebView 를 `Collapsed` 로 숨겨야** 한다.
"리사이즈가 없는" 경로(설정/MCP/드로어)는 매끄럽지만, **터미널을 리사이즈하는** 경로(분할 토글,
좌/우/사용량 패널 토글)는 reveal 순간 xterm 의 reflow 가 HWND 위로 비쳐 한 프레임 깜빡인다.

## airspace 핵심 (제일 중요)
- WebView2 의 렌더는 별도 HWND 에서 합성된다 → **항상 모든 WPF 콘텐츠 위**에 그려진다.
  WPF `Image`/`Border` 를 ZIndex 로 아무리 올려도 라이브 WebView 를 가리지 못한다.
- 따라서 "터미널 위에 무언가를 띄우는" 유일한 방법은 **별도 최상위 Window**(예: `ProjectTargetPickerWindow`,
  `WS_EX_NOACTIVATE`)뿐이다. WPF 오버레이(`ShutdownOverlay` 등)는 WebView 를 먼저 숨겨야만 보인다.

## 스냅샷 정지 메커니즘 (`WorkspacePaneView`)
- `CaptureSnapshotAsync()` = `CoreWebView2.CapturePreviewAsync(PNG)` → 현재 화면 비트맵.
- `SuspendTerminalOnlyAsync(anchorTopLeft)` : 스냅샷을 `TerminalSnapshot`(Image)에 깔고
  `TerminalHostContainer.Visibility = Collapsed`.
- `ResumeTerminalOnly()` : 컨테이너 다시 `Visible`, 스냅샷 숨김.

### ⚠️ `Collapsed` 여야 한다 (`Hidden` 금지)
HwndHost 는 `Visibility.Hidden` 에서 **레이아웃 슬롯을 남기며 네이티브 HWND 를 계속 보여준다**.
즉 `Hidden` 으로 숨기면 애니메이션 내내 라이브 터미널이 그대로 비쳐 심하게 깜빡인다.
`Collapsed`(슬롯 0)여야 HwndHost 가 HWND 를 실제로 숨긴다. (2026-06-26, Hidden 실험으로 회귀 확인 후 복원.)

### `anchorTopLeft` — 스냅샷 늘어남 방지
스냅샷 Image 가 `Stretch=Fill` + dock 이면 패널이 커질 때 같이 쭉 늘어난다.
`anchorTopLeft=true` 면 캡처 시점 크기로 **좌상단 고정** + 콘텐츠 Grid `ClipToBounds` →
패널이 잘라낼 뿐 늘어나지 않는다(실제 터미널 reflow 와 비슷한 인상).

### 흰색 클리어 방지
리사이즈 중 WebView2 가 흰색으로 클리어했다 다시 그린다 →
`webView.DefaultBackgroundColor = #0C0C0C`(터미널 배경)로 흰 플래시 차단(`TerminalHostView` 초기화).

## 왜 어떤 경로는 안 깜빡이고 어떤 건 깜빡이나
| 경로 | 터미널 리사이즈? | 결과 |
|---|---|---|
| 설정/MCP 창, 우측 드로어 오버레이 | ✗ (오버레이가 위에 슬라이드) | resume 크기 == 캡처 크기 → reflow 없음 → **매끄러움** |
| 분할 펼침/접힘, 좌/우/사용량 패널 토글 | ✓ (중앙 `*` 컬럼 변함) | reveal 시 xterm 이 0→최종폭 re-fit → HWND 위로 비쳐 **한 프레임 깜빡임** |

핵심: **reflow(re-fit) 자체가 HWND 위에서 일어나므로 in-pane 스냅샷으로는 가릴 수 없다.**
애니메이션 "도중"은 WebView 가 `Collapsed`(숨김)라 스냅샷이 덮어 깔끔하다. 문제는 **reveal 직후 1~2 프레임**.

## reveal 깜빡임 해법 시도 기록
in-pane 스냅샷으로는 불가능(airspace).

### ❌ 시도 1 — RenderTargetBitmap 정적 최상위 커버 창 (2026-06-26, 실패·롤백)
reveal 직전 `CenterSplit` 을 `RenderTargetBitmap` 으로 캡처해 picker 패턴의 borderless 창으로
덮고, 그 아래서 WebView 를 되살린 뒤(150ms settle) 닫는 방식. **실패**:
- 깜빡임이 그대로였고(= 캡처/덮기 타이밍이 실제 reflow 순간을 못 가림),
- 커버 창을 150ms 띄웠다 닫는 과정에서 "재개 시 더 이상한 동작"(정적 비트맵이 잠깐 떴다 튀는 인상)이 생김.
→ 전부 롤백하고 단순 `Collapsed + ResumeTerminalOnly`(애니메이션 도중은 깔끔, reveal 1~2프레임만 잔존)로 복귀.

### 향후 제대로 하려면 (미구현)
- **추적 커버 창**: 작업 내내(애니메이션 도중부터 settle 까지) 중앙을 덮는 창이 매 프레임 중앙 rect 를
  추종(`CompositionTarget.Rendering`)하고, 터미널은 라이브로 그 아래서 리사이즈. 정적 캡처가 아니라
  "작업 시작 전"에 띄워 reveal 까지 한 번도 안 걷어야 reflow 순간을 확실히 가린다.
- 또는 JS(xterm)에 "최종 폭 re-fit+render 완료" ack 를 추가해 그 신호 뒤에만 커버를 걷는다.

> 교훈: 정적 캡처를 reveal "직후"에만 잠깐 씌우면 ① 캡처 시점이 한 프레임 늦고 ② 걷는 순간 라이브와
> 어긋나 더 튄다. 커버는 reflow 가 시작되기 "전"부터 끝날 "때까지" 연속으로 덮고 있어야 의미가 있다.

## 적용 현황 (2026-06-26)
- 분할 펼침/접힘: `AnimateSplitOpenAsync/CloseAsync` 에서 양 패널 `SuspendTerminalOnlyAsync(anchorTopLeft)` → 애니메이션 → `ResumeTerminalOnly`.
- 좌/우/사용량 토글: `FreezeWorkspaceTerminalsAsync()`/`UnfreezeWorkspaceTerminals()` 로 동일 처리.
- 설정/MCP 창: `SuspendTerminalWithSnapshotAsync(blankCurtain:false)` (단색 커튼 X, 스냅샷 O).
- 종료("세션 닫는 중" 오버레이): 스냅샷 캡처 → 렌더 프레임 flush → `ShutdownOverlay` 표시 순서.
- 위 모두 애니메이션 "도중"은 매끄럽고, 리사이즈 경로의 reveal 1~2 프레임 깜빡임만 잔존(정적 커버 창 시도는 실패·롤백, 위 "시도 기록" 참고).

## 주의
- 새 오버레이/패널을 터미널 위에 띄울 땐 반드시 먼저 `SuspendTerminal...` 로 WebView 를 `Collapsed`.
- 터미널을 리사이즈하는 새 동작을 추가하면 reveal 깜빡임이 따라온다 — 위 "최상위 커버 창"으로만 완전 제거 가능.
- `Hidden` 으로 숨기는 코드를 발견하면 `Collapsed` 로 고칠 것(HwndHost HWND 가 안 숨겨짐).

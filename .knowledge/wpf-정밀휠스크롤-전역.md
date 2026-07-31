# WPF 정밀 휠 스크롤 — 앱 전역 ScrollViewer 트랙패드 델타 누적

## 한 줄 요약

WPF 기본 ScrollViewer 는 120 미만의 트랙패드 미세 델타(고해상도 휠)도 **한 번의 휠 틱으로 확대**해
스크롤이 뚝뚝 끊기고 과하게 튄다. `Behaviors/PrecisionWheelScroll.cs` 가 앱 전역 클래스 핸들러로
작은 델타를 **120(detent) 단위로 누적**해서만 스크롤하고, 일반 마우스의 120 단위 입력은
WPF 기본 경로를 그대로 통과시킨다. 등록은 `App.xaml.cs` 시작 시 `PrecisionWheelScroll.RegisterGlobally()` 1회.

## 증상 / 원인

- **증상**: 트랙패드(정밀 터치패드)나 고해상도 휠 마우스로 WPF 리스트/설정 화면을 스크롤하면
  작은 관성 이벤트마다 `WheelScrollLines` 줄씩 점프해 스크롤이 끊기고 증폭됨.
- **원인**: WPF `ScrollViewer` 는 `MouseWheel` 의 `e.Delta` 크기와 무관하게 이벤트 1회 = 휠 1틱으로
  처리한다(델타 누적 개념 없음). 트랙패드는 델타 10~40짜리 이벤트를 연사하므로 전부 1틱씩 확대된다.

## 구현 (`Behaviors/PrecisionWheelScroll.cs`)

- **전역 1회 등록**: `EventManager.RegisterClassHandler(typeof(ScrollViewer), Mouse.PreviewMouseWheelEvent,
  …, handledEventsToo: true)` — 앱의 모든 ScrollViewer 에 적용. `Interlocked.Exchange` 가드로 중복 등록 방지.
  `App.xaml.cs` `OnStartup` 에서 `PrecisionWheelScroll.RegisterGlobally()` 호출(grep: `RegisterGlobally`).
- **일반 마우스는 무개입**: `e.Delta % 120 == 0`(`Mouse.MouseWheelDeltaForOneLine` 배수)이면 그대로 반환
  → WPF 기본 경로가 처리해 **사용자의 `WheelScrollLines`(제어판 휠 설정)가 그대로 보존**된다.
  트랙패드 잔여 누적과 섞이지 않도록 이때 해당 뷰어의 누적 상태도 폐기한다.
- **최근접 ScrollViewer 만 처리**: Preview 는 터널링이라 바깥 ScrollViewer 가 먼저 받는다 —
  `FindScrollViewer(e.OriginalSource)` 로 이벤트 원점에서 가장 가까운 ScrollViewer 를 찾아
  `sender` 와 일치할 때만 처리(**중첩 스크롤에서 바깥이 안쪽 것을 가로채는 것 방지**).
  트리 탐색은 Visual/Content/Logical 트리를 모두 커버(`GetParent`).
- **per-ScrollViewer 누적**: `ConditionalWeakTable<ScrollViewer, WheelState>` — 뷰어 GC 를 막지 않는다.
  델타를 누적해 ±120 마다 `ScrollOneDetent`(= `WheelScrollLines` 만큼 `LineUp/LineDown`,
  설정이 -1(페이지)이면 `PageUp/PageDown`) 실행 후 `e.Handled = true`.
- **누적 리셋 규칙**:
  - 마지막 이벤트로부터 **180ms**(`GestureGapMs`) 지나면 리셋(제스처 경계).
  - **방향 전환** 시 리셋(반대 방향 잔여값이 상쇄되는 것 방지).
  - **스크롤 경계**(맨 위에서 위로 / 맨 아래에서 아래로, 또는 스크롤 불가)면 누적을 폐기하고 무개입.
  - `e.Handled` / `Delta==0` / Ctrl·Shift 수정키(확대·가로 스크롤 용도)면 무개입 + 상태 폐기.
- **opt-out 첨부 속성**: `PrecisionWheelScroll.IsEnabled`(기본 true, `Inherits`) — 특정 서브트리에서
  이 동작을 끄려면 XAML 에서 `behaviors:PrecisionWheelScroll.IsEnabled="False"` 를 조상에 설정.

## 터미널(xterm) 쪽과의 관계 — 휠 정책 2레이어

이 파일은 **WPF 레이어**(사이드바·설정·리스트 등 일반 ScrollViewer)만 담당한다.
터미널(WebView2 + xterm) 내부의 같은 문제는 **웹 레이어** `terminal.html` 의 `handleTerminalWheel` 이
별도로 처리한다(120단위 누적 정규화, 방향 전환·180ms 리셋 — `.knowledge/터미널커스텀동작.md` §7).
휠 정밀 델타 정책이 **WPF/xterm 두 레이어에 나뉘어 있으므로**, 휠 동작을 바꿀 땐 어느 레이어의
문제인지부터 판별할 것(터미널 안 = terminal.html, 터미널 밖 = PrecisionWheelScroll).

## 주의 (수정 시)

- `handledEventsToo: true` 라 다른 코드가 Handled 한 이벤트도 들어온다 — 핸들러 첫머리의
  `e.Handled` 무개입 분기를 제거하지 말 것.
- `% 120 == 0` 통과 분기를 없애면 일반 마우스의 `WheelScrollLines` 사용자 설정이 무시된다.
- 경계 폐기 분기를 없애면 경계에서 쌓인 누적이 반대 방향 첫 휠을 잡아먹는다.
- `TabBar_PreviewMouseWheel`(탭바 가로 스크롤)처럼 자체 `e.Handled = true` 를 하는 지역 핸들러는
  Preview 터널 순서상 이 전역 핸들러보다 **바깥 요소면 먼저** 실행된다 — 지역 정책이 우선.

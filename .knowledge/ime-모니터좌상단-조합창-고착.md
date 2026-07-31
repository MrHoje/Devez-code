# IME 조합창 "모니터 좌상단" 고착 — 수사 진행 중 (계측 단계)

## 증상

- 한글 조합 미리보기(조합창)가 터미널 입력란이 아니라 **모니터 좌상단(화면 원점 0,0)** 에 뜬다.
- 글자 자체는 터미널에 정상 입력된다(조합 이벤트는 페이지로 유입) — "커서 좌표가 안 맞아
  텍스트 입력이 깔끔하게 안 보이는" 형태.
- 발생 시점/조건 불명(간헐). 사용자도 트리거를 특정 못 함.

## 확정 사실 (2026-08-01)

1. **터미널 표면 재클릭으로는 복구 안 됨** — 기존 조건부 복구 `_recoverStaleImeOnPointerDown`
   이 diag.log 전체에서 **한 번도 발동한 적 없음**(`pointer-recover`/`pointer-stale` 0건).
   즉 기존 감지 조건(터미널 안 `.composition-view` 가 화면 밖/(0,0))에 **안 걸리는 다른 부류**다.
2. **터미널 밖(WPF 영역) 클릭 → 세션 재포커스만 해결** — 웹 레이어 안의 `ta.blur()→focus()`
   (resetImeNow)로는 안 풀리고, **실제 Win32 HWND 포커스 이동**이 있어야 풀린다.

## 해석 (가설)

- 모니터 (0,0)은 **Windows 가 기본 IME 조합창을 그리는 폴백 위치**다. 앱(Chromium)이 캐럿
  위치를 IME 에 보고하지 못하는 상태 = **Win32 IME 컨텍스트 층의 stale**.
- DOM 층은 정상이라(조합 이벤트 유입, 조합뷰 좌표도 정상일 가능성) 웹 안 감지가 전부 헛방이었고,
  실제 HWND blur→focus 만이 IME 컨텍스트를 재부착시켜 해결한 것으로 보인다.
- 유사 선례: 터미널커스텀동작.md §5-2 — "새 xterm 첫 포커스 때 WPF 입력 컨트롤의 Windows IME
  컨텍스트가 WebView2 에 이어지는" 기벽(한글 2회 입력). 같은 층(HWND-IME 부착)의 다른 증상일 수 있다.

## 계측 (현재 단계 — 시그니처 확보용)

양층을 같은 diag.log 라인에 남긴다:

- **웹**: `sendImeProbe()` (terminal.html, grep: `sendImeProbe`) — `beginComposition`(조합 시작)마다
  발화, **2초 스로틀**. DOM 상태(`focus`/`act`/`ta` 상대좌표/`view` 상태·좌표)를 실어 보냄.
- **C#**: `case "imeProbe"` → `DescribeWin32ImeState()` (TerminalHostView.cs, grep: `DescribeWin32ImeState`)
  — `GetGUIThreadInfo`/`ImmGetDefaultIMEWnd` 등으로 Win32 층 스냅샷을 덧붙임.

### diag.log 라인 포맷

```
[web] [ime <agent> <roomId>] probe focus=1 act=1 ta=12,340 view=on:12,340 win32[focus=<클래스명> active=<클래스명> caret=<클래스명>@x,y imc=0 compForm=<style>@x,y imeWnd=vis|hid@x,y]
```

### 판독법 (증상 재현 시 이 라인을 healthy 라인과 비교)

| 필드 | healthy 기대값 | 의심 신호 |
|---|---|---|
| `focus=`(win32) | `Chrome_RenderWidgetHostHWND` 또는 `Chrome_WidgetWin_1` (WebView2 내부) | `HwndWrapper[DevezCode...]`(WPF 본체) 등 Chromium 밖 = 포커스/IME 부착이 어긋난 확진 |
| `imeWnd=` | `hid`(숨김) — Chromium 은 조합을 인라인으로 그림 | **`vis@0,0`(또는 화면 원점 근처 visible) = 모니터 좌상단 조합창 확진** |
| `imc=` | 0 이 정상일 수 있음(타 프로세스 HWND 는 컨텍스트 조회 불가) | 값 자체보다 healthy↔재현 간 **변화**가 단서 |
| `focus=`(dom) / `act=` | 1 / 1 | 0 이면 페이지 포커스 자체가 어긋난 상태 |
| `ta=` | 입력란 캐럿 근처 좌표 | 0,0 또는 화면 밖 = xterm textarea 위치 stale |

## 다음 단계 (시그니처 확보 후)

1. 재현 시점의 probe 라인으로 stale 시그니처 확정.
2. 그 조건을 감지 트리거로 → 웹이 C#에 복구 요청 → **C#이 실제 HWND 포커스 바운스**
   (더미 WPF 요소로 `Keyboard.Focus` → `webView.Focus()` 복귀)로 자동 복구. 외부 클릭 수동 우회를 대체.

## 금지 (설계 확정 사항)

- **매 키 입력마다 무조건 재조정(blur→focus) 금지** — 한글은 키 하나하나가 조합 중간이라
  음절이 그 자리에서 취소되고, WebView2 가 다음 `compositionstart` 를 떨어뜨려 커서숨김/pin 이
  통째로 안 걸리는 2차 고장 유발. 복구는 반드시 **고착 감지 시에만** 조건부 발동.
- 웹 내부 `ta.blur()→focus()` 로 이 증상을 풀려는 시도 금지 — 이미 무효로 확인됨(실 HWND 이동 필요).

> 관련: `.knowledge/터미널커스텀동작.md` §5 (IME 아키텍처 전체), §5-2 세션 재클릭 복구(터미널 안 (0,0) 부류 — 이 문서와 다른 부류).

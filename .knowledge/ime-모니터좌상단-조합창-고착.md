# 터미널 "커서 관련" 버그 — IME 조합창 모니터 좌상단 고착 (예측 수정 적용, 관찰 중)

> 사용자가 **"커서 관련 수정"·"커서 좌표가 안 맞음"·"텍스트 입력이 깔끔하게 안 보임"** 이라고
> 말하면 이 문서다. 증상의 실체는 커서가 아니라 **Windows IME 조합창이 모니터 원점(0,0)에
> 뜨는 OS 레벨 고착**이며, 수정 이력·계측 판독법·다음 단계가 모두 여기 있다.

## 증상

- 한글 조합 미리보기(조합창)가 터미널 입력란이 아니라 **모니터 좌상단(화면 원점 0,0)** 에 뜬다.
- 글자 자체는 터미널에 정상 입력된다(조합 이벤트는 페이지로 유입) — "커서 좌표가 안 맞아
  텍스트 입력이 깔끔하게 안 보이는" 형태.
- 발생 시점/조건 불명(간헐). 사용자도 트리거를 특정 못 함.

## 확정 사실 (2026-08-01)

1. **터미널 표면 재클릭으로는 복구 안 됨** — 기존 조건부 복구 `_recoverStaleImeOnPointerDown`
   이 diag.log 전체에서 **한 번도 발동한 적 없음**(`pointer-recover`/`pointer-stale` 0건).
   즉 기존 감지 조건(터미널 안 `.composition-view` 가 화면 밖/(0,0))에 **안 걸리는 다른 부류**다.
2. **터미널 밖(WPF 영역) 클릭 → 세션 재포커스만 해결** — 실제 Win32 HWND 포커스 이동이 있어야 풀린다.
3. **페이지 내 blur→focus 처방으로는 불충분 확정** — `case 'focus'`(terminal.html)가 이미 모든
   포커스 메시지에서 helper-textarea blur 경계를 만들고, 새 세션은 `initial-focus` 리셋
   (`resetImeNow`, diag 에 기록 확인)까지 도는데도 그 경로에서 증상이 났다. 이 증상에 웹 안
   리셋을 더 쌓는 시도는 무의미 — **수정은 Win32 층에서** 해야 한다.

## 적용된 예측 수정 (2026-08-01, 시그니처 확보 전 선제 적용)

`FocusTerminal()` (TerminalHostView.cs) — **2단계 Win32 포커스 바운스**:

- **의심 전환 판정**: 호출 시점 Win32 포커스(`GetGUIThreadInfo`)가 `Chrome_*` 밖(=WPF 쪽에서
  들어오는 프로그램적 전달)일 때만 발동. 이미 Chromium 안이면 기존 동작 그대로 →
  조합 중인 터미널을 건드릴 가능성 원천 차단.
- **바운스**: ① WPF 본체 HWND 에 `SetFocus` 로 진행 중 전이를 확정 → ② 60ms 뒤 `_webView.Focus()`.
  수동 우회가 항상 성공하는 이유 = 두 전이가 사람 타이밍으로 분리되기 때문(코드 속도의 동시
  전이가 IME 재부착 레이스를 만든다)이라는 가설의 재현.
- **안전 가드**: 60ms 사이 방이 바뀌면 폐기 / `Keyboard.FocusedElement` 가 WPF TextBox 면
  (세션 이름 변경 등) 포커스를 빼앗지 않고 폐기.
- diag: `[ime focus-path] from=<호출자> suspect=0|1` → 250ms 뒤 `settle ... win32[...]`.

**효과 판정법**(간헐 버그라 며칠 관찰 필요): diag 에 `suspect=1` 경로가 여러 번 지나갔는데
이후 probe 라인에 `imeWnd=vis@0,0` 이 없고 사용자 체감 재발이 없으면 호전. **바운스 적용 후에도
재발하면** 아래 에스컬레이션으로.

## 에스컬레이션 예비안 (재발 시)

1. `AttachThreadInput` + Chromium 자식 HWND(`Chrome_WidgetWin_1`/`Chrome_RenderWidgetHostHWND`)에
   직접 `SetFocus` — 실클릭의 포커스 경로를 더 충실히 재현. 입력 큐 attach 는 무겁고 상대
   스레드가 바쁘면 지연 위험 → 의심 전환 한정 + try/finally detach 필수.
2. 바운스 지연(60ms) 증가 또는 `MoveFocus` 재호출 — 레이스 가설이 맞는데 60ms 로 부족한 경우.

## 트리거 (사용자 제보 + 구조적 예측)

**제보된 빈발 경로 (2026-08-01)**: ① 단축키(Ctrl+Shift+T/D)로 새 세션 생성 직후 ② 완료기록 카드 클릭 직후.

**공통 구조**: 둘 다 Win32 포커스가 WPF 쪽에 있다가 `FocusTerminal()`(TerminalHostView.cs)의
`_webView.Focus()` 로 **프로그램적으로** WebView2 에 넘어가는 경로다. 증상이 없는 평소 경우는
사용자가 터미널 표면을 **직접 클릭**해 Win32 포커스가 Chromium HWND 에 네이티브로 안착한다.
→ 가설: 프로그램적 전달에서 Windows IME 컨텍스트가 Chromium HWND 에 재부착되지 못하고
WPF 쪽에 남는다(새 세션 모달은 이름 입력 **WPF TextBox 에 한글 IME 가 활성**인 채 닫히므로 최악 조건).

**같은 구조라 함께 의심되는 경로** (전부 `FocusTerminal()` 경유 — 재현 시 확인할 것):

- 탭바(WPF)를 마우스로 클릭해 세션 전환
- 사이드바/프로젝트 트리에서 세션 선택
- 설정·MCP·확인 등 WPF 다이얼로그를 닫은 직후
- 세션 이름 변경 등 **WPF 텍스트 입력 컨트롤에 한글 입력 후** 터미널 복귀 (고위험)
- QuickOpen·파일 에디터 탭에서 터미널로 복귀
- MDI 오버레이 열림(ShellTerminal.FocusTerminal)

반면 웹 레이어 안에서만 도는 전환(Ctrl+Tab 등, 포커스가 WebView2 HWND 를 떠나지 않음)은
상대적으로 안전할 것으로 예측.

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
- **C# 포커스 경로**: `FocusTerminal()` 호출마다 `[ime focus-path] from=<호출자>` +
  250ms 뒤 `[ime focus-path] settle ... win32[...]` — 프로그램적 포커스 전달 후 Win32 포커스가
  실제 어디에 안착했는지(Chromium HWND vs WPF 본체)를 **조합 없이도** 관측. 제보된 트리거
  (새 세션·완료기록 카드)와 probe 라인의 시간 상관을 이 로그로 잇는다.

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

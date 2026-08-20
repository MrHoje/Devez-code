# 터미널 "커서 관련" 버그 — IME 조합창 모니터 좌상단 고착 (복귀 경계 보강, 관찰 중)

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

## 코드로 확정된 누락 (2026-08-19)

실제 고착 순간의 OS 내부 상태는 재현 시그니처가 없어 아직 확정할 수 없다. 다만 기존 복귀 코드에는
증상을 설명하고 재발을 허용하는 다음 결함이 확정됐다.

1. `Chrome_*` 클래스 판정은 **현재 포커스가 어떤 WebView/터미널인지, IME 컨텍스트가 정상인지**
   구분하지 못한다. 특히 앱은 모든 `ButtonBase`를 포커스 불가로 만들어 WPF 버튼을 눌러도 직전
   Chromium HWND가 포커스로 남을 수 있고, WPF `Popup`도 별도 HWND라 닫힌 뒤 Chromium으로 자동
   돌아올 수 있다. 기존 `suspect=0`은 이 경계를 정상 복귀로 오인했다.
2. 호출마다 지역 `DispatcherTimer`를 새로 만들고 취소하지 않았다. 창 활성화·팝업 닫힘·세션 전환이
   겹치면 여러 60ms 예약이 순서 없이 뒤늦게 실행되어, 이미 터미널을 클릭했거나 다른 입력칸으로 간
   뒤 다시 포커스를 빼앗을 수 있었다.
3. 셸의 복귀 스케줄러는 "창/팝업/다른 WebView에서 돌아옴"이라는 원인을 버리고 일반 포커스 호출로
   축약했다. 따라서 호출 시점에 우연히 `Chrome_*`이면 필요한 HWND 바운스를 생략했다.
4. 완료·응답대기 토스트는 최초 `Show()` 때 활성화되는 기본값이었다. 로드 후 `SWP_NOACTIVATE`로
   이동해도 이미 발생한 최초 포커스·IME 이탈은 막지 못했다.

이 네 항목은 로그 추정이 아니라 현재·이전 코드 흐름으로 확정된다. 반면 **Windows IME가 왜 해당
경계에서 기본 조합창으로 폴백하는지**까지는 WebView2/OS 내부 상태이므로 재현 계측 없이는 확정하지
않는다.

## 적용된 수정 (2026-08-19)

`FocusTerminal()` (TerminalHostView.cs) — **2단계 HWND 포커스 바운스**:

- **복귀 경계 판정**: `Chrome_*` 밖뿐 아니라 창 비활성화 후 복귀, WPF 팝업·콤보 닫힘, 설정
  오버레이 재개, 파일/브라우저/Claude GUI WebView에서 터미널로 복귀, 다른 `TerminalHostView`에서
  전환, 완료·응답대기 카드/알림 클릭을 명시적인 IME 재부착 경계로 전달한다. WPF 탭·사이드바에서
  세션을 바꾸는 전환도 경계로 본다. 같은 WebView 안에서만 처리되는 평상 입력·페이지 내부 전환에는
  불필요한 강제 바운스를 넣지 않는다.
- **sticky + 앱 전역 최신 요청**: 경계 플래그는 실제 터미널 포커스 적용 전까지 보존하며 뒤따른 일반
  호출이 지우지 못한다. 호스트별 단일 타이머/generation과 앱 전역 epoch를 함께 써서 좌·우 패널·하단 셸의
  이전 60ms 예약을 취소·무효화한다. pageReady 전 요청도 자기 epoch를 보존해 로딩 사이 더 최신 클릭이 있으면 재생하지 않는다.
- **바운스**: ① 기존 focus scope의 논리 포커스를 지우고 WPF 입력 공급자의 `Keyboard.Focus(owner)`로
  본체 포커스를 확정 → ② 60ms 뒤 `_webView.Focus()`. raw `SetFocus(owner)`는 WPF가 기억한 이전
  WebView2 자식 HWND를 즉시 자동 복원할 수 있어 쓰지 않는다. 수동 우회가 항상 성공하는 이유 = 두
  전이가 사람 타이밍으로 분리되기 때문이라는 가설의 재현이다.
- **직접 클릭 우선**: 터미널 웹 표면의 `mousedown`이 오면 호스트 타이머와 셸 복귀 예약을 모두
  취소한다. 사용자가 이미 만든 정상 네이티브 포커스 경계를 60ms 예약이 다시 끊지 않는다.
- **안전 가드**: 창 비활성, 숨겨진 WebView, 방/페이지 변경, 폐기된 generation/epoch, owner/현재
  터미널이 아닌 새 WPF 입력 표면이 있으면 적용하지 않고 경계 상태만 다음 복귀까지 보존한다.
  터미널 주차·숨김·오버레이·메뉴 열림 시점에 예약을 동기적으로 취소한다.
- **전환 직렬화**: 패널·하단 셸·분할·전체화면·우측 드로어·전체 오버레이·종료 캡처는 하나의 gate로
  직렬화한다. 캡처 await가 겹쳐 다른 전환의 터미널 정지/커버를 먼저 해제하지 못한다.
- **모달 조합 종료**: 메뉴·콤보·소유 모달을 열기 전 빈 `compositionend`와 textarea blur를 보내
  앱 상태뿐 아니라 xterm 내부 composition helper 상태도 종료한다.
- **복귀 대상 보존**: 하단 셸이 마지막 입력면이면 Alt+Tab 복귀도 셸로 돌려보낸다. 패널의
  터미널·파일·브라우저·Claude GUI를 누르면 즉시 패널이 다시 복귀 대상이 된다.
- **피동 알림**: 완료·응답대기 토스트는 `ShowActivated=False`로 생성해 표시 자체가 현재 터미널의
  포커스와 IME 조합을 끊지 않는다.
- **공통 적용 범위**: 같은 `TerminalHostView`를 쓰는 모든 xterm 기반 에이전트와 하단 셸에 적용된다.
  별도 Claude GUI 입력창은 대상이 아니며, Claude GUI→터미널 전환 경계만 처리한다.
- diag: `[ime focus-path] ... force=0|1 hostChanged=0|1 chromium=0|1 bounce=0|1 request=N epoch=E`
  → 250ms 뒤 같은 `request=N epoch=E`의 `settle ... win32[...]`. 그 사이 더 최신 요청이 있으면 오래된 settle도 기록하지 않는다.

정적 회귀 검사는 `Tests/TerminalImeFocus.Tests.ps1`에 둔다. 경계 sticky, 호스트 간 순서, 콜드 로딩 재생,
입력/메뉴/숨김 가드, 주차·오버레이 취소, 직접 클릭 우선, 하단 셸·브라우저 대상, 피동 알림을 검사한다. 실제 WebView2/Windows
IME 재부착 결과는 자동화로 완전히 대체할 수 없으므로 아래 수동 관찰을 병행한다.

**효과 판정법**(간헐 버그라 며칠 관찰 필요): 경계 복귀 로그에서 `bounce=1` 뒤 동일 request의
settle이 한 번만 나오고, 이후 probe에 `imeWnd=vis@0,0`이 없으며 체감 재발이 없어야 한다.
직접 터미널 클릭 뒤에는 해당 request의 늦은 settle이 나오지 않아야 한다. 재발하면 아래
에스컬레이션으로.

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

## 계측 (시그니처·복귀 직렬화 확인용)

양층을 같은 diag.log 라인에 남긴다:

- **웹**: `sendImeProbe()` (terminal.html, grep: `sendImeProbe`) — `beginComposition`(조합 시작)마다
  발화, **2초 스로틀**. DOM 상태(`focus`/`act`/`ta` 상대좌표/`view` 상태·좌표)를 실어 보냄.
- **C#**: `case "imeProbe"` → `DescribeWin32ImeState()` (TerminalHostView.cs, grep: `DescribeWin32ImeState`)
  — `GetGUIThreadInfo`/`ImmGetDefaultIMEWnd` 등으로 Win32 층 스냅샷을 덧붙임.
- **C# 포커스 경로**: `FocusTerminal()` 호출마다 `[ime focus-path] from=<호출자> ... request=N` +
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

## 다음 단계

1. 새 세션 이름창(한글 입력 후 취소/확인), ContextMenu·ComboBox, 다른 앱 Alt+Tab, 파일·브라우저·
   Claude GUI 왕복, 완료/응답대기 카드, 좌우 터미널 전환을 각각 반복한다.
2. 재현 시점의 probe와 `request=N` 라인으로 OS stale 시그니처 및 중복 settle 여부를 확정한다.
3. 경계 바운스 뒤에도 재발할 때만 Chromium 자식 HWND 직접 포커스 또는 지연 조정을 검토한다.

## 금지 (설계 확정 사항)

- **매 키 입력마다 무조건 재조정(blur→focus) 금지** — 한글은 키 하나하나가 조합 중간이라
  음절이 그 자리에서 취소되고, WebView2 가 다음 `compositionstart` 를 떨어뜨려 커서숨김/pin 이
  통째로 안 걸리는 2차 고장 유발. 복구는 반드시 **고착 감지 시에만** 조건부 발동.
- 웹 내부 `ta.blur()→focus()` 로 이 증상을 풀려는 시도 금지 — 이미 무효로 확인됨(실 HWND 이동 필요).

> 관련: `.knowledge/터미널커스텀동작.md` §5 (IME 아키텍처 전체), §5-2 세션 재클릭 복구(터미널 안 (0,0) 부류 — 이 문서와 다른 부류).

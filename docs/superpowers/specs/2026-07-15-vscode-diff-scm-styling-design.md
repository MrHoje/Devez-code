# VS Code 스타일 diff 스크롤바/오버뷰 + SCM 패널 설계

- 날짜: 2026-07-15
- 상태: 승인됨 (구현 계획 대기)
- 대상: `Resources/Monaco/web/bridge.js`, `Views/MonacoThemePayload.cs`, `Views/GitScmView.xaml(.cs)`

## 배경 / 목적

Monaco diff 탭이 정상 렌더되기 시작했다. 이제 **VS Code 룩앤필**로 다듬는다:
1. diff 패널 안(Monaco) **스크롤바 스타일**을 VS Code식(얇고 은은)으로.
2. **변경 위치를 좌/우 각 오버뷰 룰러에 빨강(삭제)·초록(추가)** 로 표시.
3. diff 패널 전반을 VS Code diff처럼.
4. **커밋 메시지 입력 + pull/push 카운트**를 VS Code SCM 스타일로.

모든 색은 테마 연동 유지(`App.ThemeChanged` → WPF `DynamicResource` + Monaco `MonacoThemePayload`).

## 목표 (Goals)

- Monaco diff: 오버뷰 룰러 빨강/초록, VS Code식 스크롤바, 읽기전용 diff 톤.
- GitScmView: VS Code SCM식 커밋박스(Ctrl+Enter 커밋, ✓ 커밋 버튼) + 동기화 헤더(브랜치 + ↓N ↑N + sync/pull/push/fetch 아이콘) + 섹션 개수 뱃지.
- 테마 전환 시 diff·패널 색 실시간 반영.

## 비목표 (Non-goals, YAGNI)

- 앱 전역 WPF 스크롤바 재스타일(이번은 Monaco diff 스크롤바만).
- 헝크 단위 revert 아이콘, inline diff 토글, 미니맵 커스텀 렌더.
- 하단 상태바 신설(카운트는 SCM 패널에 둔다).
- 기존 패널 헤더 브랜치 버블(`WorkspacePaneView`) 제거/이동 — **건드리지 않는다**(리스크 최소화). SCM 패널이 자체 동기화 행을 갖는다(경미한 중복 허용).

## 컴포넌트

### A. Monaco diff 비주얼 — `bridge.js` + `MonacoThemePayload.cs`

**A-1. `bridge.js` — createDiffEditor 옵션 추가**
```js
monaco.editor.createDiffEditor(el, {
  readOnly: true, automaticLayout: true, renderSideBySide: true,
  minimap: { enabled: true }, scrollBeyondLastLine: false,
  renderOverviewRuler: true,            // 좌/우 오버뷰 룰러(변경 표시)
  renderMarginRevertIcon: false,        // 읽기전용 — revert 아이콘 숨김
  scrollbar: { verticalScrollbarSize: 14, horizontalScrollbarSize: 14, useShadows: false },
});
```

**A-2. `MonacoThemePayload.Current()` colors 딕셔너리에 추가 키**
- 스크롤바(VS Code식, 반투명 회색 — TextMuted 색에서 알파 파생):
  - `scrollbarSlider.background` = `<TextMuted>` + `59`(≈35%)
  - `scrollbarSlider.hoverBackground` = `<TextMuted>` + `80`(≈50%)
  - `scrollbarSlider.activeBackground` = `<TextMuted>` + `A6`(≈65%)
- 오버뷰 룰러(변경 표시색 — 기존 diff 상태색 그대로):
  - `diffEditorOverviewRuler.insertedForeground` = `#3FB950` (추가=초록, 우측)
  - `diffEditorOverviewRuler.removedForeground` = `#F85149` (삭제=빨강, 좌측)
  - `editorOverviewRuler.border` = `#00000000` (경계선 제거, VS Code 톤)
- (선택) `minimap.background` = `<Bg>` 로 패널과 동화.

> 헬퍼: `MonacoThemePayload` 에 `HexA(key, alpha)` 를 추가해 `#RRGGBB` + 2자리 알파를 만든다. 슬라이더 색은 `TextMutedBrush` 에서 파생(테마 종속 유지).

### B. GitScmView VS Code SCM 스타일 — `GitScmView.xaml(.cs)`

**B-1. 동기화 헤더(커밋박스 위)**: 한 줄에
`⎇ <브랜치>  ↓<behind> ↑<ahead>  [pull] [push] [fetch]`
- 브랜치·카운트는 `GitService.BranchStateAsync` 결과. ahead/behind 0이면 해당 카운트 숨김.
- pull/push/fetch는 `IconButton` 스타일 아이콘 버튼으로 이 행에 재배치(기존 라벨 버튼 대체). 별도 sync(pull+push 합침) 버튼은 만들지 않는다.
- 색 전부 `DynamicResource`.

**B-2. 커밋박스**: 기존 카드 유지하되
- 플레이스홀더 "메시지 (Ctrl+Enter로 커밋)".
- **Ctrl+Enter** → 커밋 실행(`MsgBox.PreviewKeyDown`).
- ✓ 아이콘 + "커밋" 라벨 버튼(`PrimaryButton`), staged>0 && 메시지 有일 때 활성.

**B-3. 섹션 개수 뱃지**: `Staged Changes`/`Changes` 헤더 우측에 개수(예: `Changes 3`). 0이면 섹션 숨김(기존 로직 유지).

### C. 테마 (요건)
- WPF: 모든 색 `DynamicResource`, 폰트 `PretendardFont`/`Fs*`, 아이콘 `Icons.xaml`.
- Monaco: `MonacoThemePayload` 색 → `App.ThemeChanged` 시 `MonacoHost.ApplyTheme()` 재-push(이미 배선). 오버뷰 룰러 빨강/초록은 diff 상태색이라 `ui-lint` 예외 목록에 이미 포함.

## 검증

- 빌드는 사용자. `scripts/ui-lint.ps1 Views/GitScmView.xaml` → 위반 0.
- 앱에서: diff 좌/우 오버뷰 룰러에 빨강·초록 마커, VS Code식 스크롤바, 커밋박스 Ctrl+Enter 커밋, 동기화 헤더 카운트, 테마 전환 시 diff·패널 색 동시 반영.

## 참고
- Monaco 색 ID는 `diffEditorOverviewRuler.*`, `scrollbarSlider.*`, `editorOverviewRuler.border`.
- SCM 아이콘은 기존 `Resources/Icons.xaml` 우선 사용.

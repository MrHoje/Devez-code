# VS Code형 Git SCM + Monaco diff 설계

- 날짜: 2026-07-14
- 상태: 승인됨 (구현 계획 대기)
- 대상: `Services/GitService`(재추가·확장), `Views/FileExplorerView`(DIFF 탭 → SCM 패널), 새 `Views/MonacoDiffHostView` + 새 탭 타입, `MainWindow`/`WorkspacePaneView`(탭·브랜치 버블 배선), `Resources/Monaco/web`(오프라인 번들), `scripts/ui-lint.ps1`.

## 배경 / 목적

VS Code의 Source Control 경험을 DevezCode에 이식한다:
- 우측 패널에 **git 변경 목록 + 커밋박스 + pull/push/fetch**
- 목록의 파일을 클릭하면 **중앙 세션 영역에 Monaco diff 탭**이 열려 side-by-side 비교
- 스테이징(staged/unstaged), 커밋, pull, push, fetch, 변경 취소(discard)를 모두 GUI에서

이전 시도(네이티브 side-by-side + 단순 전체 커밋)는 되돌렸고, 이번엔 **Monaco DiffEditor**로 VS Code 급 뷰어를 목표로 한다.

## 목표 (Goals)

- 우측 FileExplorer의 'DIFF' 탭을 **Git/SCM 패널**로 재구성: `Staged Changes` / `Changes` 두 섹션, 파일별 stage/unstage/discard, 커밋박스, pull/push/fetch.
- 파일 클릭 시 중앙 Pane에 **Monaco diff 탭**(새 탭 타입) — 오프라인 Monaco `DiffEditor`.
- 파일별 **staged/unstaged** 스테이징.
- 액션: **commit / pull / push / fetch / discard**.
- **테마 완전 대응**: 네이티브 패널과 Monaco diff 모두 앱 테마 전환에 실시간 반영.
- **UI 점검 로직**: 테마·공통컨트롤 사용을 검증하는 정적 스캔 스크립트 + 체크리스트.

## 비목표 (Non-goals, YAGNI)

- per-turn(세션 로그 재구성) diff.
- 헝크/라인 단위 스테이징(Stage Selected Ranges).
- amend, merge 충돌 해결 UI, inline diff 모드 토글, 미니맵 커스텀.
- 브랜치 생성/전환/stash.

## 레이아웃

```
[좌 Sidebar]      [중앙 Pane(세션/파일/브라우저/‹diff›)]   [우 FileExplorer]
 프로젝트/세션      … 터미널 …                              [파일][브라우저][Git][큐]
                   ┌ diff 탭 (Monaco) ─────────┐            ── Git 패널 ──
                   │ 수정 전 │ 수정 후          │            ▸ Staged Changes
                   │  (side-by-side)            │              M foo.cs   [−]
                   └────────────────────────────┘            ▸ Changes
                                                               M bar.cs [+][↩]
                                                             [커밋 메시지…]
                                                             [커밋] [pull][push↑N][fetch]
```
- diff는 **중앙 세션 영역의 새 탭**으로 열린다(파일/브라우저 탭과 동종).
- 목록·커밋박스는 **우측 FileExplorer의 기존 'DIFF' 탭 자리**를 재구성해 넣는다.

## 컴포넌트

### A. GitService (재추가 + 확장)
`Services/GitService.cs` — 기존 `RunAsync(repoDir, args)` / `GitResult` / `IsRepoAsync` 위에 추가:

- `Task<GitStatus> StatusAsync(repo)` — `git status --porcelain=v1 -u`. XY 코드로 staged(X)·unstaged(Y) 분류. 한 파일이 양쪽에 동시 등장 가능(부분 스테이지). 반환: staged 목록 + unstaged 목록(각 `GitChange`).
- `Task<string> ShowFileAsync(repo, rev, path)` — `git show <rev>:<path>`. rev 예: `HEAD`, `:` (인덱스). 실패/부재 시 빈 문자열.
- `Task<GitResult> StageAsync(repo, path)` — `git add -- <path>`.
- `Task<GitResult> UnstageAsync(repo, path)` — `git restore --staged -- <path>` (신규 파일이면 `git reset -- <path>` 폴백).
- `Task<GitResult> DiscardAsync(repo, path, bool untracked)` — 추적 파일 `git checkout -- <path>`; untracked 파일은 실제 파일 삭제.
- `Task<GitResult> CommitAsync(repo, message)` — `git commit -m <message>` (스테이지된 것만; add 안 함).
- `Task<GitResult> PullAsync(repo)` / `PushAsync(repo)` / `FetchAsync(repo)`.
- `Task<bool> HasIdentityAsync(repo)` — user.name/email 확인.
- `Task<BranchState> BranchStateAsync(repo)` — 현재 브랜치, upstream 유무, ahead/behind.

모델(`Models/GitModels.cs`): `GitChange`(Status, Path, IsUntracked, IsStaged), `GitStatus`(staged/unstaged 목록), `BranchState`(branch, hasUpstream, ahead, behind).

### B. SCM 패널 (우측 FileExplorer 'DIFF' 탭 재구성)
새 `Views/GitScmView`(UserControl)를 신설하고, FileExplorer의 DIFF 뷰 슬롯(`x:Name="DiffView"` 자리)에 이것을 배치한다(기존 `GitDiffView` 참조 대체). 책임 경계를 명확히 분리:

- **두 섹션**: `Staged Changes`(접기 가능 헤더 + 파일 목록), `Changes`(동일). 비면 섹션 숨김.
- **파일 행**: 상태 글자(M/A/D/R/?) 색 + 경로(말줄임). 호버 시 액션 아이콘(`IconButton` 스타일):
  - Changes 행: `+`(stage), `↩`(discard — 확인 다이얼로그)
  - Staged 행: `−`(unstage)
  - 행 클릭 = diff 탭 열기.
- **커밋박스**: 멀티라인 TextBox(TaskQueueView 입력 카드 패턴 재사용) + `커밋` 버튼(`PrimaryButton`, staged>0 && 메시지 있음일 때만).
- **툴바**: `pull` / `push ↑N` / `fetch` 버튼(`SecondaryButton`). ahead/behind는 `BranchStateAsync` 기반.
- 모든 색 `DynamicResource`, 폰트 `PretendardFont`/`Fs*`.

### C. Monaco diff 탭 (중앙)
- **기존 `FileTabItem` 재사용**(전용 탭 타입 신설 안 함 — 통합 접점 최소화·리스크 감소). Monaco diff를 `IFileTabEditor` 구현체 `Views/MonacoDiffHostView`로 만들어 `FileTabItem.Editor`에 꽂는다. 본보기는 WebView2 기반 `IFileTabEditor`인 `Views/MarkdownFileEditorView`. 파일 탭의 열기/닫기/활성화/사이드바 배선이 그대로 재사용된다.
- `FileTabItem`에 `bool IsDiff` 추가: 제목에 "(변경)" 표시 + 영속화 제외(전환형).
- **오프라인 호스팅**: `Resources/Monaco/web/`에 Monaco 배포본 + `diff.html`/`bridge.js` 번들. 공유 `CoreWebView2Environment`(마크다운 에디터와 동일) + `SetVirtualHostNameToFolderMapping` → `https://<host>/diff.html` navigate.
- **브리지(C#→JS)**: NavigationCompleted 후 `ExecuteScriptAsync`로 `{ path, language, originalText, modifiedText, readOnly:true, themeName }` 전달 → JS가 `monaco.editor.createDiffEditor` 생성 및 `setModel({original, modified})`.
- **diff 원문 소스**:
  - Unstaged 파일: original = 인덱스(`ShowFileAsync(repo, ":", path)`), modified = 작업트리 파일 내용
  - Staged 파일: original = `ShowFileAsync(repo, "HEAD", path)`, modified = 인덱스(`ShowFileAsync(repo, ":", path)`)
  - Untracked: original = "", modified = 파일 내용
- **언어 매핑**: 확장자 → Monaco language id(작은 테이블; 미매핑은 plaintext).
- diff는 읽기 전용(편집 비목표).

## 데이터 흐름

1. SCM 패널이 활성 프로젝트 repo에 대해 `StatusAsync` + `BranchStateAsync` 조회 → 두 섹션·툴바 렌더.
2. 파일 행 클릭 → 포커스된 Pane에 해당 `GitDiffItem` 탭을 열거나(이미 있으면) 포커스 → `MonacoDiffHostView`가 원문 3소스 규칙으로 old/new 확보 → Monaco에 push.
3. stage/unstage/discard/commit/pull/push/fetch → `GitService` 호출 → 성공 시 SCM 재조회 + 브랜치 버블 갱신 이벤트 발생 + 열린 diff 탭 갱신(해당 파일이 목록에서 사라지면 탭 닫기 또는 "변경 없음").

## 테마 매핑

- 앱 테마별 Monaco 테마를 `monaco.editor.defineTheme`로 정의: `base`(vs/vs-dark), `rules`(토큰색), `colors`(`editor.background`, `editorLineNumber.foreground`, `diffEditor.insertedTextBackground`/`removedTextBackground`/`insertedLineBackground`/`removedLineBackground` 등 = 앱 팔레트 hex).
- C#에서 현재 테마의 `DynamicResource` 브러시 hex를 추출해 JS에 push.
- **로드 완료 시 1회 + `App.ThemeChanged` 구독**해 전환 때마다 팔레트 재-push + `monaco.editor.setTheme`.
- 초기 흰 플래시 방지: `diff.html`의 `body`·컨테이너 배경을 `BgBrush` hex로 선칠 후 로드.

## 갱신 · 에러 처리

- **갱신 이벤트**: SCM 패널 → `GitStateChanged(repoPath)` → FileExplorer 포워딩 → MainWindow 구독 → 각 `WorkspacePaneView.RefreshBranchIfRepo(repo)`(브랜치 버블). (이전 설계의 배선 패턴 재사용.)
- **에러**: git 실패는 `ConfirmDialog.Alert(title, stderr요약)`. identity 미설정 시 커밋 전 안내. push/pull/fetch 성공은 `NotificationPopup`.
- **확인**: push, discard(변경 취소)는 되돌리기 어려우므로 `ConfirmDialog.Show` 사전 확인.
- **동시 실행 방지**: 액션 진행 중 관련 버튼 비활성.
- **airspace**: diff 탭도 WebView2이므로, 패널 리사이즈/오버레이 시 기존 브라우저 탭과 동일한 airspace 대응(`.knowledge/webview2-airspace-패널리사이즈-깜빡임.md`)을 적용.

## UI 점검 로직 (테마 · 공통컨트롤 검증)

구현 완료 선언 전 반드시 통과하는 품질 게이트.

### 7-1. 정적 스캔 — `scripts/ui-lint.ps1`
대상: 이번 작업에서 추가/수정한 XAML·cs 파일 목록(인자로 전달).
- **하드코딩 색 금지**: `#[0-9A-Fa-f]{6,8}` 리터럴 검출 → **허용 리스트(diff 상태색: add `#3FB950`, del `#F85149`, mod `#D29922`)만 예외**, 그 외 발견 시 위반으로 출력하고 비영(非0) 종료.
- **인라인 Style 금지**: XAML 내 로컬 `<Style`(BasedOn/StaticResource 없이 정의)·리터럴 `Foreground="#..."`/`Background="#..."` 검출 시 위반.
- **공통 스타일 사용(양성 체크)**: 버튼에 `Style="{StaticResource PrimaryButton|SecondaryButton|IconButton}"`, 색은 `{DynamicResource *Brush}`, 폰트 `PretendardFont`/`Fs*` 사용 확인. 미사용 의심 지점 경고 출력.
- 출력: 파일:줄 + 위반 유형. 위반 0이면 성공.

### 7-2. 테마 라운드트립 점검 (앱 실행 시 수동)
- 앱 테마를 하나씩 전환하며 **(a) SCM 패널(네이티브)** 과 **(b) Monaco diff 탭(배경·토큰색·add/del색)** 이 동시에 바뀌는지 확인.
- `App.ThemeChanged` → Monaco `setTheme` 재호출 경로 동작 확인.

### 7-3. 체크리스트 (플랜에 포함, 완료 전 필수)
- [ ] `ui-lint.ps1` 위반 0
- [ ] 새 네이티브 UI의 모든 색 = `DynamicResource`, 버튼/입력 = 공통 스타일 재사용
- [ ] 테마 N개 각각에서 SCM 패널 + Monaco diff 색 정상 전환
- [ ] Monaco 초기 로드 흰 플래시 없음

## 검증

- **빌드하지 않는다(사용자 요청).** 설계·스펙 리뷰까지만. 구현 후 빌드·실행 확인은 사용자가 직접.
- 구현 시 검증 흐름은 플랜에서 정의(정적 스캔 + 앱 수동 확인).

## 참고

- 디자인은 `AppStyles.xaml` 전역 스타일·테마 브러시 준수(devez 디자인 시스템).
- 서버/DB 없이 로컬 git CLI만 사용.
- WebView2 오프라인 자산·공유 환경 패턴은 `Views/MarkdownWysiwygHost.cs` 참고.

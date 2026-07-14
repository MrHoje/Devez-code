# DIFF 뷰 내 commit/push 설계

- 날짜: 2026-07-14
- 상태: 승인됨 (구현 계획 대기)
- 대상: `Views/GitDiffView`, `Services/GitService`, `Views/WorkspacePaneView`(브랜치 버블 갱신)

## 배경 / 목적

DevezCode에는 이미 git 기반 diff 뷰어가 있다:

- `Services/GitService` — git CLI 얇은 래퍼(`RunAsync`, `IsRepoAsync`, UTF-8 파일명 보존).
- `Views/GitDiffView` — FileExplorer의 뷰 전환 탭 중 "DIFF" 모드. `git status --porcelain`으로 변경
  파일 목록 + 선택 파일 side-by-side diff(전체 컨텍스트), 마커 스트립, Ctrl+휠 줌, 가상화.
- `Views/WorkspacePaneView` — 상태줄에 현재 브랜치 + ahead/behind 카운트 버블(`LoadBranchAsync`).

빠진 것은 **변경을 리뷰한 뒤 GUI에서 바로 커밋/푸시**하는 흐름이다. 지금은 터미널로 나가야 한다.
이 스펙은 그 격차만 메운다.

## 목표 (Goals)

- DIFF 뷰에서 **전체 변경을 한 번에 커밋**(`git add -A` → `commit -m`).
- **수동 커밋 메시지** 입력.
- **커밋 버튼과 푸시 버튼을 분리**. 푸시는 기존 ahead/behind와 연동, upstream 없으면 첫 푸시 자동 처리.

## 비목표 (Non-goals, YAGNI)

- 파일별/헝크별 스테이징, staged·unstaged 분리 표시.
- AI 커밋 메시지 생성.
- amend, 브랜치 생성/전환, stash, revert, 커밋 히스토리 브라우저.
- Per-turn(세션 재구성) diff.

## UI

`GitDiffView` **하단에 커밋 바**(`Border`)를 추가한다. 기존 목록/ diff 레이아웃은 그대로 두고,
루트 `Grid`에 하단 행(`RowDefinition Height="Auto"`)을 하나 더 둔다.

```
┌ 변경 파일 목록 ─┬─ diff (side-by-side) ─────────┐
│ M foo.cs        │  ...기존 diff...               │
│ A bar.cs        │                                │
├─────────────────┴────────────────────────────────┤
│ [커밋 메시지...              ]  [커밋]  [푸시 ↑N] │  ← 새 커밋 바
└───────────────────────────────────────────────────┘
```

- 메시지 `TextBox` — 멀티라인(`AcceptsReturn=True`, 2~3줄). **`TaskQueueView`의 입력 바 패턴을
  그대로 재사용**: `PanelBrush` 배경 + `LineBrush` 1px 테두리 + `CornerRadius` 둥근 카드,
  내부 `TextBox`는 `BorderThickness=0`/`Background=Transparent`/`Foreground=TextBrush`/
  `CaretBrush=TextBrush`/`FontFamily=PretendardFont`, 그리고 빈 값일 때 `DataTrigger`로
  "커밋 메시지" 플레이스홀더 `TextBlock`(`TextMutedBrush`) 오버레이.
- **커밋** 버튼 — `AppStyles.xaml`의 `PrimaryButton` 스타일. 변경이 하나라도 있고 메시지가
  비어있지 않을 때만 활성.
- **푸시** 버튼 — `SecondaryButton` 스타일. ahead>0일 때만 활성, 라벨에 `↑N` 표시.
  변경 없어도 보낼 커밋이 있으면 활성.
- 바 컨테이너는 `TaskQueueView`의 `SelectionActionBar`처럼 `PanelBrush` 배경 + 상단
  `LineBrush` 1px 구분선(기존 GitDiffView 보더 관례와 일치).
- 변경이 전혀 없으면 커밋 바 전체를 흐리게(비활성) 처리.
- 폰트 크기는 `Fs11`~`Fs13` 토큰 사용. 인라인 색/치수 하드코딩 지양.

## 테마 대응 & 컨트롤 재사용 (필수 요건)

새 UI는 **기존 화면들과 시각적으로 동일**해야 하며, **테마 전환에 자동 대응**해야 한다.

- **테마 대응** — 모든 색은 `AppStyles.xaml`의 `DynamicResource` 테마 브러시로만 지정한다
  (`BgBrush`/`PanelBrush`/`PanelSoftBrush`/`LineBrush`/`TextBrush`/`TextMutedBrush`/
  `PrimaryBrush`/`SuccessBrush`/`DangerBrush` 등). `DynamicResource`라야 테마 변경 시 자동
  갱신된다. 하드코딩 색(`#RRGGBB`) 금지 — 예외는 기존 diff 셀 상태색(추가/삭제/수정)뿐이며
  이는 이미 코드에 있는 값을 그대로 둔다.
- **컨트롤/스타일 재사용** — 새 스타일을 만들지 말고 기존 것을 가져다 쓴다:
  - 입력 카드: `Views/TaskQueueView.xaml`의 입력 바(`Row 2`) 구조를 축약 재사용.
  - 버튼: `PrimaryButton`(커밋) / `SecondaryButton`(푸시). 필요 시 `IconButton`.
  - 확인 다이얼로그: 기존 `Views/ConfirmDialog`.
  - 알림: 기존 `Views/NotificationPopup`.
  - 폰트: `PretendardFont`, 크기 토큰 `Fs11`~`Fs13`.
- **레이아웃 관례** — 바 배경 `PanelBrush` + 상단 `LineBrush` 1px 구분선은 `GitDiffView`·
  `TaskQueueView`의 기존 보더 관례와 맞춘다. 라운드/여백도 인접 화면과 통일.
- 구현 시 유사 화면(TaskQueueView, GitDiffView, ConfirmDialog)을 먼저 열어 마크업을 참고한 뒤
  같은 브러시·스타일 키로 배선한다.

## GitService 추가 (기존 RunAsync 재사용)

```csharp
// 전체 스테이징 후 커밋. 성공 여부와 stderr 반환.
Task<GitResult> CommitAllAsync(string repoDir, string message);
//   → add -A ; commit -m <message>  (두 번 호출, add 실패 시 즉시 반환)

// 현재 브랜치를 푸시. upstream 없으면 push -u origin <branch>.
Task<GitResult> PushAsync(string repoDir);
//   → 현재 브랜치명 조회(rev-parse --abbrev-ref HEAD)
//   → upstream 존재 확인(rev-parse --abbrev-ref --symbolic-full-name @{u})
//   → 있으면 push, 없으면 push -u origin <branch>

// user.name / user.email 설정 여부(커밋 전 선제 검사).
Task<bool> HasIdentityAsync(string repoDir);
//   → config user.name / user.email 둘 다 비어있지 않은지
```

커밋 메시지는 `ArgumentList`에 `-m <message>`로 그대로 넣는다. `RunAsync`가 셸을 경유하지
않으므로(`UseShellExecute=false`) 여러 줄/특수문자 이스케이프가 불필요하다.

## 데이터 흐름

### 커밋
1. 메시지 공백 검사(공백뿐이면 커밋 버튼 비활성이라 도달 안 함).
2. `HasIdentityAsync` — 미설정이면 커밋 중단하고 안내(아래 에러 처리).
3. 커밋 버튼·푸시 버튼 비활성(중복 실행 방지).
4. `CommitAllAsync(repo, msg)`.
5. 성공 → 메시지 입력 비움 → `RefreshAsync()`(목록 비게 됨) → **브랜치 갱신 신호 발생**(ahead+1 반영).
6. 실패 → `NotificationPopup`로 stderr 요약. 버튼 복구.

### 푸시
1. **확인 다이얼로그**(`ConfirmDialog`): "origin/<branch>에 커밋 N개를 푸시할까요?"
   — 푸시는 외부로 나가는 되돌리기 어려운 동작이므로 사전 확인 필수.
2. 확인 시 푸시 버튼 비활성 → `PushAsync(repo)`.
3. 성공 → **브랜치 갱신 신호 발생**(ahead=0). 완료 알림(`NotificationPopup`).
4. 실패(인증·비패스트포워드 등) → `NotificationPopup`로 stderr 요약. 버튼 복구.

## 뷰 간 갱신 연동 (구조 포인트)

`GitDiffView`(FileExplorer 내부)와 브랜치 버블(`WorkspacePaneView`)은 직접 참조가 없다.
직접 참조를 새로 만들지 않고 **이벤트/콜백**으로 신호만 보낸다.

- `GitDiffView`에 `public event Action? GitStateChanged;` 추가.
- 커밋/푸시 성공 시 `GitStateChanged?.Invoke()`.
- FileExplorer→WorkspacePane 조립 지점에서 이 이벤트를 구독해
  `WorkspacePaneView.LoadBranchAsync`(또는 그 래퍼)를 다시 호출한다.
- 구현 계획 단계에서 실제 조립 경로(누가 GitDiffView를 소유하고 누가 브랜치 버블을 갱신하는지)를
  확인해 배선한다. 신호 방향은 GitDiffView → 브랜치 버블 단방향.

## 에러 / 엣지 케이스

- **git 미설치 / 저장소 아님** — 기존 `RefreshAsync` 안내 문구 흐름 그대로. 커밋 바 비활성.
- **user.name/email 미설정** — 커밋 시도 시 `NotificationPopup`로 "git 사용자 정보가 없습니다.
  git config user.name/user.email 설정 후 다시 시도하세요." 안내(커밋 진행 안 함).
- **커밋할 변경 없음** — 커밋 버튼 비활성.
- **푸시할 커밋 없음(ahead=0)** — 푸시 버튼 비활성.
- **upstream 없음** — 첫 푸시 `-u origin <branch>` 자동.
- **푸시 거부(non-fast-forward)·인증 실패** — stderr 요약 알림. 자동 pull/force 하지 않음.
- **커밋/푸시 진행 중** — 해당 버튼 비활성으로 중복 실행 차단.
- **비ASCII 커밋 메시지** — `ArgumentList` 전달 + `RunAsync`의 UTF-8 stdout 인코딩으로 보존.

## 테스트 / 검증

- 실제 git 저장소에서: 변경 생성 → 목록 표시 → 메시지 입력 → 커밋 → 목록 비고 ahead+1.
- 푸시 확인 다이얼로그 → 푸시 → ahead=0.
- upstream 없는 새 브랜치에서 첫 푸시(`-u`) 동작.
- user.name 미설정 시 안내 노출, 커밋 미실행 확인.
- git 미설치/비저장소 폴더에서 커밋 바 비활성.
- 빌드/재시작 프로세스(CLAUDE.md) 후 실제 앱에서 end-to-end 확인.

## 참고

- 디자인은 `AppStyles.xaml` 전역 스타일 사용, devez 디자인 시스템 준수.
- 서버/DB 없이 로컬 git CLI만 사용(기존 `GitService` 방침 유지).

# DIFF 뷰 commit/push Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** DIFF 뷰(`GitDiffView`)에서 전체 변경을 커밋하고 원격에 푸시하는 UI를 추가한다.

**Architecture:** 기존 `GitService`(git CLI 래퍼)에 commit/push/identity 헬퍼를 더하고, `GitDiffView` 하단에 커밋 바를 붙인다. 커밋/푸시 성공 시 `GitDiffView.GitStateChanged(repoPath)` 이벤트를 쏘고, 이를 `FileExplorerView`가 포워딩 → `MainWindow`가 구독 → 각 `WorkspacePaneView`의 브랜치 버블(ahead/behind)을 갱신한다.

**Tech Stack:** C# / .NET WPF, git CLI(프로세스 호출), 기존 `AppStyles.xaml` 테마 브러시·스타일.

## Global Constraints

- 모든 색상은 `AppStyles.xaml`의 `DynamicResource` 테마 브러시로만 지정한다(테마 자동 대응). 하드코딩 `#RRGGBB` 금지. 예외: 기존 diff 셀 상태색(코드에 이미 존재, 그대로 둠).
- 새 스타일을 만들지 말고 기존 것을 재사용한다: 입력 카드=`Views/TaskQueueView.xaml`의 입력 바 패턴, 버튼=`PrimaryButton`/`SecondaryButton`, 확인=`ConfirmDialog.Show/Alert`, 토스트=`NotificationPopup`, 폰트=`PretendardFont`, 크기 토큰 `Fs11`~`Fs13`.
- 서버/DB 없이 로컬 git CLI만 사용(`GitService` 기존 방침).
- 테스트 프레임워크 없음 → 검증은 **빌드 + 실행 앱 수동 확인**. 빌드/재시작(CLAUDE.md):
  ```powershell
  taskkill /IM DevezCode.exe /F 2>$null
  dotnet build -c Release --nologo -v quiet
  Start-Process "bin\DevezCode.exe"
  ```
  빌드 실패 시 앱을 재시작하지 말고 오류부터 수정한다.
- 커밋 메시지는 영문 접두사 없이 자유. 각 태스크 끝에 커밋한다. 전체 완료 후 push(CLAUDE.md 규칙).

## File Structure

- `Services/GitService.cs` (수정) — `HasIdentityAsync`, `CommitAllAsync`, `PushAsync`, `PushStateAsync` 추가. git 호출 로직만 담당.
- `Views/GitDiffView.xaml` (수정) — 루트 Grid에 하단 행 추가 + 커밋 바 마크업.
- `Views/GitDiffView.xaml.cs` (수정) — 커밋 바 상태/핸들러, `GitStateChanged` 이벤트.
- `Views/FileExplorerView.xaml.cs` (수정) — `DiffView.GitStateChanged` 포워딩 이벤트.
- `Views/WorkspacePaneView.xaml.cs` (수정) — `RefreshBranchIfRepo(repoDir)` 공개 메서드.
- `MainWindow.xaml.cs` (수정) — `FileExplorer.GitStateChanged` 구독 → 각 Pane 갱신.

---

### Task 1: GitService — commit/push/identity 헬퍼

**Files:**
- Modify: `Services/GitService.cs` (기존 `RunAsync`/`IsRepoAsync` 뒤, 클래스 내부에 추가)

**Interfaces:**
- Consumes: 기존 `GitService.RunAsync(repoDir, params args)` → `GitResult(bool Ok, string Output, string Error)`.
- Produces:
  - `Task<bool> HasIdentityAsync(string repoDir)`
  - `Task<GitResult> CommitAllAsync(string repoDir, string message)`
  - `Task<GitResult> PushAsync(string repoDir)`
  - `Task<(bool hasUpstream, int ahead)> PushStateAsync(string repoDir)`

- [ ] **Step 1: 헬퍼 4개 추가**

`Services/GitService.cs`의 `IsRepoAsync` 메서드 닫는 `}` 다음, 클래스 닫는 `}` 앞에 삽입:

```csharp
    /// <summary>user.name / user.email 이 모두 설정돼 있는지(커밋 전 선제 검사).</summary>
    public static async Task<bool> HasIdentityAsync(string repoDir)
    {
        var name = await RunAsync(repoDir, "config", "user.name");
        var email = await RunAsync(repoDir, "config", "user.email");
        return name.Ok && !string.IsNullOrWhiteSpace(name.Output)
            && email.Ok && !string.IsNullOrWhiteSpace(email.Output);
    }

    /// <summary>전체 스테이징(add -A) 후 커밋(commit -m). add 실패 시 그 결과를 그대로 반환.</summary>
    public static async Task<GitResult> CommitAllAsync(string repoDir, string message)
    {
        var add = await RunAsync(repoDir, "add", "-A");
        if (!add.Ok) return add;
        return await RunAsync(repoDir, "commit", "-m", message);
    }

    /// <summary>현재 브랜치 푸시. upstream 이 있으면 push, 없으면 push -u origin &lt;branch&gt;.</summary>
    public static async Task<GitResult> PushAsync(string repoDir)
    {
        var br = await RunAsync(repoDir, "rev-parse", "--abbrev-ref", "HEAD");
        if (!br.Ok) return br;
        var branch = br.Output.Trim();
        if (string.IsNullOrEmpty(branch) || branch == "HEAD")
            return new GitResult(false, "", "현재 브랜치를 확인할 수 없습니다(detached HEAD).");

        var up = await RunAsync(repoDir, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
        return up.Ok
            ? await RunAsync(repoDir, "push")
            : await RunAsync(repoDir, "push", "-u", "origin", branch);
    }

    /// <summary>푸시 버튼 상태용: upstream 유무와 보낼 커밋 수.
    /// upstream 없으면 (false, HEAD 존재 시 1 · 아니면 0) — 첫 푸시 허용 신호로 1 을 준다.</summary>
    public static async Task<(bool hasUpstream, int ahead)> PushStateAsync(string repoDir)
    {
        var up = await RunAsync(repoDir, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
        if (!up.Ok)
        {
            var head = await RunAsync(repoDir, "rev-parse", "--verify", "HEAD");
            return (false, head.Ok ? 1 : 0);
        }
        var r = await RunAsync(repoDir, "rev-list", "--count", "@{u}..HEAD");
        return (true, int.TryParse(r.Output.Trim(), out var n) ? n : 0);
    }
```

- [ ] **Step 2: 빌드 확인**

Run:
```powershell
taskkill /IM DevezCode.exe /F 2>$null
dotnet build -c Release --nologo -v quiet
```
Expected: 빌드 성공(에러 0). 경고만이면 통과. 실패 시 이 태스크 범위에서 수정.

- [ ] **Step 3: 커밋**

```bash
git add Services/GitService.cs
git commit -m "feat(git): add commit/push/identity helpers to GitService"
```

---

### Task 2: GitDiffView 커밋 바 (UI + 로직 + GitStateChanged)

**Files:**
- Modify: `Views/GitDiffView.xaml` (루트 Grid 래핑 + 커밋 바)
- Modify: `Views/GitDiffView.xaml.cs` (필드/상태/핸들러/이벤트)

**Interfaces:**
- Consumes: `GitService.HasIdentityAsync` / `CommitAllAsync` / `PushAsync` / `PushStateAsync` (Task 1). `ConfirmDialog.Show(title, message, confirmLabel)` → `bool`, `ConfirmDialog.Alert(title, message)`. `new NotificationPopup(title, content).Show()`.
- Produces: `public event Action<string>? GitStateChanged;` (repo 경로 전달) — Task 3 이 구독.

- [ ] **Step 1: XAML — 루트 Grid 를 2행으로 래핑**

`Views/GitDiffView.xaml`의 최상위 `<Grid>` 여는 태그(7행 부근)를 아래로 교체하여 행 정의와 내부 열-그리드 여는 태그를 추가한다.

기존:
```xml
    <Grid>
        <Grid.ColumnDefinitions>
```
교체 후:
```xml
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>
        <!-- 기존 목록/diff 영역 (변경 없음) -->
        <Grid Grid.Row="0">
        <Grid.ColumnDefinitions>
```

- [ ] **Step 2: XAML — 내부 열-그리드 닫고 커밋 바 추가**

파일 맨 끝의 최상위 `</Grid>`(루트 닫기) **바로 앞**에, 먼저 내부 열-그리드를 닫는 `</Grid>`를 넣고 이어서 커밋 바 `Border`를 추가한다.

기존 파일 끝(발췌):
```xml
            </Grid>
        </Border>
    </Grid>
</UserControl>
```
여기서 마지막 `</Border>`(diff 영역 Border 닫기) 다음, 루트 `</Grid>` 앞에 아래를 삽입:
```xml
        </Grid><!-- /Grid.Row=0 내부 열-그리드 -->

        <!-- 커밋 바: TaskQueueView 입력 바 패턴 재사용. 모든 색은 테마 브러시. -->
        <Border x:Name="CommitBar" Grid.Row="1"
                Background="{DynamicResource PanelBrush}"
                BorderBrush="{DynamicResource LineBrush}" BorderThickness="0,1,0,0"
                Padding="8">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>

                <!-- 메시지 입력 카드 (둥근 카드 + 플레이스홀더 오버레이) -->
                <Border Grid.Column="0"
                        Background="{DynamicResource BgBrush}"
                        BorderBrush="{DynamicResource LineBrush}" BorderThickness="1"
                        CornerRadius="10" Padding="10,6" VerticalAlignment="Center">
                    <Grid>
                        <TextBox x:Name="MsgBox"
                                 BorderThickness="0" Background="Transparent"
                                 Foreground="{DynamicResource TextBrush}"
                                 CaretBrush="{DynamicResource TextBrush}"
                                 FontFamily="{StaticResource PretendardFont}"
                                 FontSize="{DynamicResource Fs12}"
                                 MaxHeight="80" TextWrapping="Wrap" AcceptsReturn="True"
                                 VerticalScrollBarVisibility="Auto"
                                 TextChanged="MsgBox_TextChanged"/>
                        <TextBlock x:Name="MsgPlaceholder" Text="커밋 메시지"
                                   IsHitTestVisible="False" VerticalAlignment="Center"
                                   Margin="1,0,0,0"
                                   Foreground="{DynamicResource TextMutedBrush}"
                                   FontFamily="{StaticResource PretendardFont}"
                                   FontSize="{DynamicResource Fs12}">
                            <TextBlock.Style>
                                <Style TargetType="TextBlock" BasedOn="{StaticResource {x:Type TextBlock}}">
                                    <Setter Property="Visibility" Value="Collapsed"/>
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding Text, ElementName=MsgBox}" Value="">
                                            <Setter Property="Visibility" Value="Visible"/>
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </TextBlock.Style>
                        </TextBlock>
                    </Grid>
                </Border>

                <!-- 커밋 -->
                <Button x:Name="CommitBtn" Grid.Column="1" Margin="8,0,0,0"
                        Style="{StaticResource PrimaryButton}" Height="30" Padding="14,0"
                        IsEnabled="False" VerticalAlignment="Center"
                        FontSize="{DynamicResource Fs12}"
                        Click="CommitBtn_Click">
                    <TextBlock Text="커밋" Foreground="White"/>
                </Button>

                <!-- 푸시 -->
                <Button x:Name="PushBtn" Grid.Column="2" Margin="6,0,0,0"
                        Style="{StaticResource SecondaryButton}" Height="30" Padding="14,0"
                        IsEnabled="False" VerticalAlignment="Center"
                        FontSize="{DynamicResource Fs12}"
                        Click="PushBtn_Click">
                    <TextBlock x:Name="PushBtnText" Text="푸시"/>
                </Button>
            </Grid>
        </Border>
```

- [ ] **Step 3: code-behind — 필드/이벤트 추가**

`Views/GitDiffView.xaml.cs`에서 `private List<DiffRow> _diff = new();`(41행 부근) 다음에 추가:

```csharp
    /// <summary>커밋/푸시 성공 시 발생(repo 경로 전달). 브랜치 버블 갱신 신호.</summary>
    public event Action<string>? GitStateChanged;

    private bool _busy;          // 커밋/푸시 진행 중
    private bool _hasUpstream;   // 현재 브랜치에 upstream 이 있는지
    private int _ahead;          // 원격 대비 보낼 커밋 수(또는 첫 푸시 신호 1)
```

- [ ] **Step 4: code-behind — 커밋 바 상태 갱신 로직**

같은 파일 클래스 내부(예: `RefreshAsync` 위)에 추가:

```csharp
    /// <summary>커밋/푸시 버튼 활성·라벨 갱신. repoValid=false 면 바 전체 비활성.</summary>
    private async Task RefreshCommitBarAsync(bool repoValid)
    {
        if (!repoValid || string.IsNullOrEmpty(_repo))
        {
            _hasUpstream = false; _ahead = 0;
            CommitBtn.IsEnabled = false;
            PushBtn.IsEnabled = false;
            PushBtnText.Text = "푸시";
            return;
        }

        (_hasUpstream, _ahead) = await GitService.PushStateAsync(_repo);
        PushBtnText.Text = _hasUpstream ? $"푸시 ↑{_ahead}" : "푸시";
        CommitBtn.IsEnabled = !_busy && _changes.Count > 0 && !string.IsNullOrWhiteSpace(MsgBox.Text);
        PushBtn.IsEnabled = !_busy && _ahead > 0;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        MsgBox.IsEnabled = !busy;
        CommitBtn.IsEnabled = !busy && _changes.Count > 0 && !string.IsNullOrWhiteSpace(MsgBox.Text);
        PushBtn.IsEnabled = !busy && _ahead > 0;
    }

    private void MsgBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        CommitBtn.IsEnabled = !_busy && _changes.Count > 0 && !string.IsNullOrWhiteSpace(MsgBox.Text);
    }
```

- [ ] **Step 5: code-behind — RefreshAsync 에 커밋 바 갱신 연결**

`RefreshAsync` 안의 각 종료 지점에 커밋 바 갱신을 추가한다.

(a) 하드 무효(프로젝트 없음/저장소 아님/상태 읽기 실패)의 `ApplyEmpty(...); return;` 3곳을 각각:
```csharp
            ApplyEmpty("프로젝트를 선택하면 git 변경 내역이 표시됩니다.");
            await RefreshCommitBarAsync(false);
            return;
```
```csharp
            ApplyEmpty("git 저장소가 아니거나 git 이 설치되어 있지 않습니다.");
            await RefreshCommitBarAsync(false);
            return;
```
```csharp
            ApplyEmpty(string.IsNullOrWhiteSpace(r.Error) ? "git 상태를 읽지 못했습니다." : r.Error.Trim());
            await RefreshCommitBarAsync(false);
            return;
```

(b) "변경된 파일이 없습니다." 종료(저장소는 유효, 푸시는 가능할 수 있음):
```csharp
            ApplyEmpty("변경된 파일이 없습니다.");
            await RefreshCommitBarAsync(true);
            return;
```

(c) 변경 있음 경로의 맨 끝(메서드 마지막 `}` 직전, 기존 주석 `// _selected != null …` 아래)에:
```csharp
        await RefreshCommitBarAsync(true);
```

- [ ] **Step 6: code-behind — 커밋/푸시 핸들러**

클래스 내부에 추가(파일 상단에 `using DevezCode.Views;` 는 이미 같은 네임스페이스이므로 불필요):

```csharp
    private async void CommitBtn_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_repo)) return;
        var msg = MsgBox.Text.Trim();
        if (msg.Length == 0 || _changes.Count == 0) return;

        if (!await GitService.HasIdentityAsync(_repo))
        {
            ConfirmDialog.Alert("커밋 불가",
                "git 사용자 정보가 없습니다.\ngit config user.name / user.email 설정 후 다시 시도하세요.");
            return;
        }

        SetBusy(true);
        var r = await GitService.CommitAllAsync(_repo, msg);
        SetBusy(false);
        if (!r.Ok)
        {
            ConfirmDialog.Alert("커밋 실패", string.IsNullOrWhiteSpace(r.Error) ? r.Output : r.Error);
            return;
        }

        MsgBox.Clear();
        await RefreshAsync();               // 목록(비게 됨) + 푸시 상태 갱신
        GitStateChanged?.Invoke(_repo);     // 브랜치 버블 갱신 신호
    }

    private async void PushBtn_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_repo)) return;
        var msg = _hasUpstream
            ? $"이 브랜치의 커밋 {_ahead}개를 원격에 푸시할까요?"
            : "이 브랜치를 origin 에 처음 푸시할까요? (-u origin <branch>)";
        if (!ConfirmDialog.Show("푸시", msg, "푸시")) return;

        SetBusy(true);
        var r = await GitService.PushAsync(_repo);
        SetBusy(false);
        if (!r.Ok)
        {
            ConfirmDialog.Alert("푸시 실패", string.IsNullOrWhiteSpace(r.Error) ? r.Output : r.Error);
            return;
        }

        new NotificationPopup("푸시 완료", null).Show();
        await RefreshCommitBarAsync(true);  // ahead=0 반영
        GitStateChanged?.Invoke(_repo);
    }
```

- [ ] **Step 7: 빌드 + 실행 검증**

Run:
```powershell
taskkill /IM DevezCode.exe /F 2>$null
dotnet build -c Release --nologo -v quiet
Start-Process "bin\DevezCode.exe"
```
앱에서 확인:
- git 저장소 프로젝트 선택 → 우측 패널 DIFF 탭.
- 파일을 하나 수정 → 변경 목록에 표시, 커밋 바 노출. 메시지 비었을 때 커밋 버튼 비활성.
- 메시지 입력 → 커밋 버튼 활성 → 커밋 → 목록 비고 푸시 버튼 `푸시 ↑1`.
- 푸시 버튼 → 확인 다이얼로그 → 푸시 → "푸시 완료" 토스트, 버튼 `푸시 ↑0`(비활성).
- 테마 전환(설정) 시 커밋 바 색이 함께 바뀌는지 확인.

Expected: 위 흐름 정상. 실패 시 수정 후 재빌드.

- [ ] **Step 8: 커밋**

```bash
git add Views/GitDiffView.xaml Views/GitDiffView.xaml.cs
git commit -m "feat(git): add commit/push bar to DIFF view"
```

---

### Task 3: 브랜치 버블 갱신 배선 (FileExplorer → MainWindow → Pane)

**Files:**
- Modify: `Views/FileExplorerView.xaml.cs` (이벤트 포워딩)
- Modify: `Views/WorkspacePaneView.xaml.cs` (`RefreshBranchIfRepo`)
- Modify: `MainWindow.xaml.cs` (구독 → Pane 갱신)

**Interfaces:**
- Consumes: `GitDiffView.GitStateChanged` (Task 2), `WorkspacePaneView` 의 기존 `_activeProject`(ProjectItem, `.Path`) 와 `UpdateProjectBranchBubble(ProjectItem)`, `MainWindow._panes`(List<WorkspacePaneView>), `MainWindow.FileExplorer`.
- Produces:
  - `FileExplorerView`: `public event Action<string>? GitStateChanged;`
  - `WorkspacePaneView`: `public void RefreshBranchIfRepo(string repoDir)`

- [ ] **Step 1: FileExplorerView — 이벤트 포워딩**

`Views/FileExplorerView.xaml.cs` 클래스에 이벤트 선언 추가:
```csharp
    /// <summary>DIFF 뷰의 커밋/푸시로 git 상태가 바뀌었을 때(repo 경로). MainWindow 가 구독.</summary>
    public event Action<string>? GitStateChanged;
```
생성자(`public FileExplorerView()`) `InitializeComponent();` 다음에 구독 포워딩 추가:
```csharp
        DiffView.GitStateChanged += repo => GitStateChanged?.Invoke(repo);
```

- [ ] **Step 2: WorkspacePaneView — RefreshBranchIfRepo 추가**

`Views/WorkspacePaneView.xaml.cs`의 `UpdateProjectBranchBubble(ProjectItem?)` 메서드 근처(같은 영역)에 추가:
```csharp
    /// <summary>지정 repo 가 이 패널의 활성 프로젝트와 같으면 브랜치 버블(ahead/behind)을 다시 읽는다.</summary>
    public void RefreshBranchIfRepo(string repoDir)
    {
        if (_activeProject == null || string.IsNullOrEmpty(repoDir)) return;
        var a = _activeProject.Path?.TrimEnd('\\', '/');
        var b = repoDir.TrimEnd('\\', '/');
        if (!string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            UpdateProjectBranchBubble(_activeProject);
    }
```
> 참고: `_activeProject`/`UpdateProjectBranchBubble` 가 실제로 이 이름인지 확인(본 계획 작성 시 확인됨: 575·610·619행). 다르면 그 이름에 맞춘다.

- [ ] **Step 3: MainWindow — 구독 배선**

`MainWindow.xaml.cs`의 생성자에서 `PaneA`/`PaneB` 를 `_panes` 에 넣는 `SetupPane(...)` 호출들(2458행 `SetupPane` 정의) **다음**에 추가. `_panes`(41행)가 채워진 뒤여야 한다:
```csharp
        FileExplorer.GitStateChanged += repo =>
        {
            foreach (var pane in _panes) pane.RefreshBranchIfRepo(repo);
        };
```
> 정확한 삽입 위치: 생성자에서 두 패널을 셋업하고 `_panes` 에 추가하는 코드 바로 아래. 셋업 코드가 별도 메서드라면 그 메서드 호출 다음. 이벤트 콜백은 실행 시점에 `_panes` 를 읽으므로, 구독 등록만 초기화 이후 한 번 하면 된다.

- [ ] **Step 4: 빌드 + 실행 검증**

Run:
```powershell
taskkill /IM DevezCode.exe /F 2>$null
dotnet build -c Release --nologo -v quiet
Start-Process "bin\DevezCode.exe"
```
앱에서 확인:
- 프로젝트 선택(패널 헤더에 브랜치 버블 표시) → DIFF 탭에서 변경 커밋.
- **프로젝트 전환 없이** 패널 헤더의 ahead 카운트가 즉시 `↑1` 로 증가하는지.
- 이어서 푸시 → 헤더 ahead 가 사라지는지(0).
- 2분할(Split) 상태에서 같은 프로젝트를 보는 두 패널 모두 갱신되는지, 다른 프로젝트 패널은 그대로인지.

Expected: 활성 프로젝트가 커밋된 repo 와 같은 패널만 갱신.

- [ ] **Step 5: 커밋 + 푸시(전체 완료)**

```bash
git add Views/FileExplorerView.xaml.cs Views/WorkspacePaneView.xaml.cs MainWindow.xaml.cs
git commit -m "feat(git): refresh branch bubble after commit/push"
git push
```

---

## Self-Review 결과

- **스펙 커버리지:** 전체 커밋(Task1 `CommitAllAsync`+Task2 핸들러) / 수동 메시지(Task2 XAML) / 커밋·푸시 분리 버튼(Task2) / upstream 없을 때 `-u`(Task1 `PushAsync`) / 푸시 전 확인(Task2) / identity 검사(Task1+Task2) / 에러 안내(Task2 `ConfirmDialog.Alert`) / 뷰 간 갱신 이벤트(Task2 event + Task3 배선) / 테마 브러시·컨트롤 재사용(Global Constraints + Task2 XAML) — 모두 태스크 존재.
- **스펙과의 의도적 차이:** 스펙은 에러를 `NotificationPopup` 로 적었으나, 기존 코드 관례(실패 시 `ConfirmDialog.Alert`)를 따라 **에러=Alert, 성공 토스트=NotificationPopup** 로 배치. 사용자 피드백 제공이라는 의도는 동일.
- **플레이스홀더:** 없음(모든 코드 스텝에 실제 코드).
- **타입 일관성:** `GitStateChanged`(`Action<string>`), `RefreshBranchIfRepo(string)`, `PushStateAsync` 튜플 `(bool hasUpstream, int ahead)` 이름이 Task 간 일치.
- **미확정 지점(구현 중 확인):** MainWindow 생성자의 정확한 `_panes` 채움 위치, `WorkspacePaneView._activeProject`/`UpdateProjectBranchBubble` 식별자(작성 시 확인됨). 다르면 실제 이름에 맞춘다.

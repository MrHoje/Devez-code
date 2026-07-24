# 하단 슬라이드 터미널 패널 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 푸터 버튼으로 토글하는, 에이전트 미연결 전역 PowerShell 터미널 패널(중앙 세션 영역 하단 슬라이드).

**Architecture:** `AgentRegistry`에 숨김 pseudo-agent `shell`을 등록하고 `TerminalSessionManager.LaunchSession`에 pwsh 직접 실행 분기를 추가한다. UI는 `CenterSplit`에 Row 3개(콘텐츠/스플리터/패널)를 추가하고 전용 `TerminalHostView` 인스턴스를 둔다. 슬라이드는 `GridLengthAnimation`(신규)으로 Row 높이를 애니메이션하며, 전후로 기존 `FreezeWorkspaceTerminalsAsync(webCover)` 패턴으로 워크스페이스 터미널 깜빡임을 막는다.

**Tech Stack:** WPF(.NET), ConPTY(`TerminalSession`), WebView2+xterm(`TerminalHostView`).

**스펙:** `docs/superpowers/specs/2026-07-24-bottom-shell-terminal-design.md`

## Global Constraints

- **실행 중인 DevezCode 프로세스 강제 종료 절대 금지** (`taskkill /F`, `Stop-Process -Force` 등 금지). 빌드 전 CLAUDE.md "빌드 및 재시작 프로세스" 준수. 본 세션이 DevezCode 내부에서 실행 중이면 사용자 요청 없이 앱 종료/재시작 금지.
- 이 리포에는 자동 테스트 인프라가 없다. 각 태스크의 검증 = `dotnet build -c Release --nologo -v quiet` 성공. 동작 검증은 Task 6의 수동 시나리오.
- 빌드 성공 확인 후에만 커밋. 커밋은 태스크 단위, 푸시는 마지막에 1회.
- 숨김(HwndHost)은 반드시 `Collapsed` (`Hidden` 금지 — `.knowledge/webview2-airspace-패널리사이즈-깜빡임.md`).
- 클릭 가능 컨트롤 커서는 Arrow(Hand 금지), 색·간격은 `AppStyles.xaml` 전역 토큰 사용.
- 고정 roomId: `devezcode-shell-terminal`. 셸 = pwsh.exe 우선, powershell.exe 폴백. cwd = 사용자 홈.

---

### Task 1: `shell` pseudo-agent 등록 + 런치 분기

**Files:**
- Modify: `Services/AgentRegistry.cs` (`All` 배열 끝 + `HiddenFromUI`)
- Modify: `Services/Terminal/TerminalSessionManager.cs` (`LaunchSession` 분기 ~L157, 헬퍼 추가)

**Interfaces:**
- Produces: 에이전트 id `"shell"` — `SettingsService.SaveAgentForRoom(roomId, "shell")`로 매핑된 방을 `GetOrCreate(roomId, cols, rows)` 하면 pwsh ConPTY 세션이 생성됨. Task 5가 사용.

- [ ] **Step 1: AgentRegistry에 shell 항목 추가**

`Services/AgentRegistry.cs`의 `All` 배열 마지막(gajae 항목 뒤)에 추가:

```csharp
        new()
        {
            // 하단 터미널 패널 전용 pseudo-agent — 에이전트 미연결 일반 셸(pwsh 우선, powershell 폴백).
            // HiddenFromUI 로 피커/설정 비노출. 실제 커맨드는 TerminalSessionManager 의 shell 분기가 조립.
            Id = "shell", DisplayName = "터미널", Provider = "Shell",
            ExeNames = new[] { "pwsh.exe", "powershell.exe" },
            Command = "pwsh",
            InlineTui = true, // alt-screen 없음 — 로딩 오버레이를 첫 출력 기준으로 해제
        },
```

같은 파일 `HiddenFromUI` 초기화를 다음으로 변경:

```csharp
    public static readonly HashSet<string> HiddenFromUI = new(StringComparer.OrdinalIgnoreCase)
    {
        "shell", // 하단 터미널 패널 전용 — 세션 피커/설정에 노출하지 않음
    };
```

- [ ] **Step 2: LaunchSession에 shell 분기 추가**

`Services/Terminal/TerminalSessionManager.cs` — `LaunchSession` 내부, `AgentModelCatalogRefreshRequested?.Invoke(agent.Id);` (L158 부근) 다음의 `if (ccDir != null && agent.Id == "codex")` 를 `else if` 체인 앞에 shell 분기를 삽입해 다음 형태로 만든다:

```csharp
            if (agent.Id == "shell")
            {
                // 하단 터미널 패널: 에이전트 미연결 빈 셸. 훅/resume/세션 추적 없음.
                // cmd 래핑 없이 pwsh 를 직접 스폰(순수 .exe 라 셸 경유 불필요). cwd = 사용자 홈.
                startDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                commandLine = $"\"{ResolveShellExe()}\" -NoLogo";
            }
            else if (ccDir != null && agent.Id == "codex")
```

(기존 `if (ccDir != null && agent.Id == "codex")` 가 `else if` 로 바뀌는 것 외에 기존 체인은 무변.)

- [ ] **Step 3: ResolveShellExe 헬퍼 추가**

같은 파일의 `TryBuildSimpleLaunch` (L275 부근) 위에 추가:

```csharp
    /// <summary>하단 터미널 패널용 셸 실행 파일. 이름 우선순위(pwsh → powershell)로 PATH 전체를 훑는다
    /// (AgentRegistry.ResolvePath 는 디렉터리 우선이라 System32 의 powershell 이 pwsh 를 이길 수 있음).</summary>
    private static string ResolveShellExe()
    {
        foreach (var name in new[] { "pwsh.exe", "powershell.exe" })
        {
            foreach (var target in new[] { EnvironmentVariableTarget.Process,
                                           EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            {
                string path;
                try { path = Environment.GetEnvironmentVariable("PATH", target) ?? ""; }
                catch { continue; }
                foreach (var dir in path.Split(Path.PathSeparator,
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    try
                    {
                        var full = Path.Combine(dir, name);
                        if (File.Exists(full)) return full;
                    }
                    catch { /* 잘못된 경로 무시 */ }
                }
            }
        }
        return "powershell.exe"; // 비현실적 폴백 — CreateProcess 가 PATH 해석
    }
```

- [ ] **Step 4: 빌드 확인**

Run: `dotnet build -c Release --nologo -v quiet`
Expected: 오류 0. (실행 중 DevezCode 가 있어도 빌드만 하면 파일 잠금 충돌이 날 수 있음 — 잠금 오류 시 Task 6 의 정상 종료 절차 전까지 빌드 검증을 미루고 사용자에게 알린다. 강제 종료 금지.)

- [ ] **Step 5: 커밋**

```bash
git add Services/AgentRegistry.cs Services/Terminal/TerminalSessionManager.cs
git commit -m "feat: shell pseudo-agent + pwsh 직접 런치 분기 (하단 터미널 패널 백엔드)"
```

---

### Task 2: 패널 높이 설정 저장

**Files:**
- Modify: `Services/SettingsService.cs` (`SettingsData` 프로퍼티 + 정적 메서드, `SaveUsagePanelOpen` L312 부근 관례)

**Interfaces:**
- Produces: `SettingsService.LoadShellTerminalHeight(): double` (최소 120 클램프, 기본 260), `SettingsService.SaveShellTerminalHeight(double)`. Task 5가 사용.

- [ ] **Step 1: SettingsData 프로퍼티 추가**

`SettingsService.cs`의 `SettingsData` 클래스에 (`UsagePanelOpen` 프로퍼티 근처):

```csharp
    /// <summary>하단 터미널 패널 높이(px). 스플리터 드래그로 변경·저장.</summary>
    public double ShellTerminalHeight { get; set; } = 260;
```

- [ ] **Step 2: 정적 Load/Save 메서드 추가**

`SaveUsagePanelOpen` (L312 부근) 아래에:

```csharp
    public static double LoadShellTerminalHeight() => Math.Max(120, Current.ShellTerminalHeight);
    public static void SaveShellTerminalHeight(double h) { Current.ShellTerminalHeight = h; Save(); }
```

- [ ] **Step 3: 빌드 확인**

Run: `dotnet build -c Release --nologo -v quiet`
Expected: 오류 0.

- [ ] **Step 4: 커밋**

```bash
git add Services/SettingsService.cs
git commit -m "feat: 하단 터미널 패널 높이 설정 저장"
```

---

### Task 3: GridLengthAnimation 헬퍼

**Files:**
- Create: `Behaviors/GridLengthAnimation.cs`

**Interfaces:**
- Produces: `DevezCode.Behaviors.GridLengthAnimation : AnimationTimeline` — `From`/`To`(GridLength), `EasingFunction` 지원. `RowDefinition.HeightProperty`에 `BeginAnimation`으로 적용. Task 5가 사용.

- [ ] **Step 1: 클래스 작성**

```csharp
using System.Windows;
using System.Windows.Media.Animation;

namespace DevezCode.Behaviors;

/// <summary>RowDefinition.Height(GridLength) 픽셀 보간 애니메이션 — WPF 기본 미제공이라 직접 구현.
/// Pixel 단위만 지원(Star/Auto 보간 불가). 하단 터미널 패널 슬라이드에 사용.</summary>
public sealed class GridLengthAnimation : AnimationTimeline
{
    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(
        nameof(From), typeof(GridLength), typeof(GridLengthAnimation));
    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(
        nameof(To), typeof(GridLength), typeof(GridLengthAnimation));

    public GridLength From { get => (GridLength)GetValue(FromProperty); set => SetValue(FromProperty, value); }
    public GridLength To { get => (GridLength)GetValue(ToProperty); set => SetValue(ToProperty, value); }
    public IEasingFunction? EasingFunction { get; set; }

    public override Type TargetPropertyType => typeof(GridLength);
    protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue,
                                           AnimationClock clock)
    {
        double from = From.Value, to = To.Value;
        double p = clock.CurrentProgress ?? 0;
        if (EasingFunction != null) p = EasingFunction.Ease(p);
        return new GridLength(from + (to - from) * p);
    }
}
```

- [ ] **Step 2: 빌드 확인**

Run: `dotnet build -c Release --nologo -v quiet`
Expected: 오류 0.

- [ ] **Step 3: 커밋**

```bash
git add Behaviors/GridLengthAnimation.cs
git commit -m "feat: GridLengthAnimation 헬퍼 (Row 높이 슬라이드용)"
```

---

### Task 4: XAML — CenterSplit Row 구조 + 패널 + 푸터 토글 버튼

**Files:**
- Modify: `MainWindow.xaml` (`CenterSplit` L165 부근, 푸터 `WakeControlBtn` L953 부근)

**Interfaces:**
- Produces: `ShellPanelRow`(RowDefinition), `ShellPanelSplitter`(GridSplitter), `ShellTerminalPanel`(Border), `ShellTerminal`(TerminalHostView), `ShellTerminalBtn`/`ShellTerminalBtnIcon`(푸터 버튼) — Task 5의 코드비하인드가 이름으로 참조. 이벤트 핸들러 시그니처: `ShellTerminalBtn_Click(object, RoutedEventArgs)`, `ShellPanelSplitter_DragCompleted(object, DragCompletedEventArgs)` (Task 5에서 구현 — XAML 과 코드는 같은 커밋으로 묶지 않으면 빌드가 깨지므로 **Task 4는 XAML 편집까지만 하고 빌드/커밋은 Task 5와 함께** 한다).

- [ ] **Step 1: CenterSplit에 Row 구조 추가**

`MainWindow.xaml`의 `CenterSplit` Grid(L165)에 RowDefinitions를 추가하고, 기존 자식들(PaneA/PaneSplitter/PaneB/WakeTerminalHost)은 그대로(암묵 Row 0) 두고 스플리터·패널을 추가한다. 결과 형태:

```xml
                <Grid Grid.Column="2" x:Name="CenterSplit">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="*" MinHeight="220"/>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition x:Name="ShellPanelRow" Height="0"/>
                    </Grid.RowDefinitions>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition x:Name="PaneACol" Width="*"/>
                        <ColumnDefinition x:Name="PaneSplitterCol" Width="0"/>
                        <ColumnDefinition x:Name="PaneBCol" Width="0"/>
                    </Grid.ColumnDefinitions>
                    <views:WorkspacePaneView x:Name="PaneA" Grid.Column="0"/>
                    <GridSplitter x:Name="PaneSplitter" Grid.Column="1" Width="4"
                                  HorizontalAlignment="Stretch" Visibility="Collapsed"
                                  ResizeDirection="Columns" ResizeBehavior="PreviousAndNext"
                                  DragDelta="PaneSplitter_DragDelta"
                                  DragCompleted="PaneSplitter_DragCompleted"/>
                    <views:WorkspacePaneView x:Name="PaneB" Grid.Column="2" Visibility="Collapsed"/>
                <!-- 화면에 보이지 않는 깨우기 전용 터미널 호스트. 세션을 활성화하거나 포커스하지 않는다. -->
                <Grid x:Name="WakeTerminalHost" Width="1" Height="1" Margin="-100,-100,0,0"
                      IsHitTestVisible="False" Opacity="0" Panel.ZIndex="-1">
                    <views:TerminalHostView x:Name="WakeTerminal" Width="1" Height="1"/>
                </Grid>

                    <!-- 하단 터미널 패널(에이전트 미연결 pwsh). 푸터 ShellTerminalBtn 토글, 닫힘 = Row 0 + Collapsed(HwndHost 규칙). -->
                    <GridSplitter x:Name="ShellPanelSplitter" Grid.Row="1" Grid.ColumnSpan="3" Height="4"
                                  HorizontalAlignment="Stretch" Visibility="Collapsed"
                                  ResizeDirection="Rows" ResizeBehavior="PreviousAndNext"
                                  DragCompleted="ShellPanelSplitter_DragCompleted"/>
                    <Border x:Name="ShellTerminalPanel" Grid.Row="2" Grid.ColumnSpan="3" Visibility="Collapsed"
                            Background="{DynamicResource BgBrush}"
                            BorderBrush="{DynamicResource LineBrush}" BorderThickness="0,1,0,0">
                        <views:TerminalHostView x:Name="ShellTerminal"/>
                    </Border>
                </Grid>
```

(주의: 기존 자식 4개의 XML 은 들여쓰기 포함 무변 — RowDefinitions 블록과 마지막 두 요소만 추가.)

- [ ] **Step 2: 푸터 토글 버튼 추가**

`WakeControlBtn`(L953) 바로 **앞**에:

```xml
                        <Button x:Name="ShellTerminalBtn" Style="{StaticResource WinCtrlBtn}" Width="30"
                                Click="ShellTerminalBtn_Click" ToolTip="터미널" Margin="0,0,2,0">
                            <Path x:Name="ShellTerminalBtnIcon" Style="{StaticResource LucideIcon}"
                                  Data="{StaticResource IconTerminal}"
                                  Width="15" Height="15" Stroke="{DynamicResource TextMutedBrush}"/>
                        </Button>
```

- [ ] **Step 3: 빌드/커밋은 하지 않는다**

핸들러(`ShellTerminalBtn_Click` 등)가 아직 없어 XAML 컴파일이 실패한다. Task 5에서 함께 빌드·커밋.

---

### Task 5: MainWindow 코드비하인드 — 토글·애니메이션·생명주기

**Files:**
- Modify: `MainWindow.xaml.cs`

**Interfaces:**
- Consumes: Task 1의 `"shell"` 에이전트, Task 2의 `Load/SaveShellTerminalHeight`, Task 3의 `GridLengthAnimation`, Task 4의 XAML 이름들. 기존: `FreezeWorkspaceTerminalsAsync()` / `UnfreezeWorkspaceTerminals()` (L2086/2090), `TerminalHostView.ShowTerminal/CloseTerminal/SessionExited/Dispose`, `TerminalSessionManager.DisposeRoom/ClearDisposedRoom`, `SettingsService.SaveAgentForRoom`.

- [ ] **Step 1: 필드 + 토글 로직 추가**

`MainWindow.xaml.cs`의 `RunPanelToggleCovered`(L2102) 아래에 추가:

```csharp
    // ── 하단 터미널 패널 (에이전트 미연결 pwsh) ─────────────────────────
    private const string ShellRoomId = "devezcode-shell-terminal";
    private bool _shellPanelOpen;
    private bool _shellPanelBusy; // 토글 연타/exit 경합 무시

    private async void ShellTerminalBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_shellPanelBusy) return;
        _shellPanelBusy = true;
        try { await ToggleShellPanelAsync(!_shellPanelOpen); }
        catch (Exception ex) { DiagLog.Write($"shell panel toggle failed: {ex.Message}"); }
        finally { _shellPanelBusy = false; }
    }

    /// <summary>하단 터미널 패널 슬라이드 토글. 워크스페이스 터미널은 webCover 로 정지(리사이즈 경로 —
    /// .knowledge/webview2-airspace 문서), 패널 자신은 지연 생성(첫 열기에 ShowTerminal).</summary>
    private async Task ToggleShellPanelAsync(bool open)
    {
        _shellPanelOpen = open;
        await FreezeWorkspaceTerminalsAsync();
        if (open)
        {
            SettingsService.SaveAgentForRoom(ShellRoomId, "shell"); // LaunchSession 의 shell 분기로 라우팅
            ShellPanelRow.MinHeight = 0; // 애니 시작점(0) 이 MinHeight 에 클램프되지 않게
            ShellTerminalPanel.Visibility = Visibility.Visible;
            ShellPanelSplitter.Visibility = Visibility.Visible;
            ShellTerminal.ShowTerminal(ShellRoomId); // 살아있으면 재사용, 없으면 새 pwsh (지연 생성+유지)
        }
        double target = open ? SettingsService.LoadShellTerminalHeight() : 0;
        await AnimateShellPanelRowAsync(target, TimeSpan.FromMilliseconds(180));
        if (open) ShellPanelRow.MinHeight = 120; // 스플리터 드래그 하한
        else
        {
            ShellPanelRow.MinHeight = 0;
            ShellTerminalPanel.Visibility = Visibility.Collapsed; // HwndHost 는 Collapsed 로만 숨김
            ShellPanelSplitter.Visibility = Visibility.Collapsed;
        }
        UpdateLayout(); // webCover resume 전 최종 폭 확정 (expectWidth 정확성)
        UnfreezeWorkspaceTerminals();
        UpdateShellToggleVisual();
    }

    private Task AnimateShellPanelRowAsync(double to, TimeSpan duration)
    {
        var tcs = new TaskCompletionSource();
        var anim = new Behaviors.GridLengthAnimation
        {
            From = new GridLength(ShellPanelRow.ActualHeight),
            To = new GridLength(to),
            Duration = new Duration(duration),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) =>
        {
            // 애니메이션 값 지배 해제 후 로컬 값으로 확정 (이후 스플리터 드래그가 동작하도록)
            ShellPanelRow.BeginAnimation(RowDefinition.HeightProperty, null);
            ShellPanelRow.Height = new GridLength(to);
            tcs.TrySetResult();
        };
        ShellPanelRow.BeginAnimation(RowDefinition.HeightProperty, anim);
        return tcs.Task;
    }

    /// <summary>토글 버튼 아이콘 색 — 열림 = PrimaryBrush, 닫힘 = TextMutedBrush (테마 추종).</summary>
    private void UpdateShellToggleVisual()
        => ShellTerminalBtnIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,
            _shellPanelOpen ? "PrimaryBrush" : "TextMutedBrush");

    /// <summary>스플리터 드래그 끝 → 새 높이 저장 (재실행 시 복원).</summary>
    private void ShellPanelSplitter_DragCompleted(object sender,
        System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (e.Canceled || !_shellPanelOpen) return;
        SettingsService.SaveShellTerminalHeight(ShellPanelRow.ActualHeight);
    }
```

- [ ] **Step 2: 셸 exit 처리 (SessionExited 구독)**

MainWindow 생성자에서 서비스 wiring 이 모여 있는 곳(`WakeTerminal` 관련 초기화 부근, grep 앵커: `_wakeScheduler`)에 추가:

```csharp
        // 하단 터미널 패널: 셸이 exit 로 끝나면 패널을 닫고 방을 정리 — 다음 토글에 새 pwsh.
        ShellTerminal.SessionExited += id =>
        {
            if (!string.Equals(id, ShellRoomId, StringComparison.Ordinal)) return;
            Dispatcher.BeginInvoke(async () =>
            {
                ShellTerminal.CloseTerminal(ShellRoomId);
                TerminalSessionManager.Instance.DisposeRoom(ShellRoomId, purgeTracking: false);
                TerminalSessionManager.Instance.ClearDisposedRoom(ShellRoomId); // tombstone 해제 → 재생성 허용
                if (_shellPanelOpen && !_shellPanelBusy)
                {
                    _shellPanelBusy = true;
                    try { await ToggleShellPanelAsync(false); }
                    catch (Exception ex) { DiagLog.Write($"shell panel close-on-exit failed: {ex.Message}"); }
                    finally { _shellPanelBusy = false; }
                }
            });
        };
```

- [ ] **Step 3: 앱 종료 정리**

`Closed` 핸들러의 `WakeTerminal.Dispose();`(L741) 다음에 추가:

```csharp
            ShellTerminal.Dispose();
            TerminalSessionManager.Instance.DisposeRoom(ShellRoomId, purgeTracking: false);
```

- [ ] **Step 4: 빌드 확인 (Task 4 XAML 포함)**

Run: `dotnet build -c Release --nologo -v quiet`
Expected: 오류 0.

- [ ] **Step 5: 커밋 (Task 4 + 5 묶음)**

```bash
git add MainWindow.xaml MainWindow.xaml.cs
git commit -m "feat: 하단 슬라이드 터미널 패널 — 푸터 토글·webCover 프리즈·지연 생성"
```

---

### Task 6: 실행 검증 + 푸시

**Files:** 없음 (수동 검증).

- [ ] **Step 1: 앱 재시작 (CLAUDE.md 프로세스 준수)**

먼저 본 세션이 DevezCode 내부에서 실행 중인지 확인한다(내부라면 앱 재시작이 세션을 끊으므로 **사용자에게 직접 재시작·확인을 요청**하고 이 태스크를 수동 안내로 전환). 외부라면:

```powershell
$process = Get-Process -Name DevezCode -ErrorAction SilentlyContinue
if ($process) {
    $process.CloseMainWindow() | Out-Null
    if (-not $process.WaitForExit(30000)) {
        throw "DevezCode가 정상 종료되지 않아 빌드를 중단합니다. 강제 종료하지 마십시오."
    }
}
dotnet build -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Release 빌드가 실패했습니다." }
Start-Process "bin\DevezCode.exe"
```

- [ ] **Step 2: 수동 검증 (스펙 테스트 시나리오)**

1. 푸터 터미널 버튼 → 패널 슬라이드 업, pwsh 프롬프트(홈 폴더), `ls` 등 명령 정상.
2. 닫기 → 재열기: 이전 출력·상태 유지 (예: 변수 설정 후 확인).
3. 패널 열어둔 채: 새 세션 생성 / 세션 종료 / 테마 변경 / 앱 정상 종료 — 전부 정상 (충돌 회귀 검증).
4. 슬라이드 중 기존 세션 터미널 무플래시 (webCover 크로스페이드).
5. 셸에서 `exit` → 패널 자동 닫힘 → 재토글 시 새 pwsh.
6. 스플리터로 높이 조절 → 앱 재시작 후 높이 유지 + 패널은 닫힌 상태로 시작.
7. 한글 IME: 패널 셸 입력란에서 조합 중 블록커서 깜빡임 여부 확인(에이전트추가규칙 §11-2). 깜빡이면(B형) `terminal.html`의 `pinsLogicalCaret`에 `'shell'` 추가 후 재검증 — 후속 커밋.

실패 항목이 있으면 superpowers:systematic-debugging 으로 원인을 잡고 수정 후 재검증.

- [ ] **Step 3: 푸시**

```bash
git push
```

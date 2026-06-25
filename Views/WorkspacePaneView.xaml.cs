using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DevezCode.Models;
using DevezCode.Services;
using DevezCode.Services.Terminal;

namespace DevezCode.Views;

/// <summary>
/// 중앙 워크스페이스 패널(메타바 + 탭 + 세션 헤더 + 터미널/에디터 콘텐츠).
/// 자체 TerminalHostView·활성 프로젝트/세션/탭 상태를 갖는 독립 단위라
/// MainWindow 가 두 개를 나란히 두면 두 프로젝트를 동시에 열 수 있다.
/// 사이드바·파일탐색기·푸터·훅 서비스는 MainWindow(셸)가 소유하고 이벤트로 연동한다.
/// </summary>
public partial class WorkspacePaneView : UserControl
{
    private readonly TerminalHostView _terminal = new();

    private ProjectItem? _activeProject;
    private SessionItem? _activeSession;
    private TabItemBase? _activeTab;

    public ProjectItem? ActiveProject => _activeProject;
    public SessionItem? ActiveSession => _activeSession;
    public TabItemBase? ActiveTab => _activeTab;

    /// <summary>MainWindow 가 소유한 공유 프로젝트 컬렉션. 생성 후 한 번 주입한다.</summary>
    public ObservableCollection<ProjectItem> Projects { get; set; } = new();
    /// <summary>보관함 프로젝트 — 세션/부모 조회 시 활성과 함께 검색해 보관 프로젝트도 정상 실행되게 한다.</summary>
    public ObservableCollection<ProjectItem>? ArchivedProjects { get; set; }
    /// <summary>활성 + 보관 전체 — ParentOf/FindSession 조회용.</summary>
    private IEnumerable<ProjectItem> AllProjects => ArchivedProjects == null ? Projects : Projects.Concat(ArchivedProjects);
    /// <summary>statusLine 훅이 보고한 방별 실제 model/effort 조회용(공유 서비스).</summary>
    public ModelEffortService? ModelEffort { get; set; }
    /// <summary>비-claude 에이전트 last prompt 추적용(공유 서비스).</summary>
    public AgentLastMessageService? AgentLastMsg { get; set; }

    /// <summary>사용자가 이 패널을 클릭/조작 → 포커스 패널로 지정 요청.</summary>
    public event Action<WorkspacePaneView>? FocusRequested;
    /// <summary>활성 프로젝트/세션/탭이 바뀜 → 셸이 파일탐색기·사이드바 하이라이트·last-active 저장을 갱신.</summary>
    public event Action<WorkspacePaneView>? ActiveChanged;
    /// <summary>분할 토글 버튼 클릭 → 셸이 분할/해제 처리.</summary>
    public event Action<WorkspacePaneView>? SplitToggleRequested;

    public TerminalHostView Terminal => _terminal;

    public WorkspacePaneView()
    {
        InitializeComponent();
        TerminalHostContainer.Content = _terminal;

        TabsHost.PreviewMouseMove += TabsHost_PreviewMouseMove;
        TabsHost.PreviewMouseLeftButtonUp += async (_, _) => await EndTabDragAsync();
        TabsHost.LostMouseCapture += async (_, _) => await EndTabDragAsync();

        _terminal.SessionStarted += id => { var s = FindSession(id); if (s != null) s.IsAlive = true; };
        _terminal.SessionExited += id => { var s = FindSession(id); if (s != null) { s.IsAlive = false; s.IsBusy = false; } HideSessionLoadingIf(id); };
        _terminal.TerminalReady += id => HideSessionLoadingIf(id);
        // 단독 ESC 취소 → busy 스피너 즉시 해제(agent 가 idle 신호를 안 줘도 무한 스피너 방지).
        _terminal.InterruptRequested += id => { var s = FindSession(id); if (s != null) s.IsBusy = false; };
        _terminal.SessionActionRequested += OnTerminalSessionAction;
        _terminal.UserInteracted += () => FocusRequested?.Invoke(this);
        // 세션 헤더 타이틀(마지막 메시지) 폰트를 터미널 폰트 크기와 동기화.
        _terminal.FontSizePxChanged += ApplyHeaderFontSize;
        Loaded += (_, _) => ApplyHeaderFontSize(_terminal.EffectiveFontSizePx);

        App.ThemeChanged += OnThemeChanged_UpdateSeam;
        Unloaded += (_, _) => App.ThemeChanged -= OnThemeChanged_UpdateSeam;

        ApplyProjectInfoHeaderVisibility();
    }

    /// <summary>설정(프로젝트 정보 헤더 숨기기) + 프로젝트 선택 여부에 따라 메타바(MetaBar) 표시 반영.
    /// 프로젝트가 선택되지 않은 빈 패널이면 설정과 무관하게 항상 숨긴다(#·경로 아이콘 제거).</summary>
    public void ApplyProjectInfoHeaderVisibility()
    {
        if (MetaBar != null)
            MetaBar.Visibility = (_activeProject != null && !SettingsService.LoadHideProjectInfoHeader())
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Pane_PreviewInteract(object sender, MouseButtonEventArgs e) => FocusRequested?.Invoke(this);

    private void SplitBtn_Click(object sender, RoutedEventArgs e) => SplitToggleRequested?.Invoke(this);

    /// <summary>분할 상태에 맞춰 분할 토글 아이콘 강조 색을 갱신.</summary>
    public void SetSplitActive(bool active)
    {
        var key = active ? "PrimaryBrush" : "TextMutedBrush";
        if (SplitIcon != null) SplitIcon.Stroke = (Brush)FindResource(key);
    }

    /// <summary>분할 중 포커스 패널 상단 액센트 표시 여부. 좌/우 패널 전환 시 가볍게 슬라이드한다.</summary>
    public void SetFocusedVisual(bool focused, double slideFromX = 0)
    {
        if (FocusAccent == null) return;

        var transform = FocusAccent.RenderTransform as TranslateTransform;
        if (focused)
        {
            FocusAccent.Visibility = Visibility.Visible;
            if (transform != null)
            {
                transform.BeginAnimation(TranslateTransform.XProperty, null);
                transform.X = slideFromX;
                transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
                {
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(160),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }

            FocusAccent.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(120),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
        else
        {
            if (transform != null) transform.BeginAnimation(TranslateTransform.XProperty, null);
            var fade = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(90),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            fade.Completed += (_, _) =>
            {
                if (FocusAccent.Opacity <= 0.01) FocusAccent.Visibility = Visibility.Collapsed;
            };
            FocusAccent.BeginAnimation(OpacityProperty, fade);
        }
    }

    /// <summary>세션 소유권 이전 — 이 패널이 해당 세션을 활성으로 들고 있으면 배선을 끊고 다음 세션으로(없으면 비움).</summary>
    public void ReleaseSessionIfActive(SessionItem s)
    {
        if (!ReferenceEquals(_activeSession, s)) return;
        try { _terminal.CloseTerminal(s.Id); } catch { /* ignore */ }
        var parent = ParentOf(s);
        var next = parent?.Tabs.OfType<SessionItem>().FirstOrDefault(x => !ReferenceEquals(x, s) && !x.Hidden);
        if (next != null) ActivateSession(next);
        else ClearActiveSession();
    }

    /// <summary>분할 해제 시 — 이 패널이 보여주던 세션의 xterm 배선을 끊고 상태를 비운다(ConPTY·기록 보존).</summary>
    public void ClearForHide()
    {
        if (_activeSession != null)
            try { _terminal.CloseTerminal(_activeSession.Id); } catch { /* ignore */ }
        _activeProject = null;
        ClearActiveSession();
        TabsHost.ItemsSource = null;
    }

    // ── 프로젝트 ─────────────────────────────────────────────────
    /// <summary>활성 프로젝트 전환 — 중앙 탭을 그 프로젝트의 탭들로 교체. 세션 활성화는 안 함.</summary>
    private void SetActiveProject(ProjectItem proj)
    {
        _activeProject = proj;
        TabsHost.ItemsSource = proj.Tabs;
        if (ProjectPathText != null) { ProjectPathText.Text = proj.Path; ProjectPathText.ToolTip = proj.Path; }
        if (ProjectNameText != null) { ProjectNameText.Text = proj.Name; ProjectNameText.ToolTip = proj.Name; }
        UpdateProjectBranchBubble(proj);
        if (SettingsService.LoadPreloadAllProjectSessions())
            PreloadProjectSessions(proj, except: null);
    }

    private System.Threading.CancellationTokenSource? _projectCts;

    private void UpdateProjectBranchBubble(ProjectItem? proj)
    {
        if (proj == null || string.IsNullOrEmpty(proj.Path) || BranchGroup == null)
        {
            if (BranchGroup != null) BranchGroup.Visibility = Visibility.Collapsed;
            return;
        }
        _projectCts?.Cancel();
        _projectCts = new System.Threading.CancellationTokenSource();
        _ = LoadBranchAsync(proj.Path, _projectCts.Token);
    }

    private async Task LoadBranchAsync(string repoDir, System.Threading.CancellationToken ct)
    {
        string? branch = null;
        try
        {
            if (await GitService.IsRepoAsync(repoDir))
            {
                var r = await GitService.RunAsync(repoDir, "rev-parse", "--abbrev-ref", "HEAD");
                if (r.Ok)
                {
                    var name = r.Output.Trim();
                    if (!string.IsNullOrEmpty(name) && name != "HEAD") branch = name;
                }
            }
        }
        catch { /* git 미설치 등 */ }
        if (ct.IsCancellationRequested) return;
        await Dispatcher.InvokeAsync(() =>
        {
            BranchGroup.Visibility = branch != null ? Visibility.Visible : Visibility.Collapsed;
            if (branch != null) ProjectBranchText.Text = branch;
        });
    }

    /// <summary>프로젝트 선택 — 탭 교체 후 세션 하나 활성화.
    /// 우선순위: 이전 활성 세션 → 열려있는(실행 중) 세션 중 가장 위 → 첫 세션.
    /// (첫 세션이 안 열렸고 다른 세션만 열려있으면 그 열린 세션으로 연다.)</summary>
    public void SelectProject(ProjectItem proj)
    {
        SetActiveProject(proj);

        // 이전 활성 세션이 이 프로젝트 소속이면 그대로 유지.
        if (_activeSession != null && proj.Tabs.Contains(_activeSession) && !_activeSession.Hidden)
        {
            ActivateSession(_activeSession, unHide: false);
            return;
        }

        // 이 프로젝트에서 마지막으로 봤던 탭(세션/파일)을 복원 시도.
        if (TryActivateLastTab(proj)) return;

        // 폴백: 실행 중 세션 중 가장 위 → 첫 세션 → 없으면 비움.
        var sessions = proj.Tabs.OfType<SessionItem>().Where(s => !s.Hidden).ToList();
        var target = sessions.FirstOrDefault(s => s.IsAlive) ?? sessions.FirstOrDefault();
        if (target != null) ActivateSession(target, unHide: false);
        else ClearActiveSession();
        ActiveChanged?.Invoke(this);
    }

    /// <summary>proj.LastActiveTabRef("S:&lt;id&gt;"/"F:&lt;path&gt;")가 가리키는 탭을 찾아 활성화. 성공 시 true.
    /// 세션은 숨김 제외, 파일은 현재 열린 탭 중에서 찾는다(못 찾으면 false → 기본 폴백).</summary>
    private bool TryActivateLastTab(ProjectItem proj)
    {
        var rf = proj.LastActiveTabRef;
        if (string.IsNullOrEmpty(rf) || rf.Length < 2 || rf[1] != ':') return false;
        var key = rf[2..];
        switch (rf[0])
        {
            case 'S':
                var sess = proj.Tabs.OfType<SessionItem>().FirstOrDefault(s => !s.Hidden && s.Id == key);
                if (sess == null) return false;
                ActivateSession(sess, unHide: false);
                return true;
            case 'F':
                var file = proj.Tabs.OfType<FileTabItem>()
                    .FirstOrDefault(f => string.Equals(f.FilePath, key, StringComparison.OrdinalIgnoreCase));
                if (file == null) return false;
                ActivateFileTab(file);
                return true;
            default:
                return false;
        }
    }

    /// <summary>프로젝트의 "마지막 활성 탭" 참조를 갱신하고, 바뀐 경우에만 workspace.json 에 영속.</summary>
    private void RecordActiveTab(ProjectItem proj, string tabRef)
    {
        if (proj.LastActiveTabRef == tabRef) return;
        proj.LastActiveTabRef = tabRef;
        WorkspaceStore.Save(Projects);
    }

    private void PreloadProjectSessions(ProjectItem proj, SessionItem? except)
    {
        foreach (var s in proj.Tabs.OfType<SessionItem>())
        {
            if (ReferenceEquals(s, except) || s.Hidden) continue;
            SettingsService.SaveClaudeCodeRoomDir(s.Id, proj.Path);
            _terminal.PreloadTerminal(s.Id);
        }
    }

    /// <summary>활성 프로젝트가 지정된 프로젝트면 비우고, next 가 있으면 그 프로젝트를 연다.</summary>
    public void OnProjectRemoved(ProjectItem proj, ProjectItem? next)
    {
        if (!ReferenceEquals(_activeProject, proj)) return;
        _activeProject = null; _activeSession = null; _activeTab = null;
        if (next != null) SelectProject(next);
        else { TabsHost.ItemsSource = null; ClearActiveSession(); ActiveChanged?.Invoke(this); }
    }

    // ── 세션 ─────────────────────────────────────────────────────
    private void OnTerminalSessionAction(string name, int index) => Dispatcher.BeginInvoke(() =>
    {
        switch (name)
        {
            case "newSession": if (_activeProject != null) AddSession(_activeProject); break;
            case "closeSession": if (_activeSession != null) StopTrackingSession(_activeSession); break;
            case "nextSession": CycleSession(+1); break;
            case "prevSession": CycleSession(-1); break;
            case "gotoSession": GotoSession(index); break;
        }
    });

    private void GotoSession(int index)
    {
        var sessionTabs = _activeProject?.Tabs.OfType<SessionItem>().ToList();
        if (sessionTabs == null || sessionTabs.Count == 0) return;
        int i = index < 0 ? sessionTabs.Count - 1 : index;
        if (i < 0 || i >= sessionTabs.Count) return;
        OpenSession(sessionTabs[i]);
    }

    /// <summary>전역 단축키(한자+방향키)용 — 활성 세션 탭을 이전/다음으로 이동.</summary>
    public void CycleActiveSession(bool next) => CycleSession(next ? +1 : -1);

    private void CycleSession(int dir)
    {
        var sessionTabs = _activeProject?.Tabs.OfType<SessionItem>().ToList();
        if (_activeProject == null || _activeSession == null || sessionTabs == null || sessionTabs.Count < 2) return;
        int idx = sessionTabs.IndexOf(_activeSession);
        if (idx < 0) return;
        int n = sessionTabs.Count;
        OpenSession(sessionTabs[((idx + dir) % n + n) % n]);
    }

    private static string NextSessionName(ProjectItem proj)
    {
        int max = 0;
        var rx = new System.Text.RegularExpressions.Regex(@"^세션\s+(\d+)$");
        foreach (var s in proj.Tabs.OfType<SessionItem>())
        {
            var m = rx.Match(s.Name);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > max) max = n;
        }
        return $"세션 {max + 1}";
    }

    public void AddSession(ProjectItem proj)
    {
        var available = AgentRegistry.GetEnabledAndInstalled();
        if (available.Count == 0)
        {
            ConfirmDialog.Alert("에이전트 없음",
                "사용 가능한 에이전트가 없습니다.\n설정 → 에이전트 에서 하나 이상 활성화해 주세요.");
            return;
        }
        string agentId;
        if (available.Count == 1) agentId = available[0].Id;
        else
        {
            var picked = AgentPickerDialog.Pick(Window.GetWindow(this), available, proj.Path);
            if (picked == null) return;
            agentId = picked;
        }

        var session = new SessionItem { Name = NextSessionName(proj), AgentId = agentId };
        proj.Tabs.Add(session);
        proj.IsExpanded = true;
        SettingsService.SaveClaudeCodeRoomDir(session.Id, proj.Path);
        SettingsService.SaveAgentForRoom(session.Id, agentId);
        WorkspaceStore.Save(Projects);
        if (ReferenceEquals(_activeProject, proj)) OpenSession(session);
    }

    /// <summary>세션 클릭 — 필요하면 프로젝트 전환 후 해당 세션 활성화.</summary>
    public void OpenSession(SessionItem session)
    {
        var parent = ParentOf(session);
        if (parent == null) return;
        if (!ReferenceEquals(_activeProject, parent)) SetActiveProject(parent);
        ActivateSession(session);
    }

    private void ActivateSession(SessionItem session, bool unHide = true)
    {
        var parent = ParentOf(session);
        if (parent == null) return;

        if (unHide && session.Hidden) { session.Hidden = false; WorkspaceStore.Save(Projects); }
        // 접혀 있던 프로젝트의 세션이 선택되면 자동으로 펼쳐서 보이게 한다.
        if (!parent.IsExpanded) parent.IsExpanded = true;
        SettingsService.SaveClaudeCodeRoomDir(session.Id, parent.Path);

        _activeTab = session;
        _activeSession = session;
        RecordActiveTab(parent, "S:" + session.Id);
        foreach (var t in parent.Tabs) t.IsSelected = ReferenceEquals(t, session);

        session.IsAlive = true;
        var sessionAgentId = string.IsNullOrEmpty(session.AgentId) ? AgentRegistry.DefaultAgentId : session.AgentId;
        if (sessionAgentId != "claude")
            AgentLastMsg?.TrackSession(parent.Path, sessionAgentId);
        if (_terminal.IsReady(session.Id)) HideSessionLoading();
        else ShowSessionLoading(session.Id);
        _terminal.ShowTerminal(session.Id);
        _terminal.FocusTerminal();
        UpdateEmptyState();
        EnsureSelectedTabVisible(session);
        RefreshModelEffortDock();
        ActiveChanged?.Invoke(this);
    }

    /// <summary>작업 큐 → 활성 세션 터미널에 텍스트 입력 + Enter. 비활성/죽은 세션이면 false.</summary>
    public bool SendTextToActiveSession(string text)
    {
        var id = _activeSession?.Id;
        if (string.IsNullOrEmpty(id)) return false;
        var session = TerminalSessionManager.Instance.Get(id);
        if (session is not { IsAlive: true }) return false;
        session.Write(text);
        session.Write("\r");
        _terminal.ShowTerminal(id);
        _terminal.FocusTerminal();
        return true;
    }

    /// <summary>활성 세션 터미널에 텍스트만 삽입(엔터 없음). 외부 드래그로 경로 입력 시 사용 — 사용자가 확인 후 직접 Enter.</summary>
    private bool WriteToActiveSessionNoEnter(string text)
    {
        var id = _activeSession?.Id;
        if (string.IsNullOrEmpty(id)) return false;
        var session = TerminalSessionManager.Instance.Get(id);
        if (session is not { IsAlive: true }) return false;
        session.Write(text);
        _terminal.ShowTerminal(id);
        _terminal.FocusTerminal();
        return true;
    }

    // ── 외부 드래그 앤 드롭 (탐색기/이미지 등 → 활성 세션 터미널로 경로 입력) ──────────
    // 터미널 호스트(TerminalHostView)의 WebView2 외부 드롭은 비활성화되어 있어
    // WPF DragDrop 시스템이 TerminalHostContainer 의 Drop 이벤트로 라우팅된다.

    /// <summary>드롭 허용 확장자. 이미지(에이전트가 직접 읽을 수 있는 포맷) + 일반 텍스트(소스/문서/설정).
    /// 바이너리(.exe, .zip 등)는 토큰으로 의미가 없어 제외.</summary>
    private static readonly HashSet<string> DropExts = new(StringComparer.OrdinalIgnoreCase)
    {
        // 이미지
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".avif", ".tif", ".tiff",
        // 텍스트/소스/문서
        ".txt", ".md", ".markdown", ".rst", ".adoc",
        ".json", ".jsonc", ".yaml", ".yml", ".toml", ".ini", ".conf", ".config", ".env", ".editorconfig", ".props", ".targets",
        ".xml", ".html", ".htm", ".css", ".scss", ".sass", ".less", ".vue", ".svelte", ".razor", ".cshtml",
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".java", ".kt", ".kts", ".swift", ".go", ".rs",
        ".c", ".h", ".cpp", ".hpp", ".cc", ".cxx", ".py", ".rb", ".php", ".lua", ".dart", ".fs", ".fsi",
        ".vb", ".sql", ".sh", ".bash", ".zsh", ".fish", ".ps1", ".psm1", ".bat", ".cmd",
        ".gitignore", ".gitattributes", ".gitmodules", ".dockerignore",
    };

    private void TerminalHostContainer_DragOver(object sender, DragEventArgs e)
    {
        // 활성 세션이 살아있고, 파일 드롭 중이며, 그 중 허용 확장자가 하나라도 있을 때만 Copy 커서.
        if (_activeSession == null) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (files == null || files.Length == 0 || !files.Any(IsDroppableFile))
        { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void TerminalHostContainer_Drop(object sender, DragEventArgs e)
    {
        if (_activeSession == null) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (files == null || files.Length == 0) return;

        // 허용 확장자만, 각 줄에 "@경로" 형식으로. 클로드/codex 등은 @경로 를 직접 읽어 첨부.
        var accepted = files.Where(IsDroppableFile)
                            .Select(p => "@" + p)
                            .ToArray();
        if (accepted.Length == 0) return;

        var text = string.Join("\r", accepted) + "\r";
        WriteToActiveSessionNoEnter(text);
        e.Handled = true;
    }

    private static bool IsDroppableFile(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            var name = Path.GetFileName(path);
            if (DropExts.Contains(name)) return true; // .gitignore, Dockerfile 등 확장자 없는 파일
            var ext = Path.GetExtension(path);
            return !string.IsNullOrEmpty(ext) && DropExts.Contains(ext);
        }
        catch { return false; }
    }

    private void ActivateFileTab(FileTabItem tab)
    {
        var parent = ParentOfTab(tab);
        if (parent == null) return;

        _activeTab = tab;
        _activeSession = null;
        RecordActiveTab(parent, "F:" + tab.FilePath);
        foreach (var t in parent.Tabs) t.IsSelected = ReferenceEquals(t, tab);

        HideSessionLoading();
        if (!ReferenceEquals(FileEditorHostContainer.Content, tab.Editor.AsControl()))
            FileEditorHostContainer.Content = tab.Editor.AsControl();
        UpdateEmptyState();
        EnsureSelectedTabVisible(tab);
        tab.Editor.Focus();
        RefreshModelEffortDock();
        ActiveChanged?.Invoke(this);
    }

    // ── 메타바 model/effort dock ─────────────────────────────────
    private static readonly ModelEffortOption[] ModelOptions =
    {
        new("Opus 4.8", "opus"), new("Sonnet 4.6", "sonnet"),
        new("Haiku 4.5", "haiku"), new("Fable 5", "fable"),
    };
    private static readonly ModelEffortOption[] EffortOptions =
    {
        new("low", "low"), new("medium", "medium"), new("high", "high"),
        new("xhigh", "xhigh"), new("max", "max"),
    };
    private const string DefaultModelValue = "opus";
    private const string DefaultEffortValue = "high";

    private bool _suppressModelEffort;
    private readonly Dictionary<string, string> _pendingModel = new();
    private readonly Dictionary<string, string> _pendingEffort = new();

    private void RefreshModelEffortDock()
    {
        if (ModelEffortDock == null) return;
        var s = _activeSession;
        var agentId = s == null ? null : (string.IsNullOrEmpty(s.AgentId) ? AgentRegistry.DefaultAgentId : s.AgentId);
        bool isClaude = s != null && agentId == "claude";
        ModelEffortDock.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
        if (!isClaude) return;

        var (liveModelId, liveEffort) = ModelEffort?.Read(s!.Id) ?? (null, null);
        var model = ModelIdToValue(liveModelId) ?? SettingsService.LoadClaudeCodeRoomModel(s!.Id) ?? DefaultModelValue;
        var effort = (IsKnownEffort(liveEffort) ? liveEffort : null) ?? SettingsService.LoadClaudeCodeRoomEffort(s!.Id) ?? DefaultEffortValue;

        _suppressModelEffort = true;
        try
        {
            if (ModelCombo.ItemsSource == null) ModelCombo.ItemsSource = ModelOptions;
            if (EffortCombo.ItemsSource == null) EffortCombo.ItemsSource = EffortOptions;
            ModelCombo.SelectedValue = model;
            EffortCombo.SelectedValue = effort;
        }
        finally { _suppressModelEffort = false; }
    }

    private static string? ModelIdToValue(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var s = id.ToLowerInvariant();
        if (s.Contains("opus")) return "opus";
        if (s.Contains("sonnet")) return "sonnet";
        if (s.Contains("haiku")) return "haiku";
        if (s.Contains("fable") || s.Contains("mythos")) return "fable";
        return null;
    }

    private static bool IsKnownEffort(string? e)
        => e is "low" or "medium" or "high" or "xhigh" or "max";

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => OnModelEffortPicked(isModel: true);
    private void EffortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => OnModelEffortPicked(isModel: false);

    private void OnModelEffortPicked(bool isModel)
    {
        if (_suppressModelEffort) return;
        var s = _activeSession;
        if (s == null) return;
        var val = (isModel ? ModelCombo : EffortCombo).SelectedValue as string;
        if (string.IsNullOrEmpty(val)) return;

        if (isModel) SettingsService.SaveClaudeCodeRoomModel(s.Id, val);
        else SettingsService.SaveClaudeCodeRoomEffort(s.Id, val);

        if (s.IsBusy)
            (isModel ? _pendingModel : _pendingEffort)[s.Id] = val;
        else
            SendModelEffortSlash(s.Id, isModel, val);
    }

    /// <summary>응답 종료(busy→idle) 시 보류된 model/effort 변경을 라이브 주입. 셸이 호출.</summary>
    public void FlushPendingModelEffort(string roomId)
    {
        if (_pendingModel.Remove(roomId, out var m)) SendModelEffortSlash(roomId, isModel: true, m);
        if (_pendingEffort.Remove(roomId, out var ef)) SendModelEffortSlash(roomId, isModel: false, ef);
    }

    private void SendModelEffortSlash(string roomId, bool isModel, string value)
    {
        var sess = TerminalSessionManager.Instance.Get(roomId);
        if (sess is not { IsAlive: true }) return;
        _terminal.SuppressScroll(5);
        sess.Write((isModel ? "/model " : "/effort ") + value + "\r");
    }

    private void NewTabBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProject == null)
        {
            ConfirmDialog.Alert("프로젝트 없음", "먼저 왼쪽 사이드바에서 프로젝트를 추가하세요.");
            return;
        }
        AddSession(_activeProject);
    }

    // ── 세션 로딩 스피너 ─────────────────────────────────────────
    private string? _loadingRoomId;
    private System.Windows.Threading.DispatcherTimer? _loadingTimeout;

    private void ShowSessionLoading(string roomId)
    {
        _loadingRoomId = roomId;
        TerminalLoadingOverlay.Visibility = Visibility.Visible;
        _terminal.SetLoading(true);

        _loadingTimeout?.Stop();
        _loadingTimeout ??= new System.Windows.Threading.DispatcherTimer();
        _loadingTimeout.Interval = TimeSpan.FromSeconds(20);
        _loadingTimeout.Tick -= LoadingTimeout_Tick;
        _loadingTimeout.Tick += LoadingTimeout_Tick;
        _loadingTimeout.Start();
    }

    private void LoadingTimeout_Tick(object? sender, EventArgs e) => HideSessionLoading();

    private void HideSessionLoading()
    {
        _loadingTimeout?.Stop();
        _loadingRoomId = null;
        TerminalLoadingOverlay.Visibility = Visibility.Collapsed;
        _terminal.SetLoading(false);
    }

    private void HideSessionLoadingIf(string roomId)
    {
        if (_loadingRoomId == roomId) HideSessionLoading();
    }

    private void ClearActiveSession()
    {
        _activeSession = null;
        _activeTab = null;
        if (FileEditorHostContainer != null) FileEditorHostContainer.Content = null;
        HideSessionLoading();
        if (_activeProject != null)
            foreach (var t in _activeProject.Tabs) t.IsSelected = false;
        UpdateEmptyState();
        UpdateSelectedTabSeam();
        ActiveChanged?.Invoke(this);
    }

    public void RenameSession(SessionItem session)
    {
        var name = PromptDialog.Show("세션 이름 변경", "새 이름을 입력하세요.",
                                     defaultValue: session.Name, maxLength: 60);
        if (string.IsNullOrWhiteSpace(name) || name == session.Name) return;
        session.Name = name;
        WorkspaceStore.Save(Projects);
    }

    public void DeleteSession(SessionItem session)
    {
        if (!ConfirmDialog.Show("세션 삭제",
                $"'{session.Name}' 세션을 영구 삭제할까요?\n대화 기록(.jsonl)도 디스크에서 함께 삭제되며 복구할 수 없습니다.",
                okLabel: "삭제", danger: true))
            return;
        RemoveSession(session, purge: true);
    }

    public void StopTrackingSession(SessionItem session)
    {
        if (!ConfirmDialog.Show("세션 추적 중단",
                $"'{session.Name}' 세션을 목록에서 제거할까요?\n대화 기록은 디스크에 그대로 보존됩니다.",
                okLabel: "중단"))
            return;
        RemoveSession(session, purge: false);
    }

    private void RemoveSession(SessionItem session, bool purge)
    {
        var parent = ParentOf(session);
        bool wasActive = ReferenceEquals(_activeSession, session);
        int idx = parent?.Tabs.IndexOf(session) ?? -1;

        DisposeSessionProcess(session, purge);
        parent?.Tabs.Remove(session);
        WorkspaceStore.Save(Projects);

        if (wasActive)
        {
            var next = PickNeighborTab(parent, idx);
            if (next is SessionItem s) ActivateSession(s);
            else if (next is FileTabItem f) ActivateFileTab(f);
            else ClearActiveSession();
        }
    }

    /// <summary>닫힌 탭(원래 idx) 기준 왼쪽 우선, 없으면 오른쪽에서 표시 가능한 탭 선택.</summary>
    private TabItemBase? PickNeighborTab(ProjectItem? parent, int removedIdx)
    {
        if (parent == null || removedIdx < 0) return null;
        bool Visible(TabItemBase t) => !(t is SessionItem s && s.Hidden);
        for (int i = removedIdx - 1; i >= 0; i--)
            if (Visible(parent.Tabs[i])) return parent.Tabs[i];
        for (int i = removedIdx; i < parent.Tabs.Count; i++)
            if (Visible(parent.Tabs[i])) return parent.Tabs[i];
        return null;
    }

    private void RemoveFileTab(FileTabItem tab)
    {
        var parent = ParentOfTab(tab);
        bool wasActive = ReferenceEquals(_activeTab, tab);
        int idx = parent?.Tabs.IndexOf(tab) ?? -1;
        if (FileEditorHostContainer.Content == tab.Editor.AsControl())
            FileEditorHostContainer.Content = null;
        if (tab.Editor is IDisposable disposable) disposable.Dispose();
        parent?.Tabs.Remove(tab);
        PersistWorkspace(); // 닫은 파일 탭을 workspace.json 에서 제거(재시작 시 다시 안 열리도록)

        if (wasActive)
        {
            var next = PickNeighborTab(parent, idx);
            if (next is SessionItem s) ActivateSession(s);
            else if (next is FileTabItem f) ActivateFileTab(f);
            else ClearActiveSession();
        }
    }

    /// <summary>세션의 터미널 프로세스·매핑 정리(컬렉션은 건드리지 않음). 셸의 DeleteProject 도 호출.</summary>
    public void DisposeSessionProcess(SessionItem session, bool purge = true)
    {
        var workingDir = SettingsService.LoadClaudeCodeRoomDir(session.Id);
        try { _terminal.CloseTerminal(session.Id); } catch { /* ignore */ }
        try
        {
            if (purge) TerminalSessionManager.Instance.PurgeRoom(session.Id, workingDir);
            else TerminalSessionManager.Instance.DisposeRoom(session.Id, purgeTracking: false);
        }
        catch { /* ignore */ }
        if (purge) SettingsService.RemoveClaudeCodeRoomDir(session.Id);
    }

    /// <summary>활성 Claude 세션을 재시작 — MCP 매니저 저장 후 호출용. 비활성/비-Claude면 false.</summary>
    public bool TryRestartActiveClaudeSession()
    {
        if (_activeSession == null) return false;
        RestartAllClaudeSessions();
        return true;
    }

    private async void RestartAllClaudeSessions()
    {
        var allClaudeSessions = Projects
            .SelectMany(p => p.Tabs).OfType<SessionItem>()
            .Where(s =>
            {
                var aid = string.IsNullOrEmpty(s.AgentId) ? AgentRegistry.DefaultAgentId : s.AgentId;
                return aid == "claude";
            })
            .ToList();

        foreach (var s in allClaudeSessions)
        {
            try
            {
                _terminal.CloseTerminal(s.Id);
                DisposeSessionProcess(s, purge: false);
                TerminalSessionManager.Instance.ClearDisposedRoom(s.Id);
                TerminalSessionManager.Instance.GetOrCreate(s.Id, 120, 30);
            }
            catch { /* ignore */ }
        }

        await Task.Delay(150);

        if (_activeSession != null && allClaudeSessions.Contains(_activeSession))
            ActivateSession(_activeSession);
    }

    /// <summary>테마 변경 적용 — 이 패널 세션의 ConPTY 를 종료한 뒤 활성 세션을 다시 불러온다.</summary>
    public async void ReloadAllSessionsForTheme()
    {
        var active = _activeSession;
        var proj = _activeProject;

        foreach (var s in Projects.SelectMany(p => p.Tabs).OfType<SessionItem>().ToList())
        {
            try
            {
                DisposeSessionProcess(s, purge: false);
                TerminalSessionManager.Instance.ClearDisposedRoom(s.Id);
            }
            catch { /* ignore */ }
        }

        await Task.Delay(150);

        if (proj != null)
        {
            if (active != null && proj.Tabs.Contains(active))
                ActivateSession(active);
            if (SettingsService.LoadPreloadAllProjectSessions())
                PreloadProjectSessions(proj, except: active);
        }
    }

    private ProjectItem? ParentOf(SessionItem session)
        => AllProjects.FirstOrDefault(p => p.Tabs.Contains(session));

    private ProjectItem? ParentOfTab(TabItemBase tab)
        => AllProjects.FirstOrDefault(p => p.Tabs.Contains(tab));

    private SessionItem? FindSession(string id)
        => AllProjects.SelectMany(p => p.Tabs).OfType<SessionItem>().FirstOrDefault(s => s.Id == id);

    // ── 탭 이벤트 ─────────────────────────────────────────────────
    private void Tab_Click(object sender, MouseButtonEventArgs e)
    {
        if (_tabDidDrag) { _tabDidDrag = false; return; }
        if (sender is FrameworkElement { DataContext: TabItemBase tab })
        {
            if (tab is SessionItem s) OpenSession(s);
            else if (tab is FileTabItem f) ActivateFileTab(f);
        }
    }

    private void TabHide_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TabItemBase tab }) return;

        if (tab is SessionItem s)
        {
            s.Hidden = true;
            WorkspaceStore.Save(Projects);
            if (ReferenceEquals(_activeSession, s))
            {
                var parent = ParentOf(s);
                var next = parent?.Tabs.OfType<SessionItem>().FirstOrDefault(x => x != s && !x.Hidden);
                if (next != null) ActivateSession(next);
                else ClearActiveSession();
            }
        }
        else if (tab is FileTabItem f)
        {
            f.Editor.RequestClose();
        }
    }

    // ── 탭 드래그 순서변경 ─────────────────────────────────────────
    private Point _tabPressOrigin;
    private TabItemBase? _pendingTab;
    private ReorderDrag<TabItemBase>? _tabDrag;
    private bool _tabDidDrag;
    private readonly List<FrameworkElement> _hiddenTabFeet = new();
    private bool _tabDragHidSeam;

    private void Tab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _tabPressOrigin = e.GetPosition(TabsHost);
        _pendingTab = (sender as FrameworkElement)?.DataContext as TabItemBase;
        _tabDidDrag = false;
    }

    private void TabsHost_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_tabDrag != null) { _tabDrag.Update(e); return; }
        if (e.LeftButton != MouseButtonState.Pressed || _pendingTab == null) return;
        var diff = _tabPressOrigin - e.GetPosition(TabsHost);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        TryStartTabDrag(_pendingTab);
    }

    private async Task EndTabDragAsync()
    {
        var td = _tabDrag;
        _tabDrag = null;
        _pendingTab = null;
        if (Mouse.Captured == TabsHost) TabsHost.ReleaseMouseCapture();
        RestoreTabFeet();
        if (td != null) await td.FinishAsync(commit: true);
    }

    private void TryStartTabDrag(TabItemBase s)
    {
        var coll = _activeProject?.Tabs;
        if (coll == null) return;
        var rows = new List<(TabItemBase, FrameworkElement)>();
        FrameworkElement? sourceBorder = null;
        FrameworkElement? selectedRoot = null;
        foreach (var t in coll)
        {
            if (TabsHost.ItemContainerGenerator.ContainerFromItem(t) is FrameworkElement fe
                && FindTabBorder(fe) is FrameworkElement border
                && VisualTreeHelper.GetParent(border) is FrameworkElement root)
            {
                rows.Add((t, root));
                if (ReferenceEquals(t, s)) sourceBorder = border;
                if (ReferenceEquals(t, _activeTab)) selectedRoot = root;
            }
        }
        if (sourceBorder == null || rows.Count < 2) return;

        _tabDrag = ReorderDrag<TabItemBase>.TryStart(TabsHost, rows, s, sourceBorder,
            (tab, hostTarget, _) =>
            {
                var c = _activeProject?.Tabs;
                if (c != null)
                {
                    int from = c.IndexOf(tab);
                    if (from >= 0)
                    {
                        int to = Math.Clamp(hostTarget, 0, c.Count - 1);
                        if (to != from) { c.Move(from, to); WorkspaceStore.Save(Projects); }
                    }
                }
                return Task.CompletedTask;
            },
            exactFollow: true, horizontal: true, ghostSource: sourceBorder);
        if (_tabDrag != null)
        {
            _tabDidDrag = true;
            TabsHost.CaptureMouse();
            HideTabFeet(sourceBorder);
            SetupDragSeam(s, selectedRoot);
        }
        else
        {
            _pendingTab = null;
        }
    }

    private void HideTabFeet(FrameworkElement sourceBorder)
    {
        RestoreTabFeet();
        var root = VisualTreeHelper.GetParent(sourceBorder);
        if (root == null) return;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            if (VisualTreeHelper.GetChild(root, i) is FrameworkElement
                { Name: "TabFootLeftFill" or "TabFootLeftLine"
                     or "TabFootRightFill" or "TabFootRightLine" } foot)
            {
                foot.Visibility = Visibility.Collapsed;
                _hiddenTabFeet.Add(foot);
            }
        }
    }

    private void RestoreTabFeet()
    {
        foreach (var foot in _hiddenTabFeet)
            foot.ClearValue(UIElement.VisibilityProperty);
        _hiddenTabFeet.Clear();
        _tabDragHidSeam = false;
        if (SelectedTabSeam != null) SelectedTabSeam.RenderTransform = null;
        Dispatcher.InvokeAsync(UpdateSelectedTabSeam, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void SetupDragSeam(TabItemBase source, FrameworkElement? selectedRoot)
    {
        if (SelectedTabSeam == null) return;
        _tabDragHidSeam = true;
        bool draggingSelected = ReferenceEquals(source, _activeTab);
        if (draggingSelected || selectedRoot == null)
        {
            SelectedTabSeam.RenderTransform = null;
            SelectedTabSeam.Visibility = Visibility.Collapsed;
        }
        else
        {
            SelectedTabSeam.RenderTransform = GetOrCreateTranslate(selectedRoot);
        }
    }

    private static TranslateTransform GetOrCreateTranslate(UIElement el)
    {
        if (el.RenderTransform is TranslateTransform t) return t;
        if (el.RenderTransform is TransformGroup g)
        {
            var ex = g.Children.OfType<TranslateTransform>().FirstOrDefault();
            if (ex != null) return ex;
            var added = new TranslateTransform();
            g.Children.Add(added);
            return added;
        }
        var nt = new TranslateTransform();
        if (el.RenderTransform != null && el.RenderTransform != Transform.Identity)
        {
            var grp = new TransformGroup();
            grp.Children.Add(el.RenderTransform);
            grp.Children.Add(nt);
            el.RenderTransform = grp;
        }
        else el.RenderTransform = nt;
        return nt;
    }

    private static FrameworkElement? FindTabBorder(DependencyObject root)
    {
        if (root is FrameworkElement { Name: "TabBd" } fe) return fe;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            if (FindTabBorder(VisualTreeHelper.GetChild(root, i)) is FrameworkElement found)
                return found;
        }
        return null;
    }

    // ── 탭 오버플로우/seam/스크롤 ─────────────────────────────────
    private bool? _fadeLeft, _fadeRight;
    private double _fadeWidth = -1;
    private Action? _tabScrollAnimCancel;
    private const double TabScrollStep = 168;

    private void TabScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateTabOverflowButtons();
        UpdateSelectedTabSeam();
    }

    private void OnThemeChanged_UpdateSeam(string _) => Dispatcher.BeginInvoke(new Action(() =>
    {
        UpdateSelectedTabSeam();
        // 테마 변경 → 에이전트 아이콘(opencode 흑/백 등) 재평가. 값 변경 없이 바인딩만 다시 돌린다.
        foreach (var proj in AllProjects)
            foreach (var s in proj.Tabs.OfType<SessionItem>())
                s.RefreshAgentIcon();
    }));

    private void UpdateSelectedTabSeam()
    {
        if (SelectedTabSeam == null || TabBar == null || TabsHost == null) return;
        if (_tabDragHidSeam) return;
        if (_activeTab == null ||
            TabsHost.ItemContainerGenerator.ContainerFromItem(_activeTab) is not FrameworkElement container)
        {
            SelectedTabSeam.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            var pt = container.TransformToAncestor(TabBar).Transform(new Point(0, 0));
            const double seamExtra = 9.15;
            double bodyWidth = container.ActualWidth - 3;
            double seamWidth = bodyWidth + seamExtra * 2;
            double seamLeft = pt.X - seamExtra;

            double clipLeft = TabScroller.TransformToAncestor(TabBar).Transform(new Point(0, 0)).X;
            if (clipLeft < 0) clipLeft = 0;
            bool clampedLeft = false;
            if (seamLeft < clipLeft)
            {
                seamWidth -= clipLeft - seamLeft;
                seamLeft = clipLeft;
                clampedLeft = true;
            }
            if (seamWidth <= 0) { SelectedTabSeam.Visibility = Visibility.Collapsed; return; }

            SelectedTabSeam.Width = seamWidth;
            SelectedTabSeam.Margin = new Thickness(seamLeft, 0, 0, 0);

            var panelColor = (FindResource("PanelBrush") as SolidColorBrush)?.Color ?? Colors.Black;
            var clearColor = Color.FromArgb(0, panelColor.R, panelColor.G, panelColor.B);
            const double fadePx = 4;
            double f = seamWidth > 0 ? Math.Min(0.45, fadePx / seamWidth) : 0;
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            brush.GradientStops.Add(new GradientStop(clampedLeft ? panelColor : clearColor, 0));
            brush.GradientStops.Add(new GradientStop(panelColor, clampedLeft ? 0 : f));
            brush.GradientStops.Add(new GradientStop(panelColor, 1 - f));
            brush.GradientStops.Add(new GradientStop(clearColor, 1));
            brush.Freeze();
            SelectedTabSeam.Background = brush;
            SelectedTabSeam.Visibility = Visibility.Visible;
        }
        catch { SelectedTabSeam.Visibility = Visibility.Collapsed; }
    }

    private void UpdateTabOverflowButtons()
    {
        if (TabScroller == null || TabNavGroup == null) return;
        bool overflow = TabScroller.ScrollableWidth > 0.5;
        bool canLeft = TabScroller.HorizontalOffset > 0.5;
        bool canRight = TabScroller.HorizontalOffset < TabScroller.ScrollableWidth - 0.5;

        TabNavGroup.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        if (TabScrollLeftBtn != null) TabScrollLeftBtn.IsEnabled = canLeft;
        if (TabScrollRightBtn != null) TabScrollRightBtn.IsEnabled = canRight;

        ApplyTabEdgeFade(canLeft, canRight);
    }

    private void ApplyTabEdgeFade(bool fadeLeft, bool fadeRight)
    {
        double w = TabScroller.ActualWidth;
        if (_fadeLeft == fadeLeft && _fadeRight == fadeRight && Math.Abs(_fadeWidth - w) < 0.5) return;
        _fadeLeft = fadeLeft; _fadeRight = fadeRight; _fadeWidth = w;

        if (!fadeLeft && !fadeRight) { TabScroller.OpacityMask = null; return; }

        double f = Math.Min(0.10, 28 / Math.Max(1, w));
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        var black = Colors.Black;
        var clear = Colors.Transparent;
        mask.GradientStops.Add(new GradientStop(fadeLeft ? clear : black, 0));
        mask.GradientStops.Add(new GradientStop(black, fadeLeft ? f : 0));
        mask.GradientStops.Add(new GradientStop(black, fadeRight ? 1 - f : 1));
        mask.GradientStops.Add(new GradientStop(fadeRight ? clear : black, 1));
        mask.Freeze();
        TabScroller.OpacityMask = mask;
    }

    private void TabScrollLeft_Click(object sender, RoutedEventArgs e)
        => AnimateTabScroll(TabScroller.HorizontalOffset - TabScrollStep);

    private void TabScrollRight_Click(object sender, RoutedEventArgs e)
        => AnimateTabScroll(TabScroller.HorizontalOffset + TabScrollStep);

    private void TabScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TabScroller.ScrollableWidth <= 0.5) return;
        _tabScrollAnimCancel?.Invoke(); _tabScrollAnimCancel = null;
        TabScroller.ScrollToHorizontalOffset(
            Math.Clamp(TabScroller.HorizontalOffset - e.Delta, 0, TabScroller.ScrollableWidth));
        e.Handled = true;
    }

    private void AnimateTabScroll(double to)
    {
        to = Math.Clamp(to, 0, TabScroller.ScrollableWidth);
        _tabScrollAnimCancel?.Invoke();
        var from = TabScroller.HorizontalOffset;
        if (Math.Abs(to - from) < 0.5) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int dur = 240;
        bool cancelled = false;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (cancelled) { CompositionTarget.Rendering -= handler!; return; }
            var t = Math.Min(1.0, sw.ElapsedMilliseconds / (double)dur);
            TabScroller.ScrollToHorizontalOffset(from + (to - from) * EaseInOut(t));
            if (t >= 1.0) CompositionTarget.Rendering -= handler!;
        };
        CompositionTarget.Rendering += handler;
        _tabScrollAnimCancel = () => { if (cancelled) return; cancelled = true; CompositionTarget.Rendering -= handler; };
    }

    private void TabStrip_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var win = Window.GetWindow(this);
        if (win == null) return;
        if (e.ClickCount == 2)
        {
            win.WindowState = win.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        try { win.DragMove(); } catch { /* 이미 캡처 중 등 */ }
    }

    private void EnsureSelectedTabVisible(TabItemBase tab)
    {
        if (TabScroller == null) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            UpdateSelectedTabSeam();
            if (TabsHost.ItemContainerGenerator.ContainerFromItem(tab) is not FrameworkElement fe) return;
            if (TabScroller.ScrollableWidth <= 0.5) return;
            var tl = fe.TransformToAncestor(TabScroller).Transform(new Point(0, 0));
            double left = tl.X + TabScroller.HorizontalOffset;
            double right = left + fe.ActualWidth;
            const double margin = 30;
            if (left < TabScroller.HorizontalOffset + margin)
                AnimateTabScroll(left - margin);
            else if (right > TabScroller.HorizontalOffset + TabScroller.ViewportWidth - margin)
                AnimateTabScroll(right - TabScroller.ViewportWidth + margin);
        }));
    }

    private static double EaseInOut(double t) =>
        t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    // ── 상태/빈 화면 ─────────────────────────────────────────────
    private void UpdateEmptyState()
    {
        bool hasActive = _activeTab != null;

        if (_activeTab is SessionItem)
        {
            TerminalHostContainer.Visibility = Visibility.Visible;
            FileEditorHostContainer.Visibility = Visibility.Collapsed;
        }
        else if (_activeTab is FileTabItem)
        {
            TerminalHostContainer.Visibility = Visibility.Collapsed;
            FileEditorHostContainer.Visibility = Visibility.Visible;
        }
        else
        {
            TerminalHostContainer.Visibility = Visibility.Collapsed;
            FileEditorHostContainer.Visibility = Visibility.Collapsed;
        }

        EmptyState.Visibility = hasActive ? Visibility.Collapsed : Visibility.Visible;

        // 프로젝트 미선택(빈 패널) 시 새 탭(+) 버튼과 메타바(#·경로) 숨김.
        if (NewTabBtn != null)
            NewTabBtn.Visibility = _activeProject != null ? Visibility.Visible : Visibility.Collapsed;
        ApplyProjectInfoHeaderVisibility();

        SessionHeaderBar.Visibility = hasActive ? Visibility.Visible : Visibility.Collapsed;
        if (hasActive)
        {
            if (_activeTab is SessionItem sess)
            {
                var msg = sess.LastMessage;
                var hasMsg = !string.IsNullOrEmpty(msg);
                SessionHeaderTitle.Text = hasMsg ? msg : sess.Name;
                SessionHeaderTitle.ToolTip = hasMsg ? msg : null;
                LastMessageSep.Visibility = hasMsg ? Visibility.Visible : Visibility.Collapsed;
                FileHeaderIcon.Visibility = Visibility.Collapsed;
                FileHeaderPathText.Visibility = Visibility.Collapsed;
                FileDirtyDot.Visibility = Visibility.Collapsed;
                FileHeaderActions.Visibility = Visibility.Collapsed;
            }
            else if (_activeTab is FileTabItem file)
            {
                SessionHeaderTitle.Text = file.Title;
                SessionHeaderTitle.ToolTip = file.FilePath;
                LastMessageSep.Visibility = Visibility.Collapsed;
                FileHeaderIcon.Visibility = Visibility.Visible;
                FileHeaderPathText.Text = file.FilePath;
                FileHeaderPathText.ToolTip = file.FilePath;
                FileHeaderPathText.Visibility = Visibility.Visible;
                FileHeaderActions.Visibility = Visibility.Visible;
                RefreshFileHeaderState(file);
            }
        }
    }

    /// <summary>세션 헤더 타이틀(마지막 메시지)/구분자 폰트를 터미널 폰트 크기보다 2px 작게.</summary>
    private void ApplyHeaderFontSize(double px)
    {
        double size = Math.Max(1, px - 2);
        SessionHeaderTitle.FontSize = size;
        LastMessageSep.FontSize = size;
    }

    /// <summary>외부 훅이 세션 상태(lastmsg 등)를 갱신 → 이 패널의 활성 세션이면 헤더 즉시 갱신.</summary>
    public void NotifySessionStateChanged(SessionItem s)
    {
        if (ReferenceEquals(s, _activeSession)) UpdateEmptyState();
    }

    /// <summary>statusLine 훅이 model/effort 를 갱신 → 이 패널의 활성 세션이면 dock 갱신.</summary>
    public void NotifyModelEffortChanged(string roomId)
    {
        if (_activeSession != null && _activeSession.Id == roomId) RefreshModelEffortDock();
    }

    private void RefreshFileHeaderState(FileTabItem? file = null)
    {
        file ??= _activeTab as FileTabItem;
        if (file == null) return;
        bool dirty = file.Editor.IsDirty;
        FileDirtyDot.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        FileSaveBtn.IsEnabled = dirty;
    }

    private void FileSaveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is FileTabItem file && file.Editor.Save())
            RefreshFileHeaderState(file);
    }

    // ── 파일 탭 ──────────────────────────────────────────────────
    public void OpenFileAsTab(string path)
    {
        if (_activeProject == null) return;
        var tab = CreateFileTab(_activeProject, path);
        if (tab == null) return;
        ActivateFileTab(tab);
        PersistWorkspace(); // 열린 파일 탭 목록을 workspace.json 에 영속(재시작 복원용)
    }

    /// <summary>지정 프로젝트에 파일 편집기 탭을 만들어 Tabs 에 추가하고 반환(활성화는 호출부 담당).
    /// 같은 경로 탭이 이미 있으면 그것을 반환, 경로가 비었거나 로드 실패면 null.</summary>
    private FileTabItem? CreateFileTab(ProjectItem proj, string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var existing = proj.Tabs.OfType<FileTabItem>()
            .FirstOrDefault(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;

        var tab = new FileTabItem { FilePath = path, Editor = CreateFileTabEditor(path) };
        if (!tab.Editor.LoadFile(path)) return null;
        tab.Editor.CloseRequested += (_, _) => RemoveFileTab(tab);
        tab.Editor.DirtyChanged += (_, _) => { if (ReferenceEquals(_activeTab, tab)) RefreshFileHeaderState(tab); };
        proj.Tabs.Add(tab);
        return tab;
    }

    /// <summary>재시작 복원: 저장돼 있던 파일 경로들을 탭으로 다시 연다(활성화 안 함, 저장 순서 유지).
    /// 삭제됐거나 로드 실패한 파일은 건너뛴다.</summary>
    public void RestoreFileTabs(ProjectItem proj, IEnumerable<string> paths)
    {
        foreach (var path in paths)
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                CreateFileTab(proj, path);
    }

    /// <summary>열린 파일 탭 목록 변화를 workspace.json 에 반영(파일 탭 추가/제거 시 호출).</summary>
    private void PersistWorkspace() => WorkspaceStore.Save(Projects);

    private static IFileTabEditor CreateFileTabEditor(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext is ".md" or ".markdown" ? new MarkdownFileEditorView() : new FileEditorView();
    }

    // ── airspace 우회 (오버레이가 뜰 때 터미널 WebView2 정지) ──────────
    /// <summary>터미널 WebView2 를 스냅샷/커튼으로 대체하고 숨긴다. FileExplorer 는 셸이 처리.</summary>
    public async Task SuspendTerminalWithSnapshotAsync(bool blankCurtain = false)
    {
        // 파일 편집기 탭이 활성이면 그 콘텐츠(md=WebView2 HWND)가 오버레이(앱 종료 "세션 닫는 중"/설정 오버레이)를
        // 가린다. 마지막 화면을 캡처해 스냅샷 Image 로 깔고 WebView2 컨테이너를 숨긴다(오버레이가 스냅샷 위에 보임).
        // WPF 네이티브 에디터는 캡처가 null → airspace 문제 없으니 그대로 둔다. (Resume 시 복원.)
        if (_activeTab is FileTabItem file)
        {
            var fileSnap = await file.Editor.CaptureSnapshotAsync();
            if (fileSnap != null)
            {
                TerminalSnapshot.Source = fileSnap;
                TerminalSnapshot.Visibility = Visibility.Visible;
                FileEditorHostContainer.Visibility = Visibility.Collapsed;
            }
            return;
        }

        if (_activeSession == null) return;
        if (blankCurtain)
        {
            TerminalCurtain.Visibility = Visibility.Visible;
        }
        else
        {
            var snap = await _terminal.CaptureSnapshotAsync();
            if (snap != null)
            {
                TerminalSnapshot.Source = snap;
                TerminalSnapshot.Visibility = Visibility.Visible;
            }
        }
        TerminalHostContainer.Visibility = Visibility.Collapsed;
    }

    public void ResumeTerminal()
    {
        if (_activeSession != null)
            TerminalHostContainer.Visibility = Visibility.Visible;
        if (_activeTab is FileTabItem)
            FileEditorHostContainer.Visibility = Visibility.Visible;
        TerminalSnapshot.Visibility = Visibility.Collapsed;
        TerminalSnapshot.Source = null;
        TerminalCurtain.Visibility = Visibility.Collapsed;
    }

    /// <summary>스냅샷만(커튼 없이) 정지 — 우측 오버레이 드로어용.</summary>
    public async Task SuspendTerminalOnlyAsync()
    {
        if (_activeSession == null) return;
        var snap = await _terminal.CaptureSnapshotAsync();
        if (snap != null)
        {
            TerminalSnapshot.Source = snap;
            TerminalSnapshot.Visibility = Visibility.Visible;
        }
        TerminalHostContainer.Visibility = Visibility.Collapsed;
    }

    public void ResumeTerminalOnly()
    {
        if (_activeSession != null)
            TerminalHostContainer.Visibility = Visibility.Visible;
        TerminalSnapshot.Visibility = Visibility.Collapsed;
        TerminalSnapshot.Source = null;
    }

    public void DisposeTerminal() => _terminal.Dispose();
}

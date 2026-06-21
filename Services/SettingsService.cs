using System.IO;
using System.Text.Json;

namespace DevezCode.Services;

/// <summary>
/// 앱 설정 영속(터미널 폰트 크기 + Claude Code 방별 디렉터리/세션 추적).
/// %AppData%\DevezCode\settings.json 에 저장. 터미널 스택(TerminalSessionManager,
/// TerminalHostView)이 요구하는 API만 구현한 경량 버전.
/// </summary>
public static class SettingsService
{
    private sealed class SettingsData
    {
        public int TerminalFontSizePt { get; set; } = 0;
        // 앱 전체 글꼴 크기 단계(devez 이식). 0=작게(기본), 1=크게(+2px). App.SetFontScale 에 전달.
        public int FontScale { get; set; } = 0;
        // 좌·우 패널 접힘 상태 + 펼침 시 복원 폭. 로컬 전용.
        public bool   LeftPanelCollapsed  { get; set; } = false;
        public bool   RightPanelCollapsed { get; set; } = false;
        public double SidebarWidth { get; set; } = 262;
        public double FileExpWidth { get; set; } = 300;
        public Dictionary<string, string> ClaudeCodeRoomDirs { get; set; } = new();
        public Dictionary<string, string> ClaudeCodeRoomSessions { get; set; } = new();
        public List<string> ClaudeCodeRoomLaunched { get; set; } = new();
        // 범용 — "roomId|agentId" 키로 첫 실행 여부 추적. 비-Claude 에이전트도 같은 메커니즘으로
        // 첫 실행=plain, 이후=ResumeFlag(--last / -c) 분기. (Claude 는 별도 IsClaudeCodeRoomLaunched 그대로 사용)
        public List<string> AgentRoomsLaunched { get; set; } = new();
        // codex 방별 세션 ID (ClaudeCodeRoomSessions 와 동일 패턴). --session-id 첫 실행 → SessionStart 훅이 실제 ID 기록 → --resume 로 이어가기.
        public Dictionary<string, string> CodexRoomSessions { get; set; } = new();
        // opencode 방별 세션 ID (Devez 패턴 이식). 플러그인이 sessions\<room>.txt 에 기록한 최신 ID 를 영속.
        // 재오픈 시 `opencode --session <id>` 로 같은 대화 정확히 복원(같은 폴더의 여러 방도 분리).
        public Dictionary<string, string> OpenCodeRoomSessions { get; set; } = new();
        // 방별 에이전트 ID (예: "claude", "codex"). 미설정이면 기본값(claude) — 기존 세션 호환.
        public Dictionary<string, string> RoomAgents { get; set; } = new();
        // 사용자가 활성화한 에이전트 ID 목록. 빈 값이면 모든 설치된 에이전트 활성화로 간주.
        public List<string> EnabledAgents { get; set; } = new();
        // 마지막으로 활성이던 프로젝트/세션. 정상 종료(CleanShutdown=true) 때만 복원한다.
        public string? LastActiveProjectPath { get; set; }
        public string? LastActiveSessionId { get; set; }
        public bool CleanShutdown { get; set; }
        // 우측 패널 브라우저 — 프로젝트별 마지막 방문 URL(재시작 시 복원).
        // 키 = 프로젝트 절대경로. 프로젝트가 없거나 저장된 적 없으면 HomeUrl 로 폴백.
        public Dictionary<string, string> BrowserLastUrlByProject { get; set; } = new();
        // 우측 패널 작업 큐 — 프로젝트 경로별로 저장. 키 = 프로젝트 절대경로, 값 = 큐 항목 목록.
        // 로컬 전용 — 재시작 시 그대로 복원. 프로젝트 경로가 null/empty 면 "전역" 큐 (실제론 잘 안 씀).
        public Dictionary<string, List<TaskQueueEntry>> TaskQueueItemsByProject { get; set; } = new();
        // 작업 큐 입력창 사용자 지정 높이(MinHeight). 0/미설정이면 기본(자연 높이). 로컬 전용.
        public double TaskQueueInputMinHeight { get; set; }
        // 작업 큐 버블/입력 글꼴 크기(Ctrl+휠로 조절, devez BubbleFontSize 정합 10~28). 기본 14.
        public double TaskQueueBubbleFontSize { get; set; } = 14;
        // 메인 창 위치/크기 + 최대화 상태(재시작 시 복원). 화면 밖이면 복원 안 함. 로컬 전용.
        public double? WindowLeft   { get; set; }
        public double? WindowTop    { get; set; }
        public double? WindowWidth  { get; set; }
        public double? WindowHeight { get; set; }
        public bool    WindowMaximized { get; set; }
        // 우측 패널 마지막 활성 탭 (0=탐색기, 1=브라우저, 2=DIFF, 3=작업 큐). 기본=3.
        public int FileExpActiveTab { get; set; } = 3;
    }

    private static readonly object _lock = new();
    private static SettingsData? _current;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "settings.json");

    private static SettingsData Current
    {
        get
        {
            lock (_lock)
            {
                if (_current != null) return _current;
                try
                {
                    if (File.Exists(SettingsPath))
                        _current = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(SettingsPath));
                }
                catch { /* 손상 시 새로 시작 */ }
                return _current ??= new SettingsData();
            }
        }
    }

    private static void Save()
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath,
                    JsonSerializer.Serialize(_current, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* non-critical */ }
        }
    }

    // ── 터미널 폰트 크기 ──────────────────────────────────────────
    public static int LoadTerminalFontSizePt() => Current.TerminalFontSizePt;
    public static void SaveTerminalFontSizePt(int pt) { Current.TerminalFontSizePt = pt; Save(); }

    // ── 앱 전체 글꼴 크기 단계 (devez 이식: 0=작게, 1=크게) ───────
    public static int LoadFontScale() => Current.FontScale;
    public static void SaveFontScale(int v) { Current.FontScale = v; Save(); }

    // ── 좌·우 패널 접힘 상태 + 복원 폭 ───────────────────────────
    public static bool   LoadLeftPanelCollapsed()  => Current.LeftPanelCollapsed;
    public static bool   LoadRightPanelCollapsed() => Current.RightPanelCollapsed;
    public static double LoadSidebarWidth() => Current.SidebarWidth;
    public static double LoadFileExpWidth() => Current.FileExpWidth;

    public static void SaveLeftPanel(bool collapsed, double width)
    {
        Current.LeftPanelCollapsed = collapsed;
        Current.SidebarWidth = width;
        Save();
    }

    public static void SaveRightPanel(bool collapsed, double width)
    {
        Current.RightPanelCollapsed = collapsed;
        Current.FileExpWidth = width;
        Save();
    }

    // ── 우측 패널 활성 탭 ─────────────────────────────────────────
    public static int LoadFileExpActiveTab() => Current.FileExpActiveTab;
    public static void SaveFileExpActiveTab(int idx) { Current.FileExpActiveTab = idx; Save(); }

    // ── Claude Code 방 작업 디렉터리 ──────────────────────────────
    public static string? LoadClaudeCodeRoomDir(string roomId)
        => Current.ClaudeCodeRoomDirs.TryGetValue(roomId, out var d) ? d : null;

    public static void SaveClaudeCodeRoomDir(string roomId, string dir)
    {
        Current.ClaudeCodeRoomDirs[roomId] = dir;
        Save();
    }

    public static void RemoveClaudeCodeRoomDir(string roomId)
    {
        bool changed = Current.ClaudeCodeRoomDirs.Remove(roomId);
        changed |= Current.ClaudeCodeRoomSessions.Remove(roomId);
        changed |= Current.ClaudeCodeRoomLaunched.Remove(roomId);
        changed |= Current.RoomAgents.Remove(roomId);
        if (changed) Save();
    }

    // ── 방별 에이전트 ID (미설정 시 기본값 claude) ────────────────
    public static string LoadAgentForRoom(string roomId)
        => Current.RoomAgents.TryGetValue(roomId, out var a) ? a : AgentRegistry.DefaultAgentId;

    public static void SaveAgentForRoom(string roomId, string agentId)
    {
        Current.RoomAgents[roomId] = agentId;
        Save();
    }

    // ── 사용자가 활성화한 에이전트 목록 ────────────────────────────
    /// <summary>빈 값이면 모든 알려진 에이전트를 활성화한 것으로 간주(첫 실행 기본값).</summary>
    public static IReadOnlyList<string> LoadEnabledAgents()
    {
        var list = Current.EnabledAgents;
        if (list.Count == 0) return AgentRegistry.All.Select(a => a.Id).ToList();
        return list;
    }

    public static void SaveEnabledAgents(IEnumerable<string> agents)
    {
        Current.EnabledAgents = agents.ToList();
        Save();
    }

    // ── Claude 세션 ID ────────────────────────────────────────────
    public static string? LoadClaudeCodeRoomSession(string roomId)
        => Current.ClaudeCodeRoomSessions.TryGetValue(roomId, out var s) ? s : null;

    public static void SaveClaudeCodeRoomSession(string roomId, string sessionId)
    {
        Current.ClaudeCodeRoomSessions[roomId] = sessionId;
        Save();
    }

    // ── 마지막 활성 프로젝트/세션 (정상 종료 시에만 복원) ────────
    public static (string? projectPath, string? sessionId) LoadLastActive()
        => (Current.LastActiveProjectPath, Current.LastActiveSessionId);

    public static void SaveLastActive(string? projectPath, string? sessionId)
    {
        Current.LastActiveProjectPath = projectPath;
        Current.LastActiveSessionId = sessionId;
        Save();
    }

    /// <summary>직전 실행이 정상 종료됐는지. true 일 때만 마지막 세션을 복원한다(크래시 시 복원 방지).</summary>
    public static bool LoadCleanShutdown() => Current.CleanShutdown;
    public static void SaveCleanShutdown(bool v) { Current.CleanShutdown = v; Save(); }

    // ── 우측 패널 브라우저 마지막 URL (프로젝트별) ───────────────────
    /// <summary>지정 프로젝트의 저장된 마지막 URL. 없거나 프로젝트가 비었으면 null.</summary>
    public static string? LoadBrowserLastUrl(string? projectPath)
    {
        if (string.IsNullOrEmpty(projectPath)) return null;
        return Current.BrowserLastUrlByProject.TryGetValue(projectPath, out var u) ? u : null;
    }

    /// <summary>지정 프로젝트의 마지막 URL 저장. 빈 문자열/공백은 무시.</summary>
    public static void SaveBrowserLastUrl(string? projectPath, string url)
    {
        if (string.IsNullOrEmpty(projectPath)) return;
        if (string.IsNullOrWhiteSpace(url)) return;
        Current.BrowserLastUrlByProject[projectPath] = url;
        Save();
    }

    /// <summary>프로젝트 삭제 시 해당 프로젝트의 저장된 URL 도 정리.</summary>
    public static void RemoveBrowserLastUrl(string? projectPath)
    {
        if (string.IsNullOrEmpty(projectPath)) return;
        if (Current.BrowserLastUrlByProject.Remove(projectPath)) Save();
    }

    // ── 우측 패널 작업 큐 (버블 항목, 프로젝트별) ────────────────────
    /// <summary>저장된 작업 큐 항목을 (text, sortOrder) 튜플 목록으로 반환. 정렬 순서대로.
    /// projectPath 가 null/empty 면 전역 큐 (별도 "__global__" 키). 저장된 게 없으면 빈 목록.</summary>
    public static IReadOnlyList<(string Text, long SortOrder)> LoadTaskQueueItems(string? projectPath)
    {
        var key = string.IsNullOrEmpty(projectPath) ? "__global__" : projectPath;
        if (!Current.TaskQueueItemsByProject.TryGetValue(key, out var list))
            return Array.Empty<(string, long)>();
        return list
            .OrderBy(e => e.SortOrder)
            .Select(e => (e.Text ?? "", e.SortOrder))
            .ToList();
    }

    public static void SaveTaskQueueItems(string? projectPath, IEnumerable<(string Text, long SortOrder)> items)
    {
        var key = string.IsNullOrEmpty(projectPath) ? "__global__" : projectPath;
        var snapshot = items
            .Select(e => new TaskQueueEntry { Text = e.Text, SortOrder = e.SortOrder })
            .ToList();
        // 빈 큐는 키 자체를 제거해 settings.json 크기 축소.
        if (snapshot.Count == 0)
            Current.TaskQueueItemsByProject.Remove(key);
        else
            Current.TaskQueueItemsByProject[key] = snapshot;
        Save();
    }

    // ── 작업 큐 입력창 높이 ───────────────────────────────────────
    public static double LoadTaskQueueInputMinHeight() => Current.TaskQueueInputMinHeight;
    public static void SaveTaskQueueInputMinHeight(double h)
    {
        Current.TaskQueueInputMinHeight = h;
        Save();
    }

    // ── 작업 큐 글꼴 크기 (Ctrl+휠) ───────────────────────────────
    public static double LoadTaskQueueBubbleFontSize()
        => Current.TaskQueueBubbleFontSize <= 0 ? 14 : Current.TaskQueueBubbleFontSize;
    public static void SaveTaskQueueBubbleFontSize(double size)
    {
        Current.TaskQueueBubbleFontSize = size;
        Save();
    }

    // ── 메인 창 위치/크기 (재시작 복원) ──────────────────────────
    public static (double? left, double? top, double? width, double? height, bool maximized) LoadWindowPlacement()
        => (Current.WindowLeft, Current.WindowTop, Current.WindowWidth, Current.WindowHeight, Current.WindowMaximized);

    public static void SaveWindowPlacement(double left, double top, double width, double height, bool maximized)
    {
        Current.WindowLeft = left;
        Current.WindowTop = top;
        Current.WindowWidth = width;
        Current.WindowHeight = height;
        Current.WindowMaximized = maximized;
        Save();
    }

    // ── 방이 한 번이라도 실행됐는지 (resume 판단용) ──────────────
    public static bool IsClaudeCodeRoomLaunched(string roomId)
        => Current.ClaudeCodeRoomLaunched.Contains(roomId);

    public static void MarkClaudeCodeRoomLaunched(string roomId)
    {
        if (!Current.ClaudeCodeRoomLaunched.Contains(roomId))
        {
            Current.ClaudeCodeRoomLaunched.Add(roomId);
            Save();
        }
    }

    /// <summary>비-Claude 에이전트의 방별 "첫 실행 여부". roomId+agentId 조합으로 추적.</summary>
    public static bool IsAgentRoomLaunched(string roomId, string agentId)
        => Current.AgentRoomsLaunched.Contains($"{roomId}|{agentId}");

    public static void MarkAgentRoomLaunched(string roomId, string agentId)
    {
        var key = $"{roomId}|{agentId}";
        if (!Current.AgentRoomsLaunched.Contains(key))
        {
            Current.AgentRoomsLaunched.Add(key);
            Save();
        }
    }

    // ── codex 세션 ID (ClaudeCodeRoomSessions 와 동일 패턴, hooks 로 채워짐) ─────
    public static string? LoadCodexRoomSession(string roomId)
        => Current.CodexRoomSessions.TryGetValue(roomId, out var s) ? s : null;

    public static void SaveCodexRoomSession(string roomId, string sessionId)
    {
        // 불변식: 비정상 값(빈 문자열 등) 은 무시 — 코드 안전성.
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        Current.CodexRoomSessions[roomId] = sessionId;
        Save();
    }

    // ── opencode 세션 ID (Devez 패턴 이식, 플러그인이 채움) ─────────────
    public static string? LoadOpenCodeRoomSession(string roomId)
        => Current.OpenCodeRoomSessions.TryGetValue(roomId, out var s) ? s : null;

    public static void SaveOpenCodeRoomSession(string roomId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        Current.OpenCodeRoomSessions[roomId] = sessionId;
        Save();
    }
}

/// <summary>작업 큐에 저장되는 단일 항목. JSON 직렬화용.</summary>
public sealed class TaskQueueEntry
{
    public string Text { get; set; } = "";
    public long SortOrder { get; set; }
}

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
        // 헤더 성능 모니터(CPU/RAM) 칩 표시 여부. 로컬 전용(DB 동기화 없음).
        public bool ShowPerfMonitorBar { get; set; } = true;
        // 좌·우 패널 접힘 상태 + 펼침 시 복원 폭. 로컬 전용.
        public bool   LeftPanelCollapsed  { get; set; } = false;
        public bool   RightPanelCollapsed { get; set; } = false;
        public double SidebarWidth { get; set; } = 262;
        public double FileExpWidth { get; set; } = 300;
        public Dictionary<string, string> ClaudeCodeRoomDirs { get; set; } = new();
        public Dictionary<string, string> ClaudeCodeRoomSessions { get; set; } = new();
        public List<string> ClaudeCodeRoomLaunched { get; set; } = new();
        // 마지막으로 활성이던 프로젝트/세션. 정상 종료(CleanShutdown=true) 때만 복원한다.
        public string? LastActiveProjectPath { get; set; }
        public string? LastActiveSessionId { get; set; }
        public bool CleanShutdown { get; set; }
        // 우측 패널 브라우저 뷰의 마지막 방문 URL(재시작 시 복원).
        public string? BrowserLastUrl { get; set; }
        // 메인 창 위치/크기 + 최대화 상태(재시작 시 복원). 화면 밖이면 복원 안 함. 로컬 전용.
        public double? WindowLeft   { get; set; }
        public double? WindowTop    { get; set; }
        public double? WindowWidth  { get; set; }
        public double? WindowHeight { get; set; }
        public bool    WindowMaximized { get; set; }
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

    // ── 헤더 성능 모니터 칩 표시 ─────────────────────────────────
    public static bool LoadShowPerfMonitorBar() => Current.ShowPerfMonitorBar;
    public static void SaveShowPerfMonitorBar(bool v) { Current.ShowPerfMonitorBar = v; Save(); }

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
        if (changed) Save();
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

    // ── 우측 패널 브라우저 마지막 URL ────────────────────────────
    public static string? LoadBrowserLastUrl() => Current.BrowserLastUrl;
    public static void SaveBrowserLastUrl(string url) { Current.BrowserLastUrl = url; Save(); }

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
}

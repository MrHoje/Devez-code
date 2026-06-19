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
        public Dictionary<string, string> ClaudeCodeRoomDirs { get; set; } = new();
        public Dictionary<string, string> ClaudeCodeRoomSessions { get; set; } = new();
        public List<string> ClaudeCodeRoomLaunched { get; set; } = new();
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

namespace DevezCode.Services
{
    public sealed class UsagePriceRule
    {
        public string Match { get; set; } = "";
        public double InPerM { get; set; }
        public double OutPerM { get; set; }
        public double? CacheWrite5m { get; set; }
        public double? CacheWrite1h { get; set; }
        public double? CacheRead { get; set; }
    }

    public static class SettingsService
    {
        public static List<UsagePriceRule> Pricing { get; set; } = [];
        public static Dictionary<string, string> CodexSessions { get; } = [];
        public static Dictionary<string, string> VibeSessions { get; } = [];
        public static List<UsagePriceRule> LoadUsagePricing() => Pricing;
        public static string? LoadCodexRoomSession(string roomId) => CodexSessions.GetValueOrDefault(roomId);
        public static string? LoadClaudeCodeRoomSession(string roomId) => null;
        public static string? LoadDevezVibeRoomSession(string roomId) => VibeSessions.GetValueOrDefault(roomId);
    }

    public static class DevezVibeStateService
    {
        public static string StripBackendPrefix(string sid) => sid.StartsWith("claude:") ? sid[7..] : sid;
    }
}

namespace DevezCode.Services.Terminal
{
    public static class TerminalSessionManager
    {
        public static Dictionary<string, string> CodexPaths { get; } = [];
        public static string? FindClaudeTranscriptPath(string? cwd, string? sid) => null;
        public static string? FindCodexTranscriptPath(string? sid) => sid == null ? null : CodexPaths.GetValueOrDefault(sid);
    }
}

using System.Windows;

namespace DevezCode
{
    public class App : Application
    {
        public static string CurrentTheme => "minimal";
        public static bool IsDarkTheme(string theme) => theme is "dark" or "midnight";
    }
    public partial class MainWindow : Window
    {
        public static Services.CliAccountStore TestStore = null!;
        public static List<string> Events = [];
        public static Action<string>? OnEvent;
        public static void Record(string text) { Events.Add(text); OnEvent?.Invoke(text); }
        public readonly List<ProjectItem> _projects = [];
        public readonly List<WorkspacePaneView> _panes = [new()];
        private readonly System.Threading.SemaphoreSlim _sessionReloadGate = new(1, 1);
        private bool _accountSwitchRunning;
        public bool IsSwitching => _accountSwitchRunning;
        public bool IsGateAvailable => _sessionReloadGate.CurrentCount == 1;
        private readonly UsageService _codex = new();
        private readonly UsageService _usageApi = new();
        private readonly System.Windows.Controls.Border CodexPanel = new();
        private object? _lastCodex, _rlApi, _rlHook;
        private DateTime _claudeUsageCutoff;
        private DateTimeOffset _codexUsageCutoff;
        private bool _awaitingClaudeAccountUsage;
        private WorkspacePaneView PaneFor(SessionItem session) => _panes[0];
        private void UpdateFooterDivider() { }
        private void RefreshUsagePanelIfVisible() { _ = _lastCodex; _ = _codexUsageCutoff; }
        private void ReevaluateClaudeUsage() { _ = _rlApi; _ = _rlHook; _ = _claudeUsageCutoff; _ = _awaitingClaudeAccountUsage; }
        private void SchedulePendingWorkspaceNavigationDrain() => Record("drain");
    }
    public class ProjectItem
    {
        public string Path = "fixture";
        public List<object> Tabs = [];
    }
    public class SessionItem
    {
        public string Id = "", Name = "", AgentId = "claude";
        public bool IsExternal, IsAlive, IsBusy, IsWaitingChoice;
    }
    public class WorkspacePaneView
    {
        public bool FailBegin, FailComplete;
        public void BeginSessionReload(IReadOnlyList<SessionItem> all, string label)
        {
            MainWindow.Record("begin");
            if (FailBegin) throw new InvalidOperationException("fixture begin failure");
        }
        public void CompleteSessionReload(IReadOnlyList<SessionItem> all, bool restartActive = true)
        {
            MainWindow.Record("complete:" + restartActive);
            if (FailComplete) throw new InvalidOperationException("fixture complete failure");
        }
        public void PreloadSession(SessionItem session)
        {
            MainWindow.Record("start:" + session.Id);
            TerminalSessionManager.Instance.Alive.Add(session.Id);
        }
    }
    public class UsageService
    {
        public void RefreshNow() => MainWindow.Record("refresh");
        public Task ResetForAccountChangeAsync() => Task.CompletedTask;
    }
    public static class SettingsService
    {
        public static bool LoadClaudeGuiMode() => true;
        public static string LoadAgentForRoom(string id) => id.StartsWith("sdk") ? "claude" : "codex";
        public static string? LoadDevezVibeRoomSession(string id) => id;
    }
    public static class DevezVibeStateService
    {
        public static string? LoadTrackedSessionId(string room) => null;
        public static string StripBackendPrefix(string sid) => sid.StartsWith("claude:") ? sid[7..] : sid;
    }
    public partial class TerminalSessionManager
    {
        public static string TestRoutePath = "";
        private static string DevezVibeRouteStorePath() => TestRoutePath;
        public static TerminalSessionManager Instance = new();
        public HashSet<string> Alive = [];
        public bool FailStop;
        public void SuspendStartsForAccountChange(IEnumerable<string> ids) => MainWindow.Record("suspend-terminal");
        public void ResumeStartsAfterAccountChange(IEnumerable<string> ids) => MainWindow.Record("unlock-terminal");
        public SessionItem? Get(string id) => Alive.Contains(id) ? new() { IsAlive = true } : null;
        public Task GracefulDisposeRoomsAsync(IEnumerable<string> ids)
        {
            MainWindow.Record("stop-terminal");
            foreach (var id in ids) Alive.Remove(id);
            return FailStop ? Task.FromException(new InvalidOperationException("fixture stop failure")) : Task.CompletedTask;
        }
        public void ClearDisposedRoom(string id) => MainWindow.Record("clear:" + id);
    }
    public class ClaudeSdkSessionManager
    {
        public static ClaudeSdkSessionManager Instance = new();
        public HashSet<string> Alive = [];
        public Task SuspendStartsForAccountChangeAsync(IEnumerable<string> ids) { MainWindow.Record("suspend-sdk"); return Task.CompletedTask; }
        public void ResumeStartsAfterAccountChange(IEnumerable<string> ids) => MainWindow.Record("unlock-sdk");
        public bool IsStarted(string id) => Alive.Contains(id);
        public Task StopAsync(string id) { MainWindow.Record("stop-sdk:" + id); Alive.Remove(id); return Task.CompletedTask; }
        public Task EnsureStartedAsync(SessionItem session, string path)
        {
            MainWindow.Record("start-sdk:" + session.Id); Alive.Add(session.Id); return Task.CompletedTask;
        }
    }
}

namespace DevezCode.Views
{
    public static class PromptDialog
    {
        public static string? Show(string title, string text, string defaultValue, int maxLength) => null;
    }
    public static class ConfirmDialog
    {
        public static bool Show(string title, string text, string okLabel, bool danger) => false;
        public static void Alert(string title, string text) { }
    }
}

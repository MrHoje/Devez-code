using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace DevezCode.Services;

public sealed class TaskTrackingService : IDisposable
{
    private static string OpenCodeTodosDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "opencode", "todos");

    public ObservableCollection<Models.TaskItem> Tasks { get; } = new();

    public event Action? TasksChanged;

    private string? _activeRoomId;
    private string? _activeProjectPath;
    private FileSystemWatcher? _opencodeWatcher;
    private FileSystemWatcher? _claudeWatcher;
    private FileSystemWatcher? _gjacWatcher;

    public void Start()
    {
        Directory.CreateDirectory(OpenCodeTodosDir);
    }

    public void SetActiveSession(string? roomId, string? projectPath)
    {
        _activeRoomId = roomId;
        _activeProjectPath = projectPath;
        ReloadForActiveSession();
        WatchActiveFiles();
    }

    private string? ActiveTodoFilePath()
    {
        if (string.IsNullOrEmpty(_activeRoomId)) return null;
        var safe = System.Text.RegularExpressions.Regex.Replace(_activeRoomId, @"[^\w\-]", "");
        var path = Path.Combine(OpenCodeTodosDir, safe + ".json");
        return File.Exists(path) ? path : null;
    }

    private static string ClaudeTasksRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "tasks");

    // Claude Code 는 ~/.claude/tasks/{taskSession}/{n}.json 에 태스크 저장.
    // taskSession 은 "session-{세션UUID 앞 8자}" 형식(신형) 또는 풀 UUID(구형).
    private string? ClaudeTasksDir()
    {
        if (string.IsNullOrEmpty(_activeRoomId)) return null;
        var sid = SettingsService.LoadClaudeCodeRoomSession(_activeRoomId);
        if (string.IsNullOrEmpty(sid)) return null;

        var short8 = sid.Length >= 8 ? sid.Substring(0, 8) : sid;
        foreach (var cand in new[] { "session-" + short8, sid, "session-" + sid })
        {
            var dir = Path.Combine(ClaudeTasksRoot, cand);
            if (Directory.Exists(dir)) return dir;
        }
        return null;
    }

    private string? GjacGoalsDir()
    {
        if (string.IsNullOrEmpty(_activeProjectPath)) return null;
        var gjcDir = Path.Combine(_activeProjectPath, ".gjc");
        if (!Directory.Exists(gjcDir)) return null;
        var sessionDirs = Directory.GetDirectories(gjcDir, "_session-*");
        if (sessionDirs.Length == 0) return null;
        var latest = sessionDirs.OrderByDescending(d => Directory.GetLastWriteTime(d)).First();
        var goalsPath = Path.Combine(latest, "ultragoal", "goals.json");
        return File.Exists(goalsPath) ? Path.GetDirectoryName(goalsPath) : null;
    }

    private void ReloadForActiveSession()
    {
        Tasks.Clear();

        var todoPath = ActiveTodoFilePath();
        if (todoPath != null) LoadOpenCodeTodos(todoPath);

        var claudeDir = ClaudeTasksDir();
        if (claudeDir != null) LoadClaudeTasks(claudeDir);

        var gjacDir = GjacGoalsDir();
        if (gjacDir != null) LoadGjacTasks(gjacDir);

        TasksChanged?.Invoke();
    }

    private void WatchActiveFiles()
    {
        _opencodeWatcher?.Dispose(); _opencodeWatcher = null;
        _claudeWatcher?.Dispose(); _claudeWatcher = null;
        _gjacWatcher?.Dispose(); _gjacWatcher = null;

        try
        {
            Directory.CreateDirectory(OpenCodeTodosDir);
            _opencodeWatcher = new FileSystemWatcher(OpenCodeTodosDir, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _opencodeWatcher.Changed += (_, e) => DispatcherInvoke(() =>
            {
                if (MatchesActiveRoom(e.FullPath)) LoadOpenCodeTodos(e.FullPath);
            });
            _opencodeWatcher.Created += (_, e) => DispatcherInvoke(() =>
            {
                if (MatchesActiveRoom(e.FullPath)) LoadOpenCodeTodos(e.FullPath);
            });
            _opencodeWatcher.Deleted += (_, e) => DispatcherInvoke(() =>
            {
                if (MatchesActiveRoom(e.FullPath)) { Tasks.Clear(); TasksChanged?.Invoke(); }
            });
        }
        catch { }

        try
        {
            Directory.CreateDirectory(ClaudeTasksRoot);
            // 세션 디렉터리가 실행 후 생성될 수 있어 루트를 재귀 감시한다.
            _claudeWatcher = new FileSystemWatcher(ClaudeTasksRoot, "*.json")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _claudeWatcher.Changed += (_, e) => DispatcherInvoke(() => { if (MatchesActiveClaude(e.FullPath)) ReloadClaudeTasks(); });
            _claudeWatcher.Created += (_, e) => DispatcherInvoke(() => { if (MatchesActiveClaude(e.FullPath)) ReloadClaudeTasks(); });
            _claudeWatcher.Deleted += (_, e) => DispatcherInvoke(() => { if (MatchesActiveClaude(e.FullPath)) ReloadClaudeTasks(); });
            // 태스크 파일이 임시파일→리네임(atomic)으로 써질 수 있어 Renamed 도 처리. 신/구 경로 모두 검사.
            _claudeWatcher.Renamed += (_, e) => DispatcherInvoke(() =>
            {
                if (MatchesActiveClaude(e.FullPath) || MatchesActiveClaude(e.OldFullPath)) ReloadClaudeTasks();
            });
        }
        catch { }

        try
        {
            var gjacDir = GjacGoalsDir();
            if (gjacDir != null)
            {
                _gjacWatcher = new FileSystemWatcher(gjacDir, "goals.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                _gjacWatcher.Changed += (_, _) => DispatcherInvoke(() => ReloadGjacTasks());
                _gjacWatcher.Created += (_, _) => DispatcherInvoke(() => ReloadGjacTasks());
            }
        }
        catch { }
    }

    private bool MatchesActiveRoom(string filePath)
    {
        if (string.IsNullOrEmpty(_activeRoomId)) return false;
        var roomId = Path.GetFileNameWithoutExtension(filePath);
        var safe = System.Text.RegularExpressions.Regex.Replace(_activeRoomId, @"[^\w\-]", "");
        return string.Equals(roomId, safe, StringComparison.OrdinalIgnoreCase);
    }

    private void LoadOpenCodeTodos(string filePath)
    {
        try
        {
            if (!File.Exists(filePath) || !MatchesActiveRoom(filePath)) return;
            var json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json) || json == "null") return;
            var raw = JsonSerializer.Deserialize<List<TodoRaw>>(json);
            if (raw == null) return;

            Tasks.Clear();

            var source = $"opencode:{_activeRoomId}";
            foreach (var item in raw)
            {
                var content = item.content?.Trim() ?? "";
                var status = NormalizeStatus(item.status);
                if (string.IsNullOrEmpty(content)) continue;
                Tasks.Add(new Models.TaskItem
                {
                    Id = item.id ?? Guid.NewGuid().ToString("N"),
                    Description = content,
                    Status = status,
                    Priority = NormalizePriority(item.priority),
                    Source = source
                });
            }
            TasksChanged?.Invoke();
        }
        catch { }
    }

    private bool MatchesActiveClaude(string filePath)
    {
        var dir = ClaudeTasksDir();
        if (dir == null) return false;
        var parent = Path.GetDirectoryName(filePath);
        return string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase);
    }

    private void ReloadClaudeTasks()
    {
        Tasks.Clear();
        var claudeDir = ClaudeTasksDir();
        if (claudeDir != null) LoadClaudeTasks(claudeDir);
        var gjacDir = GjacGoalsDir();
        if (gjacDir != null) LoadGjacTasks(gjacDir);
        var todoPath = ActiveTodoFilePath();
        if (todoPath != null) LoadOpenCodeTodos(todoPath);
        TasksChanged?.Invoke();
    }

    private void LoadClaudeTasks(string tasksDir)
    {
        try
        {
            if (!Directory.Exists(tasksDir)) return;
            var files = Directory.GetFiles(tasksDir, "*.json")
                .OrderBy(f => int.TryParse(Path.GetFileNameWithoutExtension(f), out var n) ? n : int.MaxValue)
                .ThenBy(f => f);

            var source = $"claude:{_activeRoomId}";
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    if (string.IsNullOrWhiteSpace(json)) continue;
                    using var doc = JsonDocument.Parse(json);
                    var t = doc.RootElement;
                    var subject = t.TryGetProperty("subject", out var sub) ? sub.GetString()?.Trim() ?? "" : "";
                    if (string.IsNullOrEmpty(subject)
                        && t.TryGetProperty("content", out var cEl)) subject = cEl.GetString()?.Trim() ?? "";
                    var status = t.TryGetProperty("status", out var s) ? NormalizeStatus(s.GetString()) : "pending";
                    var id = t.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    if (string.IsNullOrEmpty(subject)) continue;
                    Tasks.Add(new Models.TaskItem
                    {
                        Id = id ?? Path.GetFileNameWithoutExtension(file),
                        Description = subject,
                        Status = status,
                        Priority = "medium",
                        Source = source
                    });
                }
                catch { }
            }
        }
        catch { }
    }

    private void ReloadGjacTasks()
    {
        Tasks.Clear();
        var gjacDir = GjacGoalsDir();
        if (gjacDir != null) LoadGjacTasks(gjacDir);
        var claudeDir = ClaudeTasksDir();
        if (claudeDir != null) LoadClaudeTasks(claudeDir);
        var todoPath = ActiveTodoFilePath();
        if (todoPath != null) LoadOpenCodeTodos(todoPath);
        TasksChanged?.Invoke();
    }

    private void LoadGjacTasks(string goalsDir)
    {
        try
        {
            var path = Path.Combine(goalsDir, "goals.json");
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);

            var source = $"gjac:{_activeProjectPath}";
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in doc.RootElement.EnumerateArray())
                {
                    var id = g.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    var title = g.TryGetProperty("title", out var tEl) ? tEl.GetString()?.Trim() ?? "" : "";
                    var objective = g.TryGetProperty("objective", out var oEl) ? oEl.GetString()?.Trim() ?? "" : "";
                    var status = g.TryGetProperty("status", out var sEl) ? NormalizeStatus(sEl.GetString()) : "pending";

                    var desc = title;
                    if (!string.IsNullOrEmpty(objective) && !objective.Equals(title, StringComparison.OrdinalIgnoreCase))
                        desc = $"{title}: {objective}";

                    if (string.IsNullOrEmpty(desc)) continue;
                    Tasks.Add(new Models.TaskItem
                    {
                        Id = id ?? Guid.NewGuid().ToString("N"),
                        Description = desc,
                        Status = MapGjacStatus(status),
                        Priority = "medium",
                        Source = source
                    });
                }
            }
        }
        catch { }
    }

    private static string MapGjacStatus(string s) => s.ToLowerInvariant() switch
    {
        "active" or "in_progress" => "in_progress",
        "completed" or "done" => "completed",
        "failed" or "cancelled" or "canceled" => "cancelled",
        _ => "pending"
    };

    private static string NormalizeStatus(string? s) => s?.ToLowerInvariant() switch
    {
        "in_progress" or "inprogress" or "active" => "in_progress",
        "completed" or "done" => "completed",
        "cancelled" or "canceled" or "failed" => "cancelled",
        _ => "pending"
    };

    private static string NormalizePriority(string? s) => s?.ToLowerInvariant() switch
    {
        "high" or "h" => "high",
        "low" or "l" => "low",
        _ => "medium"
    };

    private static void DispatcherInvoke(Action a)
    {
        if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == false)
            System.Windows.Application.Current.Dispatcher.BeginInvoke(a);
        else
            a();
    }

    private sealed class TodoRaw
    {
        public string? id { get; set; }
        public string? content { get; set; }
        public string? status { get; set; }
        public string? priority { get; set; }
    }

    public void Dispose()
    {
        _opencodeWatcher?.Dispose();
        _claudeWatcher?.Dispose();
        _gjacWatcher?.Dispose();
    }
}

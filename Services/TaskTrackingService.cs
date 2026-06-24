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

    private FileSystemWatcher? _opencodeWatcher;
    private readonly HashSet<string> _knownOpenCodeRooms = new();

    public void Start()
    {
        StartOpenCodeWatcher();
        StartClaudeWatcher();
    }

    private void StartOpenCodeWatcher()
    {
        try
        {
            Directory.CreateDirectory(OpenCodeTodosDir);
            _opencodeWatcher?.Dispose();
            _opencodeWatcher = new FileSystemWatcher(OpenCodeTodosDir, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _opencodeWatcher.Changed += (_, e) => DispatcherInvoke(() => LoadOpenCodeTodos(e.FullPath));
            _opencodeWatcher.Created += (_, e) => DispatcherInvoke(() => LoadOpenCodeTodos(e.FullPath));
            _opencodeWatcher.Deleted += (_, e) => DispatcherInvoke(() => RemoveOpenCodeTodos(e.FullPath));

            foreach (var f in Directory.EnumerateFiles(OpenCodeTodosDir, "*.json"))
                LoadOpenCodeTodos(f);
        }
        catch { }
    }

    private static string ClaudeSettingsPath(string projectPath)
        => string.IsNullOrEmpty(projectPath) ? "" : Path.Combine(projectPath, ".claude", "settings.json");

    private readonly Dictionary<string, FileSystemWatcher> _claudeWatchers = new();

    public void WatchProject(string? projectPath)
    {
        if (string.IsNullOrEmpty(projectPath)) return;
        if (_claudeWatchers.ContainsKey(projectPath)) return;

        var settingsPath = ClaudeSettingsPath(projectPath);
        var dir = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        try
        {
            var w = new FileSystemWatcher(dir, "settings.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            w.Changed += (_, _) => DispatcherInvoke(() => LoadClaudeTasks(projectPath));
            _claudeWatchers[projectPath] = w;

            LoadClaudeTasks(projectPath);
        }
        catch { }
    }

    public void UnwatchProject(string? projectPath)
    {
        if (projectPath == null || !_claudeWatchers.Remove(projectPath, out var w)) return;
        try { w.Dispose(); } catch { }
    }

    private void StartClaudeWatcher()
    {
    }

    private void LoadOpenCodeTodos(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;
            var roomId = Path.GetFileNameWithoutExtension(filePath);
            var json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json) || json == "null") return;

            var raw = JsonSerializer.Deserialize<List<TodoRaw>>(json);
            if (raw == null) return;

            _knownOpenCodeRooms.Add(roomId);

            for (int i = Tasks.Count - 1; i >= 0; i--)
            {
                if (Tasks[i].Source == $"opencode:{roomId}")
                    Tasks.RemoveAt(i);
            }

            foreach (var item in raw)
            {
                var content = item.content?.Trim() ?? "";
                var status = NormalizeStatus(item.status);
                if (string.IsNullOrEmpty(content)) continue;

                var sid = item.id ?? Guid.NewGuid().ToString("N");
                Tasks.Add(new Models.TaskItem
                {
                    Id = sid,
                    Description = content,
                    Status = status,
                    Priority = NormalizePriority(item.priority),
                    Source = $"opencode:{roomId}"
                });
            }

            TasksChanged?.Invoke();
        }
        catch { }
    }

    private void RemoveOpenCodeTodos(string filePath)
    {
        var roomId = Path.GetFileNameWithoutExtension(filePath);
        for (int i = Tasks.Count - 1; i >= 0; i--)
        {
            if (Tasks[i].Source == $"opencode:{roomId}")
                Tasks.RemoveAt(i);
        }
        TasksChanged?.Invoke();
    }

    private void LoadClaudeTasks(string projectPath)
    {
        try
        {
            var path = ClaudeSettingsPath(projectPath);
            if (!File.Exists(path)) return;

            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tasks", out var tasksEl)) return;

            for (int i = Tasks.Count - 1; i >= 0; i--)
            {
                if (Tasks[i].Source == $"claude:{projectPath}")
                    Tasks.RemoveAt(i);
            }

            foreach (var t in tasksEl.EnumerateArray())
            {
                var content = t.TryGetProperty("content", out var c) ? c.GetString()?.Trim() ?? "" : "";
                var status = t.TryGetProperty("status", out var s) ? NormalizeStatus(s.GetString()) : "pending";
                var priority = t.TryGetProperty("priority", out var pr) ? NormalizePriority(pr.GetString()) : "medium";
                var id = t.TryGetProperty("id", out var idEl) ? idEl.GetString() : Guid.NewGuid().ToString("N");
                if (string.IsNullOrEmpty(content)) continue;

                Tasks.Add(new Models.TaskItem
                {
                    Id = id ?? Guid.NewGuid().ToString("N"),
                    Description = content,
                    Status = status,
                    Priority = priority,
                    Source = $"claude:{projectPath}"
                });
            }

            TasksChanged?.Invoke();
        }
        catch { }
    }

    private static string NormalizeStatus(string? s) => s?.ToLowerInvariant() switch
    {
        "in_progress" or "inprogress" => "in_progress",
        "completed" or "done" => "completed",
        "cancelled" or "canceled" => "cancelled",
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
        foreach (var w in _claudeWatchers.Values)
            try { w.Dispose(); } catch { }
        _claudeWatchers.Clear();
    }
}

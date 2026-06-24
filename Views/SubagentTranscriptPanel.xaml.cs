using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DevezCode.Models;
using DevezCode.Services;

namespace DevezCode.Views;

public sealed partial class SubagentTranscriptPanel : UserControl
{
    private string? _jsonlPath;
    private FileSystemWatcher? _watcher;
    private SubagentStatusItem? _item;

    public SubagentTranscriptPanel()
    {
        InitializeComponent();
        Unloaded += (_, _) => StopWatching();
    }

    public void ShowTranscript(SubagentStatusItem item, bool startWatching)
    {
        _item = item;
        AgentTypeText.Text = item.AgentType;
        AgentIdText.Text = item.AgentId;
        StatusText.Text = item.Status switch
        {
            "running" => "●", "completed" => "✓",
            "error" => "✕", "cancelled" => "―",
            _ => ""
        };

        _jsonlPath = SubagentStatusService.ResolveConversationPath(item.RoomId, item.AgentId);

        if (!string.IsNullOrWhiteSpace(_jsonlPath) && File.Exists(_jsonlPath))
        {
            LoadTranscript();
            if (startWatching) StartWatching();
        }
        else
        {
            ShowFallback(item);
        }
    }

    private void LoadTranscript()
    {
        try
        {
            var entries = new ObservableCollection<TranscriptEntry>();
            if (_jsonlPath != null && File.Exists(_jsonlPath))
            {
                foreach (var line in File.ReadAllLines(_jsonlPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                        if (type is "attachment") continue;
                        if (!root.TryGetProperty("timestamp", out var ts)) continue;

                        var content = type switch
                        {
                            "user" => ExtractUserContent(root),
                            "assistant" => ExtractAssistantContent(root),
                            _ => null
                        };
                        if (!string.IsNullOrWhiteSpace(content) && type != null)
                            entries.Add(new TranscriptEntry
                            {
                                Role = type,
                                Content = content,
                                Timestamp = FormatTimestamp(ts.GetString())
                            });
                    }
                    catch { }
                }
            }

            TranscriptList.ItemsSource = entries;
            if (entries.Count > 0)
            {
                TranscriptScroll.Visibility = Visibility.Visible;
                FallbackScroll.Visibility = Visibility.Collapsed;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => TranscriptScroll.ScrollToBottom()));
            }
            else if (_item != null) ShowFallback(_item);
        }
        catch { }
    }

    private void StartWatching()
    {
        if (_jsonlPath == null) return;
        StopWatching();
        try
        {
            var dir = Path.GetDirectoryName(_jsonlPath);
            var file = Path.GetFileName(_jsonlPath);
            if (dir == null) return;
            _watcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += (_, _) =>
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new Action(LoadTranscript));
        }
        catch { }
    }

    private void StopWatching()
    {
        if (_watcher != null)
        {
            try { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); }
            catch { }
            _watcher = null;
        }
    }

    private void ShowFallback(SubagentStatusItem item)
    {
        TranscriptScroll.Visibility = Visibility.Collapsed;
        FallbackScroll.Visibility = Visibility.Visible;
        FallbackPrompt.Text = string.IsNullOrWhiteSpace(item.Prompt)
            ? "대화 transcript 없음"
            : item.Prompt;
    }

    private static string ExtractUserContent(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg)) return "";
        if (!msg.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var b in content.EnumerateArray())
                if (b.TryGetProperty("type", out var bt) && bt.GetString() == "text"
                    && b.TryGetProperty("text", out var txt))
                    parts.Add(txt.GetString() ?? "");
            return string.Join("\n", parts);
        }
        return content.ToString();
    }

    private static string ExtractAssistantContent(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg)) return "";
        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return msg.ToString();
        var parts = new List<string>();
        foreach (var b in content.EnumerateArray())
        {
            var type = b.TryGetProperty("type", out var bt) ? bt.GetString() : null;
            switch (type)
            {
                case "text":
                    if (b.TryGetProperty("text", out var tx)) parts.Add(tx.GetString() ?? "");
                    break;
                case "tool_use":
                    var name = b.TryGetProperty("name", out var nm) ? nm.GetString() ?? "?" : "?";
                    var inp = b.TryGetProperty("input", out var ip) ? ip.ToString() : "";
                    if (inp.Length > 150) inp = inp[..150] + "...";
                    parts.Add($"🛠 {name}({inp})");
                    break;
                default: parts.Add(b.ToString()); break;
            }
        }
        return string.Join("\n\n", parts);
    }

    private static string FormatTimestamp(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return "";
        if (DateTime.TryParse(iso, out var dt)) return dt.ToLocalTime().ToString("HH:mm:ss");
        return iso;
    }
}

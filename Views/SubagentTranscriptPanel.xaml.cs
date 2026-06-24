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
    public SubagentTranscriptPanel()
    {
        InitializeComponent();
    }

    public void ShowTranscript(SubagentStatusItem item)
    {
        AgentTypeText.Text = item.AgentType;
        AgentIdText.Text = item.AgentId;
        StatusText.Text = item.Status switch
        {
            "running" => "진행 중", "completed" => "완료",
            "error" => "오류", "cancelled" => "취소됨",
            _ => item.Status
        };

        var path = SubagentStatusService.ResolveConversationPath(item.RoomId, item.AgentId);

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            LoadTranscript(path);
        }
        else
        {
            ShowFallback(item);
        }
    }

    private void LoadTranscript(string path)
    {
        try
        {
            var entries = new ObservableCollection<TranscriptEntry>();
            foreach (var line in File.ReadAllLines(path))
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

            TranscriptList.ItemsSource = entries;
            if (entries.Count > 0)
            {
                TranscriptScroll.Visibility = Visibility.Visible;
                FallbackScroll.Visibility = Visibility.Collapsed;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => TranscriptScroll.ScrollToBottom()));
            }
            else ShowFallback(null);
        }
        catch
        {
            ShowFallback(null);
        }
    }

    private void ShowFallback(SubagentStatusItem? item)
    {
        TranscriptScroll.Visibility = Visibility.Collapsed;
        FallbackScroll.Visibility = Visibility.Visible;
        if (item == null) return;
        FbAgentId.Text = item.AgentId;
        FbType.Text = item.AgentType;
        FbStatus.Text = item.Status;
        FbPrompt.Text = item.Prompt;
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

    public event EventHandler? CloseRequested;
    private void CloseBtn_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}

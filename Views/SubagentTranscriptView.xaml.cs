using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using DevezCode.Models;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>TranscriptList ItemsControl 바인딩용 메시지 항목.</summary>
public sealed class TranscriptEntry
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public string Timestamp { get; set; } = "";
}

/// <summary>서브에이전트 대화 내역을 보여주는 전용 다이얼로그.
/// .jsonl 이 있으면 채팅 UI로 랜더링, 없으면 훅 JSON 데이터를 구조화된 정보로 표시.</summary>
public partial class SubagentTranscriptView : Window
{
    private readonly SubagentStatusItem _item;
    private string? _transcriptPath;

    /// <summary>사용자가 "파일로 열기"를 선택한 경우 그 경로. null 이면 일반 닫기.</summary>
    public string? RequestedFilePath { get; private set; }

    public SubagentTranscriptView(SubagentStatusItem item)
    {
        InitializeComponent();
        _item = item;

        // 헤더 정보 채움
        AgentTypeText.Text = item.AgentType;
        AgentIdText.Text = item.AgentId;
        StatusText.Text = item.Status switch
        {
            "running" => "진행 중",
            "completed" => "완료",
            "error" => "오류",
            "cancelled" => "취소됨",
            _ => item.Status
        };
        PromptPreviewText.Text = item.Prompt;

        // 상태에 따른 뱃지 색상
        if (item.Status == "running")
        {
            var warn = TryFindResource("WarningBrush") as System.Windows.Media.Brush;
            if (warn != null) StatusBadge.Background = warn;
            StatusText.Foreground = System.Windows.Media.Brushes.White;
        }
        else if (item.Status is "error" or "cancelled")
        {
            var danger = TryFindResource("DangerBrush") as System.Windows.Media.Brush;
            if (danger != null) StatusBadge.Background = danger;
            StatusText.Foreground = System.Windows.Media.Brushes.White;
        }

        Loaded += OnLoaded;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = true; Close(); } };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _transcriptPath = SubagentStatusService.ResolveConversationPath(_item.RoomId, _item.AgentId);

        if (!string.IsNullOrWhiteSpace(_transcriptPath) && File.Exists(_transcriptPath))
        {
            LoadTranscript(_transcriptPath);
        }
        else
        {
            ShowFallback();
        }
    }

    /// <summary>JSONL 파일을 읽어 채팅 메시지 목록으로 랜더링.</summary>
    private void LoadTranscript(string path)
    {
        try
        {
            var entries = new ObservableCollection<TranscriptEntry>();
            var lines = File.ReadAllLines(path);

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type is "attachment") continue; // 시스템 이벤트 스킵

                    if (!root.TryGetProperty("timestamp", out var tsEl)) continue;
                    var timeFormatted = FormatTimestamp(tsEl.GetString());

                    var content = type switch
                    {
                        "user" => ExtractUserContent(root),
                        "assistant" => ExtractAssistantContent(root),
                        _ => null
                    };

                    if (!string.IsNullOrWhiteSpace(content) && type is not null)
                        entries.Add(new TranscriptEntry { Role = type, Content = content, Timestamp = timeFormatted });
                }
                catch { /* 비정상 라인 스킵 */ }
            }

            TranscriptList.ItemsSource = entries;

            if (entries.Count > 0)
            {
                TranscriptScrollViewer.Visibility = Visibility.Visible;
                FallbackScrollViewer.Visibility = Visibility.Collapsed;

                // 랜더링 후 하단으로 자동 스크롤
                Loaded += (_, _) => Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => TranscriptScrollViewer.ScrollToBottom()));
            }
            else
            {
                ShowFallback();
            }
        }
        catch
        {
            ShowFallback();
        }
    }

    private static string ExtractUserContent(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg)) return "";
        if (!msg.TryGetProperty("content", out var content)) return "";

        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";

        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text"
                    && block.TryGetProperty("text", out var txt))
                    parts.Add(txt.GetString() ?? "");
            }
            return string.Join("\n", parts);
        }

        return content.ToString();
    }

    private static string ExtractAssistantContent(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg)) return "";
        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            // content가 없거나 배열이 아니면 메시지 전체를 문자열로 시도
            return msg.ToString();
        }

        var parts = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            var type = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
            switch (type)
            {
                case "text":
                    if (block.TryGetProperty("text", out var txt))
                        parts.Add(txt.GetString() ?? "");
                    break;
                case "tool_use":
                    var toolName = block.TryGetProperty("name", out var nm) ? nm.GetString() ?? "unknown" : "unknown";
                    var inputStr = block.TryGetProperty("input", out var inp) ? inp.ToString() : "";
                    if (inputStr.Length > 200) inputStr = inputStr[..200] + "...";
                    parts.Add($"🛠 {toolName}({inputStr})");
                    break;
                case "tool_result":
                    var resultContent = block.TryGetProperty("content", out var tc) ? tc.ToString() : "";
                    if (resultContent.Length > 300) resultContent = resultContent[..300] + "...";
                    parts.Add($"[결과]\n{resultContent}");
                    break;
                default:
                    parts.Add(block.ToString());
                    break;
            }
        }
        return string.Join("\n\n", parts);
    }

    private static string FormatTimestamp(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return "";
        if (DateTime.TryParse(iso, out var dt))
            return dt.ToLocalTime().ToString("HH:mm:ss");
        return iso;
    }

    /// <summary>훅 JSON 데이터를 구조화된 정보 뷰로 표시 (transcript 없을 때).</summary>
    private void ShowFallback()
    {
        TranscriptScrollViewer.Visibility = Visibility.Collapsed;
        FallbackScrollViewer.Visibility = Visibility.Visible;

        FallbackAgentId.Text = _item.AgentId;
        FallbackAgentType.Text = _item.AgentType;
        FallbackStatus.Text = _item.Status switch
        {
            "running" => "진행 중",
            "completed" => "완료",
            "error" => "오류",
            "cancelled" => "취소됨",
            _ => _item.Status
        };
        FallbackToolCalls.Text = $"{_item.ToolCallCount}회";
        FallbackStarted.Text = _item.StartedAt != DateTime.MinValue
            ? _item.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "-";
        FallbackEnded.Text = _item.EndedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
        FallbackPrompt.Text = _item.Prompt;

        if (!string.IsNullOrEmpty(_item.Reason))
        {
            FallbackReasonPanel.Visibility = Visibility.Visible;
            FallbackReason.Text = _item.Reason;
        }

        // transcript 없으면 파일 열기 버튼 숨김
        OpenAsFileBtn.Visibility = Visibility.Collapsed;
    }

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OpenAsFileBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_transcriptPath) && File.Exists(_transcriptPath))
        {
            RequestedFilePath = _transcriptPath;
            DialogResult = true;
            Close();
        }
    }
}

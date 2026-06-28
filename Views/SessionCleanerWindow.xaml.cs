using System.Windows;
using System.Windows.Documents;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Globalization;
using System.Windows.Media.Imaging;
using DevezCode.Models;
using DevezCode.Services;

namespace DevezCode.Views;

public partial class SessionCleanerWindow : Window
{
    private CleanerAgentKind _current;
    private readonly List<CleanerAgentKind> _visibleAgents = new();
    private readonly Dictionary<CleanerAgentKind, CleanerScanInfo?> _counts = new();
    private readonly HashSet<CleanerAgentKind> _loading = new();

    public SessionCleanerWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            BuildVisibleAgents();
            StartRefreshAll();
        };
        SizeChanged += (_, _) => ApplyRoundedClip();
    }

    private void ApplyRoundedClip()
    {
        var w = ContentBorder.ActualWidth;
        var h = ContentBorder.ActualHeight;
        if (w <= 0 || h <= 0) return;
        ContentRoot.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 13, 13);
    }

    private void BuildVisibleAgents()
    {
        var enabled = SettingsService.LoadEnabledAgents().ToHashSet(StringComparer.OrdinalIgnoreCase);

        SetAgentVisible(CleanerAgentKind.Claude, ClaudeCatBtn, enabled.Contains("claude"));
        SetAgentVisible(CleanerAgentKind.OpenCode, OpenCodeCatBtn, enabled.Contains("opencode"));
        SetAgentVisible(CleanerAgentKind.Gajae, GajaeCatBtn, enabled.Contains("gajae"));

        EmptyPanel.Visibility = _visibleAgents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ContentPanel.Visibility = _visibleAgents.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_visibleAgents.Count > 0) SetActive(_visibleAgents[0]);
    }

    private void SetAgentVisible(CleanerAgentKind kind, Button button, bool visible)
    {
        button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) { _visibleAgents.Add(kind); _counts[kind] = null; }
    }

    private void Category_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        SetActive(tag switch
        {
            "opencode" => CleanerAgentKind.OpenCode,
            "gajae" => CleanerAgentKind.Gajae,
            _ => CleanerAgentKind.Claude,
        });
        ApplyCurrentCount();
    }

    private void SetActive(CleanerAgentKind kind)
    {
        _current = kind;
        var active = (Brush)FindResource("PanelBrush");
        var primary = (Brush)FindResource("PrimaryBrush");
        var text = (Brush)FindResource("TextBrush");

        ApplyCat(ClaudeCatBtn, kind == CleanerAgentKind.Claude, active, primary, text);
        ApplyCat(OpenCodeCatBtn, kind == CleanerAgentKind.OpenCode, active, primary, text);
        ApplyCat(GajaeCatBtn, kind == CleanerAgentKind.Gajae, active, primary, text);

        var agentId = kind switch
        {
            CleanerAgentKind.OpenCode => "opencode",
            CleanerAgentKind.Gajae => "gajae",
            _ => "claude",
        };
        AgentTitle.Text = kind switch
        {
            CleanerAgentKind.OpenCode => "OpenCode",
            CleanerAgentKind.Gajae => "Gajae Code",
            _ => "Claude",
        };
        AgentTitleIcon.Source = AgentImageConverter.Instance.Convert(agentId, typeof(ImageSource), null, CultureInfo.CurrentCulture) is Uri uri
            ? new BitmapImage(uri)
            : null;
        AgentDescription.Text = "DevezCode에서 관리중이지 않은 세션을 표시합니다.";
    }

    private static void ApplyCat(Button button, bool selected, Brush active, Brush primary, Brush text)
    {
        button.Background = selected ? active : Brushes.Transparent;
        button.Foreground = selected ? primary : text;
    }

    private void StartRefreshAll()
    {
        foreach (var kind in _visibleAgents)
            _ = RefreshAgentAsync(kind);
        ApplyCurrentCount();
    }

    private async Task RefreshAgentAsync(CleanerAgentKind kind)
    {
        _loading.Add(kind);
        if (kind == _current) ApplyCurrentCount();
        try
        {
            var info = await Task.Run(() => SessionCleanerService.GetScanInfo(kind));
            _counts[kind] = info;
        }
        catch
        {
            _counts[kind] = null;
        }
        finally
        {
            _loading.Remove(kind);
            if (kind == _current) ApplyCurrentCount();
        }
    }

    private void ApplyCurrentCount()
    {
        if (_visibleAgents.Count == 0) return;
        if (_loading.Contains(_current))
        {
            AgentCountText.Visibility = Visibility.Collapsed;
            CountSpinner.Visibility = Visibility.Visible;
            DeleteBtn.IsEnabled = false;
            return;
        }

        if (_counts.TryGetValue(_current, out var info) && info is { } scan)
        {
            CountSpinner.Visibility = Visibility.Collapsed;
            AgentCountText.Visibility = Visibility.Visible;
            AgentCountText.Inlines.Clear();
            AgentCountText.Inlines.Add(new Run($"{scan.Count} ") { FontSize = 32, FontWeight = FontWeights.SemiBold });
            AgentCountText.Inlines.Add(new Run($"({FormatBytes(scan.Bytes)})") { FontSize = 17, FontWeight = FontWeights.SemiBold });
            DeleteBtn.IsEnabled = scan.Count > 0;
            return;
        }

        CountSpinner.Visibility = Visibility.Collapsed;
        AgentCountText.Visibility = Visibility.Visible;
        AgentCountText.Inlines.Clear();
        AgentCountText.Inlines.Add(new Run("-") { FontSize = 32, FontWeight = FontWeights.SemiBold });
        DeleteBtn.IsEnabled = false;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAgentAsync(_current);

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    private async Task<CleanerScanInfo> EnsureCurrentScanAsync()
    {
        if (_counts.TryGetValue(_current, out var cached) && cached is { } scan) return scan;
        await RefreshAgentAsync(_current);
        return _counts.TryGetValue(_current, out var count) && count is { } v ? v : new CleanerScanInfo(0, 0);
    }

    private async void DeleteCurrent_Click(object sender, RoutedEventArgs e)
    {
        var scan = await EnsureCurrentScanAsync();
        if (scan.Count <= 0)
        {
            await RefreshAgentAsync(_current);
            return;
        }

        var name = AgentTitle.Text;
        var ok = ConfirmDialog.Show("DevezCode에서 관리중이지 않은 세션 삭제",
            $"{name}의 DevezCode에서 관리중이지 않은 세션 {scan.Count}개를 PC에서 완전 삭제합니다.\n\n" +
            "DevezCode가 현재 관리 중인 세션은 삭제 대상에서 제외됩니다.",
            okLabel: "삭제", danger: true, iconKey: "IconTrash2");
        if (!ok) return;

        AgentCountText.Visibility = Visibility.Collapsed;
        CountSpinner.Visibility = Visibility.Visible;
        DeleteBtn.IsEnabled = false;
        var result = await Task.Run(() => SessionCleanerService.DeleteUnmanaged(_current));
        await RefreshAgentAsync(_current);

        var message = result.failed == 0
            ? $"{name}의 DevezCode에서 관리중이지 않은 세션 {result.deleted}개를 삭제했습니다."
            : $"{name}의 DevezCode에서 관리중이지 않은 세션 {result.deleted}개를 삭제했습니다.\n삭제 실패 {result.failed}개는 파일 잠금 또는 에이전트 CLI 제한으로 남았습니다.";
        ConfirmDialog.Alert("세션 클리너", message);
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}

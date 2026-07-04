using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
        Opacity = 0;
        Loaded += (_, _) =>
        {
            BuildVisibleAgents();
            StartRefreshAll();
        };
        SizeChanged += (_, _) => ApplyRoundedClip();
        ContentRendered += (_, _) => AnimateOpen();
    }

    private void AnimateOpen()
    {
        // 페이드 동안 콘텐츠 전체를 BitmapCache 로 캐시 → 매 프레임 DropShadow 블러 재계산을 없앤다
        // (플러그인 팝업과 동일 패턴). 완료 시 캐시 해제.
        var dpi = VisualTreeHelper.GetDpi(this);
        RootLayer.CacheMode = new BitmapCache { RenderAtScale = dpi.DpiScaleX };

        var dur  = new Duration(TimeSpan.FromMilliseconds(220));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var anim = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
        anim.Completed += (_, _) => RootLayer.CacheMode = null;
        BeginAnimation(OpacityProperty, anim);
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
        VacuumSection.Visibility = kind == CleanerAgentKind.OpenCode ? Visibility.Visible : Visibility.Collapsed;
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
            AgentCountText.Text = $"{scan.Count}";
            AgentCountText.FontSize = 32;
            AgentCapacityText.Text = $"({FormatBytes(scan.Bytes)})";
            AgentCapacityText.FontSize = 17;
            DeleteBtn.IsEnabled = scan.Count > 0;
            return;
        }

        CountSpinner.Visibility = Visibility.Collapsed;
        AgentCountText.Visibility = Visibility.Visible;
        AgentCountText.Text = "-";
        AgentCountText.FontSize = 32;
        AgentCapacityText.Text = "";
        DeleteBtn.IsEnabled = false;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAgentAsync(_current);

    private async void VacuumBtn_Click(object sender, RoutedEventArgs e)
    {
        VacuumBtn.IsEnabled = false;
        VacuumBtn.Content = "정리 중...";
        try
        {
            var result = await Task.Run(() => SessionCleanerService.VacuumOpenCodeDb());
            ConfirmDialog.Alert("OpenCode DB 정리", result);
            await RefreshAgentAsync(CleanerAgentKind.OpenCode);
        }
        finally
        {
            VacuumBtn.IsEnabled = true;
            VacuumBtn.Content = "OpenCode DB 정리";
        }
    }

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
        var extra = _current == CleanerAgentKind.OpenCode
            ? "\n\n⚠ 빠른 삭제를 위해 실행 중인 OpenCode가 모두 종료됩니다. 진행 중인 OpenCode 작업이 중단될 수 있습니다."
            : "";
        var ok = ConfirmDialog.Show("관리중이지 않은 세션 삭제",
            $"{name}의 DevezCode에서 관리중이지 않은 세션 {scan.Count}개를 PC에서 완전 삭제합니다.\n" +
            "DevezCode가 현재 관리 중인 세션은 삭제 대상에서 제외됩니다." + extra,
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

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DevezCode.Models;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>Claude Code MCP 라이브 상태/제어 대시보드. 편집 위주인 McpManagerDialog 와 달리
/// 실시간 모니터링 + 런타임 제어(재조회 폴링 · OAuth 인증/로그아웃 · on/off 토글 · 상세 get)에 집중.
/// 모든 제어는 <see cref="ClaudeMcpBackend"/> 의 CLI 경유 정적 메서드로 수행.</summary>
public partial class McpControlDialog : UserControl
{
    public event EventHandler? CloseRequested;

    private readonly ClaudeMcpBackend _backend = new();
    private readonly ObservableCollection<McpServer> _servers = new();
    private readonly DispatcherTimer _timer;
    private bool _refreshing;
    private bool _disposed;

    public McpControlDialog()
    {
        InitializeComponent();
        ServerList.ItemsSource = _servers;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => { LoadServers(); await RefreshAsync(); StartTimer(); };
        Unloaded += (_, _) => { _disposed = true; _timer.Stop(); };
    }

    // ── 데이터 로드 ────────────────────────────────────────────────
    private void LoadServers()
    {
        _servers.Clear();
        var disabled = ClaudeMcpBackend.LoadDisabledNames();
        foreach (var s in _backend.Load())
        {
            s.Enabled = !disabled.Contains(s.Name);
            _servers.Add(s);
        }
        EmptyState.Visibility = _servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async System.Threading.Tasks.Task RefreshAsync()
    {
        if (_refreshing || _disposed) return;
        _refreshing = true;
        try
        {
            await _backend.RefreshStatusAsync(_servers);
            // disabled 목록으로 토글 상태 재동기화(외부 변경 반영)
            var disabled = ClaudeMcpBackend.LoadDisabledNames();
            foreach (var s in _servers) s.Enabled = !disabled.Contains(s.Name);
            LastRefreshedText.Text = "갱신: " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch { /* 조용히 유지 */ }
        finally { _refreshing = false; }
    }

    // ── 툴바 ──────────────────────────────────────────────────────
    private void StartTimer()
    {
        if (AutoRefreshToggle.IsChecked == true) _timer.Start();
    }

    private void AutoRefreshToggle_Click(object sender, RoutedEventArgs e)
    {
        if (AutoRefreshToggle.IsChecked == true) _timer.Start();
        else _timer.Stop();
    }

    private void IntervalCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (IntervalCombo.SelectedItem is ComboBoxItem it && it.Tag is string tag
            && int.TryParse(tag, out var secs) && secs > 0)
        {
            _timer.Interval = TimeSpan.FromSeconds(secs);
        }
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    // ── 행 액션 ──────────────────────────────────────────────────
    private static McpServer? ServerOf(object sender) => (sender as FrameworkElement)?.DataContext as McpServer;

    private void Login_Click(object sender, RoutedEventArgs e)
    {
        if (ServerOf(sender) is not McpServer s) return;
        ClaudeMcpBackend.SpawnLogin(s.Name);
        // 인증 창은 별도 콘솔에서 사용자가 완료 — 몇 초 뒤 상태가 반영되도록 다음 폴링에 맡긴다.
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (ServerOf(sender) is not McpServer s) return;
        if (!ConfirmDialog.Show("MCP 로그아웃", $"'{s.Name}' 의 OAuth 자격증명을 삭제할까요?", "로그아웃"))
            return;
        var result = await ClaudeMcpBackend.LogoutAsync(s.Name);
        ShowDetail($"logout · {s.Name}", string.IsNullOrWhiteSpace(result) ? "완료." : result);
        await RefreshAsync();
    }

    private async void Detail_Click(object sender, RoutedEventArgs e)
    {
        if (ServerOf(sender) is not McpServer s) return;
        ShowDetail($"get · {s.Name}", "조회 중…");
        var result = await ClaudeMcpBackend.GetAsync(s.Name);
        ShowDetail($"get · {s.Name}", string.IsNullOrWhiteSpace(result) ? "(출력 없음)" : result);
    }

    private void EnableToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || ServerOf(sender) is not McpServer s) return;
        var enabled = cb.IsChecked == true;
        try
        {
            ClaudeMcpBackend.SetServerEnabledGlobally(s.Name, enabled);
            s.Enabled = enabled;
        }
        catch (Exception ex)
        {
            cb.IsChecked = !enabled; // 실패 시 원복
            ConfirmDialog.Alert("토글 실패", ex.Message);
        }
    }

    // ── 상세 패널 ─────────────────────────────────────────────────
    private void ShowDetail(string title, string body)
    {
        DetailTitle.Text = title;
        DetailText.Text = body;
        DetailPanel.Visibility = Visibility.Visible;
    }

    private void DetailClose_Click(object sender, RoutedEventArgs e)
        => DetailPanel.Visibility = Visibility.Collapsed;

    // ── 창 제어 ───────────────────────────────────────────────────
    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            Window.GetWindow(this)?.DragMove();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => TryClose();

    public void TryClose()
    {
        _timer.Stop();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}

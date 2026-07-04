using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DevezCode.Models;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>에이전트별 플러그인 관리 대시보드. 좌측에 설정에서 켜진 에이전트만 노출하고,
/// 우측에 그 에이전트의 플러그인을 띄운다.
/// - claude/gjc: 토글·상세·업데이트·삭제 + 설치/마켓플레이스
/// - opencode: 추가·삭제만(네이티브 on/off 없음)
/// 모든 제어는 <see cref="PluginControlService"/> 로 위임.</summary>
public partial class PluginControlDialog : UserControl
{
    public event EventHandler? CloseRequested;

    private readonly ObservableCollection<PluginItem> _items = new();
    private readonly ObservableCollection<ClaudeMarketplace> _markets = new();
    private readonly List<(string id, Button btn)> _agentButtons = new();
    private string _agent = "";
    private bool _busy;
    private bool _disposed;

    public PluginControlDialog()
    {
        InitializeComponent();
        PluginList.ItemsSource = _items;
        MarketList.ItemsSource = _markets;
        Loaded += (_, _) => BuildAgents();
        Unloaded += (_, _) => _disposed = true;
    }

    // ── 좌측 에이전트 목록(설정에서 켜진 것만) ────────────────────
    private void BuildAgents()
    {
        var enabled = SettingsService.LoadEnabledAgents().ToHashSet(StringComparer.OrdinalIgnoreCase);
        _agentButtons.Clear();
        Show(ClaudeCatBtn, "claude", enabled.Contains("claude"));
        Show(OpenCodeCatBtn, "opencode", enabled.Contains("opencode"));
        Show(GajaeCatBtn, "gajae", enabled.Contains("gajae"));

        var has = _agentButtons.Count > 0;
        NoAgentPanel.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        ContentPanel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (has) SetActiveAgent(_agentButtons[0].id);
    }

    private void Show(Button btn, string id, bool visible)
    {
        btn.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) _agentButtons.Add((id, btn));
    }

    private void Category_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string id && id != _agent) SetActiveAgent(id);
    }

    private async void SetActiveAgent(string id)
    {
        _agent = id;
        var primary = (Brush)FindResource("PrimaryBrush");
        var text = (Brush)FindResource("TextBrush");
        var panel = (Brush)FindResource("PanelBrush");
        foreach (var (aid, btn) in _agentButtons)
        {
            var sel = aid == id;
            btn.Background = sel ? panel : Brushes.Transparent;
            btn.Foreground = sel ? primary : text;
            btn.FontWeight = sel ? FontWeights.SemiBold : FontWeights.Normal;
        }

        // 에이전트별 UI 문구/버튼 노출
        bool full = id is "claude" or "gajae";       // 토글/상세/업데이트/마켓플레이스 지원
        InstallBtnText.Text = full ? "설치" : "추가";
        MarketBtn.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = full ? "설치된 플러그인이 없습니다" : "추가된 플러그인이 없습니다";
        MarketOverlay.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Collapsed;

        await RefreshAsync();
    }

    // ── 목록 로드 ─────────────────────────────────────────────────
    private async Task RefreshAsync()
    {
        if (_busy || _disposed) return;
        _busy = true;
        var agent = _agent;
        _items.Clear();
        EmptyState.Visibility = Visibility.Collapsed;
        RefreshSpinner.Visibility = Visibility.Visible;
        try
        {
            var list = await PluginControlService.ListAsync(agent);
            if (_disposed || agent != _agent) return;   // 그 사이 에이전트 전환되면 폐기
            foreach (var p in list)
            {
                _items.Add(p);
                await Task.Delay(40);
                if (_disposed || agent != _agent) return;
            }
            EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LastRefreshedText.Text = "갱신: " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch { }
        finally
        {
            _busy = false;
            if (!_disposed) RefreshSpinner.Visibility = Visibility.Collapsed;
        }
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    // ── 플러그인 행 액션 ──────────────────────────────────────────
    private static PluginItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as PluginItem;

    private async void EnableToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || ItemOf(sender) is not PluginItem p) return;
        var target = cb.IsChecked == true;
        cb.IsEnabled = false;
        try
        {
            var result = target ? await PluginControlService.EnableAsync(p)
                                 : await PluginControlService.DisableAsync(p);
            p.Enabled = target;
            if (!string.IsNullOrWhiteSpace(result))
                ShowDetail($"{(target ? "enable" : "disable")} · {p.Name}", result);
        }
        catch (Exception ex) { cb.IsChecked = !target; ConfirmDialog.Alert("토글 실패", ex.Message); }
        finally { cb.IsEnabled = true; }
    }

    private async void Detail_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not PluginItem p) return;
        ShowDetail($"상세 · {p.Name}", "조회 중…");
        var result = await PluginControlService.DetailsAsync(p);
        ShowDetail($"상세 · {p.Name}", string.IsNullOrWhiteSpace(result) ? "(출력 없음)" : result);
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not PluginItem p) return;
        ShowDetail($"업데이트 · {p.Name}", "업데이트 중… (재시작이 필요할 수 있습니다)");
        var result = await PluginControlService.UpdateAsync(p);
        ShowDetail($"업데이트 · {p.Name}", string.IsNullOrWhiteSpace(result) ? "완료." : result);
        await RefreshAsync();
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not PluginItem p) return;
        if (p.IsProtected) { ConfirmDialog.Alert("제거 불가", "이 플러그인은 DevezCode 연동용이라 제거할 수 없습니다."); return; }
        var verb = p.AgentId == "opencode" ? "제거" : "삭제";
        if (!ConfirmDialog.Show($"플러그인 {verb}", $"'{p.Name}' 을(를) {verb}할까요?", verb, danger: true))
            return;
        ShowDetail($"{verb} · {p.Name}", $"{verb} 중…");
        var result = await PluginControlService.UninstallAsync(p);
        ShowDetail($"{verb} · {p.Name}", string.IsNullOrWhiteSpace(result) ? "완료." : result);
        await RefreshAsync();
    }

    // ── 설치/추가 ─────────────────────────────────────────────────
    private void InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        var (title, msg) = _agent switch
        {
            "opencode" => ("플러그인 추가", "추가할 npm 모듈 이름을 입력하세요. (예: @scope/name 또는 name@latest)"),
            "gajae"    => ("플러그인 설치", "설치할 플러그인 이름/패키지/경로를 입력하세요."),
            _          => ("플러그인 설치", "설치할 플러그인 이름을 입력하세요. 특정 마켓플레이스는 name@marketplace 형식."),
        };
        var input = PromptDialog.Show(title, msg, okLabel: _agent == "opencode" ? "추가" : "설치");
        if (string.IsNullOrWhiteSpace(input)) return;
        PluginControlService.SpawnInstall(_agent, input);
        ShowDetail(title, $"'{input}' 작업을 별도 콘솔 창에서 진행합니다.\n완료되면 [새로고침] 으로 목록을 갱신하세요.");
    }

    // ── 마켓플레이스 오버레이 (claude/gjc) ────────────────────────
    private async void MarketBtn_Click(object sender, RoutedEventArgs e)
    {
        MarketOverlay.Visibility = Visibility.Visible;
        await LoadMarketsAsync();
    }

    private void MarketClose_Click(object sender, RoutedEventArgs e) => MarketOverlay.Visibility = Visibility.Collapsed;
    private void MarketScrim_Click(object sender, MouseButtonEventArgs e) => MarketOverlay.Visibility = Visibility.Collapsed;

    private async Task LoadMarketsAsync()
    {
        try
        {
            var list = await PluginControlService.MarketplacesAsync(_agent);
            if (_disposed) return;
            _markets.Clear();
            foreach (var m in list) _markets.Add(m);
        }
        catch { }
    }

    private void MarketAdd_Click(object sender, RoutedEventArgs e)
    {
        var input = PromptDialog.Show("마켓플레이스 추가",
            "URL · 로컬 경로 · GitHub 저장소(owner/repo) 중 하나를 입력하세요.", okLabel: "추가");
        if (string.IsNullOrWhiteSpace(input)) return;
        PluginControlService.SpawnMarketplaceAdd(_agent, input);
        ShowDetail("마켓플레이스 추가", $"'{input}' 추가를 별도 콘솔 창에서 진행합니다.\n완료되면 마켓플레이스 목록을 다시 열어 확인하세요.");
    }

    private async void MarketUpdateAll_Click(object sender, RoutedEventArgs e)
    {
        var result = await PluginControlService.MarketplaceUpdateAsync(_agent);
        ShowDetail("마켓플레이스 업데이트", string.IsNullOrWhiteSpace(result) ? "완료." : result);
        await LoadMarketsAsync();
    }

    private static ClaudeMarketplace? MarketOf(object sender) => (sender as FrameworkElement)?.DataContext as ClaudeMarketplace;

    private async void MarketItemUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (MarketOf(sender) is not ClaudeMarketplace m) return;
        var result = await PluginControlService.MarketplaceUpdateAsync(_agent, m.Name);
        ShowDetail($"마켓플레이스 업데이트 · {m.Name}", string.IsNullOrWhiteSpace(result) ? "완료." : result);
    }

    private async void MarketItemRemove_Click(object sender, RoutedEventArgs e)
    {
        if (MarketOf(sender) is not ClaudeMarketplace m) return;
        if (!ConfirmDialog.Show("마켓플레이스 삭제", $"'{m.Name}' 마켓플레이스를 삭제할까요?", "삭제", danger: true))
            return;
        var result = await PluginControlService.MarketplaceRemoveAsync(_agent, m.Name);
        if (!string.IsNullOrWhiteSpace(result)) ShowDetail($"마켓플레이스 삭제 · {m.Name}", result);
        await LoadMarketsAsync();
        await RefreshAsync();
    }

    // ── 상세 패널 ─────────────────────────────────────────────────
    private void ShowDetail(string title, string body)
    {
        DetailTitle.Text = title;
        DetailText.Text = body;
        DetailPanel.Visibility = Visibility.Visible;
    }

    private void DetailClose_Click(object sender, RoutedEventArgs e) => DetailPanel.Visibility = Visibility.Collapsed;

    // ── 창 제어 ───────────────────────────────────────────────────
    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            Window.GetWindow(this)?.DragMove();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => TryClose();

    public void TryClose()
    {
        if (MarketOverlay.Visibility == Visibility.Visible)
        {
            MarketOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}

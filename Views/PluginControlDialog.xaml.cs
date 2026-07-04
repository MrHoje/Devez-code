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

/// <summary>Claude Code 플러그인 관리 대시보드. 좌측 탭(플러그인 / 마켓플레이스) · 중앙 리스트 · 우측 출력 영역.
/// 상세/업데이트/삭제 등 CLI 실행 결과는 우측 출력 영역에 표시. 모든 제어는 <see cref="ClaudePluginService"/> 경유.</summary>
public partial class PluginControlDialog : UserControl
{
    public event EventHandler? CloseRequested;

    private readonly ObservableCollection<ClaudePlugin> _plugins = new();
    private readonly ObservableCollection<ClaudeMarketplace> _markets = new();
    private string _tab = "plugins";
    private bool _busy;
    private bool _disposed;

    public PluginControlDialog()
    {
        InitializeComponent();
        PluginList.ItemsSource = _plugins;
        MarketList.ItemsSource = _markets;
        Loaded += (_, _) => SetTab("plugins");
        Unloaded += (_, _) => _disposed = true;
    }

    // ── 탭 전환 ───────────────────────────────────────────────────
    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string t && t != _tab) SetTab(t);
    }

    private async void SetTab(string tab)
    {
        _tab = tab;
        bool plugins = tab == "plugins";
        var primary = (Brush)FindResource("PrimaryBrush");
        var text = (Brush)FindResource("TextBrush");
        var panel = (Brush)FindResource("PanelBrush");
        PluginTabBtn.Background = plugins ? panel : Brushes.Transparent;
        PluginTabBtn.Foreground = plugins ? primary : text;
        PluginTabBtn.FontWeight = plugins ? FontWeights.SemiBold : FontWeights.Normal;
        MarketTabBtn.Background = plugins ? Brushes.Transparent : panel;
        MarketTabBtn.Foreground = plugins ? text : primary;
        MarketTabBtn.FontWeight = plugins ? FontWeights.Normal : FontWeights.SemiBold;

        PluginActions.Visibility = plugins ? Visibility.Visible : Visibility.Collapsed;
        MarketActions.Visibility = plugins ? Visibility.Collapsed : Visibility.Visible;
        PluginScroll.Visibility = plugins ? Visibility.Visible : Visibility.Collapsed;
        MarketScroll.Visibility = plugins ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Text = plugins ? "설치된 플러그인이 없습니다" : "등록된 마켓플레이스가 없습니다";

        await RefreshAsync();
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_busy || _disposed) return;
        _busy = true;
        var tab = _tab;
        RefreshSpinner.Visibility = Visibility.Visible;
        try
        {
            if (tab == "plugins")
            {
                var list = await ClaudePluginService.ListAsync();
                if (_disposed || _tab != tab) return;
                MergeInto(list);
                UpdateEmpty(_plugins.Count);
            }
            else
            {
                var list = await ClaudePluginService.MarketplacesAsync();
                if (_disposed || _tab != tab) return;
                _markets.Clear();
                foreach (var m in list) _markets.Add(m);
                UpdateEmpty(_markets.Count);
            }
            LastRefreshedText.Text = "갱신: " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch { }
        finally
        {
            _busy = false;
            if (!_disposed) RefreshSpinner.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateEmpty(int count) => EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // 새 스냅샷을 기존 컬렉션에 병합(깜빡임 방지) — 사라진 것 제거 → 삽입 → 내용 바뀐 것만 교체, enabled 만 바뀌면 제자리.
    private void MergeInto(List<ClaudePlugin> list)
    {
        for (int i = _plugins.Count - 1; i >= 0; i--)
            if (!list.Any(n => n.Id == _plugins[i].Id)) _plugins.RemoveAt(i);
        for (int i = 0; i < list.Count; i++)
        {
            var n = list[i];
            int cur = IndexOfId(n.Id);
            if (cur < 0) { _plugins.Insert(i, n); continue; }
            if (CoreSig(_plugins[cur]) != CoreSig(n)) _plugins[cur] = n;
            else if (_plugins[cur].Enabled != n.Enabled) _plugins[cur].Enabled = n.Enabled;
            if (cur != i) _plugins.Move(cur, i);
        }
    }

    private int IndexOfId(string id)
    {
        for (int i = 0; i < _plugins.Count; i++) if (_plugins[i].Id == id) return i;
        return -1;
    }

    private static string CoreSig(ClaudePlugin p)
        => $"{p.Id}|{p.Name}|{p.Marketplace}|{p.Version}|{p.McpText}|{p.UpdatedText}";

    // ── 플러그인 행 액션 ──────────────────────────────────────────
    private static ClaudePlugin? PluginOf(object sender) => (sender as FrameworkElement)?.DataContext as ClaudePlugin;

    private async void EnableToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || PluginOf(sender) is not ClaudePlugin p) return;
        var target = cb.IsChecked == true;
        cb.IsEnabled = false;
        try
        {
            var result = target ? await ClaudePluginService.EnableAsync(p.Id)
                                 : await ClaudePluginService.DisableAsync(p.Id);
            p.Enabled = target;
            ShowOutput($"{(target ? "enable" : "disable")} · {p.Name}",
                string.IsNullOrWhiteSpace(result) ? "완료." : result);
        }
        catch (Exception ex) { cb.IsChecked = !target; ConfirmDialog.Alert("토글 실패", ex.Message); }
        finally { cb.IsEnabled = true; }
    }

    private async void Detail_Click(object sender, RoutedEventArgs e)
    {
        if (PluginOf(sender) is not ClaudePlugin p) return;
        ShowOutput($"details · {p.Name}", "조회 중…");
        var result = await ClaudePluginService.DetailsAsync(p.Id);
        ShowOutput($"details · {p.Name}", string.IsNullOrWhiteSpace(result) ? "(출력 없음)" : result);
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (PluginOf(sender) is not ClaudePlugin p) return;
        ShowOutput($"update · {p.Name}", "업데이트 중… (재시작이 필요할 수 있습니다)");
        var result = await ClaudePluginService.UpdateAsync(p.Id);
        ShowOutput($"update · {p.Name}", string.IsNullOrWhiteSpace(result) ? "완료." : result);
        await RefreshAsync();
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (PluginOf(sender) is not ClaudePlugin p) return;
        if (!ConfirmDialog.Show("플러그인 삭제", $"'{p.Name}' 플러그인을 삭제할까요?", "삭제", danger: true))
            return;
        ShowOutput($"uninstall · {p.Name}", "삭제 중…");
        var result = await ClaudePluginService.UninstallAsync(p.Id);
        ShowOutput($"uninstall · {p.Name}", string.IsNullOrWhiteSpace(result) ? "완료." : result);
        await RefreshAsync();
    }

    // ── 설치 ──────────────────────────────────────────────────────
    private void InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        var input = PromptDialog.Show("플러그인 설치",
            "설치할 플러그인 이름을 입력하세요. 특정 마켓플레이스는 name@marketplace 형식.", okLabel: "설치");
        if (string.IsNullOrWhiteSpace(input)) return;
        ClaudePluginService.SpawnInstall(input);
        ShowOutput("install", $"'{input}' 설치를 별도 콘솔 창에서 진행합니다.\n완료되면 [새로고침] 으로 목록을 갱신하세요.");
    }

    // ── 마켓플레이스 탭 ───────────────────────────────────────────
    private void MarketAdd_Click(object sender, RoutedEventArgs e)
    {
        var input = PromptDialog.Show("마켓플레이스 추가",
            "URL · 로컬 경로 · GitHub 저장소(owner/repo) 중 하나를 입력하세요.", okLabel: "추가");
        if (string.IsNullOrWhiteSpace(input)) return;
        ClaudePluginService.SpawnMarketplaceAdd(input);
        ShowOutput("marketplace add", $"'{input}' 추가를 별도 콘솔 창에서 진행합니다.\n완료되면 [새로고침] 으로 목록을 갱신하세요.");
    }

    private async void MarketUpdateAll_Click(object sender, RoutedEventArgs e)
    {
        ShowOutput("marketplace update", "업데이트 중…");
        var result = await ClaudePluginService.MarketplaceUpdateAsync();
        ShowOutput("marketplace update", string.IsNullOrWhiteSpace(result) ? "완료." : result);
        await RefreshAsync();
    }

    private static ClaudeMarketplace? MarketOf(object sender) => (sender as FrameworkElement)?.DataContext as ClaudeMarketplace;

    private async void MarketItemUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (MarketOf(sender) is not ClaudeMarketplace m) return;
        ShowOutput($"marketplace update · {m.Name}", "업데이트 중…");
        var result = await ClaudePluginService.MarketplaceUpdateAsync(m.Name);
        ShowOutput($"marketplace update · {m.Name}", string.IsNullOrWhiteSpace(result) ? "완료." : result);
    }

    private async void MarketItemRemove_Click(object sender, RoutedEventArgs e)
    {
        if (MarketOf(sender) is not ClaudeMarketplace m) return;
        if (!ConfirmDialog.Show("마켓플레이스 삭제", $"'{m.Name}' 마켓플레이스를 삭제할까요?", "삭제", danger: true))
            return;
        ShowOutput($"marketplace remove · {m.Name}", "삭제 중…");
        var result = await ClaudePluginService.MarketplaceRemoveAsync(m.Name);
        ShowOutput($"marketplace remove · {m.Name}", string.IsNullOrWhiteSpace(result) ? "완료." : result);
        await RefreshAsync();
    }

    // ── 우측 출력 영역 ────────────────────────────────────────────
    private void ShowOutput(string title, string body)
    {
        OutputTitle.Text = title;
        OutputText.Text = body;
        OutputPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void OutputClear_Click(object sender, RoutedEventArgs e)
    {
        OutputTitle.Text = "출력";
        OutputText.Text = "";
        OutputPlaceholder.Visibility = Visibility.Visible;
    }

    // ── 창 제어 ───────────────────────────────────────────────────
    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            Window.GetWindow(this)?.DragMove();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => TryClose();

    public void TryClose() => CloseRequested?.Invoke(this, EventArgs.Empty);
}

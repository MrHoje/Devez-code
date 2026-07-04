using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DevezCode.Models;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>Claude Code 플러그인 관리 대시보드. 설치된 플러그인 목록/상태 + 런타임 제어
/// (enable/disable 토글 · details 상세 · update · uninstall) 및 마켓플레이스 관리(add/list/remove/update).
/// 모든 제어는 <see cref="ClaudePluginService"/> 의 CLI 경유.</summary>
public partial class PluginControlDialog : UserControl
{
    public event EventHandler? CloseRequested;

    private readonly ObservableCollection<ClaudePlugin> _plugins = new();
    private readonly ObservableCollection<ClaudeMarketplace> _markets = new();
    private bool _busy;
    private bool _disposed;

    public PluginControlDialog()
    {
        InitializeComponent();
        PluginList.ItemsSource = _plugins;
        MarketList.ItemsSource = _markets;
        Loaded += (_, _) => { _ = RefreshAsync(); };
        Unloaded += (_, _) => _disposed = true;
    }

    // ── 목록 로드 ─────────────────────────────────────────────────
    private async Task RefreshAsync()
    {
        if (_busy || _disposed) return;
        _busy = true;
        RefreshSpinner.Visibility = Visibility.Visible;
        try
        {
            // 기존 리스트를 비우지 않고, 먼저 조회한 뒤 바뀐 항목만 교체/추가/삭제하여 병합한다(깜빡임 방지).
            var list = await ClaudePluginService.ListAsync();
            if (_disposed) return;
            MergeInto(list);
            EmptyState.Visibility = _plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LastRefreshedText.Text = "갱신: " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch { }
        finally
        {
            _busy = false;
            if (!_disposed) RefreshSpinner.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>새 스냅샷을 기존 컬렉션에 병합. 사라진 항목 제거 → 새 항목 삽입 →
    /// 남은 항목은 순서만 맞추고, 내용(버전/메타/이름/출처)이 바뀐 것만 인스턴스 교체(=재렌더),
    /// enabled 만 바뀐 것은 제자리에서 갱신(토글 애니메이션 유지).</summary>
    private void MergeInto(System.Collections.Generic.List<ClaudePlugin> list)
    {
        // 1) 새 목록에 없는 것 제거
        for (int i = _plugins.Count - 1; i >= 0; i--)
            if (!list.Any(n => n.Id == _plugins[i].Id))
                _plugins.RemoveAt(i);

        // 2) 순서대로 삽입/갱신
        for (int i = 0; i < list.Count; i++)
        {
            var n = list[i];
            int cur = IndexOfId(n.Id);
            if (cur < 0) { _plugins.Insert(i, n); continue; }
            if (CoreSig(_plugins[cur]) != CoreSig(n))
                _plugins[cur] = n;                    // 내용 변경 → 인스턴스 교체(Replace 알림)
            else if (_plugins[cur].Enabled != n.Enabled)
                _plugins[cur].Enabled = n.Enabled;    // enabled 만 변경 → 제자리 갱신
            if (cur != i) _plugins.Move(cur, i);      // 순서 정렬
        }
    }

    private int IndexOfId(string id)
    {
        for (int i = 0; i < _plugins.Count; i++) if (_plugins[i].Id == id) return i;
        return -1;
    }

    /// <summary>enabled 를 제외한 표시 내용 서명(달라지면 카드 재렌더 필요).</summary>
    private static string CoreSig(ClaudePlugin p)
        => $"{p.Id}|{p.Name}|{p.Marketplace}|{p.Version}|{p.McpText}|{p.UpdatedText}";

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

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
            if (!string.IsNullOrWhiteSpace(result))
                ShowDetail($"{(target ? "enable" : "disable")} · {p.Name}", result);
        }
        catch (Exception ex)
        {
            cb.IsChecked = !target;
            ConfirmDialog.Alert("토글 실패", ex.Message);
        }
        finally { cb.IsEnabled = true; }
    }

    private async void Detail_Click(object sender, RoutedEventArgs e)
    {
        if (PluginOf(sender) is not ClaudePlugin p) return;
        ShowDetail($"details · {p.Name}", "조회 중…");
        var result = await ClaudePluginService.DetailsAsync(p.Id);
        ShowDetail($"details · {p.Name}", string.IsNullOrWhiteSpace(result) ? "(출력 없음)" : result);
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (PluginOf(sender) is not ClaudePlugin p) return;
        ShowDetail($"update · {p.Name}", "업데이트 중… (재시작이 필요할 수 있습니다)");
        var result = await ClaudePluginService.UpdateAsync(p.Id);
        ShowDetail($"update · {p.Name}", string.IsNullOrWhiteSpace(result) ? "완료." : result);
        await RefreshAsync();
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (PluginOf(sender) is not ClaudePlugin p) return;
        if (!ConfirmDialog.Show("플러그인 삭제", $"'{p.Name}' 플러그인을 삭제할까요?", "삭제", danger: true))
            return;
        ShowDetail($"uninstall · {p.Name}", "삭제 중…");
        var result = await ClaudePluginService.UninstallAsync(p.Id);
        ShowDetail($"uninstall · {p.Name}", string.IsNullOrWhiteSpace(result) ? "완료." : result);
        await RefreshAsync();
    }

    // ── 설치 ──────────────────────────────────────────────────────
    private void InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        var input = PromptDialog.Show("플러그인 설치",
            "설치할 플러그인 이름을 입력하세요. 특정 마켓플레이스는 name@marketplace 형식.",
            okLabel: "설치");
        if (string.IsNullOrWhiteSpace(input)) return;
        ClaudePluginService.SpawnInstall(input);
        // 설치는 별도 콘솔에서 진행(신뢰/설정 프롬프트) — 완료 후 새로고침 안내.
        ShowDetail("install", $"'{input}' 설치를 별도 콘솔 창에서 진행합니다.\n완료되면 [새로고침] 을 눌러 목록을 갱신하세요.");
    }

    // ── 마켓플레이스 오버레이 ─────────────────────────────────────
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
            var list = await ClaudePluginService.MarketplacesAsync();
            if (_disposed) return;
            _markets.Clear();
            foreach (var m in list) _markets.Add(m);
        }
        catch { }
    }

    private void MarketAdd_Click(object sender, RoutedEventArgs e)
    {
        var input = PromptDialog.Show("마켓플레이스 추가",
            "URL · 로컬 경로 · GitHub 저장소(owner/repo) 중 하나를 입력하세요.",
            okLabel: "추가");
        if (string.IsNullOrWhiteSpace(input)) return;
        ClaudePluginService.SpawnMarketplaceAdd(input);
        ShowDetail("marketplace add", $"'{input}' 추가를 별도 콘솔 창에서 진행합니다.\n완료되면 마켓플레이스 목록을 다시 열어 확인하세요.");
    }

    private async void MarketUpdateAll_Click(object sender, RoutedEventArgs e)
    {
        var result = await ClaudePluginService.MarketplaceUpdateAsync();
        ShowDetail("marketplace update", string.IsNullOrWhiteSpace(result) ? "완료." : result);
        await LoadMarketsAsync();
    }

    private static ClaudeMarketplace? MarketOf(object sender) => (sender as FrameworkElement)?.DataContext as ClaudeMarketplace;

    private async void MarketItemUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (MarketOf(sender) is not ClaudeMarketplace m) return;
        var result = await ClaudePluginService.MarketplaceUpdateAsync(m.Name);
        ShowDetail($"marketplace update · {m.Name}", string.IsNullOrWhiteSpace(result) ? "완료." : result);
    }

    private async void MarketItemRemove_Click(object sender, RoutedEventArgs e)
    {
        if (MarketOf(sender) is not ClaudeMarketplace m) return;
        if (!ConfirmDialog.Show("마켓플레이스 삭제", $"'{m.Name}' 마켓플레이스를 삭제할까요?", "삭제", danger: true))
            return;
        var result = await ClaudePluginService.MarketplaceRemoveAsync(m.Name);
        if (!string.IsNullOrWhiteSpace(result)) ShowDetail($"marketplace remove · {m.Name}", result);
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

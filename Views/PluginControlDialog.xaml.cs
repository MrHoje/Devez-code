using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
    private readonly ObservableCollection<ClaudeAvailablePlugin> _avail = new();
    private readonly ObservableCollection<ClaudeSkill> _skills = new();
    private readonly ObservableCollection<ClaudeAgent> _agents = new();
    private string _tab = "plugins";
    private string _query = "";
    private string _discQuery = "";   // 하단 Discover 검색어
    private ClaudeMarketplace? _selectedMarket;   // 상세 열린(선택된) 마켓
    // 출력은 탭별로 분리 저장 — 탭 전환 시 서로의 내용이 유지되지 않는다.
    private string _outPlugins = "";
    private string _outMarket = "";
    private string _outSkills = "";
    private string _outAgents = "";
    private bool _busy;
    private bool _pendingRefresh;   // 로딩 중 들어온 탭 전환/새로고침 예약
    private bool _disposed;

    // 검색 지연 적용(파일 탐색기와 동일한 180ms 디바운스). 타이핑 중 매 키마다 필터를 돌리지 않는다.
    private readonly System.Windows.Threading.DispatcherTimer _searchDebounce =
        new() { Interval = TimeSpan.FromMilliseconds(180) };
    // 하단 Discover 검색 디바운스(입력마다 Refresh 하면 컨테이너 재생성으로 살짝 멈춤).
    private readonly System.Windows.Threading.DispatcherTimer _discSearchDebounce =
        new() { Interval = TimeSpan.FromMilliseconds(180) };

    public PluginControlDialog()
    {
        InitializeComponent();
        // 우측 출력 영역 너비 복원(스플리터 드래그 값). 최소폭 미만이면 기본값 유지.
        var savedW = SettingsService.LoadPluginOutputWidth();
        if (savedW >= 340) OutputCol.Width = new GridLength(savedW);
        PluginList.ItemsSource = _plugins;
        MarketList.ItemsSource = _markets;
        DiscoverList.ItemsSource = _avail;
        SkillList.ItemsSource = _skills;
        AgentList.ItemsSource = _agents;
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); ApplyFilter(); };
        _discSearchDebounce.Tick += (_, _) => { _discSearchDebounce.Stop(); ApplyDiscFilter(); };
        CollectionViewSource.GetDefaultView(_plugins).Filter = o => o is ClaudePlugin p && Match(p.Name, p.Id, p.Marketplace);
        CollectionViewSource.GetDefaultView(_markets).Filter = o => o is ClaudeMarketplace m && Match(m.Name, m.OriginText);
        CollectionViewSource.GetDefaultView(_avail).Filter = o => o is ClaudeAvailablePlugin a && DiscMatch(a);
        CollectionViewSource.GetDefaultView(_skills).Filter = o => o is ClaudeSkill s && Match(s.Name, s.Description);
        CollectionViewSource.GetDefaultView(_agents).Filter = o => o is ClaudeAgent a && Match(a.Name, a.Description);
        // 탭 시각만 즉시 세팅. 실제 데이터 로드(서브프로세스 실행)는 창 오픈 애니메이션이 끝난 뒤
        // BeginInitialLoad() 로 시작한다 — 페이드 첫 프레임과 Process.Start 가 겹쳐 버벅이던 문제 해결.
        Loaded += (_, _) => SetTabVisual("plugins");
        Unloaded += (_, _) => { _disposed = true; _searchDebounce.Stop(); _discSearchDebounce.Stop(); };
    }

    /// <summary>창 오픈 애니메이션 완료 후 호출 — 첫 목록 로드를 시작한다.</summary>
    public async void BeginInitialLoad() { await RefreshAsync(); }

    private bool Match(params string?[] fields)
        => string.IsNullOrEmpty(_query)
           || fields.Any(f => f != null && f.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchClearBtn.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Collapsed : Visibility.Visible;
        _searchDebounce.Stop();
        // 비우면 즉시 해제, 입력 중이면 180ms 지연 후 적용.
        if (string.IsNullOrEmpty(SearchBox.Text)) ApplyFilter();
        else _searchDebounce.Start();
    }

    private void ApplyFilter()
    {
        _query = SearchBox.Text.Trim();
        CollectionViewSource.GetDefaultView(_plugins).Refresh();
        CollectionViewSource.GetDefaultView(_markets).Refresh();
        CollectionViewSource.GetDefaultView(_skills).Refresh();
        CollectionViewSource.GetDefaultView(_agents).Refresh();
        UpdateEmptyForTab();
    }

    private void SearchClear_Click(object sender, RoutedEventArgs e) => SearchBox.Text = "";

    // 검색 행 토글 — 좌측 사이드바처럼 Height 0↔49 슬라이드. 닫으면 검색어 초기화(필터 해제).
    private bool _searchOpen;
    private const double SearchRowHeightPx = 49;   // 35(pill) + 14(margin-bottom)

    private void SearchToggle_Click(object sender, RoutedEventArgs e)
    {
        _searchOpen = !_searchOpen;
        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = _searchOpen ? SearchRowHeightPx : 0,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
        };
        SearchRow.BeginAnimation(HeightProperty, anim);
        if (_searchOpen)
            Dispatcher.BeginInvoke(new Action(() => { SearchBox.Focus(); SearchBox.SelectAll(); }),
                System.Windows.Threading.DispatcherPriority.Input);
        else
            SearchBox.Text = "";   // 닫으면 검색어 지워 목록 전체 표시
    }

    private void UpdateEmptyForTab()
    {
        var view = _tab switch
        {
            "plugins" => CollectionViewSource.GetDefaultView(_plugins),
            "marketplaces" => CollectionViewSource.GetDefaultView(_markets),
            "skills" => CollectionViewSource.GetDefaultView(_skills),
            _ => CollectionViewSource.GetDefaultView(_agents),
        };
        UpdateEmpty(view.Cast<object>().Count());
    }

    // ── 탭 전환 ───────────────────────────────────────────────────
    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string t && t != _tab) SetTab(t);
    }

    private async void SetTab(string tab)
    {
        SetTabVisual(tab);
        await RefreshAsync();
    }

    // 탭 버튼 하이라이트/액션·리스트 표시만 즉시 전환(데이터 로드 없음).
    private void SetTabVisual(string tab)
    {
        _tab = tab;
        bool plugins = tab == "plugins";
        bool market = tab == "marketplaces";
        bool skills = tab == "skills";
        bool agents = tab == "agents";

        // 탭 전환 시 하단 Discover 닫아 출력이 꽉 차게(특히 플러그인 탭은 설치 가능 목록이 없음).
        CloseDiscover();
        // 편집 중이었다면 편집기를 닫고 출력 표시로 되돌린다.
        ExitEditor();
        // 탭별 출력만 표시(전환 시 내용 유지 안 함).
        RestoreOutputForTab();

        StyleTab(PluginTabBtn, plugins);
        StyleTab(MarketTabBtn, market);
        StyleTab(SkillTabBtn, skills);
        StyleTab(AgentTabBtn, agents);

        PluginActions.Visibility = plugins ? Visibility.Visible : Visibility.Collapsed;
        MarketActions.Visibility = market ? Visibility.Visible : Visibility.Collapsed;
        SkillActions.Visibility = skills ? Visibility.Visible : Visibility.Collapsed;
        AgentActions.Visibility = agents ? Visibility.Visible : Visibility.Collapsed;
        PluginScroll.Visibility = plugins ? Visibility.Visible : Visibility.Collapsed;
        MarketScroll.Visibility = market ? Visibility.Visible : Visibility.Collapsed;
        SkillScroll.Visibility = skills ? Visibility.Visible : Visibility.Collapsed;
        AgentScroll.Visibility = agents ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = plugins ? "설치된 플러그인이 없습니다"
                       : market ? "등록된 마켓플레이스가 없습니다"
                       : skills ? "스킬이 없습니다"
                       : "에이전트가 없습니다 (~/.claude/agents)";
        // 우측 헤더: 스킬·에이전트 탭은 "편집", 나머지는 "출력"(편집 중이면 OpenEditorAsync 가 별도 설정).
        if (_editPath == null) OutputTitle.Text = DefaultOutputTitle();
    }

    private void StyleTab(Button btn, bool active)
    {
        btn.Background = active ? (Brush)FindResource("PanelBrush") : Brushes.Transparent;
        btn.Foreground = active ? (Brush)FindResource("PrimaryBrush") : (Brush)FindResource("TextBrush");
        btn.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_disposed) return;
        // 로딩 중 다른 탭을 누르면 조회가 씹히던 문제: 진행 중이면 예약만 하고, 끝난 뒤 현재 탭으로 다시 돈다.
        if (_busy) { _pendingRefresh = true; return; }
        _busy = true;
        RefreshSpinner.Visibility = Visibility.Visible;
        RefreshIcon.Visibility = Visibility.Collapsed;
        try
        {
            do
            {
                _pendingRefresh = false;
                var tab = _tab;
                if (tab == "plugins")
                {
                    var list = await ClaudePluginService.ListAsync();
                    if (_disposed) return;
                    if (_tab == tab) MergeInto(list);   // 로딩 중 탭이 바뀌었으면 적용하지 않고 루프 재실행
                }
                else if (tab == "marketplaces")
                {
                    var list = await ClaudePluginService.MarketplacesAsync();
                    if (_disposed) return;
                    if (_tab == tab) { _markets.Clear(); foreach (var m in list) _markets.Add(m); }
                }
                else if (tab == "skills")
                {
                    var list = await ClaudeExtensionService.SkillsAsync();
                    if (_disposed) return;
                    if (_tab == tab) { _skills.Clear(); foreach (var s in list) _skills.Add(s); }
                }
                else
                {
                    var list = await ClaudeExtensionService.AgentsAsync();
                    if (_disposed) return;
                    if (_tab == tab) { _agents.Clear(); foreach (var a in list) _agents.Add(a); }
                }
                UpdateEmptyForTab();
            }
            // 로딩 중 탭 전환/새로고침이 예약됐으면 현재 탭으로 한 번 더.
            while (!_disposed && _pendingRefresh);
        }
        catch { }
        finally
        {
            _busy = false;
            if (!_disposed) { RefreshSpinner.Visibility = Visibility.Collapsed; RefreshIcon.Visibility = Visibility.Visible; }
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
        => $"{p.Id}|{p.Name}|{p.Marketplace}|{p.Version}|{p.McpText}|{p.UpdatedText}|{p.LatestVersion}|{p.UpdateAvailable}";

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
    private async void InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        var input = PromptDialog.Show("플러그인 설치",
            "설치할 플러그인 이름을 입력하세요. 특정 마켓플레이스는 name@marketplace 형식.", okLabel: "설치");
        if (string.IsNullOrWhiteSpace(input)) return;
        ShowOutput("install", $"'{input}' 설치 중… (잠시 걸릴 수 있습니다)");
        var r = await ClaudePluginService.InstallAsync(input);
        if (_disposed) return;
        ShowOutput("install", string.IsNullOrWhiteSpace(r.Text) ? (r.Ok ? "완료." : "설치 실패") : r.Text, isError: !r.Ok);
        await RefreshAsync();   // 설치 후 목록 자동 갱신
    }

    private async void PluginUpdateAll_Click(object sender, RoutedEventArgs e)
    {
        var targets = _plugins.ToList();
        if (targets.Count == 0) { ShowOutput("update --all", "설치된 플러그인이 없습니다."); return; }
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < targets.Count; i++)
        {
            var p = targets[i];
            ShowOutput("전체 업데이트", $"({i + 1}/{targets.Count}) {p.Name} 업데이트 중…\n\n{sb}");
            var r = await ClaudePluginService.UpdateAsync(p.Id);
            if (_disposed) return;
            sb.Append($"• {p.Name}: {(string.IsNullOrWhiteSpace(r) ? "완료" : r.Replace("\r", " ").Replace("\n", " "))}\n");
        }
        ShowOutput("전체 업데이트", sb.ToString().TrimEnd());
        await RefreshAsync();
    }

    // ── Discover(설치 가능한 플러그인) ────────────────────────────
    // 카드 클릭 → 상세 팝업(좌: 이름·설명 + 설치, 우: 출력).
    private void DiscoverCard_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ClaudeAvailablePlugin a) return;
        var win = new PluginDetailWindow(a) { Owner = Window.GetWindow(this) };
        win.ShowDialog();
        // 팝업에서 설치했으면 설치 가능한 목록에서 제거 + 플러그인 탭 목록을 뒤에서 미리 갱신.
        if (win.Installed)
        {
            _avail.Remove(a);
            var view = CollectionViewSource.GetDefaultView(_avail);
            DiscoverEmpty.Visibility = view.Cast<object>().Any() ? Visibility.Collapsed : Visibility.Visible;
            _ = ReloadPluginsSilentAsync();
        }
    }

    // 탭 전환 없이 설치된 플러그인 목록만 백그라운드로 갱신(설치 직후 미리 반영).
    private async Task ReloadPluginsSilentAsync()
    {
        try
        {
            var list = await ClaudePluginService.ListAsync();
            if (_disposed) return;
            MergeInto(list);
            if (_tab == "plugins") UpdateEmptyForTab();
        }
        catch { }
    }

    // ── 마켓플레이스 탭 ───────────────────────────────────────────
    private async void MarketAdd_Click(object sender, RoutedEventArgs e)
    {
        var input = PromptDialog.Show("마켓플레이스 추가",
            "URL · 로컬 경로 · GitHub 저장소(owner/repo) 중\n하나를 입력하세요.", okLabel: "추가");
        if (string.IsNullOrWhiteSpace(input)) return;
        ShowOutput("marketplace add", $"'{input}' 추가 중… (잠시 걸릴 수 있습니다)");
        var r = await ClaudePluginService.MarketplaceAddAsync(input);
        if (_disposed) return;
        ShowOutput("marketplace add", string.IsNullOrWhiteSpace(r.Text) ? (r.Ok ? "완료." : "추가 실패") : r.Text, isError: !r.Ok);
        await RefreshAsync();   // 추가 후 목록 자동 갱신
    }

    private async void MarketUpdateAll_Click(object sender, RoutedEventArgs e)
    {
        ShowOutput("marketplace update", "업데이트 중…");
        var result = await ClaudePluginService.MarketplaceUpdateAsync();
        ShowOutput("marketplace update", string.IsNullOrWhiteSpace(result) ? "완료." : result);
        await RefreshAsync();
    }

    private static ClaudeMarketplace? MarketOf(object sender) => (sender as FrameworkElement)?.DataContext as ClaudeMarketplace;

    // 마켓 상세: 상단엔 텍스트 정보, 하단 절반엔 이 마켓플레이스의 설치 가능한 플러그인(Discover) 표시.
    private async void MarketItemDetail_Click(object sender, RoutedEventArgs e)
    {
        if (MarketOf(sender) is not ClaudeMarketplace m) return;

        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(m.Source)) sb.AppendLine($"소스: {m.Source}");
        if (!string.IsNullOrWhiteSpace(m.Repo)) sb.AppendLine($"저장소: {m.Repo}");
        if (!string.IsNullOrWhiteSpace(m.Url)) sb.AppendLine($"URL: {m.Url}");
        if (!string.IsNullOrWhiteSpace(m.InstallLocation)) sb.AppendLine($"설치 위치: {m.InstallLocation}");
        ShowOutput($"marketplace · {m.Name}", sb.Length == 0 ? "(추가 정보 없음)" : sb.ToString().TrimEnd());

        // 선택 표시(프로젝트 카드처럼 보더 하이라이트) — 이전 선택 해제 후 현재만 선택.
        if (_selectedMarket != null && !ReferenceEquals(_selectedMarket, m)) _selectedMarket.IsSelected = false;
        _selectedMarket = m;
        m.IsSelected = true;

        // 하단 Discover 패널 열기 — 출력은 200px 고정, 나머지 공간은 Discover 차지.
        DiscoverTitle.Text = m.Name;
        OutputRow.Height = new GridLength(200);
        DiscoverRow.Height = new GridLength(1, GridUnitType.Star);
        DiscoverSearchBox.Text = "";   // 이전 검색어 초기화
        DiscoverSpinner.Visibility = Visibility.Visible;
        DiscoverEmpty.Visibility = Visibility.Collapsed;
        _avail.Clear();
        try
        {
            var list = await ClaudePluginService.AvailableAsync();
            if (_disposed) return;
            // 설치된 것을 상단에 모은다(설치됨 → 설치 수 내림차순).
            foreach (var a in list.Where(a => string.Equals(a.Marketplace, m.Name, StringComparison.OrdinalIgnoreCase))
                                   .OrderByDescending(a => a.IsInstalled)
                                   .ThenByDescending(a => a.InstallCount))
                _avail.Add(a);
        }
        catch { }
        finally
        {
            if (!_disposed)
            {
                DiscoverSpinner.Visibility = Visibility.Collapsed;
                DiscoverEmpty.Visibility = _avail.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    // 하단 Discover 패널을 닫고 출력이 전체 높이를 차지하도록 되돌린다.
    private void CloseDiscover()
    {
        if (_selectedMarket != null) { _selectedMarket.IsSelected = false; _selectedMarket = null; }
        OutputRow.Height = new GridLength(1, GridUnitType.Star);
        DiscoverRow.Height = new GridLength(0);
        _avail.Clear();
        DiscoverSearchBox.Text = "";
    }

    // 하단 Discover 검색(설치 가능한 플러그인 필터). 목록이 작아 즉시 필터.
    private bool DiscMatch(ClaudeAvailablePlugin a)
        => string.IsNullOrEmpty(_discQuery)
           || (a.Name?.IndexOf(_discQuery, StringComparison.OrdinalIgnoreCase) >= 0)
           || (a.Id?.IndexOf(_discQuery, StringComparison.OrdinalIgnoreCase) >= 0)
           || (a.Description?.IndexOf(_discQuery, StringComparison.OrdinalIgnoreCase) >= 0);

    private void DiscoverSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        DiscoverSearchClearBtn.Visibility = string.IsNullOrEmpty(DiscoverSearchBox.Text) ? Visibility.Collapsed : Visibility.Visible;
        _discSearchDebounce.Stop();
        // 비우면 즉시 해제, 입력 중이면 180ms 지연 후 적용(입력 중 Refresh 반복으로 인한 멈춤 방지).
        if (string.IsNullOrEmpty(DiscoverSearchBox.Text)) ApplyDiscFilter();
        else _discSearchDebounce.Start();
    }

    private void ApplyDiscFilter()
    {
        _discQuery = DiscoverSearchBox.Text.Trim();
        var view = CollectionViewSource.GetDefaultView(_avail);
        view.Refresh();
        DiscoverEmpty.Visibility = view.Cast<object>().Any() ? Visibility.Collapsed : Visibility.Visible;
    }

    private void DiscoverSearchClear_Click(object sender, RoutedEventArgs e) => DiscoverSearchBox.Text = "";

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

    // ── 스킬 탭 ───────────────────────────────────────────────────
    private void SkillFolder_Click(object sender, RoutedEventArgs e) => ClaudeExtensionService.OpenFolder(skills: true);

    private static ClaudeSkill? SkillOf(object sender) => (sender as FrameworkElement)?.DataContext as ClaudeSkill;

    // 잠금(숨김) 토글 — 켜면 SKILL.md, 끄면 SKILL.md.off. 파일명만 바꿔 Claude 스킬 탐색에서 제외.
    private async void SkillLock_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || SkillOf(sender) is not ClaudeSkill s) return;
        var target = cb.IsChecked == true;
        cb.IsEnabled = false;
        try
        {
            var newPath = await ClaudeExtensionService.SetSkillEnabledAsync(s, target);
            if (_disposed) return;
            if (newPath == null)
            {
                cb.IsChecked = !target;
                ShowOutput($"skill · {s.Name}", target ? "활성화 실패." : "잠금 실패.", isError: true);
            }
            else
            {
                s.Enabled = target;
                s.FilePath = newPath;
                ShowOutput($"skill · {s.Name}", target
                    ? "활성화됨 — Claude 가 이 스킬을 다시 사용합니다."
                    : "잠금(숨김) — Claude 가 이 스킬을 발견하지 않습니다.");
            }
        }
        finally { if (!_disposed) cb.IsEnabled = true; }
    }

    private async void SkillEdit_Click(object sender, RoutedEventArgs e)
    {
        if (SkillOf(sender) is not ClaudeSkill s) return;
        await OpenEditorAsync($"편집 · {s.Name}", s.FilePath);
    }

    // ── 에이전트 탭 ───────────────────────────────────────────────
    private void AgentFolder_Click(object sender, RoutedEventArgs e) => ClaudeExtensionService.OpenFolder(skills: false);

    private async void AgentEdit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ClaudeAgent a) return;
        await OpenEditorAsync($"편집 · {a.Name}", a.FilePath);
    }

    private async void AgentDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ClaudeAgent a) return;
        if (!ConfirmDialog.Show("에이전트 삭제", $"'{a.Name}' 에이전트를 삭제할까요?", "삭제", danger: true))
            return;
        var ok = await ClaudeExtensionService.DeleteAgentAsync(a.FilePath);
        if (_disposed) return;
        if (ok)
        {
            // 편집 중이던 파일이면 편집기를 닫는다.
            if (_editPath == a.FilePath) ExitEditor();
            _agents.Remove(a);
            UpdateEmptyForTab();
        }
        else ConfirmDialog.Alert("삭제 실패", "파일을 삭제할 수 없습니다.");
    }

    // ── 인라인 에디터(우측 출력 영역) ─────────────────────────────
    // 스킬/에이전트 편집은 팝업 대신 출력 영역을 편집기로 전환해 표시한다.
    private string? _editPath;

    private async Task OpenEditorAsync(string title, string path)
    {
        _editPath = path;
        OutputTitle.Text = title;
        EditorActions.Visibility = Visibility.Visible;
        OutputBorder.Visibility = Visibility.Collapsed;
        EditorBorder.Visibility = Visibility.Visible;
        EditorBox.Text = "불러오는 중…";
        EditorBox.IsReadOnly = true;
        var content = await ClaudeExtensionService.ReadAsync(path);
        if (_disposed || _editPath != path) return;   // 그 사이 닫혔으면 무시
        EditorBox.IsReadOnly = false;
        EditorBox.Text = content;
        EditorBox.CaretIndex = 0;
        EditorBox.Focus();
    }

    // 편집기를 닫고 출력 표시로 되돌린다.
    private void ExitEditor()
    {
        if (_editPath == null) return;
        _editPath = null;
        EditorActions.Visibility = Visibility.Collapsed;
        EditorBorder.Visibility = Visibility.Collapsed;
        OutputBorder.Visibility = Visibility.Visible;
        OutputTitle.Text = DefaultOutputTitle();
        EditorBox.Clear();
    }

    // 스킬·에이전트 탭은 주 용도가 편집이므로 헤더를 "편집"으로, 나머지는 "출력".
    private string DefaultOutputTitle() => _tab is "skills" or "agents" ? "편집" : "출력";

    // 저장 — 파일에 기록(성공 메시지는 표시하지 않음). 저장 후 편집기는 그대로 유지.
    private async void EditorSave_Click(object sender, RoutedEventArgs e) => await SaveEditorAsync();

    private async Task SaveEditorAsync()
    {
        if (_editPath == null || EditorBox.IsReadOnly) return;
        var path = _editPath;
        EditorSaveBtn.IsEnabled = false;
        await ClaudeExtensionService.WriteAsync(path, EditorBox.Text);
        if (!_disposed) EditorSaveBtn.IsEnabled = true;
    }

    private void EditorClose_Click(object sender, RoutedEventArgs e) => ExitEditor();

    private async void EditorBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        { e.Handled = true; await SaveEditorAsync(); }
        else if (e.Key == Key.Escape)
        { e.Handled = true; ExitEditor(); }
    }

    // ── 우측 출력 영역 ────────────────────────────────────────────
    // 헤더는 항상 "출력" 고정. 실행 맥락(명령명)은 본문 첫 줄에 표시.
    // 출력은 현재 탭 버퍼에 저장 → 탭 전환 시 서로 섞이지 않는다. 색상은 줄 단위(✔ 초록/✘ 빨강)로 렌더러가 처리.
    private void ShowOutput(string title, string body, bool isError = false)
    {
        var text = string.IsNullOrEmpty(title) ? body : $"{title}\n\n{body}";
        switch (_tab)
        {
            case "plugins": _outPlugins = text; break;
            case "marketplaces": _outMarket = text; break;
            case "skills": _outSkills = text; break;
            default: _outAgents = text; break;
        }
        PluginOutputRenderer.Render(OutputText, text);
        OutputPlaceholder.Visibility = string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;
    }

    // 탭에 저장된 출력만 표시(없으면 안내문구).
    private void RestoreOutputForTab()
    {
        var text = _tab switch
        {
            "plugins" => _outPlugins,
            "marketplaces" => _outMarket,
            "skills" => _outSkills,
            _ => _outAgents,
        };
        PluginOutputRenderer.Render(OutputText, text);
        OutputPlaceholder.Visibility = string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 창 제어 ───────────────────────────────────────────────────
    // 스크롤 상·하단 페이드(프로젝트 영역과 동일) — 스크롤 위치에 따라 top/bottom 페이드 토글.
    private void ListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        ListFadeTop.Visibility = sv.VerticalOffset > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        ListFadeBottom.Visibility = sv.VerticalOffset < sv.ScrollableHeight - 0.5 ? Visibility.Visible : Visibility.Collapsed;
    }

    // 스플리터 드래그 완료 시 우측 출력 영역 너비를 영속.
    private void OutputSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        => SettingsService.SavePluginOutputWidth(OutputCol.ActualWidth);

    private void MaxRestore_Click(object sender, RoutedEventArgs e)
    {
        var w = Window.GetWindow(this);
        if (w == null) return;
        w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    /// <summary>창이 최대화/복원될 때 버튼 글리프·툴팁을 동기화(창에서 호출).</summary>
    public void SetMaximizedVisual(bool maximized)
    {
        MaxRestoreGlyph.Text = maximized ? "❐" : "□";  // ❐ 복원 / □ 최대화
        MaxRestoreBtn.ToolTip = maximized ? "이전 크기로" : "최대화";
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => TryClose();

    public void TryClose() => CloseRequested?.Invoke(this, EventArgs.Empty);
}

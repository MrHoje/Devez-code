using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>설정창 (devez 이식). 오버레이로 사용: 최상위 Grid에 올린 뒤 <see cref="CloseRequested"/> 로 닫는다.
/// 동작은 devez 와 동일: 변경은 라이브 미리보기로만 반영되고 디스크 저장은 [저장] 버튼에서만 한다.
/// [취소]·헤더 X·딤 배경은 미리보기를 원래값으로 되돌린다(미저장 변경이 있으면 저장 여부 확인).</summary>
public partial class SettingsDialog : UserControl
{
    /// <summary>닫기 요청 시 발생.</summary>
    public event EventHandler? CloseRequested;

    // 열림 시점의 저장값(기준). 미저장 변경 판정 + 취소 시 복원에 사용. 저장하면 갱신된다.
    private string _originalTheme;
    private int    _originalFontScale;
    private HashSet<string> _originalEnabledAgents = new(StringComparer.OrdinalIgnoreCase);
    private int _originalRetentionDays = ClaudeGlobalSettings.DefaultCleanupPeriodDays;

    private string _selectedTheme;
    private int    _selectedFontScale;
    private readonly ObservableCollection<AgentItem> _agentItems = new();
    // 현재 활성 좌측 카테고리. 테마 변경 시 활성 버튼의 brush instance가 stale 되므로 재계산에 사용.
    private string _activeCategoryKey = "theme";

    // ── 업데이트 내역(Changelog) 데이터 — devez 정합. 최신 5개만 유지, 새 버전 추가 시 가장 오래된 항목 제거. ──
    private static readonly (string Version, string Date, bool IsLatest, string[] Notes)[] _changelog =
    {
        ("v1.0.0", "2026-06-22", true, new[]
        {
            "DevezCode 정식 출시 — Claude Code·OpenCode·Codex 등 코딩 에이전트를 한 창에서 사용합니다.",
            "테마 변경 시 모든 세션이 새 테마로 자동 재시작되며, 재시작 동안 로딩 스피너가 표시됩니다.",
            "프로젝트를 선택하면 그 프로젝트의 모든 세션을 미리 불러오고, 프로젝트 카드에 세션 실행 상태가 표시됩니다.",
        }),
    };
    private const int ChangelogPageSize = 5;
    private int _changelogPage = 0;
    private readonly Action<string> _themeChangedHandler;
    private readonly Action<int> _fontScaleChangedHandler;
    private bool _subscribed;

    public SettingsDialog()
    {
        InitializeComponent();
        _originalTheme       = App.CurrentTheme;
        _selectedTheme       = App.CurrentTheme;
        _originalFontScale   = SettingsService.LoadFontScale();
        _selectedFontScale   = _originalFontScale;
        BuildAgentList();
        UpdateThemeSelectionVisual();
        UpdateFontSelectionVisual();
        SetActiveCategory("theme");

        // 라이브 미리보기: 테마/글꼴 변경 시 좌측 탭 활성 배경·테마 카드 보더·글꼴 카드 보더를
        // 즉시 재계산. 캡처된 brush instance 라 DynamicResource 가 자동 갱신되지 않는 케이스 보정.
        _themeChangedHandler    = _ => RefreshAfterThemeChange();
        _fontScaleChangedHandler = _ => UpdateFontSelectionVisual();
        App.ThemeChanged     += _themeChangedHandler;
        App.FontScaleChanged  += _fontScaleChangedHandler;
        _subscribed = true;
        Unloaded += (_, _) => Unsubscribe();
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        try { App.ThemeChanged    -= _themeChangedHandler;    } catch { }
        try { App.FontScaleChanged -= _fontScaleChangedHandler; } catch { }
    }

    // ── 카테고리 전환 ─────────────────────────────────────────────
    private void Category_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string key) SetActiveCategory(key);
    }

    /// <summary>좌측 카테고리 활성 표시 + 우측 패널 전환.</summary>
    private void SetActiveCategory(string key)
    {
        _activeCategoryKey = key;
        var active  = (Brush)FindResource("PanelBrush");
        var primary = (Brush)FindResource("PrimaryBrush");
        var text    = (Brush)FindResource("TextBrush");

        CatThemeBtn.Background = key == "theme"  ? active : Brushes.Transparent;
        CatThemeBtn.Foreground = key == "theme"  ? primary : text;
        CatAgentBtn.Background = key == "agent"  ? active : Brushes.Transparent;
        CatAgentBtn.Foreground = key == "agent"  ? primary : text;
        CatMcpBtn.Background   = key == "mcp"    ? active : Brushes.Transparent;
        CatMcpBtn.Foreground   = key == "mcp"    ? primary : text;
        CatChangelogBtn.Background = key == "changelog" ? active : Brushes.Transparent;
        CatChangelogBtn.Foreground = key == "changelog" ? primary : text;

        ThemePanel.Visibility     = key == "theme"     ? Visibility.Visible : Visibility.Collapsed;
        AgentPanel.Visibility     = key == "agent"     ? Visibility.Visible : Visibility.Collapsed;
        McpPanel.Visibility       = key == "mcp"       ? Visibility.Visible : Visibility.Collapsed;
        ChangelogPanel.Visibility = key == "changelog" ? Visibility.Visible : Visibility.Collapsed;

        if (key == "changelog") { _changelogPage = 0; RenderChangelogPage(); }
    }

    // ── 업데이트 내역 렌더링/페이지네이션 (devez 정합) ──
    private void RenderChangelogPage()
    {
        ChangelogItemsHost.Children.Clear();
        var totalPages = (int)System.Math.Ceiling(_changelog.Length / (double)ChangelogPageSize);
        var items = _changelog.Skip(_changelogPage * ChangelogPageSize).Take(ChangelogPageSize);
        foreach (var (version, date, isLatest, notes) in items)
            ChangelogItemsHost.Children.Add(MakeVersionCard(version, date, isLatest, notes));

        if (totalPages > 1)
        {
            ChangelogPager.Visibility = Visibility.Visible;
            PageIndicator.Text = $"{_changelogPage + 1} / {totalPages}";
            PrevPageBtn.IsEnabled = _changelogPage > 0;
            NextPageBtn.IsEnabled = _changelogPage < totalPages - 1;
        }
        else ChangelogPager.Visibility = Visibility.Collapsed;
    }

    private Border MakeVersionCard(string version, string date, bool isLatest, string[] notes)
    {
        var badgeBg = isLatest ? (Brush)FindResource("PrimarySoftBrush") : (Brush)FindResource("PanelSoftBrush");
        var badgeFg = isLatest ? (Brush)FindResource("PrimaryBrush")     : (Brush)FindResource("TextMutedBrush");

        var badge = new Border
        {
            Background   = badgeBg,
            CornerRadius = new CornerRadius(6),
            Padding      = new Thickness(8, 3, 8, 3),
            Child        = new TextBlock { Text = version, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = badgeFg },
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(badge);
        header.Children.Add(new TextBlock
        {
            Text = date, FontSize = 12, Foreground = (Brush)FindResource("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
        });

        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 13,
            Foreground = (Brush)FindResource("TextBrush"), LineHeight = 22,
        };
        foreach (var note in notes)
        {
            if (tb.Inlines.Count > 0) tb.Inlines.Add(new System.Windows.Documents.LineBreak());
            tb.Inlines.Add(new System.Windows.Documents.Run($"• {note}"));
        }

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(tb);
        return new Border
        {
            Background = (Brush)FindResource("PanelBrush"),
            CornerRadius = new CornerRadius(10),
            BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(16, 14, 16, 14),
            Child = body,
        };
    }

    private void PrevPageBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_changelogPage > 0) { _changelogPage--; RenderChangelogPage(); }
    }

    private void NextPageBtn_Click(object sender, RoutedEventArgs e)
    {
        var totalPages = (int)System.Math.Ceiling(_changelog.Length / (double)ChangelogPageSize);
        if (_changelogPage < totalPages - 1) { _changelogPage++; RenderChangelogPage(); }
    }

    /// <summary>테마 변경 시 — brush instance 가 stale 된 좌측 활성 배경·테마/글꼴 카드 보더를 모두 재계산.</summary>
    private void RefreshAfterThemeChange()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            SetActiveCategory(_activeCategoryKey);
            UpdateThemeSelectionVisual();
            UpdateFontSelectionVisual();
        }));
    }

    /// <summary>MCP 서버 관리 — 별도 오버레이 창으로 열기. 설정창은 닫지 않는다(독립 편집).</summary>
    private void OpenMcpManager_Click(object sender, RoutedEventArgs e)
    {
        // 변경 중인 다른 설정이 있을 수 있으니 미리보기는 원복 후 떠준다.
        RevertPreview();
        var dlg = new McpManagerWindow { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
        // 다시 돌아왔을 때 카테고리는 mcp 그대로 유지
        SetActiveCategory("mcp");
    }

    // ── 미리보기(저장 없이 화면에만 반영) ──────────────────────────
    private void ThemeCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is string key)
        {
            _selectedTheme = key;
            (Application.Current as App)?.SetTheme(key, persist: false); // 미리보기만
            UpdateThemeSelectionVisual();
        }
    }

    private void FontSizeCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is string tag && int.TryParse(tag, out var scale))
        {
            _selectedFontScale = scale;
            (Application.Current as App)?.SetFontScale(scale); // 미리보기만(즉시 반영)
            UpdateFontSelectionVisual();
        }
    }

    // ── 저장 / 취소 / 닫기 ────────────────────────────────────────
    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        var themeChanged = _selectedTheme != _originalTheme;
        if (_selectedTheme != _originalTheme)
        {
            var proceed = ConfirmDialog.Show(
                "테마 변경 적용",
                "테마 변경을 적용하려면 열려 있는 Claude Code 세션을 다시 시작합니다.\n" +
                "응답 생성 중인 세션은 중단될 수 있으며, 필요한 경우 요청을 다시 보내야 합니다.\n\n" +
                "변경사항을 저장하시겠습니까?",
                okLabel: "저장",
                iconKey: "IconPalette",
                wideLayout: true); // 세션 재시작 안내 — 긴 본문이라 넓게 유지
            if (!proceed) return;
        }

        ApplySettings();
        if (themeChanged)
            (Application.Current.MainWindow as MainWindow)?.ReloadAllSessionsForTheme();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>[취소] 버튼: 확인 없이 미리보기를 되돌리고 닫는다.</summary>
    private void ForceCancelBtn_Click(object sender, RoutedEventArgs e)
    {
        RevertPreview();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>헤더 X — devez 처럼 미저장 변경이 있으면 저장 여부를 묻는다.</summary>
    private void CancelBtn_Click(object sender, RoutedEventArgs e) => TryCloseWithConfirm();

    /// <summary>헤더 드래그 → 부모 SettingsWindow 이동 (devez SettingsDialog 이식).</summary>
    private void Header_DragMove(object sender, MouseButtonEventArgs e)
        => Window.GetWindow(this)?.DragMove();

    /// <summary>ESC / 외부에서 호출하는 닫기 — 미저장 변경이 있으면 저장 여부를 묻는다.</summary>
    public void TryCloseWithConfirm()
    {
        if (HasUnsavedChanges())
        {
            var save = ConfirmDialog.Show(
                "저장되지 않은 변경사항",
                "저장되지 않은 변경사항이 있습니다.\n저장하시겠습니까? (취소 시 변경사항이 사라집니다)",
                okLabel: "저장", iconKey: "IconSettings");
            if (save) ApplySettings();
            else      RevertPreview();
        }
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool HasUnsavedChanges()
    {
        if (_selectedTheme != _originalTheme) return true;
        if (_selectedFontScale != _originalFontScale) return true;
        var current = new HashSet<string>(
            _agentItems.Where(a => a.Enabled).Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        if (!current.SetEquals(_originalEnabledAgents)) return true;
        var claude = _agentItems.FirstOrDefault(a => a.IsClaudeCode);
        return claude != null && claude.RetentionDays != _originalRetentionDays;
    }

    /// <summary>현재 UI 값을 디스크에 저장·확정하고 기준값을 갱신한다.</summary>
    private void ApplySettings()
    {
        (Application.Current as App)?.SetTheme(_selectedTheme); // persist
        SettingsService.SaveFontScale(_selectedFontScale);
        UpdateAgentEnabledInSettings();

        _originalTheme       = _selectedTheme;
        _originalFontScale   = _selectedFontScale;
        _originalEnabledAgents = new HashSet<string>(
            _agentItems.Where(a => a.Enabled).Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        _originalRetentionDays = _agentItems.FirstOrDefault(a => a.IsClaudeCode)?.RetentionDays
            ?? ClaudeGlobalSettings.DefaultCleanupPeriodDays;
    }

    /// <summary>미리보기를 열림 시점(저장값)으로 되돌린다.</summary>
    private void RevertPreview()
    {
        if (_selectedTheme != _originalTheme)
        {
            _selectedTheme = _originalTheme;
            (Application.Current as App)?.SetTheme(_originalTheme, persist: false);
            UpdateThemeSelectionVisual();
        }
        if (_selectedFontScale != _originalFontScale)
        {
            _selectedFontScale = _originalFontScale;
            (Application.Current as App)?.SetFontScale(_originalFontScale);
            UpdateFontSelectionVisual();
        }
        // 에이전트 활성화 상태 되돌리기
        foreach (var item in _agentItems)
        {
            item.Enabled = _originalEnabledAgents.Contains(item.Id);
            if (item.IsClaudeCode) item.RetentionDays = _originalRetentionDays;
        }
    }

    private void UpdateThemeSelectionVisual()
    {
        var primary = (Brush)FindResource("PrimaryBrush");
        var line    = (Brush)FindResource("LineBrush");

        foreach (var (card, dot, key) in new (Border, Ellipse, string)[]
        {
            (ThemeCard_Minimal, ThemeRadioDot_Minimal, "minimal"),
            (ThemeCard_Soft,    ThemeRadioDot_Soft,    "soft"),
            (ThemeCard_Dark,    ThemeRadioDot_Dark,    "dark"),
        })
        {
            var selected = _selectedTheme == key;
            card.BorderBrush = selected ? primary : line;
            dot.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateFontSelectionVisual()
    {
        var primary = (Brush)FindResource("PrimaryBrush");
        var line    = (Brush)FindResource("LineBrush");

        foreach (var (card, dot, scale) in new (Border, Ellipse, int)[]
        {
            (FontCard_Small, FontRadioDot_Small, 0),
            (FontCard_Large, FontRadioDot_Large, 1),
        })
        {
            var selected = _selectedFontScale == scale;
            card.BorderBrush = selected ? primary : line;
            dot.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ── 에이전트 패널 ──────────────────────────────────────────────
    private void BuildAgentList()
    {
        // 경로 재스캔 (Settings 가 늦게 열릴 수 있으므로 매번 새로).
        AgentRegistry.InvalidateCache();
        var muted = (Brush)FindResource("TextMutedBrush");

        _agentItems.Clear();
        var enabledSet = new HashSet<string>(SettingsService.LoadEnabledAgents(), StringComparer.OrdinalIgnoreCase);
        foreach (var agent in AgentRegistry.All)
        {
            // UI 노출 제외 (codex 등) — 세션 생성 피커와 동일한 정책 유지
            if (AgentRegistry.HiddenFromUI.Contains(agent.Id)) continue;
            bool installed = AgentRegistry.IsInstalled(agent);
            _agentItems.Add(new AgentItem
            {
                Id = agent.Id,
                DisplayName = agent.DisplayName,
                CommandHint = $"실행 명령: {agent.Command}",
                Installed = installed,
                InstalledLabel = installed ? "설치됨" : "미설치",
                InstalledBrush = installed
                    ? (Brush)FindResource("PrimaryBrush")
                    : muted,
                Enabled = installed && enabledSet.Contains(agent.Id),
                IsClaudeCode = agent.Id == "claude",
                RetentionDays = agent.Id == "claude"
                    ? ClaudeGlobalSettings.GetCleanupPeriodDays()
                    : ClaudeGlobalSettings.DefaultCleanupPeriodDays,
            });
        }
        _originalEnabledAgents = new HashSet<string>(
            _agentItems.Where(a => a.Enabled).Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        _originalRetentionDays = _agentItems.FirstOrDefault(a => a.IsClaudeCode)?.RetentionDays
            ?? ClaudeGlobalSettings.DefaultCleanupPeriodDays;
        AgentList.ItemsSource = _agentItems;
    }

    private void UpdateAgentEnabledInSettings()
    {
        var enabled = _agentItems.Where(a => a.Enabled).Select(a => a.Id).ToList();
        SettingsService.SaveEnabledAgents(enabled);
        AgentRegistry.InvalidateCache();

        // Claude Code 세션 유지기간 → ~/.claude/settings.json 전역설정
        var claude = _agentItems.FirstOrDefault(a => a.IsClaudeCode);
        if (claude != null) ClaudeGlobalSettings.SetCleanupPeriodDays(claude.RetentionDays);
    }

    /// <summary>토글 변경 시 저장 (UI 토글은 즉시 반영되지만, 디스크 저장은 [저장] 버튼에서만 — 다른 설정과 동일).</summary>
    private void AgentItem_EnabledChanged(object? sender, System.Windows.RoutedPropertyChangedEventArgs<bool> e)
    {
        // [저장] 버튼을 눌러야 디스크에 기록되므로 여기선 _selectedEnabledAgents 만 갱신하면 됨.
        // (BuildAgentList 가 기준값을 잡았고, ApplySettings 가 enabled 목록을 디스크에 쓴다.)
    }
}

/// <summary>설정 → 에이전트 패널의 한 줄 (이름·설치 상태·활성화 토글).</summary>
public sealed class AgentItem : INotifyPropertyChanged
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string CommandHint { get; set; } = "";
    public bool Installed { get; set; }
    public string InstalledLabel { get; set; } = "";
    public Brush InstalledBrush { get; set; } = Brushes.Gray;

    private bool _enabled;
    public bool Enabled { get => _enabled; set { if (_enabled != value) { _enabled = value; OnPropertyChanged(); } } }

    /// <summary>Claude Code 항목에만 세션 유지기간 설정 노출.</summary>
    public bool IsClaudeCode { get; set; }

    /// <summary>유지기간 프리셋. ComboBox 바인딩용(DisplayMemberPath=Label, SelectedValuePath=Days).</summary>
    public RetentionOption[] RetentionOptions { get; } =
    {
        new(7,   "7일"),
        new(14,  "14일"),
        new(30,  "30일"),
        new(60,  "60일"),
        new(90,  "90일"),
        new(180, "180일"),
        new(365, "365일"),
        new(ClaudeGlobalSettings.PermanentDays, "영구 보관"),
    };

    private int _retentionDays = ClaudeGlobalSettings.DefaultCleanupPeriodDays;
    public int RetentionDays { get => _retentionDays; set { if (_retentionDays != value) { _retentionDays = value; OnPropertyChanged(); } } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>세션 유지기간 프리셋 한 항목. ToString=Label (콤보 SelectionBox 가 DisplayMemberPath 대신 ToString 사용).</summary>
public sealed record RetentionOption(int Days, string Label)
{
    public override string ToString() => Label;
}

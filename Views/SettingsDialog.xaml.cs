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

    private string _selectedTheme;
    private int    _selectedFontScale;
    private readonly ObservableCollection<AgentItem> _agentItems = new();
    // 현재 활성 좌측 카테고리. 테마 변경 시 활성 버튼의 brush instance가 stale 되므로 재계산에 사용.
    private string _activeCategoryKey = "theme";
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

        ThemePanel.Visibility  = key == "theme"  ? Visibility.Visible : Visibility.Collapsed;
        AgentPanel.Visibility  = key == "agent"  ? Visibility.Visible : Visibility.Collapsed;
        McpPanel.Visibility    = key == "mcp"    ? Visibility.Visible : Visibility.Collapsed;
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
                iconKey: "IconPalette");
            if (!proceed) return;
        }

        ApplySettings();
        if (themeChanged)
            (Application.Current.MainWindow as MainWindow)?.TryRestartActiveClaudeSession();
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
        return !current.SetEquals(_originalEnabledAgents);
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
            item.Enabled = _originalEnabledAgents.Contains(item.Id);
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
            });
        }
        _originalEnabledAgents = new HashSet<string>(
            _agentItems.Where(a => a.Enabled).Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        AgentList.ItemsSource = _agentItems;
    }

    private void UpdateAgentEnabledInSettings()
    {
        var enabled = _agentItems.Where(a => a.Enabled).Select(a => a.Id).ToList();
        SettingsService.SaveEnabledAgents(enabled);
        AgentRegistry.InvalidateCache();
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

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

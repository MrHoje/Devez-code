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

    /// <summary>성능 모니터 표시 미리보기 변경 시 발생(MainWindow 가 칩을 즉시 켜고/끈다).</summary>
    public event EventHandler<bool>? PerfMonitorBarChanged;

    // 열림 시점의 저장값(기준). 미저장 변경 판정 + 취소 시 복원에 사용. 저장하면 갱신된다.
    private string _originalTheme;
    private bool   _originalPerfMonitor;

    private string _selectedTheme;

    public SettingsDialog()
    {
        InitializeComponent();
        _originalTheme       = App.CurrentTheme;
        _selectedTheme       = App.CurrentTheme;
        _originalPerfMonitor = SettingsService.LoadShowPerfMonitorBar();
        PerfMonitorToggle.IsChecked = _originalPerfMonitor;
        UpdateThemeSelectionVisual();
        SetActiveCategory("theme");
    }

    // ── 카테고리 전환 ─────────────────────────────────────────────
    private void Category_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string key) SetActiveCategory(key);
    }

    /// <summary>좌측 카테고리 활성 표시 + 우측 패널 전환.</summary>
    private void SetActiveCategory(string key)
    {
        var active  = (Brush)FindResource("PanelBrush");
        var primary = (Brush)FindResource("PrimaryBrush");
        var text    = (Brush)FindResource("TextBrush");

        CatThemeBtn.Background  = key == "theme"  ? active : Brushes.Transparent;
        CatThemeBtn.Foreground  = key == "theme"  ? primary : text;
        CatTopBarBtn.Background = key == "topbar" ? active : Brushes.Transparent;
        CatTopBarBtn.Foreground = key == "topbar" ? primary : text;

        ThemePanel.Visibility  = key == "theme"  ? Visibility.Visible : Visibility.Collapsed;
        TopBarPanel.Visibility = key == "topbar" ? Visibility.Visible : Visibility.Collapsed;
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

    private void PerfMonitorToggle_Click(object sender, RoutedEventArgs e)
        => PerfMonitorBarChanged?.Invoke(this, PerfMonitorToggle.IsChecked == true); // 미리보기만

    // ── 저장 / 취소 / 닫기 ────────────────────────────────────────
    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        ApplySettings();
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

    /// <summary>딤 배경 클릭 — 헤더 X 와 동일.</summary>
    private void Backdrop_Click(object sender, MouseButtonEventArgs e) => TryCloseWithConfirm();

    private void TryCloseWithConfirm()
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
        => _selectedTheme != _originalTheme
        || (PerfMonitorToggle.IsChecked == true) != _originalPerfMonitor;

    /// <summary>현재 UI 값을 디스크에 저장·확정하고 기준값을 갱신한다.</summary>
    private void ApplySettings()
    {
        (Application.Current as App)?.SetTheme(_selectedTheme); // persist
        var perf = PerfMonitorToggle.IsChecked == true;
        SettingsService.SaveShowPerfMonitorBar(perf);

        _originalTheme       = _selectedTheme;
        _originalPerfMonitor = perf;
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
        if ((PerfMonitorToggle.IsChecked == true) != _originalPerfMonitor)
        {
            PerfMonitorToggle.IsChecked = _originalPerfMonitor;
            PerfMonitorBarChanged?.Invoke(this, _originalPerfMonitor);
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
}

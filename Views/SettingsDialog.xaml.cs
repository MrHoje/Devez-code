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
    private bool   _originalPreloadAllSessions;
    private bool   _originalAutoLoadLastProject;
    private bool   _originalHideProjectInfoHeader;
    private bool   _originalUseFullScreen;
    private HashSet<string> _originalEnabledAgents = new(StringComparer.OrdinalIgnoreCase);
    private int _originalRetentionDays = ClaudeGlobalSettings.DefaultCleanupPeriodDays;

    private int    _originalProjectColumns;
    private string _selectedTheme;
    private int    _selectedFontScale;
    private bool   _selectedPreloadAllSessions;
    private bool   _selectedAutoLoadLastProject;
    private bool   _selectedHideProjectInfoHeader;
    private bool   _selectedUseFullScreen;
    private int    _selectedProjectColumns;
    // DeepSeek 연결 토글 — 다른 설정과 동일하게 [저장] 시점에만 디스크 반영(끄고 저장 시 키 삭제).
    private bool   _originalDeepSeekEnabled;
    private bool   _selectedDeepSeekEnabled;

    // 탭 이동 단축키(가상키코드). 디스크 저장은 [저장] 버튼에서만 — 다른 설정과 동일.
    private int _originalHkMod, _originalHkPrev, _originalHkNext;
    private int _selectedHkMod, _selectedHkPrev, _selectedHkNext;
    private string? _capturingField; // 캡처 중인 필드 키("mod"/"prev"/"next"), null=비캡처
    private readonly ObservableCollection<AgentItem> _agentItems = new();
    // 현재 활성 좌측 카테고리. 테마 변경 시 활성 버튼의 brush instance가 stale 되므로 재계산에 사용.
    private string _activeCategoryKey = "theme";
    private int _titleClickCount;

    // ── 업데이트 내역(Changelog) 데이터 — devez 정합. 최신 5개만 유지, 새 버전 추가 시 가장 오래된 항목 제거. ──
    private static readonly (string Version, string Date, bool IsLatest, string[] Notes)[] _changelog =
    {
        ("v1.11.1", "2026-07-05", true, new[]
        {
            "터미널에서 세션을 /exit·Ctrl+C 로 종료해도 세션이 유지된 채 자동으로 다시 시작됩니다.",
            "사소한 오류를 수정하고 일부 UI를 개선했습니다.",
        }),
        ("v1.11.0", "2026-07-05", false, new[]
        {
            "세션을 포크해 대화를 분기할 수 있습니다.",
            "세션 대화 내역을 파일로 내보낼 수 있습니다.",
            "(프로젝트 카드의 세션 우클릭, 세션 탭 헤더 우클릭 메뉴에 포크·내보내기 추가)",
            "우측 하단에 Claude 플러그인 관리 메뉴가 추가되었습니다.",
        }),
        ("v1.10.2", "2026-07-03", false, new[]
        {
            "일부 안내 대화상자가 Windows 기본 창으로 표시되던 것을 앱 디자인에 맞게 통일했습니다.",
            "분할된 패널에서 텍스트 파일을 닫으면 이전 세션이 자동으로 선택되지 않던 문제를 수정했습니다.",
        }),
        ("v1.10.1", "2026-07-03", false, new[]
        {
            "분할된 프로젝트에서 상단 탭 헤더를 드래그해 반대쪽 패널로 옮길 수 있습니다.",
            "창 크기를 조절할 때 터미널이 부드럽게 따라오고 스크롤 위치가 유실되지 않도록 개선했습니다.",
        }),
        ("v1.10.0", "2026-07-03", false, new[]
        {
            "여러 파일과 세션을 좌우로 나눠 볼 수 있는 패널 분할 기능을 추가했습니다.",
            "계정 사용량에 한도 도달 예상 시점을 표시합니다. (설정 > 계정 사용량에서 켤 수 있습니다.)",
            "파일 뷰어에서 문서 내 텍스트를 검색할 수 있습니다.",
            "파일 뷰어 저장 버튼 옆에 파일 용량을 표시합니다.",
            "다른 프로젝트를 보는 중에도 외부에서 수정된 마크다운 파일의 변경 사항이 반영되도록 수정했습니다.",
        }),
        ("v1.9.7", "2026-07-02", false, new[]
        {
            "세션 로드 폴백 상황을 진단 로그에 기록하도록 개선했습니다.",
        }),
        ("v1.9.6", "2026-07-02", false, new[]
        {
            "계정 사용량 패널에 Fable 사용량 정보를 표시합니다.",
            "프로젝트 카드 우클릭 메뉴에서 디렉토리 열기를 할 수 있습니다.",
            "클립보드 처리 방식을 개선해 다른 프로그램에 영향을 주지 않도록 했습니다.",
            "특정 환경에서 Claude 세션에 들어갈 때마다 로딩 스피너가 표시되던 문제를 수정했습니다.",
        }),
        ("v1.9.5", "2026-07-01", false, new[]
        {
            "Claude 세션에서 서브에이전트 작업 지시 후 작업 완료 신호가 잘못 수신되던 문제를 수정했습니다.",
        }),
        ("v1.9.4", "2026-07-01", false, new[]
        {
            "업데이트 누락으로 회귀되던 문제를 수정했습니다.",
        }),
        ("v1.9.3", "2026-07-01", false, new[]
        {
            "일부 환경에서 발생하던 클립보드 충돌 문제를 해결했습니다.",
        }),
        ("v1.9.2", "2026-07-01", false, new[]
        {
            "전반적인 성능개선 작업을 진행했습니다.",
        }),
        ("v1.9.1", "2026-06-30", false, new[]
        {
            "프로젝트 이름을 변경한 뒤 재실행해도 변경한 이름이 유지됩니다.",
            "프로젝트·세션 이름을 변경하면 세션 완료 기록 카드에도 즉시 반영됩니다.",
        }),
        ("v1.9.0", "2026-06-29", false, new[]
        {
            "같은 프로젝트(폴더)를 여러 개 등록할 수 있습니다.",
            "세션 터미널에서 마우스로 드래그해 텍스트를 선택·복사할 수 있습니다.",
            "계정 사용량 패널에 DeepSeek API 잔액을 표시합니다.",
            "그 외 사소한 오류를 수정했습니다.",
        }),
        ("v1.8.2", "2026-06-29", false, new[]
        {
            "Claude 세션 상태줄(statusLine)이 간헐적으로 불러와지지 않던 문제를 수정했습니다.",
        }),
        ("v1.8.1", "2026-06-28", false, new[]
        {
            "계정 사용량 패널에 OpenAI Codex 초기화권 개수와 유효기간을 표시합니다.",
            "기타 사소한 오류를 수정했습니다.",
        }),
        ("v1.8.0", "2026-06-28", false, new[]
        {
            "사용하지 않는 세션을 한 번에 정리하는 세션 클리너 기능을 추가했습니다.",
            "OpenCode 데이터베이스 용량을 정리(VACUUM)하는 기능을 추가했습니다.",
            "세션 응답 대기 표시가 잘못 뜨거나 누락되던 문제를 수정했습니다.",
        }),
        ("v1.7.0", "2026-06-27", false, new[]
        {
            "세션 완료 기록 기능을 추가했습니다.",
            "세션 입력 대기 상태 표시 기능을 추가했습니다.",
            "세션 탭 헤더 우클릭 메뉴를 추가했습니다.",
            "마크다운 편집 화면의 스타일을 변경했습니다.",
            "우클릭 붙여넣기가 두 번 입력되던 문제를 수정했습니다.",
            "그 외 사소한 오류를 수정했습니다.",
        }),
        ("v1.6.0", "2026-06-26", false, new[]
        {
            "세션 화면을 좌우로 분할해 두 프로젝트를 동시에 볼 수 있는 분할 기능을 추가했습니다.",
            "응답 완료 알림이 서브에이전트 작업을 본 응답 완료로 잘못 인식하던 문제를 수정했습니다.",
        }),
        ("v1.5.2", "2026-06-25", false, new[]
        {
            "하단 푸터의 계정 사용량 표시가 동작하지 않던 문제를 수정했습니다. (설정 > 계정 사용량 > 하단 푸터 표시)",
        }),
        ("v1.5.1", "2026-06-25", false, new[]
        {
            "전체화면 기능을 추가했습니다.",
            "가재코드(Gajae Code) 에이전트를 추가했습니다.",
            "프로젝트에 열어두었던 파일을 재실행 시 그대로 유지합니다.",
        }),
        ("v1.5.0", "2026-06-25", false, new[]
        {
            "계정 사용량을 우측 전용 사이드바로 분리하고 차트 버튼 또는 F1 키로 열고 닫을 수 있습니다.",
            "마크다운 파일 뷰어를 옵시디언 스타일로 개선했습니다.",
            "좌우 패널 접기 버튼 등 자잘한 UI를 다듬었습니다.",
        }),
        ("v1.4.0", "2026-06-23", false, new[]
        {
            "프로젝트 목록을 1열 또는 2열로 표시하는 옵션을 추가했습니다. (설정 > 프로젝트)",
            "2열에서는 프로젝트 카드를 좌우로 끌어 원하는 열에 배치할 수 있습니다.",
        }),
        ("v1.3.0", "2026-06-23", false, new[]
        {
            "프로젝트 보관함 기능을 추가했습니다.",
            "프로젝트 우클릭 메뉴에 이름 변경 기능을 추가했습니다.",
            "탭 이동 단축키 기능을 추가했습니다.",
            "터미널 드래그 선택과 복사 기능을 개선했습니다.",
            "세션을 불러오는 성능을 개선했습니다.",
            "관리자 권한 바로가기가 실행되지 않던 문제를 수정했습니다.",
            "프로그램 종료 시 간헐적으로 세션 대화 내용이 소실되던 문제를 수정했습니다.",
        }),
        ("v1.2.1", "2026-06-23", false, new[]
        {
            "선택하지 않은 프로젝트의 세션도 우클릭 메뉴와 마우스 호버가 동작합니다.",
            "선택하지 않은 프로젝트의 세션을 클릭하면 해당 세션이 바로 열립니다.",
            "세션을 클릭할 때 다른 세션이 함께 로드되던 문제를 수정했습니다.",
        }),
        ("v1.2.0", "2026-06-23", false, new[]
        {
            "마크다운 파일을 에디터 탭에서 열고 구문 강조를 지원합니다.",
            "파일 탐색기에 검색과 파일 타입 아이콘을 추가했습니다.",
        }),
        ("v1.1.0", "2026-06-22", false, new[]
        {
            "사이드바에 새로운 메뉴를 추가했습니다.",
            "프로젝트에 실행파일 바로가기 추가 기능을 도입했습니다.",
            "전반적인 UI를 개선했습니다.",
            "세션 변경 시 항상 표시되던 스피너 문제를 수정했습니다.",
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
        _originalPreloadAllSessions = SettingsService.LoadPreloadAllProjectSessions();
        _selectedPreloadAllSessions = _originalPreloadAllSessions;
        PreloadAllSessionsToggle.IsChecked = _selectedPreloadAllSessions;
        _originalAutoLoadLastProject = SettingsService.LoadAutoLoadLastProject();
        _selectedAutoLoadLastProject = _originalAutoLoadLastProject;
        AutoLoadLastProjectToggle.IsChecked = _selectedAutoLoadLastProject;
        _originalHideProjectInfoHeader = SettingsService.LoadHideProjectInfoHeader();
        _selectedHideProjectInfoHeader = _originalHideProjectInfoHeader;
        HideProjectInfoHeaderToggle.IsChecked = _selectedHideProjectInfoHeader;
        _originalUseFullScreen = SettingsService.LoadUseFullScreen();
        _selectedUseFullScreen = _originalUseFullScreen;
        UseFullScreenToggle.IsChecked = _selectedUseFullScreen;
        _originalProjectColumns = SettingsService.LoadProjectColumns();
        _selectedProjectColumns = _originalProjectColumns;
        UpdateProjectColumnsVisual();
        (_originalHkMod, _originalHkPrev, _originalHkNext) = SettingsService.LoadTabHotkey();
        _selectedHkMod = _originalHkMod; _selectedHkPrev = _originalHkPrev; _selectedHkNext = _originalHkNext;
        UpdateShortcutVisual();
        BuildAgentList();
        UpdateThemeSelectionVisual();
        UpdateFontSelectionVisual();
        SetActiveCategory("general");

        // 라이브 미리보기: 테마/글꼴 변경 시 좌측 탭 활성 배경·테마 카드 보더·글꼴 카드 보더를
        // 즉시 재계산. 캡처된 brush instance 라 DynamicResource 가 자동 갱신되지 않는 케이스 보정.
        _themeChangedHandler    = _ => RefreshAfterThemeChange();
        _fontScaleChangedHandler = _ => UpdateFontSelectionVisual();
        App.ThemeChanged     += _themeChangedHandler;
        App.FontScaleChanged  += _fontScaleChangedHandler;
        _subscribed = true;
        Unloaded += (_, _) => { Unsubscribe(); GlobalTabHotkey.CancelCapture(); };
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

        CatGeneralBtn.Background   = key == "general"    ? active : Brushes.Transparent;
        CatGeneralBtn.Foreground   = key == "general"    ? primary : text;
        CatProjectBtn.Background   = key == "project"    ? active : Brushes.Transparent;
        CatProjectBtn.Foreground   = key == "project"    ? primary : text;
        CatThemeBtn.Background     = key == "theme"      ? active : Brushes.Transparent;
        CatThemeBtn.Foreground     = key == "theme"      ? primary : text;
        CatAgentBtn.Background     = key == "agent"      ? active : Brushes.Transparent;
        CatAgentBtn.Foreground     = key == "agent"      ? primary : text;
        CatSidePanelBtn.Background = key == "sidepanel"  ? active : Brushes.Transparent;
        CatSidePanelBtn.Foreground = key == "sidepanel"  ? primary : text;
        CatUsageBtn.Background     = key == "usage"      ? active : Brushes.Transparent;
        CatUsageBtn.Foreground     = key == "usage"      ? primary : text;
        CatMcpBtn.Background       = key == "mcp"        ? active : Brushes.Transparent;
        CatMcpBtn.Foreground       = key == "mcp"        ? primary : text;
        CatChangelogBtn.Background = key == "changelog"  ? active : Brushes.Transparent;
        CatChangelogBtn.Foreground = key == "changelog"  ? primary : text;
        CatShortcutBtn.Background  = key == "shortcut"   ? active : Brushes.Transparent;
        CatShortcutBtn.Foreground  = key == "shortcut"   ? primary : text;
        CatNotifyBtn.Background    = key == "notify"     ? active : Brushes.Transparent;
        CatNotifyBtn.Foreground    = key == "notify"     ? primary : text;

        GeneralPanel.Visibility    = key == "general"    ? Visibility.Visible : Visibility.Collapsed;
        ProjectPanel.Visibility    = key == "project"    ? Visibility.Visible : Visibility.Collapsed;
        ThemePanel.Visibility      = key == "theme"      ? Visibility.Visible : Visibility.Collapsed;
        AgentPanel.Visibility      = key == "agent"      ? Visibility.Visible : Visibility.Collapsed;
        SidePanelPanel.Visibility  = key == "sidepanel"  ? Visibility.Visible : Visibility.Collapsed;
        UsagePanel.Visibility      = key == "usage"      ? Visibility.Visible : Visibility.Collapsed;
        McpPanel.Visibility        = key == "mcp"        ? Visibility.Visible : Visibility.Collapsed;
        ChangelogPanel.Visibility  = key == "changelog"  ? Visibility.Visible : Visibility.Collapsed;
        ShortcutPanel.Visibility   = key == "shortcut"   ? Visibility.Visible : Visibility.Collapsed;
        NotifyPanel.Visibility     = key == "notify"     ? Visibility.Visible : Visibility.Collapsed;
        DiscordPanel.Visibility    = key == "discord"    ? Visibility.Visible : Visibility.Collapsed;

        if (key != "shortcut") CancelShortcutCapture(); // 패널 떠나면 캡처 중단
        if (key == "sidepanel") LoadSidePanelSettings();
        if (key == "usage") LoadFooterUsageSettings();
        if (key == "notify") LoadNotifySettings();
        if (key == "discord") LoadDiscordSettings();
        if (key == "changelog") { _changelogPage = 0; RenderChangelogPage(); }
    }
    // ── 타이틀 10번 클릭 → Discord 버튼 표시 ─────────────────────────
    private void SettingsTitle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _titleClickCount++;
        if (_titleClickCount >= 10)
        {
            _titleClickCount = 10; // 고정
            DiscordRevealBtn.Visibility = Visibility.Visible;
        }
    }

    private void DiscordRevealBtn_Click(object sender, RoutedEventArgs e)
    {
        SetActiveCategory("discord");
    }

    private void PreloadAllSessionsToggle_Changed(object sender, RoutedEventArgs e)
    {
        _selectedPreloadAllSessions = PreloadAllSessionsToggle.IsChecked == true;
    }

    private void AutoLoadLastProjectToggle_Changed(object sender, RoutedEventArgs e)
    {
        _selectedAutoLoadLastProject = AutoLoadLastProjectToggle.IsChecked == true;
    }

    private void HideProjectInfoHeaderToggle_Changed(object sender, RoutedEventArgs e)
    {
        _selectedHideProjectInfoHeader = HideProjectInfoHeaderToggle.IsChecked == true;
    }

    private void UseFullScreenToggle_Changed(object sender, RoutedEventArgs e)
    {
        _selectedUseFullScreen = UseFullScreenToggle.IsChecked == true;
    }

    // ── 프로젝트 목록 열 수 (1/2) — 적용은 [저장] 시점에만(라이브 미리보기 없음) ──
    private void ProjectColumnsCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is string tag && int.TryParse(tag, out var cols))
        {
            _selectedProjectColumns = cols == 2 ? 2 : 1;
            UpdateProjectColumnsVisual();
        }
    }

    private void UpdateProjectColumnsVisual()
    {
        var primary = (Brush)FindResource("PrimaryBrush");
        var line    = (Brush)FindResource("LineBrush");
        foreach (var (card, dot, cols) in new (Border, Ellipse, int)[]
        {
            (ColCard_1, ColRadioDot_1, 1),
            (ColCard_2, ColRadioDot_2, 2),
        })
        {
            var selected = _selectedProjectColumns == cols;
            card.BorderBrush = selected ? primary : line;
            dot.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
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
            OpenCodeLoginIcon.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(App.OpenCodeIconUri)); // 테마별 흑백 아이콘
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

    // ── 사이드 패널 뷰 전환 버튼 표시 설정 ──────────────────────────
    private bool _loadingSidePanel;
    private void LoadSidePanelSettings()
    {
        _loadingSidePanel = true;
        ShowDirViewToggle.IsChecked     = SettingsService.LoadShowDirViewBtn();
        ShowQueueViewToggle.IsChecked   = SettingsService.LoadShowQueueViewBtn();
        ShowBrowserViewToggle.IsChecked = SettingsService.LoadShowBrowserViewBtn();
        ShowDiffViewToggle.IsChecked    = SettingsService.LoadShowDiffViewBtn();
        _loadingSidePanel = false;
    }

    private void SidePanelToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSidePanel) return;
        SettingsService.SaveShowDirViewBtn(ShowDirViewToggle.IsChecked == true);
        SettingsService.SaveShowQueueViewBtn(ShowQueueViewToggle.IsChecked == true);
        SettingsService.SaveShowBrowserViewBtn(ShowBrowserViewToggle.IsChecked == true);
        SettingsService.SaveShowDiffViewBtn(ShowDiffViewToggle.IsChecked == true);
        (Application.Current.MainWindow as MainWindow)?.ApplySidePanelButtonVisibility();
    }

    // ── 하단 푸터 계정 사용량(provider) 표시 설정 ─────────────────
    private bool _loadingFooterUsage;
    private void LoadFooterUsageSettings()
    {
        _loadingFooterUsage = true;
        ShowFooterClaudeToggle.IsChecked = SettingsService.LoadShowFooterClaude();
        ShowFooterCodexToggle.IsChecked  = SettingsService.LoadShowFooterCodex();
        ShowFooterGoToggle.IsChecked     = SettingsService.LoadShowFooterGo();
        ShowFooterDeepSeekToggle.IsChecked = SettingsService.LoadShowFooterDeepSeek();
        _loadingFooterUsage = false;
        UpdateConnectionBadges();

        // 한도 도달 예상 표시 토글
        ShowEstimateToggle.IsChecked = SettingsService.LoadShowEstimate();

        // DeepSeek 연결 토글 상태 복원 — 키가 이미 저장되어 있으면 입력 영역은 숨김
        bool hasKey = DeepSeekCredentialStore.IsConnected();
        _originalDeepSeekEnabled = hasKey;
        _selectedDeepSeekEnabled = hasKey;
        DeepSeekEnabledToggle.IsChecked = hasKey;
        DeepSeekKeyArea.Visibility = Visibility.Collapsed;
        DeepSeekKeyStatus.Visibility = Visibility.Collapsed;
    }

    private void FooterUsageToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingFooterUsage) return;
        SettingsService.SaveShowFooterClaude(ShowFooterClaudeToggle.IsChecked == true);
        SettingsService.SaveShowFooterCodex(ShowFooterCodexToggle.IsChecked == true);
        SettingsService.SaveShowFooterGo(ShowFooterGoToggle.IsChecked == true);
        SettingsService.SaveShowFooterDeepSeek(ShowFooterDeepSeekToggle.IsChecked == true);
        (Application.Current.MainWindow as MainWindow)?.ApplyFooterUsageVisibility();
    }

    private void EstimateToggle_Changed(object sender, RoutedEventArgs e)
    {
        SettingsService.SaveShowEstimate(ShowEstimateToggle.IsChecked == true);
        (Application.Current.MainWindow as MainWindow)?.RefreshUsagePanelIfVisible();
    }

    // ── 계정 사용량 로그인/재연결 — OAuth 창을 띄운다(갱신은 설정 닫힐 때 MainWindow 가 RefreshNow). ──
    private void ClaudeLogin_Click(object sender, RoutedEventArgs e)
    {
        new ClaudeLoginWindow(Window.GetWindow(this)).ShowDialog();
        UpdateConnectionBadges();
    }

    private void CodexLogin_Click(object sender, RoutedEventArgs e)
    {
        new CodexLoginWindow(Window.GetWindow(this)).ShowDialog();
        UpdateConnectionBadges();
    }

    private void OpenCodeLogin_Click(object sender, RoutedEventArgs e)
    {
        new OpenCodeGoLoginWindow(Window.GetWindow(this)).ShowDialog();
        UpdateConnectionBadges();
    }

    // ── DeepSeek API 키 토글/저장 ─────────────────────────────────

    private void DeepSeekToggle_Changed(object sender, RoutedEventArgs e)
    {
        // 디스크 반영은 [저장] 시점에만 — 여기선 선택값과 입력 영역 표시만 토글.
        _selectedDeepSeekEnabled = DeepSeekEnabledToggle.IsChecked == true;
        DeepSeekKeyStatus.Visibility = Visibility.Collapsed;
        // ON + 키 없음 → 키 입력 영역 노출, 그 외(ON+연결됨 / OFF)는 숨김
        DeepSeekKeyArea.Visibility =
            _selectedDeepSeekEnabled && !DeepSeekCredentialStore.IsConnected()
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DeepSeekKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        => DeepSeekKeyHint.Visibility = string.IsNullOrEmpty(DeepSeekKeyBox.Password)
            ? Visibility.Visible : Visibility.Collapsed;

    private void DeepSeekSaveKey_Click(object sender, RoutedEventArgs e)
    {
        var key = DeepSeekKeyBox.Password?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            DeepSeekKeyStatus.Text = "API 키를 입력하세요.";
            DeepSeekKeyStatus.Foreground = (Brush)FindResource("DangerBrush");
            DeepSeekKeyStatus.Visibility = Visibility.Visible;
            return;
        }

        DeepSeekCredentialStore.SaveApiKey(key);
        _originalDeepSeekEnabled = true;   // 즉시 연결 — [저장] 기준값도 갱신
        _selectedDeepSeekEnabled = true;
        DeepSeekConnectedBadge.Visibility = Visibility.Visible;
        DeepSeekKeyBox.Clear();
        // 저장 직후 입력 영역 숨김 — 토글만 켜진 상태 유지
        DeepSeekKeyArea.Visibility = Visibility.Collapsed;
        DeepSeekKeyStatus.Visibility = Visibility.Collapsed;

        (Application.Current.MainWindow as MainWindow)?.RefreshDeepSeekUsage();
    }

    /// <summary>provider 별 "연결됨" 배지를 현재 토큰/자격증명 상태로 갱신.</summary>
    private void UpdateConnectionBadges()
    {
        ClaudeConnectedBadge.Visibility = UsageApiService.IsConnected() ? Visibility.Visible : Visibility.Collapsed;
        CodexConnectedBadge.Visibility = CodexUsageService.IsConnected() ? Visibility.Visible : Visibility.Collapsed;
        GoConnectedBadge.Visibility = OpenCodeGoCredentialStore.IsConnected() ? Visibility.Visible : Visibility.Collapsed;
        DeepSeekConnectedBadge.Visibility = DeepSeekCredentialStore.IsConnected() ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 알림 설정 (sidepanel 과 동일하게 즉시 저장 — 테스트가 선택값을 바로 반영) ──
    private bool _loadingNotify;
    private string _notifyPos = "br";
    private void LoadNotifySettings()
    {
        _loadingNotify = true;
        NotifyEnabledToggle.IsChecked = SettingsService.LoadNotifySessionDoneEnabled();
        _notifyPos = SettingsService.LoadNotifyPosition();
        UpdateNotifyPositionVisual();
        SelectComboByTag(NotifyAutoCloseCombo, SettingsService.LoadNotifyAutoCloseSeconds().ToString());
        InitNotifyMonitorCombo();
        UpdateNotifyDetailVisibility();
        _loadingNotify = false;
    }

    /// <summary>세션 종료 알림이 꺼져 있으면 상세 설정(자동닫힘/위치/모니터)과 테스트 버튼을 모두 숨긴다.</summary>
    private void UpdateNotifyDetailVisibility()
    {
        var on = NotifyEnabledToggle.IsChecked == true;
        NotifyDetailPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        TestNotifyBtn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>설치된 모니터 목록을 콤보에 채우고 저장된 선택값(없으면 주 모니터)을 선택.</summary>
    private void InitNotifyMonitorCombo()
    {
        var monitors = MonitorHelper.GetAllMonitors();
        var saved = SettingsService.LoadNotifyMonitorDevice();
        NotifyMonitorCombo.Items.Clear();
        int selectIndex = 0;
        for (int i = 0; i < monitors.Count; i++)
        {
            NotifyMonitorCombo.Items.Add(new ComboBoxItem { Content = monitors[i].DisplayName, Tag = monitors[i].DeviceName });
            if (monitors[i].DeviceName == saved) selectIndex = i;
        }
        if (NotifyMonitorCombo.Items.Count > 0) NotifyMonitorCombo.SelectedIndex = selectIndex;
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (var obj in combo.Items)
            if (obj is ComboBoxItem it && (string?)it.Tag == tag) { combo.SelectedItem = it; return; }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private void NotifyEnabledToggle_Changed(object sender, RoutedEventArgs e)
    {
        UpdateNotifyDetailVisibility();
        if (_loadingNotify) return;
        SettingsService.SaveNotifySessionDoneEnabled(NotifyEnabledToggle.IsChecked == true);
    }

    private void NotifyAutoCloseCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingNotify) return;
        if (NotifyAutoCloseCombo.SelectedItem is ComboBoxItem it && int.TryParse((string)it.Tag, out var sec))
            SettingsService.SaveNotifyAutoCloseSeconds(sec);
    }

    private void NotifyMonitorCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingNotify) return;
        if (NotifyMonitorCombo.SelectedItem is ComboBoxItem it)
            SettingsService.SaveNotifyMonitorDevice((string)(it.Tag ?? ""));
    }

    private void NotifyPositionCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b || b.Tag is not string pos) return;
        _notifyPos = pos;
        SettingsService.SaveNotifyPosition(pos);
        UpdateNotifyPositionVisual();
    }

    private void UpdateNotifyPositionVisual()
    {
        var primary = (Brush)FindResource("PrimaryBrush");
        var line    = (Brush)FindResource("LineBrush");
        foreach (var (card, dot, pos) in new (Border, Ellipse, string)[]
        {
            (PosCard_tl, PosDot_tl, "tl"),
            (PosCard_tr, PosDot_tr, "tr"),
            (PosCard_bl, PosDot_bl, "bl"),
            (PosCard_br, PosDot_br, "br"),
        })
        {
            var selected = _notifyPos == pos;
            card.BorderBrush = selected ? primary : line;
            dot.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void TestNotify_Click(object sender, RoutedEventArgs e)
        => App.ShowNotification("테스트 알림", "세션이 끝나면 이렇게 알려드립니다.");

    // ── Discord 설정 (즉시 저장 — 토큰/서버 ID 변경 후 재연결 버튼으로 런타임 반영) ──
    private bool _loadingDiscord;

    private void LoadDiscordSettings()
    {
        _loadingDiscord = true;
        DiscordEnabledToggle.IsChecked = SettingsService.LoadDiscordEnabled();
        DiscordTokenBox.Password = SettingsService.LoadDiscordBotToken();
        var guildId = SettingsService.LoadDiscordGuildId();
        DiscordGuildIdBox.Text = guildId == 0 ? "" : guildId.ToString();
        DiscordNotifyDoneToggle.IsChecked = SettingsService.LoadDiscordNotifySessionDone();
        _loadingDiscord = false;
    }

    private void DiscordSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingDiscord) return;
        SettingsService.SaveDiscordEnabled(DiscordEnabledToggle.IsChecked == true);
        SettingsService.SaveDiscordBotToken(DiscordTokenBox.Password);
        SettingsService.SaveDiscordNotifySessionDone(DiscordNotifyDoneToggle.IsChecked == true);
        if (ulong.TryParse(DiscordGuildIdBox.Text.Trim(), out var guildId))
            SettingsService.SaveDiscordGuildId(guildId);
        else if (string.IsNullOrWhiteSpace(DiscordGuildIdBox.Text))
            SettingsService.SaveDiscordGuildId(0);
    }

    private void DiscordReconnect_Click(object sender, RoutedEventArgs e)
    {
        DiscordSetting_Changed(sender, e);
        DiscordBotService.Instance.Restart();
    }

    // ── 탭 이동 단축키 수식키 리바인드 (방향키는 ← / → 고정) ──────
    /// <summary>수식키 칸 클릭 → 전역 훅 캡처 시작. 다음 키다운 1회를 수식키로 지정.</summary>
    private void KeyField_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b || b.Tag is not string field) return;
        if (_capturingField == field) { CancelShortcutCapture(); return; } // 같은 칸 재클릭 = 취소
        _capturingField = field;
        UpdateShortcutVisual();
        GlobalTabHotkey.BeginCapture(vk =>
        {
            if (vk != 0x1B) _selectedHkMod = vk; // Esc = 취소, 그 외 = 지정
            _capturingField = null;
            UpdateShortcutVisual();
        });
    }

    private void CancelShortcutCapture()
    {
        if (_capturingField == null) return;
        _capturingField = null;
        GlobalTabHotkey.CancelCapture();
        UpdateShortcutVisual();
    }

    /// <summary>수식키 칸 텍스트/보더를 현재 선택값(또는 캡처 중 표시)으로 갱신.</summary>
    private void UpdateShortcutVisual()
    {
        bool capturing = _capturingField == "mod";
        ModKeyText.Text = capturing ? "키 입력…" : GlobalTabHotkey.KeyName(_selectedHkMod);
        ModKeyField.BorderBrush = capturing
            ? (Brush)FindResource("PrimaryBrush")
            : (Brush)FindResource("LineBrush");
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
        if (_selectedPreloadAllSessions != _originalPreloadAllSessions) return true;
        if (_selectedAutoLoadLastProject != _originalAutoLoadLastProject) return true;
        if (_selectedHideProjectInfoHeader != _originalHideProjectInfoHeader) return true;
        if (_selectedUseFullScreen != _originalUseFullScreen) return true;
        if (_selectedProjectColumns != _originalProjectColumns) return true;
        if (_selectedDeepSeekEnabled != _originalDeepSeekEnabled) return true;
        if (_selectedHkMod != _originalHkMod || _selectedHkPrev != _originalHkPrev || _selectedHkNext != _originalHkNext) return true;
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
        SettingsService.SavePreloadAllProjectSessions(_selectedPreloadAllSessions);
        SettingsService.SaveAutoLoadLastProject(_selectedAutoLoadLastProject);
        if (_selectedHideProjectInfoHeader != _originalHideProjectInfoHeader)
        {
            SettingsService.SaveHideProjectInfoHeader(_selectedHideProjectInfoHeader);
            (Application.Current.MainWindow as MainWindow)?.ApplyProjectInfoHeaderVisibility();
        }
        if (_selectedUseFullScreen != _originalUseFullScreen)
        {
            SettingsService.SaveUseFullScreen(_selectedUseFullScreen);
            (Application.Current.MainWindow as MainWindow)?.ApplyFullScreen(_selectedUseFullScreen);
        }
        if (_selectedProjectColumns != _originalProjectColumns)
        {
            SettingsService.SaveProjectColumns(_selectedProjectColumns);
            (Application.Current.MainWindow as MainWindow)?.ApplyProjectColumns(_selectedProjectColumns);
        }
        UpdateAgentEnabledInSettings();

        // DeepSeek: 토글 OFF로 저장 → 저장된 키 삭제. (ON은 키 입력 영역의 [저장]에서 이미 반영됨)
        if (!_selectedDeepSeekEnabled && _originalDeepSeekEnabled)
        {
            DeepSeekCredentialStore.SaveApiKey(null);
            DeepSeekConnectedBadge.Visibility = Visibility.Collapsed;
            DeepSeekKeyArea.Visibility = Visibility.Collapsed;
            (Application.Current.MainWindow as MainWindow)?.ApplyFooterUsageVisibility();
            (Application.Current.MainWindow as MainWindow)?.RefreshDeepSeekUsage();
        }
        _originalDeepSeekEnabled = _selectedDeepSeekEnabled;

        if (_selectedHkMod != _originalHkMod || _selectedHkPrev != _originalHkPrev || _selectedHkNext != _originalHkNext)
        {
            SettingsService.SaveTabHotkey(_selectedHkMod, _selectedHkPrev, _selectedHkNext);
            GlobalTabHotkey.Configure(_selectedHkMod, _selectedHkPrev, _selectedHkNext); // 런타임 즉시 적용
        }

        _originalTheme       = _selectedTheme;
        _originalFontScale   = _selectedFontScale;
        _originalPreloadAllSessions = _selectedPreloadAllSessions;
        _originalAutoLoadLastProject = _selectedAutoLoadLastProject;
        _originalHideProjectInfoHeader = _selectedHideProjectInfoHeader;
        _originalUseFullScreen = _selectedUseFullScreen;
        _originalProjectColumns = _selectedProjectColumns;
        _originalHkMod = _selectedHkMod; _originalHkPrev = _selectedHkPrev; _originalHkNext = _selectedHkNext;
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
        if (_selectedPreloadAllSessions != _originalPreloadAllSessions)
        {
            _selectedPreloadAllSessions = _originalPreloadAllSessions;
            PreloadAllSessionsToggle.IsChecked = _selectedPreloadAllSessions;
        }
        if (_selectedAutoLoadLastProject != _originalAutoLoadLastProject)
        {
            _selectedAutoLoadLastProject = _originalAutoLoadLastProject;
            AutoLoadLastProjectToggle.IsChecked = _selectedAutoLoadLastProject;
        }
        if (_selectedHideProjectInfoHeader != _originalHideProjectInfoHeader)
        {
            _selectedHideProjectInfoHeader = _originalHideProjectInfoHeader;
            HideProjectInfoHeaderToggle.IsChecked = _selectedHideProjectInfoHeader;
        }
        if (_selectedUseFullScreen != _originalUseFullScreen)
        {
            _selectedUseFullScreen = _originalUseFullScreen;
            UseFullScreenToggle.IsChecked = _selectedUseFullScreen;
        }
        if (_selectedProjectColumns != _originalProjectColumns)
        {
            _selectedProjectColumns = _originalProjectColumns; // 라이브 미적용이라 선택값만 복원
            UpdateProjectColumnsVisual();
        }
        if (_selectedDeepSeekEnabled != _originalDeepSeekEnabled)
        {
            _selectedDeepSeekEnabled = _originalDeepSeekEnabled; // 미적용 — 선택값만 복원(키는 건드리지 않음)
            DeepSeekEnabledToggle.IsChecked = _originalDeepSeekEnabled;
            DeepSeekKeyArea.Visibility = Visibility.Collapsed;
        }
        // 단축키 미저장 변경 되돌리기 (디스크 저장 안 했으므로 선택값만 복원 + 캡처 중단)
        CancelShortcutCapture();
        _selectedHkMod = _originalHkMod; _selectedHkPrev = _originalHkPrev; _selectedHkNext = _originalHkNext;
        UpdateShortcutVisual();
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

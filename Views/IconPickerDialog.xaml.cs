using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DevezCode.Views;

/// <summary>프로젝트 아이콘·색상 선택 다이얼로그 (devez IconPickerDialog 이식).</summary>
public partial class IconPickerDialog : Window
{
    // 의미별 그룹 정렬 (10열 5행)
    private static readonly (string Label, string? Key)[] _icons =
    {
        ("프로젝트","IconBox"),  ("폴더","IconFolder"), ("책갈피","IconBookmark"), ("별","IconStar"),     ("하트","IconHeart"),     ("사용자","IconUserRound"),("아기","IconBaby"),       ("골뱅이","IconAtSign"),      ("연락","IconPhone"),       ("클로버","IconClover"),
        ("문서","IconFileText"), ("할일","IconListTodo"),("코드","IconCode"),       ("계산기","IconCalculator"),("설정","IconSettings"),  ("달러","IconDollarSign"), ("지갑","IconWallet"),     ("장바구니","IconShoppingCart"),("차트","IconStocks"),    ("통계","IconPieChart"),
        ("SQL","IconDatabase"),  ("서버","IconServer"), ("클라우드","IconCloud"),   ("지구본","IconGlobe"), ("링크","IconLink"),      ("외부링크","IconExternalLink"),("RSS","IconRss"),       ("프린터","IconPrinter"),     ("보안","IconLock"),        ("열쇠","IconKey"),
        ("시계","IconClock"),    ("알람","IconAlarmClock"),("사이렌","IconSiren"),  ("경고","IconTriangleAlert"),("버그","IconBug"),     ("번개","IconZap"),        ("불꽃","IconFlame"),      ("전구","IconLightbulb"),     ("AI","IconSparkle"),      ("태양","IconSun"),
        ("카메라","IconCamera"), ("이미지","IconImage"),("음악","IconMusic"),       ("팔레트","IconPalette"),("자동차","IconCar"),    ("비행기","IconPlane"),    ("위치","IconMapPin"),     ("식사","IconSoup"),          ("커피","IconCoffee"),      ("강아지발","IconPawPrint"),
    };

    // 색상환 순서 + 무채색 + 기본. 10열 2행.
    private static readonly (string Label, string? Hex)[] _colorItems =
    {
        ("빨강",  "#ef4444"), ("로즈",   "#f43f5e"), ("주황",    "#f97316"), ("호박",     "#f59e0b"), ("노랑",  "#eab308"),
        ("라임",  "#84cc16"), ("초록",   "#22c55e"), ("에메랄드","#10b981"), ("청록",     "#0d9488"), ("시안",  "#06b6d4"),
        ("하늘",  "#0ea5e9"), ("파랑",   "#3b82f6"), ("남색",    "#6366f1"), ("바이올렛", "#8b5cf6"), ("보라",  "#a855f7"),
        ("자주",  "#d946ef"), ("분홍",   "#ec4899"), ("슬레이트","#334155"), ("회색",     "#71717a"), ("기본",  null),
    };

    private string? _selectedIcon;
    private string? _selectedColor;

    public string? ResultIcon  { get; private set; }
    public string? ResultColor { get; private set; }

    private IconPickerDialog(string? currentIcon, string? currentColor, string subtitle)
    {
        InitializeComponent();
        _selectedIcon  = currentIcon;
        _selectedColor = currentColor;
        KeyDown += OnKeyDown;
        Loaded  += (_, _) =>
        {
            SubtitleText.Text = subtitle;
            BuildIconGrid();
            BuildColorGrid();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, SyncIconTileHeights);
            IconGrid.SizeChanged += (_, _) => SyncIconTileHeights();
        };
    }

    public static (string? Icon, string? Color)? Show(string? currentIcon, string? currentColor, string subtitle)
    {
        var dlg = new IconPickerDialog(currentIcon, currentColor, subtitle);
        if (Application.Current.MainWindow is { IsLoaded: true } mw && mw != dlg)
            dlg.Owner = mw;
        return dlg.ShowDialog() == true ? (dlg.ResultIcon, dlg.ResultColor) : null;
    }

    // ── grid builders ─────────────────────────────────────────────────────────

    private void BuildIconGrid()
    {
        IconGrid.Children.Clear();
        foreach (var (label, key) in _icons)
            IconGrid.Children.Add(MakeIconTile(label, key));
        SyncIconTileHeights();
    }

    private void SyncIconTileHeights()
    {
        if (IconGrid.Columns <= 0 || !(IconGrid.ActualWidth > 0)) return;
        var cellW = IconGrid.ActualWidth / IconGrid.Columns;
        var cardSize = Math.Floor(cellW - 8);
        if (cardSize <= 0) return;
        foreach (FrameworkElement fe in IconGrid.Children)
        {
            if (Math.Abs(fe.Width - cardSize) < 0.5) continue;
            fe.Width  = cardSize;
            fe.Height = cardSize;
            fe.HorizontalAlignment = HorizontalAlignment.Center;
        }
    }

    private void BuildColorGrid()
    {
        ColorGrid.Children.Clear();
        foreach (var (label, hex) in _colorItems)
            ColorGrid.Children.Add(MakeColorDot(label, hex));
    }

    // ── tile factories ─────────────────────────────────────────────────────────

    private Border MakeIconTile(string label, string? key)
    {
        bool sel = key == _selectedIcon;

        var primary     = R("PrimaryBrush");
        var primarySoft = R("PrimarySoftBrush");
        var lineBrush   = R("LineBrush");
        var textBrush   = R("TextBrush");
        var panelBrush  = R("PanelBrush");
        var panelSoft   = R("PanelSoftBrush");

        var border = new Border
        {
            CornerRadius    = new CornerRadius(8),
            Padding         = new Thickness(6),
            Margin          = new Thickness(4),
            Cursor          = Cursors.Hand,
            BorderThickness = new Thickness(1.5),
            Background      = sel ? primarySoft : panelBrush,
            BorderBrush     = sel ? primary     : lineBrush,
        };

        var sp = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };

        if (TryFindResource(key ?? "IconFolder") is Geometry geom)
        {
            sp.Children.Add(new Path
            {
                Data                = geom,
                Width               = key == "IconSparkle" ? 18 : 22,
                Height              = key == "IconSparkle" ? 18 : 22,
                Stroke              = sel ? primary : textBrush,
                StrokeThickness     = 1.5,
                StrokeStartLineCap  = PenLineCap.Round,
                StrokeEndLineCap    = PenLineCap.Round,
                StrokeLineJoin      = PenLineJoin.Round,
                Stretch             = Stretch.Uniform,
                Fill                = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }

        border.ToolTip = label;
        border.Child   = sp;

        var capturedKey = key;
        border.MouseLeftButtonDown += (_, _) => { _selectedIcon = capturedKey; BuildIconGrid(); };
        border.MouseEnter += (_, _) => { if (capturedKey != _selectedIcon) border.Background = panelSoft; };
        border.MouseLeave += (_, _) => { if (capturedKey != _selectedIcon) border.Background = panelBrush; };

        return border;
    }

    private Border MakeColorDot(string label, string? hex)
    {
        bool sel = hex == _selectedColor;

        var primary    = R("PrimaryBrush");
        var lineBrush   = R("LineBrush");
        var panelBrush  = R("PanelBrush");
        var panelSoft   = R("PanelSoftBrush");
        Brush fill = string.IsNullOrEmpty(hex)
            ? R("TextMutedBrush")
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        var outer = new Border
        {
            CornerRadius    = new CornerRadius(8),
            Margin          = new Thickness(2),
            Cursor          = Cursors.Hand,
            Padding         = new Thickness(4),
            BorderThickness = new Thickness(1.5),
            BorderBrush     = sel ? primary : lineBrush,
            Background      = sel ? R("PrimarySoftBrush") : panelBrush,
            ToolTip         = label,
        };

        outer.Child = new Border
        {
            Width = 16, Height = 16,
            CornerRadius = new CornerRadius(8),
            Background   = fill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };

        var capturedHex = hex;
        outer.MouseLeftButtonDown += (_, _) => { _selectedColor = capturedHex; BuildColorGrid(); };
        outer.MouseEnter += (_, _) => { if (capturedHex != _selectedColor) outer.Background = panelSoft; };
        outer.MouseLeave += (_, _) => { if (capturedHex != _selectedColor) outer.Background = panelBrush; };

        return outer;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private Brush R(string key) => (FindResource(key) as Brush) ?? Brushes.Gray;

    private void Commit()
    {
        ResultIcon   = _selectedIcon;
        ResultColor  = _selectedColor;
        DialogResult = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)  { Commit();             e.Handled = true; }
        if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
    }

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OkBtn_Click(object     sender, RoutedEventArgs e) => Commit();
    private void CancelBtn_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void CloseBtn_Click(object  sender, RoutedEventArgs e) => DialogResult = false;
}

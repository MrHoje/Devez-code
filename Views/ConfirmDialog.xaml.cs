using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;

namespace DevezCode.Views;

/// <summary>3버튼 확인 다이얼로그 결과.</summary>
public enum ConfirmChoice { Primary, Secondary, Cancel }

public partial class ConfirmDialog : Window
{
    private string? _confirmText;
    private ConfirmChoice _choice = ConfirmChoice.Cancel;

    private ConfirmDialog(string title, string message, string okLabel, string iconKey, bool danger, string? confirmText, bool wideLayout, bool autoWidth = false)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        OkText.Text = okLabel;
        _confirmText = confirmText;

        if (confirmText != null)
        {
            ConfirmInputPanel.Visibility = Visibility.Visible;
            ConfirmHintText.Text = $"계속하려면 '{confirmText}'을(를) 입력하세요.";
            OkBtn.IsEnabled = false;
            Loaded += (_, _) => ConfirmInputBox.Focus();
        }
        if (autoWidth)
        {
            // 업데이트 노트 전용: 텍스트 길이에 따라 폭을 자동 조절(상한 660), 짧으면 360.
            SizeToContent = SizeToContent.WidthAndHeight;
            MinWidth = 360;
            MaxWidth = 660;
        }
        else if (confirmText == null)
        {
            // devez 크기 로직: 폭은 490 고정(XAML), 높이는 본문 줄 수로 계단식 결정.
            // 스크롤 없이 3줄이 다 보이도록 각 단계를 살짝 높임.
            var lines = message.Split('\n').Length;
            Height = lines <= 2 ? 260 : lines <= 4 ? 325 : 360;

            // 가장 긴 줄이 기본 폭에 안 들어가면 필요한 만큼 폭을 넓혀 줄바꿈을 줄인다(최대 +100px).
            GrowWidthForLongestLine(message);
        }

        KeyDown += OnKeyDown;
        PreviewKeyDown += OnPreviewKeyDown;
        // 창이 뜨면 OK 버튼에 포커스를 둬서 Enter 가 곧바로 Primary 동작이 되게 한다.
        Loaded += (_, _) =>
        {
            try
            {
                WindowCenter.CenterOverOwner(this); // 가짜 전체화면/보조 모니터 배율에서 CenterOwner 가 어긋나는 것 보정
                if (OkBtn.IsVisible) OkBtn.Focus();
            }
            catch { }
        };
    }

    public static bool Show(
        string title,
        string message,
        string okLabel = "확인",
        string iconKey = "IconLogOut",
        bool danger = false,
        string? confirmText = null,
        bool topMost = false,
        bool wideLayout = false,
        bool autoWidth = false)
    {
        var dialog = new ConfirmDialog(title, message, okLabel, iconKey, danger, confirmText, wideLayout, autoWidth);

        if (Application.Current.MainWindow != null
            && Application.Current.MainWindow.IsLoaded
            && Application.Current.MainWindow != dialog)
        {
            dialog.Owner = Application.Current.MainWindow;
        }

        // Topmost 플로팅 창(스티커 메모 등) 위로 뜨도록
        if (topMost) dialog.Topmost = true;

        return dialog.ShowDialog() == true;
    }

    /// <summary>3버튼 확인 다이얼로그. Primary(주 동작)·Secondary(중간 동작)·Cancel 중 하나를 반환한다.</summary>
    public static ConfirmChoice ShowThreeWay(
        string title,
        string message,
        string primaryLabel,
        string secondaryLabel,
        string iconKey = "IconMessageSquare",
        bool danger = false,
        bool topMost = false,
        bool hideCancel = false)
    {
        var dialog = new ConfirmDialog(title, message, primaryLabel, iconKey, danger, null, wideLayout: false);
        dialog.MiddleText.Text = secondaryLabel;
        dialog.MiddleBtn.Visibility = Visibility.Visible;
        if (hideCancel) dialog.CancelBtn.Visibility = Visibility.Collapsed; // 취소 버튼 숨김(두 선택지만)

        if (Application.Current.MainWindow != null
            && Application.Current.MainWindow.IsLoaded
            && Application.Current.MainWindow != dialog)
        {
            dialog.Owner = Application.Current.MainWindow;
        }

        if (topMost) dialog.Topmost = true;

        dialog.ShowDialog();
        return dialog._choice;
    }

    /// <summary>메시지에서 가장 긴 줄이 기본 폭(490)에 안 들어가면 필요한 만큼 창 폭을 넓힌다(최대 +150px → 640).</summary>
    private void GrowWidthForLongestLine(string message)
    {
        try
        {
            double dpi = 1.0;
            try { dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { }
            var typeface = new System.Windows.Media.Typeface(
                (System.Windows.Media.FontFamily)FindResource("PretendardFont"),
                FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var fontSize = (double)FindResource("Fs13");

            double maxLine = 0;
            foreach (var line in message.Split('\n'))
            {
                var ft = new System.Windows.Media.FormattedText(
                    line,
                    System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, typeface, fontSize,
                    System.Windows.Media.Brushes.Black, dpi);
                if (ft.Width > maxLine) maxLine = ft.Width;
            }

            // 창 폭 = 본문텍스트폭 + 좌우 본문여백(28*2) + 그림자여백(20*2) + 여유(8).
            double target = maxLine + 56 + 40 + 8;
            if (target > Width) Width = System.Math.Min(Width + 150, target);
        }
        catch { }
    }

    private void ConfirmInputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        OkBtn.IsEnabled = _confirmText != null && ConfirmInputBox.Text == _confirmText;
    }

    public static void Alert(
        string title,
        string message,
        string okLabel = "확인",
        string iconKey = "IconMessageSquare")
    {
        var dialog = new ConfirmDialog(title, message, okLabel, iconKey, false, null, wideLayout: false);
        dialog.CancelBtn.Visibility = Visibility.Collapsed;

        if (Application.Current.MainWindow != null
            && Application.Current.MainWindow.IsLoaded
            && Application.Current.MainWindow != dialog)
        {
            dialog.Owner = Application.Current.MainWindow;
        }

        dialog.ShowDialog();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (_confirmText == null || ConfirmInputBox.Text == _confirmText)
            {
                _choice = ConfirmChoice.Primary;
                DialogResult = true;
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }

    /// <summary>Preview 단계에서 Enter/Esc 를 가로채 항상 동작하게 한다.
    /// 자식 컨트롤이 KeyDown 을 먼저 먹어도(예: TextBox) PreviewKeyDown 은 라우팅 최상위에서 먼저 도달.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // 확인 텍스트 입력이 있고 아직 일치하지 않으면 Primary 로 넘기지 않음.
            if (_confirmText != null && ConfirmInputBox.Text != _confirmText) return;
            _choice = ConfirmChoice.Primary;
            DialogResult = true;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e) { _choice = ConfirmChoice.Primary; DialogResult = true; }
    private void MiddleBtn_Click(object sender, RoutedEventArgs e) { _choice = ConfirmChoice.Secondary; DialogResult = true; }
    private void CancelBtn_Click(object sender, RoutedEventArgs e) { _choice = ConfirmChoice.Cancel; DialogResult = false; }
}

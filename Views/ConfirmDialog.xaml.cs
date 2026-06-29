using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;

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
        else
        {
            // 본문 가장 긴 줄의 실제 렌더 폭을 측정해 최소 360 에서 딱 필요한 만큼만 확장(상한 720).
            Width = MeasureWidth(message);
        }

        KeyDown += OnKeyDown;
        PreviewKeyDown += OnPreviewKeyDown;
        // 창이 뜨면 OK 버튼에 포커스를 둬서 Enter 가 곧바로 Primary 동작이 되게 한다.
        Loaded += (_, _) =>
        {
            try
            {
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

    /// <summary>본문 최장 줄을 실제 글꼴로 측정해 필요한 창 폭을 산정. [500, 720] 클램프.</summary>
    private double MeasureWidth(string message)
    {
        double fontSize = TryFindResource("Fs13") is double fs ? fs : 13.0;
        var typeface = new Typeface(MessageText.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        double maxLine = 0;
        foreach (var line in message.Split('\n'))
        {
            var ft = new FormattedText(line, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, fontSize, Brushes.Black, dpi);
            if (ft.WidthIncludingTrailingWhitespace > maxLine) maxLine = ft.WidthIncludingTrailingWhitespace;
        }

        // chrome: 본문 좌우 패딩 28*2 + 창 그림자 마진 20*2 + 테두리/여유.
        double needed = maxLine + 56 + 40 + 8;
        // 하한 360(footer 버튼 취소+중단 최소폭) — 짧은 본문은 텍스트에 딱 붙임.
        return Math.Max(360, Math.Min(720, needed));
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

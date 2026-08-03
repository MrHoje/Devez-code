using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;

namespace DevezCode.Views;

/// <summary>3버튼 확인 다이얼로그 결과.</summary>
public enum ConfirmChoice { Primary, Secondary, Cancel }

/// <summary>진행 작업 결과. 앱 업데이트 성공 시에는 재실행되어 반환되지 않는다.</summary>
public enum UpdateOutcome { Cancelled, Completed, Failed, ElevationDenied }

public partial class ConfirmDialog : Window
{
    private string? _confirmText;
    private ConfirmChoice _choice = ConfirmChoice.Cancel;
    private bool _linkClicked;

    // 업데이트 진행률 모드용.
    private Func<IProgress<double>, Task>? _download;
    private bool _downloading;
    private bool _indeterminateProgress;
    private UpdateOutcome _updateOutcome = UpdateOutcome.Cancelled;

    /// <summary>Owner 는 "지금 활성인 창" 우선 — 설정 화면처럼 메인창 위에 떠 있는 비모달 창에서 띄울 때
    /// Owner 를 메인창으로 고정하면 형제 창인 그 창 뒤로 숨을 수 있다.</summary>
    private static void ApplyOwner(Window dialog)
    {
        var active = Application.Current.Windows.OfType<Window>()
            .FirstOrDefault(w => w.IsActive && w.IsLoaded && w != dialog);
        var owner = active ?? Application.Current.MainWindow;
        if (owner is { IsLoaded: true } && owner != dialog) dialog.Owner = owner;
    }

    private ConfirmDialog(string title, string message, string okLabel, string iconKey, bool danger, string? confirmText, bool wideLayout, bool autoWidth = false, string? cancelLabel = null)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        OkText.Text = okLabel;
        if (cancelLabel != null) CancelText.Text = cancelLabel;   // 취소가 단순 취소가 아닌 경우(예: 되돌리기)
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
            // 업데이트 노트 전용: 텍스트 길이에 따라 폭을 자동 조절(상한 700), 짧아도 기본 확인창과 같은 490.
            SizeToContent = SizeToContent.WidthAndHeight;
            MinWidth = 490;
            MaxWidth = 700;
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
        bool autoWidth = false,
        string? cancelLabel = null)
    {
        var dialog = new ConfirmDialog(title, message, okLabel, iconKey, danger, confirmText, wideLayout, autoWidth, cancelLabel);

        ApplyOwner(dialog);

        // Topmost 플로팅 창(스티커 메모 등) 위로 뜨도록
        if (topMost) dialog.Topmost = true;

        return dialog.ShowDialog() == true;
    }

    /// <summary>업데이트 노트 팝업 → "업데이트" 클릭 시 창을 닫지 않고 같은 창 안에서 진행률을
    /// 표시하며 <paramref name="download"/> 를 실행한다. 일반 작업 완료는 <see cref="UpdateOutcome.Completed"/>,
    /// 사용자가 취소하면 <see cref="UpdateOutcome.Cancelled"/>, 작업이 실패하면
    /// <see cref="UpdateOutcome.Failed"/> 를 반환한다.</summary>
    public static UpdateOutcome ShowUpdate(
        string title,
        string message,
        Func<IProgress<double>, Task> download,
        string okLabel = "업데이트",
        string iconKey = "IconDownload",
        string cancelLabel = "나중에",
        string progressLabel = "다운로드 중…",
        bool indeterminateProgress = false)
    {
        var dialog = new ConfirmDialog(title, message, okLabel, iconKey, danger: false,
                                       confirmText: null, wideLayout: false, autoWidth: true);
        dialog._download = download;
        dialog._indeterminateProgress = indeterminateProgress;
        dialog.CancelText.Text = cancelLabel;
        dialog.ProgressLabel.Text = progressLabel;
        // 내용에 맞춰 폭을 줄이되 최대 700까지만 넓힌다. 약 10줄까지 높이 자동 확장한다.
        dialog.SizeToContent = SizeToContent.WidthAndHeight;
        dialog.MaxHeight = 430;
        dialog.MinWidth = 490;
        dialog.MaxWidth = 700;

        ApplyOwner(dialog);

        dialog.ShowDialog();
        return dialog._updateOutcome;
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

        ApplyOwner(dialog);

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

        ApplyOwner(dialog);

        dialog.ShowDialog();
    }

    /// <summary>Alert + 좌측 하단 보조 링크(예: "잠금 해제 후 삭제"). 링크를 클릭해 닫으면 true,
    /// 확인/닫기로 닫으면 false를 반환한다.</summary>
    public static bool AlertWithLink(
        string title,
        string message,
        string linkLabel,
        string okLabel = "확인",
        string iconKey = "IconMessageSquare")
    {
        var dialog = new ConfirmDialog(title, message, okLabel, iconKey, false, null, wideLayout: false);
        dialog.CancelBtn.Visibility = Visibility.Collapsed;
        dialog.LinkText.Text = linkLabel;
        dialog.LinkText.Visibility = Visibility.Visible;

        ApplyOwner(dialog);

        dialog.ShowDialog();
        return dialog._linkClicked;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_downloading) { e.Handled = true; return; } // 진행 중 Enter/Esc 무시
        if (e.Key == Key.Enter)
        {
            if (_confirmText == null || ConfirmInputBox.Text == _confirmText)
            {
                TriggerPrimary();
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
        if (_downloading) { e.Handled = true; return; } // 진행 중 Enter/Esc 무시
        if (e.Key == Key.Enter)
        {
            // 확인 텍스트 입력이 있고 아직 일치하지 않으면 Primary 로 넘기지 않음.
            if (_confirmText != null && ConfirmInputBox.Text != _confirmText) return;
            TriggerPrimary();
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

    private void OkBtn_Click(object sender, RoutedEventArgs e) => TriggerPrimary();
    private void MiddleBtn_Click(object sender, RoutedEventArgs e) { _choice = ConfirmChoice.Secondary; DialogResult = true; }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading) return; // 다운로드 진행 중에는 닫기 차단
        _choice = ConfirmChoice.Cancel;
        DialogResult = false;
    }

    /// <summary>Primary(확인/업데이트) 동작. 진행률 모드면 창을 닫지 않고 다운로드를 시작한다.</summary>
    private async void TriggerPrimary()
    {
        if (_downloading) return;
        _choice = ConfirmChoice.Primary;

        if (_download == null) { DialogResult = true; return; }

        // ── 업데이트 진행률 모드 ──
        _downloading = true;
        FooterButtons.Visibility = Visibility.Collapsed;
        HeaderCloseBtn.Visibility = Visibility.Collapsed;
        ConfirmInputPanel.Visibility = Visibility.Collapsed;
        ProgressArea.Visibility = Visibility.Visible;
        if (_indeterminateProgress)
        {
            ProgressPercent.Visibility = Visibility.Collapsed;
            ProgressTrack.Visibility = Visibility.Collapsed;
            IndeterminateProgress.Visibility = Visibility.Visible;
        }

        var progress = new Progress<double>(v =>
        {
            var pct = Math.Clamp(v, 0, 1);
            ProgressPercent.Text = $"{pct:P0}";
            ProgressFill.Width = ProgressTrack.ActualWidth * pct;
        });

        try
        {
            // 실제 업데이트는 이 호출 안에서 앱이 종료·재실행되므로 반환되지 않는다.
            // (테스트 다운로드 등으로) 정상 복귀하면 진행률을 100%로 채우고 창을 닫는다.
            await _download(progress);
            ProgressPercent.Text = "100 %";
            ProgressFill.Width = ProgressTrack.ActualWidth;
            _downloading = false;
            _updateOutcome = UpdateOutcome.Completed;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _downloading = false;
            // UAC 거부는 파일·네트워크 실패와 조치가 달라(권한 승인 후 재시도) 별도 결과로 올린다.
            _updateOutcome = ex is DevezCode.Services.UpdateElevationDeniedException
                ? UpdateOutcome.ElevationDenied
                : UpdateOutcome.Failed;
            DialogResult = false; // 창을 닫고 호출측이 수동 설치 안내를 하도록 한다.
        }
    }
    private void LinkText_Click(object sender, MouseButtonEventArgs e) { _linkClicked = true; DialogResult = true; }
}

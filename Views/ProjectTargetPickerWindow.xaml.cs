using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace DevezCode.Views;

/// <summary>분할 시 '다른 프로젝트' 클릭 → 어느 패널에 띄울지 고르는 오버레이.
/// 별도 최상위 창이라 터미널(WebView2 HWND) 위에 합성된다. MainWindow 가
/// 중앙 패널 영역에 정확히 겹치도록 위치·크기·컬럼 폭을 채운다.
/// WS_EX_NOACTIVATE 로 포커스를 가져가지 않아(메인 창이 계속 foreground) 닫힐 때
/// 다른 프로세스 창이 위로 튀어나오는 z-order 문제를 피한다. 취소(바깥 클릭·Esc)는
/// MainWindow 가 처리한다.</summary>
public partial class ProjectTargetPickerWindow : Window
{
    public event Action<string>? PaneSelected; // "A" or "B"

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public ProjectTargetPickerWindow()
    {
        InitializeComponent();
        ShowActivated = false; // 보일 때 포커스를 가져가지 않는다(메인 창 활성 유지).
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE); // 클릭해도 활성화되지 않게.
    }

    /// <summary>좌/우 프로젝트 이름과 컬럼 폭을 채운다. 폭은 실제 CenterSplit 의 컬럼 GridLength
    /// (PaneACol/PaneSplitterCol/PaneBCol)를 그대로 받아 복사한다. 창 전체 폭이 CenterSplit 과
    /// 같으므로 동일한 제약으로 풀려 좌/우가 패널과 픽셀 단위로 정확히 일치한다.</summary>
    public void Configure(string paneAName, string paneBName, string paneAPath, string paneBPath,
                          GridLength left, GridLength gap, GridLength right)
    {
        PaneAText.Text = paneAName;
        PaneBText.Text = paneBName;
        PaneAPath.Text = paneAPath;
        PaneBPath.Text = paneBPath;
        LeftCol.Width = left;
        GapCol.Width = gap;
        RightCol.Width = right;

        // 다크 테마는 검은 그림자, 밝은(minimal/soft) 테마는 흰 그림자로 글자 가독성을 높인다.
        var shadow = App.CurrentTheme == "dark" ? Colors.Black : Colors.White;
        ShadowA.Color = shadow;
        ShadowB.Color = shadow;
    }

    private void PaneA_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        PaneSelected?.Invoke("A");
        Close();
    }

    private void PaneB_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        PaneSelected?.Invoke("B");
        Close();
    }
}

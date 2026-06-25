using System;
using System.Windows;
using System.Windows.Input;
using DevezCode.Models;

namespace DevezCode.Views;

/// <summary>분할 시 '다른 프로젝트' 클릭 → 어느 패널에 띄울지 고르는 오버레이.
/// 별도 최상위 창이라 터미널(WebView2 HWND) 위에 합성된다. MainWindow 가
/// 중앙 패널 영역에 정확히 겹치도록 위치·크기·컬럼 폭을 채운다.</summary>
public partial class ProjectTargetPickerWindow : Window
{
    public event Action<string>? PaneSelected; // "A" or "B"

    public ProjectTargetPickerWindow()
    {
        InitializeComponent();
    }

    /// <summary>좌/우 프로젝트 이름과 컬럼 폭을 채운다. 폭은 실제 CenterSplit 의 컬럼 GridLength
    /// (PaneACol/PaneSplitterCol/PaneBCol)를 그대로 받아 복사한다. 창 전체 폭이 CenterSplit 과
    /// 같으므로 동일한 제약으로 풀려 좌/우가 패널과 픽셀 단위로 정확히 일치한다.</summary>
    public void Configure(string paneAName, string paneBName, GridLength left, GridLength gap, GridLength right)
    {
        PaneAText.Text = paneAName;
        PaneBText.Text = paneBName;
        LeftCol.Width = left;
        GapCol.Width = gap;
        RightCol.Width = right;
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

    // 바깥(메인 창 등) 클릭 → 포커스 잃음 = 취소.
    private void Window_Deactivated(object? sender, EventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}

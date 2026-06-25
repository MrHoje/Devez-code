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

    // 바깥(메인 창 등) 클릭 → 포커스 잃음 = 취소. 소유자(메인 창)를 먼저 최상단으로 올린 뒤 닫는다.
    // 안 그러면 이 창(작업표시줄 미표시 owned 창)이 닫힐 때 OS 가 z-order 상 아래의 '다른 프로세스' 창을
    // 최상단으로 올린다. 포커스를 잃는 순간엔 아직 우리 프로세스가 foreground 라 Activate 가 먹는다
    // (사용자가 진짜 다른 앱을 클릭한 경우엔 SetForegroundWindow 제한으로 무시되어 뺏지 않음).
    private void Window_Deactivated(object? sender, EventArgs e)
    {
        Owner?.Activate();
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}

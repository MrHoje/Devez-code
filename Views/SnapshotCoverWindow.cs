using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace DevezCode.Views;

/// <summary>터미널(WebView2 HWND) 위를 덮는 borderless 최상위 창. airspace 우회로
/// 패널 리사이즈 reveal 순간의 xterm reflow 를 가린다(ProjectTargetPickerWindow 와 동일 패턴).
/// WS_EX_NOACTIVATE 로 포커스를 안 가져가고, WS_EX_TRANSPARENT 로 클릭을 통과시킨다.
/// <see cref="Shown"/> 은 창 내용이 실제로 한 번 렌더(=화면에 올라옴)된 뒤 완료된다 —
/// 그 전에 아래 WebView 를 되살리면 커버가 못 가려 깜빡이므로, 호출부는 반드시 이 Task 를 기다린다.</summary>
public sealed class SnapshotCoverWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly TaskCompletionSource<bool> _shown =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>창 내용이 한 번 렌더된 뒤 완료(=실제로 위에 올라옴). 이걸 기다린 뒤 WebView 를 되살릴 것.</summary>
    public Task Shown => _shown.Task;

    private readonly Image _image;

    public SnapshotCoverWindow(ImageSource image)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;     // 보일 때 포커스를 가져가지 않는다.
        Background = Brushes.Black;
        _image = new Image { Source = image, Stretch = Stretch.Fill };
        Content = _image;
    }

    /// <summary>닫기 직전, 정착된 라이브 화면 비트맵으로 교체해 커버를 걷을 때 내용이 튀지 않게 한다.</summary>
    public void UpdateImage(ImageSource image) => _image.Source = image;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _shown.TrySetResult(true);
    }
}

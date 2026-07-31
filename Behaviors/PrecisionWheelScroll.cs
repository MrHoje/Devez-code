using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DevezCode.Behaviors;

/// <summary>
/// WPF 기본 ScrollViewer가 120 미만의 트랙패드 델타도 한 번의 휠 틱으로 확대하지 않도록
/// 작은 델타를 누적한다. 일반 마우스의 120 단위 입력은 기존 WPF 경로를 그대로 사용한다.
/// </summary>
public static class PrecisionWheelScroll
{
    private const int Detent = Mouse.MouseWheelDeltaForOneLine;
    private const long GestureGapMs = 180;
    private static readonly MouseWheelEventHandler PreviewMouseWheelHandler = OnPreviewMouseWheel;
    private static readonly ConditionalWeakTable<ScrollViewer, WheelState> States = new();
    private static int _isRegistered;

    private sealed class WheelState
    {
        public int Delta;
        public long LastAt;
    }

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(PrecisionWheelScroll),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    /// <summary>앱의 모든 WPF ScrollViewer에 정밀 휠 처리를 한 번만 등록한다.</summary>
    public static void RegisterGlobally()
    {
        if (Interlocked.Exchange(ref _isRegistered, 1) != 0) return;
        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            Mouse.PreviewMouseWheelEvent,
            PreviewMouseWheelHandler,
            handledEventsToo: true);
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;

        // Preview 터널에서 바깥 ScrollViewer가 중첩된 안쪽 스크롤을 먼저 가로채지 않게 한다.
        var nearest = FindScrollViewer(e.OriginalSource as DependencyObject);
        if (!ReferenceEquals(viewer, nearest))
        {
            States.Remove(viewer);
            return;
        }

        if (e.Handled || e.Delta == 0 || !GetIsEnabled(viewer)
            || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
        {
            States.Remove(viewer);
            return;
        }

        // 홈이 있는 일반 마우스는 경계 위치에서도 트랙패드 잔여값과 섞지 않고
        // 기존 WPF 스크롤과 설정값을 그대로 보존한다.
        if (e.Delta % Detent == 0)
        {
            States.Remove(viewer);
            return;
        }
        if (viewer.ScrollableHeight <= 0.5
            || (e.Delta > 0 && viewer.VerticalOffset <= 0.01)
            || (e.Delta < 0 && viewer.VerticalOffset >= viewer.ScrollableHeight - 0.01))
        {
            States.Remove(viewer);
            return;
        }

        var state = States.GetValue(viewer, static _ => new WheelState());
        var now = Environment.TickCount64;
        if (now - state.LastAt > GestureGapMs)
            state.Delta = 0;
        if (state.Delta != 0 && Math.Sign(state.Delta) != Math.Sign(e.Delta))
            state.Delta = 0;
        state.LastAt = now;
        state.Delta += e.Delta;

        while (state.Delta >= Detent)
        {
            ScrollOneDetent(viewer, up: true);
            state.Delta -= Detent;
        }
        while (state.Delta <= -Detent)
        {
            ScrollOneDetent(viewer, up: false);
            state.Delta += Detent;
        }

        e.Handled = true;
    }

    private static void ScrollOneDetent(ScrollViewer viewer, bool up)
    {
        var lines = SystemParameters.WheelScrollLines;
        if (lines < 0)
        {
            if (up) viewer.PageUp(); else viewer.PageDown();
            return;
        }

        for (var i = 0; i < lines; i++)
        {
            if (up) viewer.LineUp(); else viewer.LineDown();
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? source)
    {
        for (var current = source; current != null; current = GetParent(current))
        {
            if (current is ScrollViewer viewer) return viewer;
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject child)
    {
        if (child is ContentElement content)
        {
            var contentParent = ContentOperations.GetParent(content);
            if (contentParent != null) return contentParent;
            if (content is FrameworkContentElement frameworkContent)
                return frameworkContent.Parent;
        }

        try
        {
            var visualParent = VisualTreeHelper.GetParent(child);
            if (visualParent != null) return visualParent;
        }
        catch (InvalidOperationException)
        {
            // ContentElement 등 Visual이 아닌 입력 원본은 논리 트리로 올라간다.
        }

        return LogicalTreeHelper.GetParent(child);
    }
}

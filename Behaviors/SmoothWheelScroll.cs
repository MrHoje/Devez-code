using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DevezCode.Behaviors;

/// <summary>
/// 휠·트랙패드 세로 스크롤을 픽셀 단위 감속 애니메이션으로 움직인다(eGhisWorks SmoothWheelScroll 이식).
/// 휠 입력은 목표 위치에 쌓고 매 프레임 목표로 다가간다. 안쪽 스크롤이 끝에 닿으면 바깥 스크롤로 넘긴다.
/// 터미널(WebView2)은 WPF 휠 이벤트를 받지 않으므로 영향이 없다.
/// </summary>
public static class SmoothWheelScroll
{
    // 휠 한 칸 72px(시스템 기본 3줄 기준). 트랙패드는 같은 비율로 작은 델타가 여러 번 온다.
    private const double PixelsPerLine = 24.0;
    // 남은 거리가 이 시간 상수로 줄어든다(약 150ms에 95%).
    private const double TimeConstantSeconds = 0.05;
    private static readonly Dictionary<ScrollViewer, Animation> Active = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double _lastFrameSeconds;
    private static bool _registered;

    private sealed class Animation
    {
        public double Target;
        public double LastSet;
    }

    /// <summary>앱의 모든 ScrollViewer(목록·트리·텍스트 입력 포함)에 한 번만 등록한다.</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        // 가상화 목록의 기본 항목 단위 스크롤은 항목 높이만큼 계단식으로 움직이므로 픽셀 단위로 바꾼다.
        // (ItemsControl이 이미 메타데이터를 재정의해 OverrideMetadata는 쓸 수 없다. XAML에서 정한 값은 그대로 둔다.)
        EventManager.RegisterClassHandler(typeof(ItemsControl), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            var items = (ItemsControl)sender;
            if (DependencyPropertyHelper.GetValueSource(items, VirtualizingPanel.ScrollUnitProperty).BaseValueSource == BaseValueSource.Default)
                items.SetCurrentValue(VirtualizingPanel.ScrollUnitProperty, ScrollUnit.Pixel);
        }));
        EventManager.RegisterClassHandler(typeof(ScrollViewer), Mouse.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel), handledEventsToo: true);
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Ctrl(확대)·Shift(가로)는 기본 동작에 맡긴다.
        if (e.Handled || e.Delta == 0 || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0) return;

        // Preview 터널은 바깥부터 오므로, 이 방향으로 움직일 수 있는 가장 안쪽 ScrollViewer만 처리한다.
        var target = FindScrollTarget(e.OriginalSource as DependencyObject, e.Delta > 0);
        if (target == null || !ReferenceEquals(sender, target)) return;

        int lines = SystemParameters.WheelScrollLines;
        double pixels = lines < 0 ? target.ViewportHeight : lines * PixelsPerLine;
        double distance = e.Delta / (double)Mouse.MouseWheelDeltaForOneLine * pixels;

        if (!Active.TryGetValue(target, out var animation))
        {
            animation = new Animation { Target = target.VerticalOffset, LastSet = target.VerticalOffset };
            if (Active.Count == 0)
            {
                _lastFrameSeconds = Clock.Elapsed.TotalSeconds;
                CompositionTarget.Rendering += OnRendering;
            }
            Active[target] = animation;
        }
        animation.Target = Math.Max(0, Math.Min(target.ScrollableHeight, animation.Target - distance));
        e.Handled = true;
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        double now = Clock.Elapsed.TotalSeconds;
        double factor = 1 - Math.Exp(-(now - _lastFrameSeconds) / TimeConstantSeconds);
        _lastFrameSeconds = now;

        foreach (var (viewer, animation) in Active.ToList())
        {
            // 자동 하단 고정 등으로 다른 곳에서 옮겼으면 같은 만큼 목표도 옮겨 남은 이동을 이어 간다.
            double shift = viewer.VerticalOffset - animation.LastSet;
            double target = Math.Max(0, Math.Min(viewer.ScrollableHeight, animation.Target + shift));
            double next = viewer.VerticalOffset + (target - viewer.VerticalOffset) * factor;
            if (!viewer.IsLoaded || Math.Abs(target - next) < 0.5)
            {
                next = target;
                Active.Remove(viewer);
            }
            else
            {
                animation.Target = target;
            }
            animation.LastSet = next;
            viewer.ScrollToVerticalOffset(next);
        }

        if (Active.Count == 0) CompositionTarget.Rendering -= OnRendering;
    }

    private static ScrollViewer? FindScrollTarget(DependencyObject? source, bool up)
    {
        for (var current = source; current != null; current = GetParent(current))
        {
            if (current is not ScrollViewer viewer) continue;
            // 가로 전용 스트립(탭바 등)은 자기 휠 핸들러가 처리하므로 바깥으로 넘기지 않는다.
            if (viewer.ScrollableHeight <= 0.5)
            {
                if (viewer.ScrollableWidth > 0.5) return null;
                continue;
            }
            double offset = Active.TryGetValue(viewer, out var animation) ? animation.Target : viewer.VerticalOffset;
            if (up ? offset > 0.01 : offset < viewer.ScrollableHeight - 0.01) return viewer;
        }

        return null;
    }

    // 팝업(콤보 목록·메뉴) 안에서 끝에 닿아도 뒤 창이 스크롤되지 않도록 시각 트리 안에서만 올라간다.
    private static DependencyObject? GetParent(DependencyObject child)
    {
        if (child is ContentElement content)
            return ContentOperations.GetParent(content) ?? LogicalTreeHelper.GetParent(content);
        return child is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(child) : null;
    }
}

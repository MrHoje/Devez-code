using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace DevezCode.Views;

/// <summary>
/// 드래그 중 원본의 반투명 스냅샷("고스트")을 커서 옆에 띄운다 (devez DragHelper 이식, 핵심부만).
/// 어도너 레이어에 그려지므로 창 전역에서 보인다.
/// </summary>
internal static class DragHelper
{
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    private struct POINT { public int X, Y; }

    public interface IGhost : IDisposable
    {
        void MoveToMouse();
    }

    internal static Brush CaptureSnapshot(
        FrameworkElement source,
        FrameworkElement? backgroundTarget = null,
        Brush? background = null)
    {
        var w = Math.Max(1, source.ActualWidth);
        var h = Math.Max(1, source.ActualHeight);
        var dpi = VisualTreeHelper.GetDpi(source);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(w * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(h * dpi.DpiScaleY)),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        var vb = new VisualBrush(source) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (backgroundTarget != null && background != null)
            {
                try
                {
                    var origin = ReferenceEquals(source, backgroundTarget)
                        ? new Point()
                        : backgroundTarget.TransformToAncestor(source).Transform(new Point());
                    var radius = backgroundTarget is Border border
                        ? Math.Max(Math.Max(border.CornerRadius.TopLeft, border.CornerRadius.TopRight),
                            Math.Max(border.CornerRadius.BottomLeft, border.CornerRadius.BottomRight))
                        : 0;
                    dc.DrawRoundedRectangle(
                        background,
                        null,
                        new Rect(origin.X, origin.Y,
                            Math.Max(1, backgroundTarget.ActualWidth),
                            Math.Max(1, backgroundTarget.ActualHeight)),
                        radius,
                        radius);
                }
                catch (InvalidOperationException)
                {
                    // 캡처 직전 시각 트리에서 분리되면 기존 투명 스냅샷으로 폴백한다.
                }
            }
            dc.DrawRectangle(vb, null, new Rect(0, 0, w, h));
        }
        bitmap.Render(dv);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
        brush.Freeze();
        return brush;
    }

    internal static Brush CaptureSnapshot(UIElement source, Rect bounds)
    {
        var w = Math.Max(1, bounds.Width);
        var h = Math.Max(1, bounds.Height);
        var dpi = VisualTreeHelper.GetDpi(source);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(w * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(h * dpi.DpiScaleY)),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        var vb = new VisualBrush(source)
        {
            Viewbox = bounds,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, w, h),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill
        };
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(vb, null, new Rect(0, 0, w, h));
        bitmap.Render(dv);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap)
        {
            Stretch = Stretch.Fill,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top
        };
        brush.Freeze();
        return brush;
    }

    public sealed class ManualDragSession : IGhost
    {
        private readonly Window _window;
        private readonly AdornerLayer _layer;
        private readonly DragAdorner _adorner;
        private readonly List<(FrameworkElement Target, double Opacity, bool HadLocalOpacity, bool IsHitTestVisible)> _hideTargets;
        private readonly Point _grab;
        private readonly double _ghostH;
        private readonly double _ghostW;
        private readonly FrameworkElement? _clampHost; // 지정되면 고스트 X를 이 요소의 좌우 안쪽으로 제한(Y는 자유).
        private bool _disposed;

        internal ManualDragSession(Window window, AdornerLayer layer, UIElement root,
            FrameworkElement snapshotSource, FrameworkElement hideTarget,
            FrameworkElement? snapshotBackgroundTarget, Brush? snapshotBackground,
            FrameworkElement? clampHost)
        {
            _window = window;
            _layer = layer;
            _grab = Mouse.GetPosition(snapshotSource);
            _ghostH = snapshotSource.ActualHeight;
            _ghostW = snapshotSource.ActualWidth;
            _clampHost = clampHost;
            _adorner = new DragAdorner(
                root,
                snapshotSource,
                snapshotBackgroundTarget,
                snapshotBackground);
            _layer.Add(_adorner);
            _hideTargets = HideTargets(new[] { hideTarget });
            MoveToMouse();
        }

        internal ManualDragSession(Window window, AdornerLayer layer, UIElement root,
            Brush snapshot, Size size, Point grab, IReadOnlyList<FrameworkElement> hideTargets,
            FrameworkElement? clampHost)
        {
            _window = window;
            _layer = layer;
            _grab = grab;
            _ghostH = size.Height;
            _ghostW = size.Width;
            _clampHost = clampHost;
            _adorner = new DragAdorner(root, snapshot, size);
            _layer.Add(_adorner);
            _hideTargets = HideTargets(hideTargets);
            MoveToMouse();
        }

        private static List<(FrameworkElement, double, bool, bool)> HideTargets(
            IEnumerable<FrameworkElement> targets)
        {
            var states = new List<(FrameworkElement, double, bool, bool)>();
            foreach (var target in targets.Distinct())
            {
                states.Add((
                    target,
                    target.Opacity,
                    target.ReadLocalValue(UIElement.OpacityProperty) != DependencyProperty.UnsetValue,
                    target.IsHitTestVisible));
                target.Opacity = 0;
                target.IsHitTestVisible = false;
            }
            return states;
        }

        public void MoveToMouse()
        {
            if (_disposed) return;
            if (!GetCursorPos(out var p)) return;
            var windowPt = _window.PointFromScreen(new Point(p.X, p.Y));
            var x = windowPt.X - _grab.X;
            var y = windowPt.Y - _grab.Y;
            if (_ghostH > 0) y = Math.Min(y, _window.ActualHeight - _ghostH);
            if (TryGetClampRect(out var clamp))
            {
                double maxX = Math.Max(clamp.Left, clamp.Right - _ghostW);
                x = Math.Clamp(x, clamp.Left, maxX);
            }
            _adorner.SetPosition(x, y);
        }

        private bool TryGetClampRect(out Rect rect)
        {
            rect = Rect.Empty;
            if (_clampHost == null || _clampHost.ActualWidth <= 0) return false;
            try
            {
                var origin = _clampHost.TransformToAncestor(_window).Transform(new Point());
                rect = new Rect(origin.X, origin.Y, _clampHost.ActualWidth, _clampHost.ActualHeight);
                return true;
            }
            catch { return false; } // 트리에서 분리된 호스트는 클램프 없이 진행
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var (target, opacity, hadLocalOpacity, isHitTestVisible) in _hideTargets)
            {
                if (hadLocalOpacity) target.Opacity = opacity;
                else target.ClearValue(UIElement.OpacityProperty);
                target.IsHitTestVisible = isHitTestVisible;
            }
            try { _layer.Remove(_adorner); } catch { /* 이미 제거됨 */ }
        }
    }

    public static ManualDragSession? BeginManualDrag(
        FrameworkElement source,
        FrameworkElement? hideTarget = null,
        FrameworkElement? snapshotBackgroundTarget = null,
        Brush? snapshotBackground = null,
        FrameworkElement? clampHost = null)
    {
        var window = Window.GetWindow(source);
        if (window == null) return null;

        AdornerLayer? layer = null;
        if (window.Content is AdornerDecorator ad) layer = ad.AdornerLayer;
        else if (window.Content is UIElement wc) layer = AdornerLayer.GetAdornerLayer(wc);
        layer ??= FindOutermostAdornerLayer(source);

        var root = window.Content as UIElement ?? source;
        if (layer == null) return null;

        return new ManualDragSession(
            window,
            layer,
            root,
            source,
            hideTarget ?? source,
            snapshotBackgroundTarget,
            snapshotBackground,
            clampHost);
    }

    public static ManualDragSession? BeginManualDrag(
        UIElement snapshotHost,
        Rect snapshotBounds,
        IReadOnlyList<FrameworkElement> hideTargets,
        Point grab,
        FrameworkElement? clampHost = null)
    {
        var window = Window.GetWindow(snapshotHost);
        if (window == null || snapshotBounds.IsEmpty) return null;

        AdornerLayer? layer = null;
        if (window.Content is AdornerDecorator ad) layer = ad.AdornerLayer;
        else if (window.Content is UIElement wc) layer = AdornerLayer.GetAdornerLayer(wc);
        layer ??= FindOutermostAdornerLayer(snapshotHost);

        var root = window.Content as UIElement ?? snapshotHost;
        if (layer == null) return null;

        var size = new Size(Math.Max(1, snapshotBounds.Width), Math.Max(1, snapshotBounds.Height));
        return new ManualDragSession(
            window,
            layer,
            root,
            CaptureSnapshot(snapshotHost, snapshotBounds),
            size,
            grab,
            hideTargets,
            clampHost);
    }

    private static AdornerLayer? FindOutermostAdornerLayer(DependencyObject start)
    {
        AdornerLayer? outermost = null;
        var node = start;
        while (node != null)
        {
            if (node is Visual v)
            {
                var layer = AdornerLayer.GetAdornerLayer(v);
                if (layer != null) outermost = layer;
            }
            node = VisualTreeHelper.GetParent(node);
        }
        return outermost;
    }

    private sealed class DragAdorner : Adorner
    {
        private const double Cushion = 40;
        private readonly Rectangle _ghost;
        private double _x, _y;

        public DragAdorner(
            UIElement root,
            FrameworkElement source,
            FrameworkElement? snapshotBackgroundTarget,
            Brush? snapshotBackground)
            : this(
                root,
                CaptureSnapshot(source, snapshotBackgroundTarget, snapshotBackground),
                new Size(Math.Max(1, source.ActualWidth), Math.Max(1, source.ActualHeight)))
        {
        }

        public DragAdorner(UIElement root, Brush snapshot, Size size) : base(root)
        {
            IsHitTestVisible = false;
            _ghost = new Rectangle
            {
                Width = Math.Max(1, size.Width),
                Height = Math.Max(1, size.Height),
                IsHitTestVisible = false,
                Opacity = 0.78,
                Fill = snapshot,
                Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.25, BlurRadius = 12, ShadowDepth = 3, Direction = 270 },
                RenderTransform = new TranslateTransform()
            };
            AddVisualChild(_ghost);
        }

        public void SetPosition(double x, double y)
        {
            _x = x - Cushion; _y = y - Cushion;
            if (Parent is AdornerLayer layer) layer.Update(AdornedElement);
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _ghost;

        protected override Size MeasureOverride(Size constraint)
        {
            _ghost.Measure(constraint);
            var d = _ghost.DesiredSize;
            return new Size(d.Width + Cushion * 2, d.Height + Cushion * 2);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _ghost.Arrange(new Rect(Cushion, Cushion, _ghost.DesiredSize.Width, _ghost.DesiredSize.Height));
            return finalSize;
        }

        public override GeneralTransform GetDesiredTransform(GeneralTransform transform)
        {
            var group = new GeneralTransformGroup();
            if (transform != null) group.Children.Add(transform);
            group.Children.Add(new TranslateTransform(_x, _y));
            return group;
        }
    }
}

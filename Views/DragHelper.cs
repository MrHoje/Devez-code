using System.Runtime.InteropServices;
using System.Windows;
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

    internal static Brush CaptureSnapshot(FrameworkElement source)
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
            dc.DrawRectangle(vb, null, new Rect(0, 0, w, h));
        bitmap.Render(dv);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
        brush.Freeze();
        return brush;
    }

    public sealed class ManualDragSession : IGhost
    {
        private readonly Window _window;
        private readonly AdornerLayer _layer;
        private readonly DragAdorner _adorner;
        private readonly FrameworkElement _hideTarget;
        private readonly double _originalOpacity;
        private readonly bool _hadLocalOpacity;
        private readonly bool _originalHitTest;
        private readonly Point _grab;
        private readonly double _ghostH;
        private bool _disposed;

        internal ManualDragSession(Window window, AdornerLayer layer, UIElement root,
            FrameworkElement snapshotSource, FrameworkElement hideTarget)
        {
            _window = window;
            _layer = layer;
            _hideTarget = hideTarget;
            _grab = Mouse.GetPosition(snapshotSource);
            _ghostH = snapshotSource.ActualHeight;
            _hadLocalOpacity = hideTarget.ReadLocalValue(UIElement.OpacityProperty) != DependencyProperty.UnsetValue;
            _originalOpacity = hideTarget.Opacity;
            _originalHitTest = hideTarget.IsHitTestVisible;

            _adorner = new DragAdorner(root, snapshotSource);
            _layer.Add(_adorner);
            hideTarget.Opacity = 0;
            hideTarget.IsHitTestVisible = false;
            MoveToMouse();
        }

        public void MoveToMouse()
        {
            if (_disposed) return;
            if (!GetCursorPos(out var p)) return;
            var windowPt = _window.PointFromScreen(new Point(p.X, p.Y));
            var x = windowPt.X - _grab.X;
            var y = windowPt.Y - _grab.Y;
            if (_ghostH > 0) y = Math.Min(y, _window.ActualHeight - _ghostH);
            _adorner.SetPosition(x, y);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_hadLocalOpacity) _hideTarget.Opacity = _originalOpacity;
            else _hideTarget.ClearValue(UIElement.OpacityProperty);
            _hideTarget.IsHitTestVisible = _originalHitTest;
            try { _layer.Remove(_adorner); } catch { /* 이미 제거됨 */ }
        }
    }

    public static ManualDragSession? BeginManualDrag(FrameworkElement source, FrameworkElement? hideTarget = null)
    {
        var window = Window.GetWindow(source);
        if (window == null) return null;

        AdornerLayer? layer = null;
        if (window.Content is AdornerDecorator ad) layer = ad.AdornerLayer;
        else if (window.Content is UIElement wc) layer = AdornerLayer.GetAdornerLayer(wc);
        layer ??= FindOutermostAdornerLayer(source);

        var root = window.Content as UIElement ?? source;
        if (layer == null) return null;

        return new ManualDragSession(window, layer, root, source, hideTarget ?? source);
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

        public DragAdorner(UIElement root, FrameworkElement source) : base(root)
        {
            IsHitTestVisible = false;
            var w = Math.Max(1, source.ActualWidth);
            var h = Math.Max(1, source.ActualHeight);
            _ghost = new Rectangle
            {
                Width = w, Height = h,
                IsHitTestVisible = false,
                Opacity = 0.78,
                Fill = CaptureSnapshot(source),
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

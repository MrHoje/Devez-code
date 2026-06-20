using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DevezCode.Views;

/// <summary>드롭 대상 위/아래 가장자리에 삽입 위치 가로선을 그린다 (devez DragHelper.DropIndicator 이식).</summary>
internal static class DropIndicator
{
    private static DropLineAdorner? _current;
    private static AdornerLayer? _layer;

    public static void Show(UIElement target, bool atTop)
    {
        var layer = AdornerLayer.GetAdornerLayer(target);
        if (layer == null) return;

        if (_current != null && _layer == layer && ReferenceEquals(_current.AdornedElement, target))
        {
            _current.AtTop = atTop;
            _current.InvalidateVisual();
            return;
        }
        Clear();
        _current = new DropLineAdorner(target, atTop);
        _layer = layer;
        layer.Add(_current);
    }

    public static void Clear()
    {
        if (_current != null && _layer != null)
        {
            try { _layer.Remove(_current); } catch { /* 이미 제거됨 */ }
        }
        _current = null;
        _layer = null;
    }

    private sealed class DropLineAdorner : Adorner
    {
        public bool AtTop { get; set; }

        public DropLineAdorner(UIElement element, bool atTop) : base(element)
        {
            AtTop = atTop;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var size = AdornedElement.RenderSize;
            double y = AtTop ? 0 : size.Height;

            var brush = Application.Current.TryFindResource("PrimaryBrush") as Brush ?? Brushes.DodgerBlue;
            var pen = new Pen(brush, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

            dc.DrawLine(pen, new Point(2, y), new Point(size.Width - 2, y));
            dc.DrawEllipse(brush, null, new Point(2, y), 3, 3);
            dc.DrawEllipse(brush, null, new Point(size.Width - 2, y), 3, 3);
        }
    }
}

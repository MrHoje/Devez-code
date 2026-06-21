using System.Windows;
using System.Windows.Media;

namespace DevezCode.Behaviors;

/// <summary>FrameworkElement 를 자신의 크기에 맞는 둥근 사각형으로 클립한다.
/// Border 의 CornerRadius + DropShadowEffect(글로우) 조합에서, 배경의 사각 모서리(라운드 밖
/// 영역)가 클리핑되지 않아 글로우 위로 노출되던 문제를 막는다. SizeChanged 마다 갱신.</summary>
public static class CornerRadiusClip
{
    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.RegisterAttached(
            "Radius", typeof(double), typeof(CornerRadiusClip),
            new PropertyMetadata(0.0, OnChanged));

    public static double GetRadius(DependencyObject o) => (double)o.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject o, double v) => o.SetValue(RadiusProperty, v);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.SizeChanged -= OnSize;
        fe.SizeChanged += OnSize;
        Apply(fe);
    }

    private static void OnSize(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement fe)
    {
        double w = fe.ActualWidth, h = fe.ActualHeight;
        if (w <= 0 || h <= 0) { fe.Clip = null; return; }
        var r = GetRadius(fe);
        fe.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
    }
}

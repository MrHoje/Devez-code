using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DevezCode.Views;

/// <summary>TextBlock 이 실제로 …(ellipsis)로 잘렸을 때만 ToolTip 을 띄운다.
/// <para>사용법: TextTrimming 이 있는 TextBlock 에 <c>v:RowToolTip.ShowWhenTrimmed="True"</c> 를 붙이고
/// ToolTip 은 평소처럼 지정한다. 텍스트가 안 잘렸으면 ToolTipOpening 을 취소한다.</para></summary>
public static class RowToolTip
{
    public static readonly DependencyProperty ShowWhenTrimmedProperty =
        DependencyProperty.RegisterAttached("ShowWhenTrimmed", typeof(bool), typeof(RowToolTip),
            new PropertyMetadata(false, OnChanged));

    public static void SetShowWhenTrimmed(DependencyObject o, bool v) => o.SetValue(ShowWhenTrimmedProperty, v);
    public static bool GetShowWhenTrimmed(DependencyObject o) => (bool)o.GetValue(ShowWhenTrimmedProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.ToolTipOpening -= OnOpening;
        if ((bool)e.NewValue) tb.ToolTipOpening += OnOpening;
    }

    private static void OnOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is TextBlock tb && !IsTrimmed(tb)) e.Handled = true;   // 안 잘렸으면 툴팁 표시 취소
    }

    private static bool IsTrimmed(TextBlock tb)
    {
        if (tb.TextTrimming == TextTrimming.None || string.IsNullOrEmpty(tb.Text) || tb.ActualWidth <= 0)
            return false;
        var typeface = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
        double pixelsPerDip = VisualTreeHelper.GetDpi(tb).PixelsPerDip;
        var ft = new FormattedText(tb.Text, CultureInfo.CurrentCulture, tb.FlowDirection,
            typeface, tb.FontSize, Brushes.Black, pixelsPerDip);
        return ft.Width > tb.ActualWidth + 0.5;   // 실제 글자 너비가 표시 폭보다 크면 잘린 것
    }
}

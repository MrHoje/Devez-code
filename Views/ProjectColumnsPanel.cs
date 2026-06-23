using System.Windows;
using System.Windows.Controls;
using DevezCode.Models;

namespace DevezCode.Views;

/// <summary>
/// 프로젝트 카드 배치 패널. Columns=1 이면 일반 세로 스택, Columns=2 이면 두 개의 독립 컬럼으로
/// 카드를 위에서부터 쌓는다(masonry 아님 — 각 카드는 자신이 가진 ProjectItem.Column 으로만 좌/우 결정).
/// → 1↔2열 토글 시 카드가 자동 재배치되지 않고, 드래그로만 컬럼이 바뀐다.
/// 각 컬럼은 1열처럼 콘텐츠(세션 수) 높이대로 위로 붙어 쌓이며, 행 높이에 묶이지 않는다.
/// </summary>
public sealed class ProjectColumnsPanel : Panel
{
    /// <summary>컬럼 사이 가로 간격(px).</summary>
    private const double Gap = 6;

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(ProjectColumnsPanel),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    private bool TwoCol => Columns >= 2;

    private static int ColumnOf(UIElement child)
        => (child as FrameworkElement)?.DataContext is ProjectItem p && p.Column == 1 ? 1 : 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        double availW = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;

        if (!TwoCol)
        {
            double h = 0;
            foreach (UIElement c in InternalChildren)
            {
                c.Measure(new Size(availableSize.Width, double.PositiveInfinity));
                h += c.DesiredSize.Height;
            }
            return new Size(availW, h);
        }

        double colW = System.Math.Max(0, (availW - Gap) / 2);
        double h0 = 0, h1 = 0;
        foreach (UIElement c in InternalChildren)
        {
            c.Measure(new Size(colW, double.PositiveInfinity));
            if (ColumnOf(c) == 0) h0 += c.DesiredSize.Height; else h1 += c.DesiredSize.Height;
        }
        return new Size(availW, System.Math.Max(h0, h1));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!TwoCol)
        {
            double y = 0;
            foreach (UIElement c in InternalChildren)
            {
                c.Arrange(new Rect(0, y, finalSize.Width, c.DesiredSize.Height));
                y += c.DesiredSize.Height;
            }
            return finalSize;
        }

        double colW = System.Math.Max(0, (finalSize.Width - Gap) / 2);
        double y0 = 0, y1 = 0;
        foreach (UIElement c in InternalChildren)
        {
            if (ColumnOf(c) == 0)
            {
                c.Arrange(new Rect(0, y0, colW, c.DesiredSize.Height));
                y0 += c.DesiredSize.Height;
            }
            else
            {
                c.Arrange(new Rect(colW + Gap, y1, colW, c.DesiredSize.Height));
                y1 += c.DesiredSize.Height;
            }
        }
        return finalSize;
    }
}

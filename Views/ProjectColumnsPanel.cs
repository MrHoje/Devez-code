using System.Windows;
using System.Windows.Controls;
using DevezCode.Models;

namespace DevezCode.Views;

/// <summary>
/// 프로젝트 루트 배치 패널. 프로젝트는 1/2열의 지정 컬럼에 쌓고,
/// 폴더는 두 컬럼을 모두 차지하는 전체폭 구분 행으로 배치한다.
/// </summary>
public sealed class ProjectColumnsPanel : Panel
{
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

    // 전체폭(좌우 두 컬럼 차지) 여부: 2열 설정된 폴더만 전체폭 구분 행. 1열 폴더는 좌측 반폭 카드.
    private static bool IsFullWidthFolder(UIElement child) =>
        (child as FrameworkElement)?.DataContext is ProjectFolderItem { EffectiveColumns: >= 2 };

    // 반폭 카드가 놓일 컬럼(0=좌, 1=우). 우열 프로젝트만 1, 1열 폴더 포함 나머지는 좌열.
    private static int ColumnOf(UIElement child) =>
        (child as FrameworkElement)?.DataContext is ProjectItem { Column: 1 } ? 1 : 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        double availableWidth = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        if (!TwoCol)
        {
            double height = 0;
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
                height += child.DesiredSize.Height;
            }
            return new Size(availableWidth, height);
        }

        double columnWidth = Math.Max(0, (availableWidth - Gap) / 2);
        double leftHeight = 0;
        double rightHeight = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (IsFullWidthFolder(child))
            {
                child.Measure(new Size(availableWidth, double.PositiveInfinity));
                double next = Math.Max(leftHeight, rightHeight) + child.DesiredSize.Height;
                leftHeight = rightHeight = next;
                continue;
            }

            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            if (ColumnOf(child) == 0) leftHeight += child.DesiredSize.Height;
            else rightHeight += child.DesiredSize.Height;
        }
        return new Size(availableWidth, Math.Max(leftHeight, rightHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!TwoCol)
        {
            double y = 0;
            foreach (UIElement child in InternalChildren)
            {
                child.Arrange(new Rect(0, y, finalSize.Width, child.DesiredSize.Height));
                y += child.DesiredSize.Height;
            }
            return finalSize;
        }

        double columnWidth = Math.Max(0, (finalSize.Width - Gap) / 2);
        double leftY = 0;
        double rightY = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (IsFullWidthFolder(child))
            {
                double y = Math.Max(leftY, rightY);
                child.Arrange(new Rect(0, y, finalSize.Width, child.DesiredSize.Height));
                leftY = rightY = y + child.DesiredSize.Height;
                continue;
            }

            if (ColumnOf(child) == 0)
            {
                child.Arrange(new Rect(0, leftY, columnWidth, child.DesiredSize.Height));
                leftY += child.DesiredSize.Height;
            }
            else
            {
                child.Arrange(new Rect(columnWidth + Gap, rightY, columnWidth, child.DesiredSize.Height));
                rightY += child.DesiredSize.Height;
            }
        }
        return finalSize;
    }
}

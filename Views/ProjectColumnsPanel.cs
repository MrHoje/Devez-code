using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DevezCode.Models;

namespace DevezCode.Views;

/// <summary>
/// 프로젝트 루트 배치 패널. 프로젝트는 1/2열의 지정 컬럼에 쌓는다.
/// 폴더는 2열(EffectiveColumns>=2)이면 전체폭 구분 행, 1열이면 좌/우 반폭 카드로 배치한다.
/// 열 토글 시 AnimateNextLayout 예약으로 이전→현재 위치 슬라이드(FLIP) 애니메이션.
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

    // 반폭 카드가 놓일 컬럼(0=좌, 1=우). 우열 프로젝트/1열 폴더는 1, 나머지는 좌열.
    private static int ColumnOf(UIElement child) =>
        (child as FrameworkElement)?.DataContext switch
        {
            ProjectItem { Column: 1 } => 1,
            ProjectFolderItem { EffectiveColumns: 1, Column: 1 } => 1,
            _ => 0,
        };

    // FLIP 애니메이션: 직전 배치 위치 기억 → 다음 배치 때 이전→현재 위치로 슬라이드.
    private Dictionary<UIElement, Rect> _prevRects = new();
    private bool _animateArrange;

    /// <summary>다음 Arrange 를 이전 위치에서 슬라이드하는 애니메이션으로 수행하도록 예약.</summary>
    public void AnimateNextLayout() => _animateArrange = true;

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
        bool animate = _animateArrange;
        _animateArrange = false;
        var newRects = new Dictionary<UIElement, Rect>();

        if (!TwoCol)
        {
            double y = 0;
            foreach (UIElement child in InternalChildren)
            {
                PlaceChild(child, new Rect(0, y, finalSize.Width, child.DesiredSize.Height), animate, newRects);
                y += child.DesiredSize.Height;
            }
            _prevRects = newRects;
            return finalSize;
        }

        double columnWidth = Math.Max(0, (finalSize.Width - Gap) / 2);
        double leftY = 0;
        double rightY = 0;
        foreach (UIElement child in InternalChildren)
        {
            Rect rect;
            if (IsFullWidthFolder(child))
            {
                double y = Math.Max(leftY, rightY);
                rect = new Rect(0, y, finalSize.Width, child.DesiredSize.Height);
                leftY = rightY = y + child.DesiredSize.Height;
            }
            else if (ColumnOf(child) == 0)
            {
                rect = new Rect(0, leftY, columnWidth, child.DesiredSize.Height);
                leftY += child.DesiredSize.Height;
            }
            else
            {
                rect = new Rect(columnWidth + Gap, rightY, columnWidth, child.DesiredSize.Height);
                rightY += child.DesiredSize.Height;
            }
            PlaceChild(child, rect, animate, newRects);
        }
        _prevRects = newRects;
        return finalSize;
    }

    // 자식을 최종 위치에 배치하고, 애니메이션 예약 시 이전 위치·크기에서 슬라이드(FLIP)한다.
    // 위치(dx,dy)뿐 아니라 폭 변화(scaleX)도 애니메이션 — 내용 없는 폴더의 반폭↔전체폭 전환도 부드럽게.
    private void PlaceChild(UIElement child, Rect rect, bool animate, Dictionary<UIElement, Rect> store)
    {
        child.Arrange(rect);
        store[child] = rect;
        if (!animate || !_prevRects.TryGetValue(child, out var old)) return;

        double dx = old.X - rect.X;
        double dy = old.Y - rect.Y;
        double sx = rect.Width > 0.5 && old.Width > 0.5 ? old.Width / rect.Width : 1;
        bool moved = Math.Abs(dx) >= 0.5 || Math.Abs(dy) >= 0.5;
        bool scaled = Math.Abs(sx - 1) >= 0.01;
        if (!moved && !scaled) return;

        var (tt, st) = EnsureFlipTransforms(child);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(200);
        tt.BeginAnimation(TranslateTransform.XProperty, moved ? new DoubleAnimation(dx, 0, dur) { EasingFunction = ease } : null);
        tt.BeginAnimation(TranslateTransform.YProperty, moved ? new DoubleAnimation(dy, 0, dur) { EasingFunction = ease } : null);
        st.BeginAnimation(ScaleTransform.ScaleXProperty, scaled ? new DoubleAnimation(sx, 1, dur) { EasingFunction = ease } : null);
    }

    // 좌상단(0,0) 기준 Scale + Translate 그룹 확보. ReorderDrag 는 그룹 안 TranslateTransform 을 재사용하므로 호환.
    private static (TranslateTransform, ScaleTransform) EnsureFlipTransforms(UIElement child)
    {
        if (child.RenderTransform is TransformGroup g)
        {
            var t = g.Children.OfType<TranslateTransform>().FirstOrDefault();
            var s = g.Children.OfType<ScaleTransform>().FirstOrDefault();
            if (t != null && s != null) return (t, s);
        }
        var scale = new ScaleTransform();
        var translate = new TranslateTransform();
        child.RenderTransform = new TransformGroup { Children = { scale, translate } };
        return (translate, scale);
    }
}

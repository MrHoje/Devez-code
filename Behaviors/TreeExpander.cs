using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DevezCode.Behaviors;

/// <summary>
/// 세션 트리 컨테이너(Border)의 펼침/접힘을 devez `AnimateFolderTree`와 동일하게
/// MaxHeight + Opacity 애니메이션으로 처리하는 Attached Property.
/// DataTemplate 안에서 IsExpanded 에 바인딩한다. 컨테이너 기본 Visibility 는 Collapsed 여야 한다
/// (DP 기본값이 false 라 접힌 항목은 콜백이 안 불려 그대로 Collapsed 로 남는다).
/// </summary>
public static class TreeExpander
{
    private static readonly Duration Dur = new(TimeSpan.FromMilliseconds(220));
    private static CubicEase Ease => new() { EasingMode = EasingMode.EaseInOut };

    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.RegisterAttached(
            "IsExpanded", typeof(bool), typeof(TreeExpander),
            new PropertyMetadata(false, OnChanged));

    public static bool GetIsExpanded(DependencyObject o) => (bool)o.GetValue(IsExpandedProperty);
    public static void SetIsExpanded(DependencyObject o, bool v) => o.SetValue(IsExpandedProperty, v);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border tree) return;
        bool expand = (bool)e.NewValue;

        // 컨테이너가 아직 비주얼 트리에 붙기 전(초기 템플릿 적용)이면 애니메이션 없이 상태만 맞춘다.
        if (!tree.IsLoaded) { SetImmediate(tree, expand); return; }
        Animate(tree, expand);
    }

    private static void SetImmediate(Border tree, bool expand)
    {
        tree.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
        tree.BeginAnimation(UIElement.OpacityProperty, null);
        tree.MaxHeight = double.PositiveInfinity;
        tree.Opacity = 1;
        tree.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void Animate(Border tree, bool expand)
    {
        tree.LayoutTransform = Transform.Identity;

        if (expand)
        {
            tree.MaxHeight = 0;
            tree.Opacity = 0;
            tree.Visibility = Visibility.Visible;

            var h = new DoubleAnimation(0, 2000, Dur) { EasingFunction = Ease };
            h.Completed += (_, _) =>
            {
                tree.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
                tree.MaxHeight = double.PositiveInfinity;
            };
            tree.BeginAnimation(FrameworkElement.MaxHeightProperty, h);
            tree.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, Dur) { EasingFunction = Ease });
        }
        else
        {
            var from = tree.ActualHeight;
            var h = new DoubleAnimation(from, 0, Dur) { EasingFunction = Ease };
            h.Completed += (_, _) =>
            {
                tree.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
                tree.BeginAnimation(UIElement.OpacityProperty, null);
                tree.MaxHeight = double.PositiveInfinity;
                tree.Opacity = 1;
                tree.Visibility = Visibility.Collapsed;
            };
            tree.BeginAnimation(FrameworkElement.MaxHeightProperty, h);
            tree.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1, 0, Dur) { EasingFunction = Ease });
        }
    }
}

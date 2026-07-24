using System.Windows;
using System.Windows.Media.Animation;

namespace DevezCode.Behaviors;

/// <summary>RowDefinition.Height(GridLength) 픽셀 보간 애니메이션 — WPF 기본 미제공이라 직접 구현.
/// Pixel 단위만 지원(Star/Auto 보간 불가). 하단 터미널 패널 슬라이드에 사용.</summary>
public sealed class GridLengthAnimation : AnimationTimeline
{
    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(
        nameof(From), typeof(GridLength), typeof(GridLengthAnimation));
    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(
        nameof(To), typeof(GridLength), typeof(GridLengthAnimation));

    public GridLength From { get => (GridLength)GetValue(FromProperty); set => SetValue(FromProperty, value); }
    public GridLength To { get => (GridLength)GetValue(ToProperty); set => SetValue(ToProperty, value); }
    public IEasingFunction? EasingFunction { get; set; }

    public override Type TargetPropertyType => typeof(GridLength);
    protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue,
                                           AnimationClock clock)
    {
        double from = From.Value, to = To.Value;
        double p = clock.CurrentProgress ?? 0;
        if (EasingFunction != null) p = EasingFunction.Ease(p);
        return new GridLength(from + (to - from) * p);
    }
}

using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Animation;

namespace DevezCode.Behaviors;

/// <summary>
/// 완료·입력대기 알림 펄스(탭 / 사이드바 세션 행)를 <b>공용 위상 시계</b>에 맞춰 재생하는 Attached Property.
///
/// 기존에는 DataTemplate 안에서 DataTrigger.EnterActions 로 각자 BeginStoryboard 했다. 주기는 같아도
/// 시작 시각이 세션마다 달라(완료 시점·컨테이너 재생성·연속 알림 재트리거) 여러 행이 동시에 보이면
/// 제각각 깜빡이는 것처럼 보였다. 여기서는 앱 시작부터 흐르는 하나의 Stopwatch 로 현재 사이클
/// 오프셋을 구해 Storyboard.Seek 로 스냅한다 → 언제 시작해도 모든 펄스가 같은 박자·같은 위상.
///
/// 사용: 펄스용 Border 에 <c>b:AttentionPulse.PeakOpacity</c>(선택) 와
/// <c>b:AttentionPulse.IsPulsing="{Binding IsCompletionPulsing}"</c> 를 건다.
/// 대상 요소의 Opacity 로컬값은 0 이어야 한다(정지 시 Storyboard.Remove 로 그 값으로 복귀).
/// </summary>
public static class AttentionPulse
{
    /// <summary>0 → peak 편도 시간. AutoReverse 라 왕복 한 사이클은 이 값의 2배.</summary>
    private static readonly TimeSpan HalfCycle = TimeSpan.FromSeconds(0.8);

    /// <summary>모든 펄스가 공유하는 위상 기준. 앱 시작부터 계속 흐르지만 시계일 뿐이라 렌더 비용은 없다
    /// (실제 애니메이션은 펄스 중인 요소에만 붙고, 꺼지면 Stop/Remove 로 완전히 사라진다).</summary>
    private static readonly Stopwatch PhaseClock = Stopwatch.StartNew();

    public static readonly DependencyProperty IsPulsingProperty =
        DependencyProperty.RegisterAttached(
            "IsPulsing", typeof(bool), typeof(AttentionPulse),
            new PropertyMetadata(false, OnIsPulsingChanged));

    public static bool GetIsPulsing(DependencyObject o) => (bool)o.GetValue(IsPulsingProperty);
    public static void SetIsPulsing(DependencyObject o, bool v) => o.SetValue(IsPulsingProperty, v);

    /// <summary>펄스 최대 불투명도(표면별로 다름: 탭 0.12, 사이드바 행 0.10).</summary>
    public static readonly DependencyProperty PeakOpacityProperty =
        DependencyProperty.RegisterAttached(
            "PeakOpacity", typeof(double), typeof(AttentionPulse),
            new PropertyMetadata(0.12));

    public static double GetPeakOpacity(DependencyObject o) => (double)o.GetValue(PeakOpacityProperty);
    public static void SetPeakOpacity(DependencyObject o, double v) => o.SetValue(PeakOpacityProperty, v);

    /// <summary>이 요소에서 돌고 있는 스토리보드(정지·제거용 핸들).</summary>
    private static readonly DependencyProperty RunningStoryboardProperty =
        DependencyProperty.RegisterAttached(
            "RunningStoryboard", typeof(Storyboard), typeof(AttentionPulse), new PropertyMetadata(null));

    private static void OnIsPulsingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;

        Stop(el);
        if (e.NewValue is not true) return;

        // 컨테이너가 아직 트리에 붙기 전이면 Storyboard.Begin(el, true) 의 이름 스코프가 없을 수 있다.
        // Loaded 이후에 시작하되, 그때도 공용 시계로 위상을 맞추므로 어긋나지 않는다.
        if (!el.IsLoaded)
        {
            void OnLoaded(object s, RoutedEventArgs _)
            {
                el.Loaded -= OnLoaded;
                if (GetIsPulsing(el)) Start(el);
            }
            el.Loaded += OnLoaded;
            return;
        }

        Start(el);
    }

    private static void Start(FrameworkElement el)
    {
        Stop(el);

        var anim = new DoubleAnimation
        {
            From = 0,
            To = GetPeakOpacity(el),
            Duration = new Duration(HalfCycle),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            FillBehavior = FillBehavior.Stop,
        };
        Storyboard.SetTarget(anim, el);
        Storyboard.SetTargetProperty(anim, new PropertyPath(UIElement.OpacityProperty));

        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin(el, isControllable: true);
        // 공용 시계의 현재 사이클 오프셋으로 스냅 → 시작 시점과 무관하게 모든 펄스가 같은 위상.
        sb.Seek(el, CurrentPhaseOffset(), TimeSeekOrigin.BeginTime);

        el.SetValue(RunningStoryboardProperty, sb);
    }

    private static void Stop(FrameworkElement el)
    {
        if (el.GetValue(RunningStoryboardProperty) is not Storyboard sb) return;
        el.SetValue(RunningStoryboardProperty, null);
        sb.Stop(el);
        sb.Remove(el); // 애니메이션 값 해제 → XAML 로컬값(Opacity=0)으로 복귀
    }

    /// <summary>현재 왕복 사이클(HalfCycle×2) 안에서의 경과 오프셋.</summary>
    private static TimeSpan CurrentPhaseOffset()
    {
        long cycle = HalfCycle.Ticks * 2;
        return TimeSpan.FromTicks(PhaseClock.Elapsed.Ticks % cycle);
    }
}

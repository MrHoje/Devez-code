using System.Windows;

namespace DevezCode.Models;

/// <summary>회전 스피너 요소(SpinnerSync.Angle 을 바인딩하는 Ellipse 등)에 부착해, 그 요소가 실제로
/// 화면에 보일 때만 SpinnerSync 의 렌더 루프가 돌게 한다. 모든 스피너가 숨겨지면(idle) SpinnerSync 가
/// CompositionTarget.Rendering 을 해제해 60fps wakeup 이 사라진다.
/// 사용법: 스피너 FrameworkElement 에 <c>models:SpinnerActivator.Track="True"</c>.
/// <para>요소가 카운트에 1 기여 중인지를 Counted 플래그로 추적해 중복 Acquire / 이중 Release 를 막는다.
/// IsVisible 은 자신 + 모든 조상의 Visibility 를 종합하므로, 스피너 자신이 Collapsed 이거나 부모
/// 오버레이가 Collapsed 이면 false 가 되어 정확히 "보일 때만" 카운트된다. 전부 UI 스레드에서 동작.</para></summary>
public static class SpinnerActivator
{
    public static readonly DependencyProperty TrackProperty =
        DependencyProperty.RegisterAttached("Track", typeof(bool), typeof(SpinnerActivator),
            new PropertyMetadata(false, OnTrackChanged));

    public static void SetTrack(DependencyObject d, bool value) => d.SetValue(TrackProperty, value);
    public static bool GetTrack(DependencyObject d) => (bool)d.GetValue(TrackProperty);

    // 이 요소가 현재 SpinnerSync 카운트에 1 기여 중인지(요소당 0/1). 중복 Acquire·이중 Release 방지.
    static readonly DependencyProperty CountedProperty =
        DependencyProperty.RegisterAttached("Counted", typeof(bool), typeof(SpinnerActivator),
            new PropertyMetadata(false));

    static void OnTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if (e.NewValue is true)
        {
            fe.IsVisibleChanged += OnVisibilityChanged;
            fe.Unloaded += OnUnloaded;
            Sync(fe); // 설정 시점에 이미 보이는 상태일 수 있어 즉시 반영
        }
        else
        {
            fe.IsVisibleChanged -= OnVisibilityChanged;
            fe.Unloaded -= OnUnloaded;
            ReleaseIfCounted(fe);
        }
    }

    static void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        => Sync((FrameworkElement)sender);

    // visual tree 에서 떨어질 때(세션/탭 제거 등) IsVisible=false 이벤트가 보장되지 않는 경로 대비 백업 해제.
    static void OnUnloaded(object sender, RoutedEventArgs e)
        => ReleaseIfCounted((FrameworkElement)sender);

    static void Sync(FrameworkElement fe)
    {
        bool want = fe.IsVisible;
        bool counted = (bool)fe.GetValue(CountedProperty);
        if (want == counted) return;
        fe.SetValue(CountedProperty, want);
        if (want) SpinnerSync.Instance.Acquire();
        else SpinnerSync.Instance.Release();
    }

    static void ReleaseIfCounted(FrameworkElement fe)
    {
        if ((bool)fe.GetValue(CountedProperty))
        {
            fe.SetValue(CountedProperty, false);
            SpinnerSync.Instance.Release();
        }
    }
}

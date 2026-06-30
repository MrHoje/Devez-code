using System.ComponentModel;
using System.Windows.Media;

namespace DevezCode.Models;

/// <summary>모든 회전 스피너가 공유하는 단일 각도 소스.
/// CompositionTarget.Rendering 의 RenderingTime(앱 전역 단일 시계)에서 Angle 을 계산하므로
/// 스피너가 언제 화면에 나타나든 위상이 항상 일치한다.
/// RotateTransform.Angle 을 {Binding Angle, Source={x:Static models:SpinnerSync.Instance}} 로 바인딩.
/// <para>렌더 루프 게이팅: CompositionTarget.Rendering 은 구독돼 있는 동안 화면 변화가 없어도 WPF 가
/// 매 프레임(보통 60fps) 렌더 패스를 강제로 돈다. 그래서 생성자에서 무조건 구독하지 않고,
/// 실제로 화면에 보이는 스피너가 있을 때만(Acquire/Release 카운트>0) 구독한다. 모든 스피너가
/// 숨겨진 idle 상태에서는 구독이 해제돼 불필요한 60fps wakeup 이 사라진다.
/// Acquire/Release 는 SpinnerActivator 가 스피너의 IsVisible 변화에 맞춰 호출한다(UI 스레드 전용).</para></summary>
public sealed class SpinnerSync : INotifyPropertyChanged
{
    public static readonly SpinnerSync Instance = new();

    const double PeriodSeconds = 0.85; // 1회전 주기

    double _angle;
    int _active;       // 현재 화면에 보이는(추적 중) 스피너 수 — UI 스레드에서만 변경
    bool _subscribed;  // CompositionTarget.Rendering 구독 상태

    SpinnerSync() { } // 구독은 첫 Acquire 까지 지연 — idle 에 렌더 루프가 돌지 않게 한다.

    /// <summary>스피너가 화면에 나타남. 첫 스피너에서 렌더 루프(60fps Angle 갱신)를 시작한다.</summary>
    public void Acquire()
    {
        if (++_active == 1 && !_subscribed)
        {
            CompositionTarget.Rendering += OnRendering;
            _subscribed = true;
        }
    }

    /// <summary>스피너가 사라짐. 마지막 스피너에서 렌더 루프를 정지해 idle 60fps wakeup 을 없앤다.</summary>
    public void Release()
    {
        if (_active == 0) return; // 불균형 호출 방어(이중 Release 등)
        if (--_active == 0 && _subscribed)
        {
            CompositionTarget.Rendering -= OnRendering;
            _subscribed = false;
        }
    }

    public double Angle
    {
        get => _angle;
        private set
        {
            if (_angle == value) return;
            _angle = value;
            PropertyChanged?.Invoke(this, AngleChangedArgs);
        }
    }

    static readonly PropertyChangedEventArgs AngleChangedArgs = new(nameof(Angle));

    void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs r)
            Angle = r.RenderingTime.TotalSeconds / PeriodSeconds % 1.0 * 360.0;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

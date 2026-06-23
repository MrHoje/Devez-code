using System.ComponentModel;
using System.Windows.Media;

namespace DevezCode.Models;

/// <summary>모든 회전 스피너가 공유하는 단일 각도 소스.
/// CompositionTarget.Rendering 의 RenderingTime(앱 전역 단일 시계)에서 Angle 을 계산하므로
/// 스피너가 언제 화면에 나타나든 위상이 항상 일치한다.
/// RotateTransform.Angle 을 {Binding Angle, Source={x:Static models:SpinnerSync.Instance}} 로 바인딩.</summary>
public sealed class SpinnerSync : INotifyPropertyChanged
{
    public static readonly SpinnerSync Instance = new();

    const double PeriodSeconds = 0.85; // 1회전 주기

    double _angle;

    SpinnerSync() => CompositionTarget.Rendering += OnRendering;

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

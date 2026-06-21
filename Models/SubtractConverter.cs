using System;
using System.Globalization;
using System.Windows.Data;

namespace DevezCode.Models;

/// <summary>숫자 값에서 고정값을 빼서 반환. parameter 가 "20" 이면 value-20.
/// TaskQueueView 의 버블 MaxWidth 가 ListBox 폭에 따라 동적으로 잡히도록 사용.</summary>
public sealed class SubtractConverter : IValueConverter
{
    public static readonly SubtractConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var v = value switch
        {
            double d => d,
            float f  => (double)f,
            int i    => (double)i,
            _        => double.NaN,
        };
        if (double.IsNaN(v)) return double.PositiveInfinity;
        var sub = 0.0;
        if (parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
            sub = p;
        var result = v - sub;
        return result < 0 ? 0 : result;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

using System.Globalization;
using System.Windows.Data;

namespace DevezCode.Models;

/// <summary>탭 아이템과 패널의 SelectedTab 을 비교해 "이 패널에서 선택된 탭인지" 반환.
/// TabItemBase.IsSelected(공유 bool)는 두 패널이 같은 프로젝트를 보여줄 때 서로 덮어써
/// 비포커스 패널의 선택 표시가 사라진다 → 패널별 SelectedTab 과 아이템을 MultiBinding 으로
/// 비교해 각 패널이 독립적으로 자기 선택 탭을 강조하게 한다.
/// values[0] = 탭 아이템(DataContext), values[1] = 패널의 SelectedTab.</summary>
public sealed class TabActiveConverter : IMultiValueConverter
{
    public static readonly TabActiveConverter Instance = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        => values.Length == 2 && values[0] != null && ReferenceEquals(values[0], values[1]);

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

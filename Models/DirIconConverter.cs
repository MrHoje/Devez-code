using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DevezCode.Models;

/// <summary>IsDirectory(bool) → 폴더/파일 아이콘 PathGeometry. 파일 탐색기 트리용.</summary>
public sealed class DirIconConverter : IValueConverter
{
    public static readonly DirIconConverter Instance = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is true ? "IconFolder" : "IconFileText";
        return Application.Current?.TryFindResource(key) as Geometry;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → Visibility (true=Visible, false=Collapsed).</summary>
public sealed class BoolVisibilityConverter : IValueConverter
{
    public static readonly BoolVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>IsExpanded(bool) → 아래/오른쪽 chevron 아이콘. 프로젝트 트리 펼침 표시용.</summary>
public sealed class ChevronConverter : IValueConverter
{
    public static readonly ChevronConverter Instance = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is true ? "IconChevronDown" : "IconChevronRight";
        return Application.Current?.TryFindResource(key) as Geometry;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

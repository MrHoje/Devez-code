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

/// <summary>프로젝트 아이콘 키(string) → PathGeometry. 없으면 IconBox 폴백.</summary>
public sealed class ProjectIconConverter : IValueConverter
{
    public static readonly ProjectIconConverter Instance = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value as string;
        if (string.IsNullOrEmpty(key)) key = "IconBox";
        return (Application.Current?.TryFindResource(key)
                ?? Application.Current?.TryFindResource("IconBox")) as Geometry;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>프로젝트 아이콘 색상 hex(string?) → Brush. null/빈값이면 테마 RailIconBrush.</summary>
public sealed class ProjectIconColorConverter : IValueConverter
{
    public static readonly ProjectIconColorConverter Instance = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrEmpty(hex))
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
            catch { /* 잘못된 hex → 기본색 */ }
        }
        return Application.Current?.TryFindResource("RailIconBrush") as Brush
               ?? System.Windows.Media.Brushes.Gray;
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

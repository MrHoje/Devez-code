using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DevezCode.Models;

namespace DevezCode.Models;

/// <summary>bool → Visibility (param: "invert" 면 반전).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is bool bv && bv;
        var invert = parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase);
        var show = invert ? !b : b;
        return show ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Status enum → 상태 점 색 (실시간 미조회 = 회색).</summary>
public sealed class McpStatusToBrushConverter : IValueConverter
{
    public static readonly McpStatusToBrushConverter Instance = new();

    private static readonly Brush OkBrush   = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)); // green
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)); // amber
    private static readonly Brush ErrBrush  = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)); // red
    private static readonly Brush MuteBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)); // slate-400
    private static readonly Brush Orange    = new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16)); // orange

    static McpStatusToBrushConverter()
    {
        OkBrush.Freeze(); WarnBrush.Freeze(); ErrBrush.Freeze(); MuteBrush.Freeze(); Orange.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not McpLiveStatus s) return MuteBrush;
        return s switch
        {
            McpLiveStatus.Connected => OkBrush,
            McpLiveStatus.Disabled => MuteBrush,
            McpLiveStatus.NeedsAuth => WarnBrush,
            McpLiveStatus.Failed => ErrBrush,
            McpLiveStatus.NeedsClientRegistration => Orange,
            _ => MuteBrush,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Status enum → 한글 라벨.</summary>
public sealed class McpStatusToLabelConverter : IValueConverter
{
    public static readonly McpStatusToLabelConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not McpLiveStatus s) return "미확인";
        return s switch
        {
            McpLiveStatus.Connected => "연결됨",
            McpLiveStatus.Disabled => "비활성",
            McpLiveStatus.NeedsAuth => "인증 필요",
            McpLiveStatus.Failed => "오류",
            McpLiveStatus.NeedsClientRegistration => "등록 필요",
            _ => "미확인",
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Status enum → 오류 메시지 표시 여부. Failed/NeedsAuth/NeedsClientRegistration 일 때만 노출.</summary>
public sealed class McpStatusToVisibilityConverter : IValueConverter
{
    public static readonly McpStatusToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is McpLiveStatus s &&
            (s == McpLiveStatus.Failed || s == McpLiveStatus.NeedsAuth || s == McpLiveStatus.NeedsClientRegistration))
            return Visibility.Visible;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>McpServerType → Visibility. 원격(http/sse)일 때만 Visible — OAuth 인증/로그아웃 버튼 게이팅용.</summary>
public sealed class McpRemoteToVisibilityConverter : IValueConverter
{
    public static readonly McpRemoteToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is McpServerType t && t == McpServerType.Remote ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>McpServerType → 한글 라벨 (local/remote → 로컬/원격).</summary>
public sealed class McpTypeToLabelConverter : IValueConverter
{
    public static readonly McpTypeToLabelConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is McpServerType t && t == McpServerType.Remote ? "원격" : "로컬";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

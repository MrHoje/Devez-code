using System.Globalization;
using System.Windows.Data;
using DevezCode.Services;

namespace DevezCode.Models;

/// <summary>세션의 AgentId(string) → pack URI 이미지 (Image.Source 바인딩용).
/// opencode 는 다크/라이트 테마에 따라 흑/백 변형 아이콘을 사용 (devez 정합).
/// 빈 값 / 미등록 에이전트는 Claude 아이콘으로 폴백.</summary>
public sealed class AgentImageConverter : IValueConverter
{
    public static readonly AgentImageConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var id = (value as string)?.ToLowerInvariant() ?? "";
        if (string.IsNullOrEmpty(id)) id = AgentRegistry.DefaultAgentId;
        bool isDark = App.CurrentTheme == "dark";
        string fileName = id switch
        {
            "opencode"  => isDark ? "opencode_icon_white_50.png" : "opencode_icon_black_50.png",
            "claude"    => "claude_code.png",
            "codex"     => "codex.png",
            "gajaecode" => "gajae_code.png",
            _           => "claude_code.png",
        };
        return new System.Uri($"pack://application:,,,/Resources/Images/ShellPresets/{fileName}", System.UriKind.Absolute);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

using System.Globalization;
using System.Windows.Data;
using DevezCode.Services;

namespace DevezCode.Models;

/// <summary>세션의 AgentId(string) → pack URI 이미지 (Image.Source 바인딩용).
/// codex·opencode·grok·kimi 는 다크/라이트 테마에 따라 흑/백 변형 아이콘을 사용 (devez 정합).
/// devezvibe 는 채워진 컬러 로고라 테마 무관 한 장.
/// 빈 값 / 미등록 에이전트는 DevezCode 자체 로고를 중립 아이콘으로 사용.
/// 테마 전환 시 바인딩 재평가: SessionItem.RefreshAgentIcon() → PropertyChanged(AgentId).</summary>
public sealed class AgentImageConverter : IValueConverter
{
    public static readonly AgentImageConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // 저장소에 남은 개명 이전 ID(devezcli 등)는 현재 ID로 정규화한다.
        var id = AgentRegistry.NormalizeId(value as string).ToLowerInvariant();
        bool isDark = App.IsDarkTheme(App.CurrentTheme);
        string? fileName = id switch
        {
            "opencode"    => isDark ? "opencode_icon_white_50.png" : "opencode_icon_black_50.png",
            "grok"        => isDark ? "grok_icon_white_50.png" : "grok_icon_black_50.png",
            "claude"      => "claude_code.png",
            "codex"       => isDark ? "codex_icon_white_50.png" : "codex_icon_black_50.png",
            "gajae"       => "gajae_code.png",
            "antigravity" => "anti.png",
            "kimi"        => isDark ? "kimi_icon_white_50.png" : "kimi_icon_black_50.png",
            "devezvibe"   => "devezvibe_icon.png",
            _              => null,
        };

        // 알 수 없는 에이전트를 Claude로 오인시키지 않고 자체 로고로 표시한다.
        string uri = fileName == null
            ? "pack://application:,,,/Resources/Logos/logo.png"
            : $"pack://application:,,,/Resources/Images/ShellPresets/{fileName}";
        return new System.Uri(uri, System.UriKind.Absolute);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

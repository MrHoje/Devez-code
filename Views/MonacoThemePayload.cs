using System.Windows;
using System.Windows.Media;

namespace DevezCode.Views;

/// <summary>현재 앱 테마 브러시에서 Monaco 테마 색을 추출한다.</summary>
public static class MonacoThemePayload
{
    private static string Hex(string key)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush b)
        {
            var c = b.Color;
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }
        return "#1E1E1E";
    }

    /// <summary>테마 브러시 색 + 2자리 알파(예: "59")로 #RRGGBBAA 생성.</summary>
    private static string HexA(string key, string alpha)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush b)
        {
            var c = b.Color;
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}{alpha}";
        }
        return "#808080" + alpha;
    }

    /// <summary>다크 계열인지(배경 밝기로 판정) — Monaco base 선택용.</summary>
    private static bool IsDark()
    {
        if (Application.Current?.TryFindResource("BgBrush") is SolidColorBrush b)
            return (0.299 * b.Color.R + 0.587 * b.Color.G + 0.114 * b.Color.B) < 128;
        return true;
    }

    public static (string @base, object[] rules, object colors) Current()
    {
        var dark = IsDark();
        // 추가=초록/삭제=빨강. 다크는 GitHub-dark(=codex 톤), 라이트는 GitHub-light.
        string ins = dark ? "#3FB950" : "#2DA44E";
        string rem = dark ? "#F85149" : "#CF222E";
        // 라인 배경(은은), 문자 배경(진하게) — 사진처럼 변경 라인 가득 채움.
        string lineA = dark ? "2E" : "24";   // 라인 배경 알파(≈18%/14%)
        string textA = dark ? "4D" : "40";   // 문자 배경 알파(≈30%/25%)

        var colors = new System.Collections.Generic.Dictionary<string, string>
        {
            ["editor.background"] = Hex("BgBrush"),
            ["editor.foreground"] = Hex("TextBrush"),
            ["editorLineNumber.foreground"] = Hex("TextMutedBrush"),
            ["editorGutter.background"] = Hex("BgBrush"),
            ["diffEditor.insertedTextBackground"] = ins + textA,
            ["diffEditor.removedTextBackground"] = rem + textA,
            ["diffEditor.insertedLineBackground"] = ins + lineA,
            ["diffEditor.removedLineBackground"] = rem + lineA,
            ["diffEditor.diagonalFill"] = HexA("TextMutedBrush", "1A"),   // 빈 쪽 사선 채움(은은)
            ["diffEditor.border"] = Hex("LineBrush"),                     // 좌우 분할선 = 앱 세퍼레이터색
            ["sash.hoverBorder"] = Hex("PrimaryBrush"),                   // 드래그 핸들 hover
            ["scrollbarSlider.background"] = HexA("TextMutedBrush", "59"),
            ["scrollbarSlider.hoverBackground"] = HexA("TextMutedBrush", "80"),
            ["scrollbarSlider.activeBackground"] = HexA("TextMutedBrush", "A6"),
            ["diffEditorOverviewRuler.insertedForeground"] = ins,
            ["diffEditorOverviewRuler.removedForeground"] = rem,
            ["editorOverviewRuler.border"] = "#00000000",
            ["editorOverviewRuler.addedForeground"] = ins,
            ["editorOverviewRuler.deletedForeground"] = rem,
        };
        return (dark ? "vs-dark" : "vs", System.Array.Empty<object>(), colors);
    }
}

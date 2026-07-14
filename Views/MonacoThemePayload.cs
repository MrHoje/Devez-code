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
        var colors = new System.Collections.Generic.Dictionary<string, string>
        {
            ["editor.background"] = Hex("BgBrush"),
            ["editor.foreground"] = Hex("TextBrush"),
            ["editorLineNumber.foreground"] = Hex("TextMutedBrush"),
            ["editorGutter.background"] = Hex("BgBrush"),
            ["diffEditor.insertedTextBackground"] = "#3FB95033",
            ["diffEditor.removedTextBackground"] = "#F8514933",
            ["diffEditor.insertedLineBackground"] = "#3FB9501A",
            ["diffEditor.removedLineBackground"] = "#F851491A",
            ["scrollbarSlider.background"] = HexA("TextMutedBrush", "59"),
            ["scrollbarSlider.hoverBackground"] = HexA("TextMutedBrush", "80"),
            ["scrollbarSlider.activeBackground"] = HexA("TextMutedBrush", "A6"),
            ["diffEditorOverviewRuler.insertedForeground"] = "#3FB950",
            ["diffEditorOverviewRuler.removedForeground"] = "#F85149",
            ["editorOverviewRuler.border"] = "#00000000",
            ["minimap.background"] = Hex("BgBrush"),
        };
        return (IsDark() ? "vs-dark" : "vs", System.Array.Empty<object>(), colors);
    }
}

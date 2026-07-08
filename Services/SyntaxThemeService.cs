using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;

namespace DevezCode.Services;

/// <summary>
/// AvalonEdit 내장 구문 하이라이팅 정의의 색을 현재 테마에 맞춰 덮어쓴다.
/// 테마는 전역 하나뿐이라, 정의 싱글턴(HighlightingManager.Instance)의 색을 현재 테마로
/// 바꿔치기하는 방식이 가장 단순·안전하다. (에디터를 열 때/테마가 바뀔 때 항상 재적용)
///
/// 색 이름은 언어(.xshd)마다 제각각이라 정확한 이름을 다 열거하지 않고,
/// 이름을 의미 카테고리(주석/문자열/숫자/키워드/속성)로 분류하는 휴리스틱으로 매핑한다.
/// 매칭되지 않는 색은 내장 색을 그대로 둔다.
/// </summary>
public static class SyntaxThemeService
{
    private readonly record struct Palette(
        Color Comment, Color Str, Color Keyword, Color Number, Color Attribute);

    // VS Dark 근사.
    private static readonly Palette Dark = new(
        Comment:   Color.FromRgb(0x6A, 0x99, 0x55),
        Str:       Color.FromRgb(0xCE, 0x91, 0x78),
        Keyword:   Color.FromRgb(0x56, 0x9C, 0xD6),
        Number:    Color.FromRgb(0xB5, 0xCE, 0xA8),
        Attribute: Color.FromRgb(0x9C, 0xDC, 0xFE));

    // VS Light 근사 (minimal + soft 공용).
    private static readonly Palette Light = new(
        Comment:   Color.FromRgb(0x00, 0x80, 0x00),
        Str:       Color.FromRgb(0xA3, 0x15, 0x15),
        Keyword:   Color.FromRgb(0x00, 0x00, 0xFF),
        Number:    Color.FromRgb(0x09, 0x88, 0x58),
        Attribute: Color.FromRgb(0xFF, 0x00, 0x00));

    /// <summary>등록된 모든 내장 정의의 named color 를 현재 테마 팔레트로 덮어쓴다.</summary>
    public static void Apply(string theme)
    {
        var p = theme == "dark" ? Dark : Light;
        foreach (var def in HighlightingManager.Instance.HighlightingDefinitions)
        {
            foreach (var c in def.NamedHighlightingColors)
            {
                var color = Categorize(c.Name, p);
                if (color is Color col)
                    c.Foreground = new SimpleHighlightingBrush(col);
            }
        }
    }

    private static Color? Categorize(string name, Palette p)
    {
        if (name.Contains("Comment")) return p.Comment;
        if (name == "AttributeName") return p.Attribute;
        if (name == "AttributeValue" || name.Contains("String") || name.Contains("Char"))
            return p.Str;
        if (name.Contains("Number") || name.Contains("Numeric") || name.Contains("Digit"))
            return p.Number;
        if (name.Contains("Keyword") || name.Contains("Modifier") || name.Contains("Visibility")
            || name == "XmlTag" || name.Contains("Preprocessor") || name.Contains("Entity")
            || name.Contains("Directive") || name.Contains("Type") || name == "TrueFalse"
            || name.Contains("Reference"))
            return p.Keyword;
        return null;
    }
}

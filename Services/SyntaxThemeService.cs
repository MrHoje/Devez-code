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

    // VS Light 근사 (minimal — 쨍한 순색).
    private static readonly Palette Minimal = new(
        Comment:   Color.FromRgb(0x00, 0x80, 0x00),
        Str:       Color.FromRgb(0xA3, 0x15, 0x15),
        Keyword:   Color.FromRgb(0x00, 0x00, 0xFF),
        Number:    Color.FromRgb(0x09, 0x88, 0x58),
        Attribute: Color.FromRgb(0xFF, 0x00, 0x00));

    // soft — minimal 을 살짝 톤다운(채도↓)한 부드러운 색. 크림/베이지 배경에 어울림.
    private static readonly Palette Soft = new(
        Comment:   Color.FromRgb(0x3C, 0x7A, 0x3C),
        Str:       Color.FromRgb(0xB1, 0x4A, 0x42),
        Keyword:   Color.FromRgb(0x33, 0x55, 0xCC),
        Number:    Color.FromRgb(0x3C, 0x80, 0x60),
        Attribute: Color.FromRgb(0xC0, 0x50, 0x4D));

    private static readonly Palette Gray = new(
        Comment:   Color.FromRgb(0x5F, 0x67, 0x74),
        Str:       Color.FromRgb(0x9D, 0x3D, 0x3A),
        Keyword:   Color.FromRgb(0x3D, 0x5F, 0x8A),
        Number:    Color.FromRgb(0x2F, 0x7A, 0x55),
        Attribute: Color.FromRgb(0x76, 0x55, 0x8F));

    private static readonly Palette SoftPink = new(
        Comment:   Color.FromRgb(0x73, 0x57, 0x63),
        Str:       Color.FromRgb(0xA1, 0x4D, 0x62),
        Keyword:   Color.FromRgb(0x6C, 0x4A, 0x9A),
        Number:    Color.FromRgb(0x2F, 0x7A, 0x55),
        Attribute: Color.FromRgb(0xB5, 0x4A, 0x6B));

    private static readonly Palette Midnight = new(
        Comment:   Color.FromRgb(0x9C, 0xA3, 0xAF),
        Str:       Color.FromRgb(0xF8, 0x71, 0x71),
        Keyword:   Color.FromRgb(0x93, 0xC5, 0xFD),
        Number:    Color.FromRgb(0x34, 0xD3, 0x99),
        Attribute: Color.FromRgb(0xA7, 0x8B, 0xFA));

    /// <summary>테마별 하이퍼링크(자동 URL) 색. 다크에서 안 보이던 기본 진파랑을 밝게 교체.</summary>
    public static Color LinkColor(string theme) => theme switch
    {
        "dark" => Color.FromRgb(0x4F, 0xA6, 0xFF),
        "soft" => Color.FromRgb(0x3A, 0x6F, 0xA5),
        "gray" => Color.FromRgb(0x32, 0x6A, 0xA5),
        "softpink" => Color.FromRgb(0x32, 0x6A, 0x9F),
        "midnight" => Color.FromRgb(0x60, 0xA5, 0xFA),
        _      => Color.FromRgb(0x05, 0x63, 0xC1),
    };

    /// <summary>등록된 모든 내장 정의의 named color 를 현재 테마 팔레트로 덮어쓴다.</summary>
    public static void Apply(string theme)
    {
        var p = theme switch { "dark" => Dark, "soft" => Soft, "gray" => Gray, "softpink" => SoftPink, "midnight" => Midnight, _ => Minimal };
        foreach (var def in HighlightingManager.Instance.HighlightingDefinitions)
        {
            foreach (var c in def.NamedHighlightingColors)
            {
                // 분류된 토큰만 팔레트 색. 미분류(메서드명·구두점 등)는 내장색(라이트 기준)을 버리고
                // null 로 두어 에디터 기본 전경색(테마 텍스트색)을 상속 → 다크/라이트 모두 항상 가독.
                var color = Categorize(c.Name, p);
                c.Foreground = color is Color col ? new SimpleHighlightingBrush(col) : null;
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

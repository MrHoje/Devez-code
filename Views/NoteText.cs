using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DevezCode.Views;

/// <summary>
/// 업데이트 노트처럼 글머리 기호가 붙은 목록을 그릴 때 쓰는 렌더 헬퍼.
///
/// 단일 TextBlock 에 "· 본문" 을 넣으면 본문이 워드랩될 때 둘째 줄이 글머리 기호 자리까지
/// 왼쪽으로 붙어 항목 경계가 사라진다(hanging indent 부재). WPF TextBlock 에는 매달린 들여쓰기
/// 기능이 없으므로 글머리 기호 열과 본문 열을 나눈 Grid 로 한 항목을 구성한다.
/// </summary>
internal static class NoteText
{
    /// <summary>노트 한 줄을 여는 글머리 기호로 인정하는 문자들.</summary>
    private static readonly char[] BulletChars = ['·', '•', '-'];

    /// <summary>줄이 "· 본문" 형태면 기호와 본문을 분리한다.</summary>
    public static bool TryParseBullet(string line, out string bullet, out string text)
    {
        bullet = ""; text = "";
        var trimmed = line.TrimStart();
        if (trimmed.Length < 2 || Array.IndexOf(BulletChars, trimmed[0]) < 0) return false;
        if (!char.IsWhiteSpace(trimmed[1])) return false;
        bullet = trimmed[0].ToString();
        text = trimmed[1..].TrimStart();
        return text.Length > 0;
    }

    /// <summary>글머리 기호 열(고정) + 본문 열(가변 워드랩)로 구성한 한 항목.
    /// 본문이 여러 줄로 접혀도 둘째 줄부터 기호 아래가 아니라 본문 첫 글자에 맞춰 정렬된다.</summary>
    public static Grid BulletRow(string bullet, string text, Brush foreground,
                                 double fontSize, double lineHeight, Thickness margin = default)
    {
        var row = new Grid { Margin = margin };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var mark = new TextBlock
        {
            Text = bullet,
            FontSize = fontSize,
            Foreground = foreground,
            LineHeight = lineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Margin = new Thickness(0, 0, 6, 0),
        };
        var body = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            Foreground = foreground,
            LineHeight = lineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(mark, 0);
        Grid.SetColumn(body, 1);
        row.Children.Add(mark);
        row.Children.Add(body);
        return row;
    }

    /// <summary>일반(글머리 기호 없는) 줄. 버전 헤딩·안내 문장 등에 쓴다.</summary>
    public static TextBlock PlainRow(string text, Brush foreground, double fontSize, double lineHeight,
                                     FontWeight? weight = null, Thickness margin = default)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            Foreground = foreground,
            LineHeight = lineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextWrapping = TextWrapping.Wrap,
            Margin = margin,
        };
        if (weight.HasValue) tb.FontWeight = weight.Value;
        return tb;
    }

    /// <summary>
    /// 여러 줄 메시지를 host 에 렌더한다. 글머리 기호 줄은 매달린 들여쓰기로, 빈 줄은 여백으로 바꾼다.
    /// </summary>
    /// <returns>글머리 기호 줄이 하나라도 있었는지(없으면 호출부가 기존 단일 TextBlock 경로를 쓰면 된다).</returns>
    public static bool Render(Panel host, string message, Brush foreground, double fontSize, double lineHeight)
    {
        host.Children.Clear();
        bool sawBullet = false;
        var lines = message.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                // 연속 빈 줄은 하나의 단락 여백으로 합친다(첫 줄·마지막 줄의 빈 줄은 버린다).
                if (host.Children.Count > 0 && i < lines.Length - 1)
                    host.Children.Add(new Border { Height = 8 });
                continue;
            }
            if (TryParseBullet(line, out var bullet, out var text))
            {
                sawBullet = true;
                host.Children.Add(BulletRow(bullet, text, foreground, fontSize, lineHeight));
            }
            else
            {
                host.Children.Add(PlainRow(line.TrimEnd(), foreground, fontSize, lineHeight));
            }
        }
        return sawBullet;
    }
}

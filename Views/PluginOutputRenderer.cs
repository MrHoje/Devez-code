using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DevezCode.Views;

/// <summary>플러그인 출력(RichTextBox)에 줄 단위 색상을 입힌다.
/// CLI 성공줄(✔/✓)은 초록, 실패줄(✘/✗)은 빨강, 나머지는 기본색 — 윈도우 터미널 느낌.
/// CLI 가 "Installing...✔ Successfully..." 처럼 한 줄에 붙여 내보내는 경우 상태표시(✔/✘) 앞에서 줄바꿈해
/// 상태 문장이 독립 줄로 색이 입혀지게 한다.</summary>
internal static class PluginOutputRenderer
{
    // 상태 마커(✔ 성공 / ✘ 실패 계열)
    private const string OkMarks = "✔✓✅√";
    private const string ErrMarks = "✘✗✖❌×";

    public static void Render(RichTextBox rtb, string text)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0) };
        rtb.Document = doc;
        if (string.IsNullOrEmpty(text)) return;

        var def = Res("TextBrush", Brushes.Gray);
        var ok = Res("SuccessBrush", Brushes.Green);
        var err = Res("DangerBrush", Brushes.Red);
        var lineBrush = Res("LineBrush", Brushes.DimGray);

        // 마커가 줄 중간에 붙어 있으면(앞이 개행이 아니면) 그 앞에서 줄바꿈 — 앞의 공백/탭은 흡수.
        text = Regex.Replace(text, @"(?<=[^\n])[ \t]*(?=[" + OkMarks + ErrMarks + "])", "\n");

        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        // details 출력의 Component inventory는 독립된 정보 묶음이므로 위·아래 선으로 구분한다.
        var inventoryStart = Array.FindIndex(lines,
            line => string.Equals(line.Trim(), "Component inventory", StringComparison.OrdinalIgnoreCase));
        if (inventoryStart >= 0)
        {
            var beforeEnd = inventoryStart;
            while (beforeEnd > 0 && string.IsNullOrWhiteSpace(lines[beforeEnd - 1])) beforeEnd--;

            var inventoryEnd = inventoryStart + 1;
            while (inventoryEnd < lines.Length && !string.IsNullOrWhiteSpace(lines[inventoryEnd])) inventoryEnd++;

            var afterStart = inventoryEnd;
            while (afterStart < lines.Length && string.IsNullOrWhiteSpace(lines[afterStart])) afterStart++;

            AddParagraph(doc, lines, 0, beforeEnd, def, ok, err);
            var inventory = AddParagraph(doc, lines, inventoryStart, inventoryEnd, def, ok, err);
            inventory.BorderBrush = lineBrush;
            inventory.BorderThickness = new Thickness(0, 1, 0, 1);
            inventory.Padding = new Thickness(0, 8, 0, 8);
            inventory.Margin = new Thickness(0, 12, 0, 12);
            AddParagraph(doc, lines, afterStart, lines.Length, def, ok, err);
            return;
        }

        AddParagraph(doc, lines, 0, lines.Length, def, ok, err);
    }

    private static Paragraph AddParagraph(FlowDocument doc, string[] lines, int start, int end,
        Brush def, Brush ok, Brush err)
    {
        var para = new Paragraph { Margin = new Thickness(0) };
        for (var i = start; i < end; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            var brush = def;
            if (trimmed.Length > 0 && OkMarks.IndexOf(trimmed[0]) >= 0) brush = ok;
            else if (trimmed.Length > 0 && ErrMarks.IndexOf(trimmed[0]) >= 0) brush = err;
            para.Inlines.Add(new Run(line) { Foreground = brush });
            if (i < end - 1) para.Inlines.Add(new LineBreak());
        }
        doc.Blocks.Add(para);
        return para;
    }

    private static Brush Res(string key, Brush fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? fallback;
}

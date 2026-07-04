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

        // 마커가 줄 중간에 붙어 있으면(앞이 개행이 아니면) 그 앞에서 줄바꿈 — 앞의 공백/탭은 흡수.
        text = Regex.Replace(text, @"(?<=[^\n])[ \t]*(?=[" + OkMarks + ErrMarks + "])", "\n");

        var para = new Paragraph { Margin = new Thickness(0) };
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var t = line.TrimStart();
            Brush b = def;
            if (t.Length > 0 && OkMarks.IndexOf(t[0]) >= 0) b = ok;
            else if (t.Length > 0 && ErrMarks.IndexOf(t[0]) >= 0) b = err;
            para.Inlines.Add(new Run(line) { Foreground = b });
            if (i < lines.Length - 1) para.Inlines.Add(new LineBreak());
        }
        doc.Blocks.Add(para);
    }

    private static Brush Res(string key, Brush fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? fallback;
}

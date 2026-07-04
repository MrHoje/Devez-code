using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DevezCode.Views;

/// <summary>플러그인 출력(RichTextBox)에 줄 단위 색상을 입힌다.
/// CLI 성공줄(✔/✓)은 초록, 실패줄(✘/✗)은 빨강, 나머지는 기본색 — 윈도우 터미널 느낌.</summary>
internal static class PluginOutputRenderer
{
    public static void Render(RichTextBox rtb, string text)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0) };
        rtb.Document = doc;
        if (string.IsNullOrEmpty(text)) return;

        var def = Res("TextBrush", Brushes.Gray);
        var ok = Res("SuccessBrush", Brushes.Green);
        var err = Res("DangerBrush", Brushes.Red);

        var para = new Paragraph { Margin = new Thickness(0) };
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var t = line.TrimStart();
            Brush b = def;
            if (t.StartsWith("✔") || t.StartsWith("✓") || t.StartsWith("✅") || t.StartsWith("√"))
                b = ok;
            else if (t.StartsWith("✘") || t.StartsWith("✗") || t.StartsWith("✖") || t.StartsWith("❌") || t.StartsWith("×"))
                b = err;
            para.Inlines.Add(new Run(line) { Foreground = b });
            if (i < lines.Length - 1) para.Inlines.Add(new LineBreak());
        }
        doc.Blocks.Add(para);
    }

    private static Brush Res(string key, Brush fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? fallback;
}

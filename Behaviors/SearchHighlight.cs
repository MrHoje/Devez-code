using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace DevezCode.Behaviors;

/// <summary>검색어와 일치하는 TextBlock 구간만 강조색으로 렌더링한다.</summary>
public static class SearchHighlight
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text",
            typeof(string),
            typeof(SearchHighlight),
            new PropertyMetadata("", OnHighlightPropertyChanged));

    public static readonly DependencyProperty QueryProperty =
        DependencyProperty.RegisterAttached(
            "Query",
            typeof(string),
            typeof(SearchHighlight),
            new PropertyMetadata("", OnHighlightPropertyChanged));

    public static void SetText(DependencyObject element, string value)
        => element.SetValue(TextProperty, value);

    public static string GetText(DependencyObject element)
        => (string)element.GetValue(TextProperty);

    public static void SetQuery(DependencyObject element, string value)
        => element.SetValue(QueryProperty, value);

    public static string GetQuery(DependencyObject element)
        => (string)element.GetValue(QueryProperty);

    private static void OnHighlightPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock) return;

        string text = GetText(textBlock) ?? "";
        string query = GetQuery(textBlock)?.Trim() ?? "";
        textBlock.Inlines.Clear();

        if (text.Length == 0 || query.Length == 0)
        {
            textBlock.Inlines.Add(new Run(text));
            return;
        }

        int offset = 0;
        while (offset < text.Length)
        {
            int match = text.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase);
            if (match < 0)
            {
                textBlock.Inlines.Add(new Run(text[offset..]));
                break;
            }

            if (match > offset)
                textBlock.Inlines.Add(new Run(text[offset..match]));

            var highlighted = new Run(text.Substring(match, query.Length));
            highlighted.SetResourceReference(TextElement.ForegroundProperty, "PrimaryBrush");
            textBlock.Inlines.Add(highlighted);
            offset = match + query.Length;
        }
    }
}

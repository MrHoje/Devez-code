using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DevezCode.Views;

public partial class QuickOpenWindow : Window
{
    private const int MaxResults = 180;
    private readonly List<Models.QuickOpenItem> _allItems;
    private readonly ObservableCollection<Models.QuickOpenItem> _results = [];

    public Models.QuickOpenItem? SelectedItem { get; private set; }

    private QuickOpenWindow(IEnumerable<Models.QuickOpenItem> items)
    {
        InitializeComponent();
        _allItems = items?.ToList() ?? [];
        ResultList.ItemsSource = _results;

        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            ApplyFilter();
        };
    }

    public static Models.QuickOpenItem? Pick(Window owner, IEnumerable<Models.QuickOpenItem> items)
    {
        var dlg = new QuickOpenWindow(items)
        {
            Owner = owner
        };
        return dlg.ShowDialog() == true ? dlg.SelectedItem : null;
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        _results.Clear();
        if (_allItems.Count == 0) return;

        if (string.IsNullOrWhiteSpace(query))
        {
            foreach (var item in _allItems.Take(MaxResults))
                _results.Add(item);
        }
        else
        {
            var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var scored = _allItems
                .Select(i => new { Item = i, Score = CalculateScore(i, tokens) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Item.Kind)
                .ThenBy(x => x.Item.Title)
                .Take(MaxResults)
                .Select(x => x.Item)
                .ToList();
            foreach (var item in scored) _results.Add(item);
        }

        SearchHintText.Text = $"결과 {_results.Count}건";
        ResultList.SelectedIndex = _results.Count > 0 ? 0 : -1;
    }

    private static int CalculateScore(Models.QuickOpenItem item, string[] tokens)
    {
        int score = 0;
        var searchable = item.SearchText;
        foreach (var token in tokens)
        {
            int idx = searchable.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return 0;
            score += idx == 0 ? 120 : idx < 20 ? 70 : 40;
            if (token.Length > 2) score += 20;
        }
        return score;
    }

    private void CommitSelection()
    {
        SelectedItem = ResultList.SelectedItem as Models.QuickOpenItem;
        if (SelectedItem == null) return;
        DialogResult = true;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Down && _results.Count > 0)
        {
            ResultList.Focus();
            ResultList.SelectedIndex = 0;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (_results.Count == 0) return;
            ResultList.SelectedIndex = Math.Max(0, ResultList.SelectedIndex);
            CommitSelection();
            e.Handled = true;
        }
    }

    private void ResultList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }

    private void ResultList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        CommitSelection();
        e.Handled = true;
    }
}

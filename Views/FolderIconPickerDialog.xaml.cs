using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DevezCode.Models;

namespace DevezCode.Views;

/// <summary>폴더 아이콘 10개를 5x2로 표시하는 로컬 전용 선택창.</summary>
public partial class FolderIconPickerDialog : Window
{
    private string _selectedIconKey;

    private FolderIconPickerDialog(string currentIconKey, string folderName)
    {
        InitializeComponent();
        _selectedIconKey = FolderIconCatalog.Normalize(currentIconKey);
        FolderNameText.Text = folderName;
        BuildIconGrid();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Complete(); e.Handled = true; }
            else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        };
    }

    public static string? Pick(Window owner, string currentIconKey, string folderName)
    {
        var dialog = new FolderIconPickerDialog(currentIconKey, folderName) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog._selectedIconKey : null;
    }

    private void BuildIconGrid()
    {
        IconGrid.Children.Clear();
        foreach (var option in FolderIconCatalog.Options)
            IconGrid.Children.Add(CreateIconTile(option));
    }

    private Border CreateIconTile(FolderIconOption option)
    {
        bool selected = option.Key == _selectedIconKey;
        var tile = new Border
        {
            Width = 54,
            Height = 54,
            Margin = new Thickness(4),
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1.5),
            BorderBrush = ResourceBrush(selected ? "PrimaryBrush" : "LineBrush"),
            Background = ResourceBrush(selected ? "PanelSoftBrush" : "PanelBrush"),
            Cursor = Cursors.Arrow,
            ToolTip = option.Label,
        };

        if (TryFindResource(option.Key) is Geometry geometry)
        {
            double iconSize = option.Key == "IconSparkle" ? 18 : 20;
            tile.Child = new Path
            {
                Data = geometry,
                Width = iconSize,
                Height = iconSize,
                Stretch = Stretch.Uniform,
                Stroke = ResourceBrush("TextMutedBrush"),
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        tile.MouseLeftButtonDown += (_, e) =>
        {
            _selectedIconKey = option.Key;
            BuildIconGrid();
            e.Handled = true;
        };
        tile.MouseEnter += (_, _) =>
        {
            if (option.Key != _selectedIconKey)
                tile.Background = ResourceBrush("PanelSoftBrush");
        };
        tile.MouseLeave += (_, _) =>
        {
            if (option.Key != _selectedIconKey)
                tile.Background = ResourceBrush("PanelBrush");
        };
        return tile;
    }

    private Brush ResourceBrush(string key) =>
        TryFindResource(key) as Brush ?? Brushes.Gray;

    private void Complete()
    {
        DialogResult = true;
        Close();
    }

    private void ApplyBtn_Click(object sender, RoutedEventArgs e) => Complete();

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
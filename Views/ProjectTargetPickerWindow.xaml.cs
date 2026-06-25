using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DevezCode.Models;

namespace DevezCode.Views;

public partial class ProjectTargetPickerWindow : Window
{
    public event Action<string>? PaneSelected; // "A" or "B"

    public ProjectTargetPickerWindow()
    {
        InitializeComponent();
        Opacity = 0;
        Loaded += async (_, _) => await AnimateOpenAsync();
    }

    public void SetLabels(string paneAName, string paneBName)
    {
        PaneAText.Text = paneAName;
        PaneBText.Text = paneBName;
    }

    private async Task AnimateOpenAsync()
    {
        var dur = new Duration(TimeSpan.FromMilliseconds(200));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });

        await Task.Delay(50);
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        if (Card.RenderTransform is ScaleTransform scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        }
    }

    private void SelectPane(string pane)
    {
        PaneSelected?.Invoke(pane);
        Close();
    }

    private void PaneA_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SelectPane("A");
    }

    private void PaneB_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SelectPane("B");
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}

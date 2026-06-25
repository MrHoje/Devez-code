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
        Loaded += async (_, _) =>
        {
            MatchOwnerBounds();
            await AnimateOpenAsync();
        };
    }

    // 딤 오버레이가 메인 창 영역에만 깔리도록 소유자 창과 동일한 위치·크기로 맞춘다.
    // (크기를 지정하지 않으면 기본 크기로 떠서 오버레이가 프로그램 밖으로 넘친다.)
    private void MatchOwnerBounds()
    {
        if (Owner == null) return;
        var topLeft = Owner.PointToScreen(new Point(0, 0));
        var src = PresentationSource.FromVisual(Owner);
        var m = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var dip = m.Transform(topLeft); // 화면 픽셀 → DIP
        Left = dip.X;
        Top = dip.Y;
        Width = Owner.ActualWidth;
        Height = Owner.ActualHeight;
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

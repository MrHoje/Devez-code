using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DevezCode.Services;

namespace DevezCode.Views;

public partial class WakeSchedulerWindow : Window
{
    public bool Saved { get; private set; }

    public WakeSchedulerWindow(Window owner)
    {
        InitializeComponent();
        Owner = owner;

        var agents = AgentRegistry.GetEnabledAndInstalled()
            .Where(a => string.Equals(a.Id, "claude", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(a.Id, "codex", StringComparison.OrdinalIgnoreCase))
            .ToList();

        AgentList.ItemsSource = agents;
        EmptyHint.Visibility = agents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EditorPanel.IsEnabled = agents.Count > 0;
        if (agents.Count > 0) AgentList.SelectedIndex = 0;

        RootContent.SizeChanged += (_, _) => ApplyRoundedClip();
        Loaded += (_, _) =>
        {
            WindowCenter.CenterOverOwner(this);
            ApplyRoundedClip();
        };
    }

    private void AgentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        EditorPanel.DataContext = AgentList.SelectedItem as AgentDef;
        EditorPanel.IsEnabled = EditorPanel.DataContext != null;
    }

    // UI 구조 확인 단계: 새 저장 형식과 실제 전송 동작은 후속 작업에서 연결한다.
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Saved = false;
        DialogResult = true;
    }

    private void ApplyRoundedClip()
    {
        double width = RootContent.ActualWidth;
        double height = RootContent.ActualHeight;
        if (width <= 0 || height <= 0) return;
        RootContent.Clip = new RectangleGeometry(new Rect(0, 0, width, height), 13, 13);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}

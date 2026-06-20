using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>새 세션 추가 시 사용할 에이전트를 고르는 작은 다이얼로그.
/// 선택 가능한 에이전트가 1개면 호출부에서 피커를 띄우지 않고 그 에이전트를 바로 사용.</summary>
public partial class AgentPickerDialog : Window
{
    /// <summary>사용자가 선택한 에이전트 ID. 취소 시 null.</summary>
    public string? SelectedAgentId { get; private set; }

    private readonly IReadOnlyList<AgentDef> _agents;

    private AgentPickerDialog(IReadOnlyList<AgentDef> agents, string? projectPath)
    {
        InitializeComponent();
        _agents = agents;
        HintText.Text = projectPath != null
            ? $"‘{projectPath}’ 에서 실행할 에이전트를 선택하세요."
            : "실행할 에이전트를 선택하세요.";
        AgentList.ItemsSource = agents;
        // Loaded 후 첫 카드에 포커스 (Enter 로 즉시 선택 가능)
        Loaded += (_, _) =>
        {
            // Tag 가 설정된 (에이전트) Border 만 찾기 — 그림자/콘텐츠 border 는 제외
            var first = FindVisualChildren<Border>(this)
                .FirstOrDefault(b => b.Tag is string);
            first?.Focus();
        };
    }

    /// <summary>사용 가능한 에이전트 목록을 보여주고 선택을 받는다. 취소 시 null.</summary>
    public static string? Pick(Window owner, IReadOnlyList<AgentDef> agents, string? projectPath = null)
    {
        if (agents.Count == 0) return null;
        var dlg = new AgentPickerDialog(agents, projectPath) { Owner = owner };
        dlg.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                dlg.SelectedAgentId = null;
                dlg.DialogResult = false;
                e.Handled = true;
            }
        };
        return dlg.ShowDialog() == true ? dlg.SelectedAgentId : null;
    }

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is string id) Select(id);
    }

    private void Card_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Space) return;
        if (sender is Border b && b.Tag is string id)
        {
            Select(id);
            e.Handled = true;
        }
    }

    private void Select(string id)
    {
        SelectedAgentId = id;
        DialogResult = true;
        Close();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        SelectedAgentId = null;
        DialogResult = false;
        Close();
    }

    /// <summary>시각 트리에서 T 타입 자손을 모두 수집 (깊이 우선).</summary>
    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            foreach (var sub in FindVisualChildren<T>(child)) yield return sub;
        }
    }
}

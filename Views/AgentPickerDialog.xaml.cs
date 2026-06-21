using System.Collections.Generic;
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

    private AgentPickerDialog(IReadOnlyList<AgentDef> agents, string? projectPath)
    {
        InitializeComponent();
        HintText.Text = projectPath ?? "";
        if (string.IsNullOrEmpty(HintText.Text)) HintText.Visibility = Visibility.Collapsed;
        AgentList.ItemsSource = agents;
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

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string id)
        {
            SelectedAgentId = id;
            DialogResult = true;
            Close();
        }
    }
}

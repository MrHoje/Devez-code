using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>새 세션 추가 시 사용할 에이전트를 고르는 작은 다이얼로그.
/// 선택 가능한 에이전트가 1개면 호출부에서 피커를 띄우지 않고 그 에이전트를 바로 사용.
/// 단축키로 열면(keyboardMode) 맨 위 항목을 하이라이트하고 ↑/↓ 이동 + Enter 확정을 지원한다.</summary>
public partial class AgentPickerDialog : Window
{
    /// <summary>사용자가 선택한 에이전트 ID. 취소 시 null.</summary>
    public string? SelectedAgentId { get; private set; }

    private readonly IReadOnlyList<AgentDef> _agents;
    private readonly bool _keyboardMode;
    private List<RadioButton> _cards = new();
    private int _highlight = -1;

    private AgentPickerDialog(IReadOnlyList<AgentDef> agents, string? projectPath, bool keyboardMode)
    {
        InitializeComponent();
        _agents = agents;
        _keyboardMode = keyboardMode;
        HintText.Text = projectPath ?? "";
        if (string.IsNullOrEmpty(HintText.Text)) HintText.Visibility = Visibility.Collapsed;
        AgentList.ItemsSource = agents;
        if (keyboardMode) Loaded += OnLoadedKeyboard;
    }

    /// <summary>사용 가능한 에이전트 목록을 보여주고 선택을 받는다. 취소 시 null.</summary>
    /// <param name="keyboardMode">단축키로 열었는지. true 면 첫 항목 하이라이트 + ↑/↓ + Enter 네비게이션.</param>
    public static string? Pick(Window owner, IReadOnlyList<AgentDef> agents, string? projectPath = null, bool keyboardMode = false)
    {
        if (agents.Count == 0) return null;
        var dlg = new AgentPickerDialog(agents, projectPath, keyboardMode) { Owner = owner };
        dlg.PreviewKeyDown += dlg.OnPreviewKeyDown;
        return dlg.ShowDialog() == true ? dlg.SelectedAgentId : null;
    }

    /// <summary>키보드 모드 진입 — 카드 컨테이너를 수집하고 첫 항목을 하이라이트.</summary>
    private void OnLoadedKeyboard(object sender, RoutedEventArgs e)
    {
        _cards = FindVisualChildren<RadioButton>(AgentList).ToList();
        _highlight = _cards.Count > 0 ? 0 : -1;
        UpdateHighlight();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                SelectedAgentId = null;
                DialogResult = false;
                e.Handled = true;
                break;
            case Key.Down when _keyboardMode && _cards.Count > 0:
                _highlight = System.Math.Min(_highlight + 1, _cards.Count - 1);
                UpdateHighlight();
                e.Handled = true;
                break;
            case Key.Up when _keyboardMode && _cards.Count > 0:
                _highlight = System.Math.Max(_highlight - 1, 0);
                UpdateHighlight();
                e.Handled = true;
                break;
            case Key.Enter when _keyboardMode && _highlight >= 0 && _highlight < _agents.Count:
                Confirm(_agents[_highlight].Id);
                e.Handled = true;
                break;
        }
    }

    /// <summary>하이라이트 인덱스에 해당하는 카드에만 점선 큐(HiCue)를 표시.</summary>
    private void UpdateHighlight()
    {
        for (int i = 0; i < _cards.Count; i++)
        {
            if (_cards[i].Template?.FindName("HiCue", _cards[i]) is Rectangle cue)
                cue.Visibility = i == _highlight ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string id) Confirm(id);
    }

    private void Confirm(string id)
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

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in FindVisualChildren<T>(child)) yield return d;
        }
    }
}

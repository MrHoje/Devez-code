using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DevezCode.Models;

namespace DevezCode.Views;

/// <summary>프로젝트 카드 → "세션 관리자". 프로젝트의 모든 세션(숨김 포함)을 보여주고
/// 여러 세션을 골라 숨기기 / 닫기(추적 중단) / 삭제를 일괄 수행한다. 위험 동작은 호출측이
/// 넘긴 콜백(= MainWindow 의 검증된 일괄 메서드, 자체 확인창 포함)에 그대로 위임한다.</summary>
public partial class SessionManagerDialog : Window
{
    private readonly ProjectItem _project;
    private readonly Action<IReadOnlyList<SessionItem>> _onHide;
    private readonly Action<IReadOnlyList<SessionItem>> _onClose;
    private readonly Action<IReadOnlyList<SessionItem>> _onDelete;
    private readonly ObservableCollection<SessionManagerRow> _rows = new();
    private bool _syncingSelectAll;

    public SessionManagerDialog(
        ProjectItem project,
        Action<IReadOnlyList<SessionItem>> onHide,
        Action<IReadOnlyList<SessionItem>> onClose,
        Action<IReadOnlyList<SessionItem>> onDelete)
    {
        InitializeComponent();
        _project = project;
        _onHide = onHide;
        _onClose = onClose;
        _onDelete = onDelete;

        Title = $"세션 관리 — {project.Name}";
        ProjectNameText.Text = project.Name;
        SessionList.ItemsSource = _rows;

        BuildRows();
        _project.Sessions.CollectionChanged += Sessions_CollectionChanged;

        KeyDown += OnKeyDown;
        Loaded += (_, _) => { try { WindowCenter.CenterOverOwner(this); } catch { } };
        Closed += (_, _) => _project.Sessions.CollectionChanged -= Sessions_CollectionChanged;
    }

    /// <summary>행 목록을 프로젝트 세션에서 다시 구성한다(선택은 초기화). 세션 추가/삭제/닫기 후 호출.</summary>
    private void BuildRows()
    {
        foreach (var row in _rows) row.PropertyChanged -= Row_PropertyChanged;
        _rows.Clear();
        // 숨김 안 된 세션을 위로, 숨김 세션을 아래로(각 그룹 내 원래 순서 유지).
        foreach (var session in _project.Sessions.OrderBy(s => s.Hidden))
        {
            var row = new SessionManagerRow(session);
            row.PropertyChanged += Row_PropertyChanged;
            _rows.Add(row);
        }
        UpdateState();
    }

    private void Sessions_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Dispatcher.BeginInvoke(new Action(BuildRows));

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionManagerRow.IsChecked)) UpdateState();
    }

    /// <summary>선택 개수·카운트 라벨·버튼 활성·전체선택 체크 상태를 갱신한다.</summary>
    private void UpdateState()
    {
        int total = _rows.Count;
        int selectable = _rows.Count(r => r.Selectable);
        int selected = _rows.Count(r => r.Selectable && r.IsChecked);

        EmptyText.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = selected > 0 ? $"{selected}개 선택 / 전체 {total}" : $"전체 {total}";

        HideBtn.IsEnabled = CloseSessionsBtn.IsEnabled = DeleteBtn.IsEnabled = selected > 0;

        _syncingSelectAll = true;
        SelectAllBox.IsEnabled = selectable > 0;
        SelectAllBox.IsChecked = selectable > 0 && selected == selectable;
        _syncingSelectAll = false;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingSelectAll) return;
        bool check = SelectAllBox.IsChecked == true;
        foreach (var row in _rows.Where(r => r.Selectable)) row.IsChecked = check;
    }

    /// <summary>행 아무 곳이나 클릭하면 선택 토글(체크박스 자체 클릭은 중복 방지로 제외).</summary>
    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SessionManagerRow row }) return;
        if (!row.Selectable) return;
        if (IsWithinCheckBox(e.OriginalSource as DependencyObject)) return;
        row.IsChecked = !row.IsChecked;
    }

    private static bool IsWithinCheckBox(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is CheckBox) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private IReadOnlyList<SessionItem> SelectedSessions()
        => _rows.Where(r => r.Selectable && r.IsChecked).Select(r => r.Session).ToList();

    private void RunAction(Action<IReadOnlyList<SessionItem>> action)
    {
        var targets = SelectedSessions();
        if (targets.Count == 0) return;
        action(targets);   // 각 콜백이 자체 확인창·하위세션·잠금 검사 수행
        BuildRows();       // 컬렉션/숨김 상태 반영 + 선택 초기화
    }

    private void HideBtn_Click(object sender, RoutedEventArgs e) => RunAction(_onHide);
    private void CloseSessionsBtn_Click(object sender, RoutedEventArgs e) => RunAction(_onClose);
    private void DeleteBtn_Click(object sender, RoutedEventArgs e) => RunAction(_onDelete);

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DevezCode.Models;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>우측 패널 작업 큐 — 버블 입력(Enter) / 다중선택 / 선택삭제 / 영속화.</summary>
public partial class TaskQueueView : UserControl
{
    public ObservableCollection<TaskQueueItem> Items { get; } = new();

    public TaskQueueView()
    {
        InitializeComponent();
        DataContext = this;
        Items.CollectionChanged += (_, _) =>
        {
            UpdateCount();
            UpdateEmptyHint();
            Save();
            ScrollToBottom();
        };
        Load();
        UpdateCount();
        UpdateEmptyHint();
    }

    // ── 영속화 ───────────────────────────────────────────────────
    private void Load()
    {
        Items.Clear();
        var saved = SettingsService.LoadTaskQueueItems();
        foreach (var (text, sortOrder) in saved)
            Items.Add(new TaskQueueItem { Text = text, SortOrder = sortOrder });
    }

    private void Save()
    {
        SettingsService.SaveTaskQueueItems(Items.Select(i => (i.Text, i.SortOrder)));
    }

    private void UpdateCount()
    {
        CountText.Text = Items.Count.ToString();
    }

    private void UpdateEmptyHint()
    {
        EmptyHint.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScrollToBottom()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            BubbleScroller.ScrollToEnd();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    // ── 입력 처리 ────────────────────────────────────────────────
    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Enter 활성화 힌트: 텍스트가 있으면 약간 진해짐
        SendHint.Opacity = string.IsNullOrWhiteSpace(InputBox.Text) ? 0.4 : 0.9;
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            AddBubble();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            AddBubble();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            InputBox.Clear();
            e.Handled = true;
        }
    }

    private void AddBubble()
    {
        var text = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        var nextOrder = Items.Count == 0 ? 0 : Items.Max(i => i.SortOrder) + 1;
        Items.Add(new TaskQueueItem { Text = text, SortOrder = nextOrder });
        InputBox.Clear();
    }

    // ── 다중선택 / 삭제 ─────────────────────────────────────────
    /// <summary>우클릭 시: 미선택이면 이 항목만 선택 / 이미 선택(다중) 상태면 선택 유지.
     /// WPF ListBox 기본은 우클릭이 선택을 바꾸지 않아 ContextMenu 가 '선택 N개 삭제'처럼 오해 소지가 있어
     /// 이 핸들러에서 '우클릭한 항목이 선택되도록' 정규화한다. (doit 정합)</summary>
    private void BubbleItem_PreviewRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item && !item.IsSelected)
        {
            BubblesList.UnselectAll();
            item.IsSelected = true;
        }
    }

    /// <summary>컨텍스트 메뉴 열릴 때: 선택 수에 따라 메뉴 헤더를 '삭제' ↔ '선택 N개 삭제' 로 갱신.</summary>
    private void BubbleContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu cm && cm.Items[0] is MenuItem mi)
        {
            var count = BubblesList.SelectedItems.Count;
            mi.Header = count > 1 ? $"선택 {count}개 삭제" : "삭제";
        }
    }

    /// <summary>컨텍스트 메뉴의 '삭제' 클릭 — 현재 ListBox 선택 항목을 모두 제거.
     /// 선택이 비어있으면(예외 상황) 클릭한 항목 Tag 로 폴백.</summary>
    private void BubbleDelete_Click(object sender, RoutedEventArgs e)
    {
        if (BubblesList.SelectedItems.Count > 0)
        {
            // 스냅샷 후 제거 — Remove 가 SelectedItems 컬렉션을 변경하면 enumeration 오류 가능
            var toRemove = BubblesList.SelectedItems.Cast<TaskQueueItem>().ToList();
            foreach (var item in toRemove)
                Items.Remove(item);
        }
        else if (sender is MenuItem { DataContext: TaskQueueItem item })
        {
            Items.Remove(item);
        }
    }

    /// <summary>키보드 단축키:
     /// Delete = 선택 항목 삭제 / Escape = 선택 해제.
     /// Ctrl+A 는 ListBox SelectionMode=Extended 의 기본 구현(select all)이 자동 처리.</summary>
    private void BubblesList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && BubblesList.SelectedItems.Count > 0)
        {
            var toRemove = BubblesList.SelectedItems.Cast<TaskQueueItem>().ToList();
            foreach (var item in toRemove)
                Items.Remove(item);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && BubblesList.SelectedItems.Count > 0)
        {
            BubblesList.UnselectAll();
            e.Handled = true;
        }
    }

    /// <summary>외부에서 큐를 비울 때(필요 시).</summary>
    public void ClearAll()
    {
        Items.Clear();
    }
}

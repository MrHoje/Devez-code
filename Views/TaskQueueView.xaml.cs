using System.Collections.ObjectModel;
using System.Collections.Specialized;
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

        // 컬렉션 변경 → 카운트/빈안내/저장/스크롤
        // (인스턴스 수명 동안 1회만 등록 — FileExplorerView 안에서 재사용되므로 Unloaded 시 해제하지 않음)
        Items.CollectionChanged += Items_CollectionChanged;
        Load();
        UpdateCount();
        UpdateEmptyHint();

        // 뷰가 처음 화면에 표시될 때 입력창 자동 포커스 (doit 정합: 입력 대기 상태로 시작)
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() => InputBox.Focus()),
            System.Windows.Threading.DispatcherPriority.Input);

        // 탭 전환(Collapsed↔Visible)으로 다시 나타날 때도 입력 포커스 복원.
        // 우측 패널 안에서 FileExplorerView 의 SwitchTab 으로 보였다 숨었다 하는 경우 대응.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                Dispatcher.BeginInvoke(new Action(() => InputBox.Focus()),
                    System.Windows.Threading.DispatcherPriority.Input);
        };
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
            // 사용자가 이미 하단에 있을 때만 자동 스크롤 (위로 스크롤해서 보고 있을 때는 방해 금지)
            var sv = FindScrollViewer(BubblesList);
            if (sv != null)
            {
                var atBottom = sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 1.0;
                if (!atBottom) return;
            }
            var last = LastItem();
            if (last != null) BubblesList.ScrollIntoView(last);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var result = FindScrollViewer(child);
            if (result != null) return result;
        }
        return null;
    }

    private object? LastItem() => Items.Count == 0 ? null : Items[^1];

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateCount();
        UpdateEmptyHint();
        Save();
        ScrollToBottom();
    }

    // ── 입력 처리 ────────────────────────────────────────────────
    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // placeholder 갱신은 Style.DataTrigger 가 자동 처리
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter (모디파이어 없음): 버블 추가.
        // Shift+Enter: TextBox.AcceptsReturn=True 가 줄바꿈을 처리 (e.Handled 안 함).
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
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
    /// WPF ListBox 기본은 우클릭이 선택을 바꾸지 않아 컨텍스트 메뉴가 '선택 N개 삭제'처럼
    /// 오해 소지가 있어 이 핸들러에서 '우클릭한 항목이 선택되도록' 정규화한다 (doit 정합).</summary>
    private void BubbleItem_PreviewRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item && !item.IsSelected)
        {
            BubblesList.UnselectAll();
            item.IsSelected = true;
        }
    }

    /// <summary>컨텍스트 메뉴의 '삭제' 클릭 — 현재 ListBox 선택 항목을 모두 제거.
    /// 선택이 비어있으면(예외 상황) 클릭한 항목의 DataContext 로 폴백.</summary>
    private void BubbleDelete_Click(object sender, RoutedEventArgs e)
    {
        if (BubblesList.SelectedItems.Count > 0)
        {
            var toRemove = BubblesList.SelectedItems.Cast<TaskQueueItem>().ToList();
            foreach (var item in toRemove)
                Items.Remove(item);
        }
        else if (sender is MenuItem { DataContext: TaskQueueItem item })
        {
            Items.Remove(item);
        }
    }

    /// <summary>컨텍스트 메뉴의 '복사' 클릭 — 선택 항목(또는 우클릭 항목)의 텍스트를 클립보드로 복사.</summary>
    private void BubbleCopy_Click(object sender, RoutedEventArgs e)
    {
        var texts = (BubblesList.SelectedItems.Count > 0
            ? BubblesList.SelectedItems.Cast<TaskQueueItem>()
            : (sender is MenuItem { DataContext: TaskQueueItem single } ? new[] { single } : Array.Empty<TaskQueueItem>())
          ).Select(i => i.Text).ToList();
        if (texts.Count == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, texts)); } catch { /* 점유 무시 */ }
    }

    /// <summary>컨텍스트 메뉴 열릴 때: 선택 수에 따라 메뉴 헤더를 '복사/삭제' ↔ '선택 N개 복사/삭제' 로 갱신.
    /// XAML 의 ContextMenu 자식 순서: [0]=복사, [1]=Separator, [2]=삭제. (Setter 내부 x:Name 사용 불가 회피)</summary>
    private void BubbleContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;
        var count = BubblesList.SelectedItems.Count;
        if (cm.Items.Count > 0 && cm.Items[0] is MenuItem copy)  // [0] 복사
            copy.Header = count > 1 ? $"선택 {count}개 복사" : "복사";
        if (cm.Items.Count > 2 && cm.Items[2] is MenuItem del)   // [1] Separator, [2] 삭제
            del.Header = count > 1 ? $"선택 {count}개 삭제" : "삭제";
    }

    /// <summary>키보드 단축키: Delete=선택삭제, Esc=선택해제. Ctrl+A 는 ListBox SelectionMode=Extended 기본 처리.</summary>
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

    /// <summary>빈 영역(아이템이 아닌 곳) 클릭 시 입력창으로 포커스 이동.
    /// doit 정합: 마우스가 어디에 있든 Enter 로 즉시 입력 가능하도록.</summary>
    private void BubblesList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d)
        {
            // 클릭 소스가 ListBoxItem(또는 그 자식)인지 확인 → 아니면(=빈 영역) 입력 포커스
            for (var cur = d; cur != null; cur = System.Windows.Media.VisualTreeHelper.GetParent(cur))
            {
                if (cur is ListBoxItem) return;
                if (cur == BubblesList) break;
            }
        }
        Dispatcher.BeginInvoke(new Action(() => InputBox.Focus()),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>외부에서 큐를 비울 때(필요 시).</summary>
    public void ClearAll()
    {
        Items.Clear();
    }
}

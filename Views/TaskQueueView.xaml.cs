using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DevezCode.Models;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>우측 패널 작업 큐 — 버블 입력(Enter) / 호버 X 삭제 / 영속화.</summary>
public partial class TaskQueueView : UserControl
{
    public ObservableCollection<TaskQueueItem> Items { get; } = new();

    public TaskQueueView()
    {
        InitializeComponent();
        BubblesHost.ItemsSource = Items;
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
            // Enter: 버블 추가 (단일 라인 모드 — Shift+Enter는 줄바꿈으로 둘 수도 있지만
            // 한 줄 작업 위주이므로 Shift+Enter도 그냥 추가 처리).
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

    // ── 버블 삭제 ────────────────────────────────────────────────
    private void BubbleDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TaskQueueItem item })
        {
            Items.Remove(item);
        }
    }

    /// <summary>외부에서 큐를 비울 때(필요 시).</summary>
    public void ClearAll()
    {
        Items.Clear();
    }
}

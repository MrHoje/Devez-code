using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DevezCode.Models;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>우측 패널 작업 큐 — doit MemoThreadView 의 task queue 슬림 포팅.
/// 원본 차용: 우클릭 → 선택모드 진입 / 러버밴드 다중선택 / SelectCheck + SelectionRing / 상단 액션바.
/// 원본 제외: 태그/핀/별/코드/시트/할일/타이머/첨부/URL/링크프리뷰/댓글/공유/일정등록/편집/AI채팅.
/// 영속화: 프로젝트 경로별(settings.json Dictionary).</summary>
public partial class TaskQueueView : UserControl, INotifyPropertyChanged
{
    // ── INotifyPropertyChanged: DataContext=this 이므로 XAML DataTrigger(IsSelectionMode)가
    //    갱신되려면 UserControl 자신이 INPC 를 구현해 알림을 쏴줘야 한다. (devez 는 ViewModel) ──
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public ObservableCollection<TaskQueueItem> Items { get; } = new();

    public int Count => Items.Count;
    public event Action? CountChanged;

    /// <summary>현재 큐가 바인딩된 프로젝트 경로. null/empty 면 전역 큐.</summary>
    private string? _projectPath;
    public string? ProjectPath
    {
        get => _projectPath;
        set { if (_projectPath == value) return; _projectPath = value; Reload(); }
    }

    // ── 선택 모드 (debit IsSelectionMode 패턴) ──
    private bool _isSelectionMode;
    public bool IsSelectionMode
    {
        get => _isSelectionMode;
        set
        {
            if (_isSelectionMode == value) return;
            _isSelectionMode = value;
            OnPropertyChanged();   // XAML DataTrigger(체크박스 노출 + 버블 좌측 이동) 발화
            OnIsSelectionModeChanged(value);
        }
    }

    /// <summary>선택 모드 해제 시 모든 선택 초기화. (debit MainViewModel.OnIsSelectionModeChanged 동일)</summary>
    private void OnIsSelectionModeChanged(bool value)
    {
        if (!value)
        {
            foreach (var it in Items) it.IsSelected = false;
            _rightClickHighlightedItem = null;
            UpdateSelectionActionBar();
        }
    }

    private void UpdateSelectionActionBar()
    {
        var n = CountSelected();
        SelectionActionBar.Visibility = _isSelectionMode ? Visibility.Visible : Visibility.Collapsed;
        SelectionCountText.Text = $"{n}개 선택";
    }

    private int CountSelected() => Items.Count(i => i.IsSelected);

    // ── 우클릭 하이라이트 (debit 우클릭 직후 한 버블 강조용) ──
    private TaskQueueItem? _rightClickHighlightedItem;

    // ── Shift 범위 선택용 anchor ──
    private string? _selectionAnchorItemId;

    // ── 러버밴드 드래그 상태 ──
    private bool _rubberActive;
    private Point? _rubberPendingOrigin;
    private Point _rubberLastPoint;
    private double _rubberOriginVOffset;
    private int _rubberTopIdx = -1;
    private int _rubberBottomIdx = -1;
    private bool _rubberAdditiveSelection;
    private HashSet<string>? _rubberSelectionBase;

    public TaskQueueView()
    {
        InitializeComponent();
        DataContext = this;

        Items.CollectionChanged += Items_CollectionChanged;
        Reload();
        CountChanged?.Invoke();

        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() => InputBox.Focus()),
            System.Windows.Threading.DispatcherPriority.Input);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                Dispatcher.BeginInvoke(new Action(() => InputBox.Focus()),
                    System.Windows.Threading.DispatcherPriority.Input);
        };
    }

    private void TaskQueueView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateSelectionActionBar();
    }

    // ── 영속화 ──
    private void Reload()
    {
        Items.Clear();
        var saved = SettingsService.LoadTaskQueueItems(_projectPath);
        foreach (var (text, sortOrder) in saved)
            Items.Add(new TaskQueueItem { Text = text, SortOrder = sortOrder });
        IsSelectionMode = false;
        UpdateSelectionActionBar();
    }

    private void Save()
    {
        SettingsService.SaveTaskQueueItems(_projectPath, Items.Select(i => (i.Text, i.SortOrder)));
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyHint();
        Save();
        ScrollToBottom();
        CountChanged?.Invoke();
        UpdateSelectionActionBar();
    }

    private void UpdateEmptyHint()
    {
        EmptyHint.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScrollToBottom()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var sv = FindScrollViewer(BubblesList);
            if (sv != null)
            {
                var atBottom = sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 1.0;
                if (!atBottom) return;
            }
            if (Items.Count > 0) BubblesList.ScrollIntoView(Items[^1]);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var r = FindScrollViewer(child);
            if (r != null) return r;
        }
        return null;
    }

    // ── 입력 ──
    private void InputBox_TextChanged(object sender, TextChangedEventArgs e) { }

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
            if (_isSelectionMode) { IsSelectionMode = false; e.Handled = true; return; }
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

    // ── 버블 클릭 (debit Bubble_LeftClick / Bubble_RightClick 슬림) ──
    /// <summary>버블 좌클릭 Preview: 드래그(러버밴드 후보) 준비. 좌표만 저장, 토글은 MouseDown 에서.</summary>
    private void Bubble_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not TaskQueueItem item) return;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0) return;
        // 마우스 다운 시점에 좌표만 저장. MouseDown 으로 가서 토글 처리.
    }

    private void Bubble_LeftClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not TaskQueueItem item) return;

        // 직전 우클릭 하이라이트 정리
        ClearPreviousRightClickHighlight(item);

        // 클릭 직후 우클릭 하이라이트를 켜서 (선택 모드 진입 전) 한 번 강조
        if (!item.IsSelected)
        {
            if (_rightClickHighlightedItem != null)
                _rightClickHighlightedItem.IsRightClickHighlighted = false;
            item.IsRightClickHighlighted = true;
            _rightClickHighlightedItem = item;
        }

        int clickedIndex = Items.IndexOf(item);
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        bool ctrl  = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        if (shift)
        {
            ApplyShiftRangeSelection(item, clickedIndex);
            e.Handled = true;
            return;
        }
        if (ctrl)
        {
            if (!_isSelectionMode)
            {
                IsSelectionMode = true;
                if (_rightClickHighlightedItem != null)
                {
                    _rightClickHighlightedItem.IsRightClickHighlighted = false;
                    _rightClickHighlightedItem.IsSelected = true;
                }
                item.IsSelected = true;
            }
            else
            {
                item.IsSelected = !item.IsSelected;
            }
            item.IsRightClickHighlighted = false;
            _rightClickHighlightedItem = null;
            _selectionAnchorItemId = item.Id;
            UpdateSelectionActionBar();
            e.Handled = true;
            return;
        }
        if (_isSelectionMode)
        {
            // 선택 모드: 클릭으로 토글
            item.IsSelected = !item.IsSelected;
            _selectionAnchorItemId = item.Id;
            UpdateSelectionActionBar();
            e.Handled = true;
            return;
        }
        // 일반 모드: 그냥 하이라이트만 (debit 우클릭 전 단계와 동일)
        _selectionAnchorItemId = item.Id;
        e.Handled = true;
    }

    private void Bubble_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not TaskQueueItem item) return;
        ClearPreviousRightClickHighlight(item);

        // 선택 모드 + 미선택 버블 우클릭 → 기존 선택 전체 해제, 이 버블만 선택
        if (_isSelectionMode && !item.IsSelected)
        {
            foreach (var it in Items) { it.IsSelected = false; it.IsRightClickHighlighted = false; }
            _rightClickHighlightedItem = null;
            item.IsSelected = true;
        }
        UpdateSelectionActionBar();
        // ContextMenu 는 Setter 로 자동 부착됨
    }

    private void ClearPreviousRightClickHighlight(TaskQueueItem current)
    {
        if (_rightClickHighlightedItem == null) return;
        if (ReferenceEquals(_rightClickHighlightedItem, current)) return;
        _rightClickHighlightedItem.IsRightClickHighlighted = false;
        _rightClickHighlightedItem = null;
    }

    private void ApplyShiftRangeSelection(TaskQueueItem clicked, int clickedIndex)
    {
        if (clickedIndex < 0) return;
        int anchorIndex = -1;
        if (!string.IsNullOrEmpty(_selectionAnchorItemId))
            anchorIndex = FindIndexById(_selectionAnchorItemId);
        if (anchorIndex < 0)
        {
            var selectedIdx = Enumerable.Range(0, Items.Count).FirstOrDefault(i => Items[i].IsSelected);
            anchorIndex = selectedIdx >= 0 ? selectedIdx : 0;
            if (selectedIdx == 0 && Items.Count > 0 && !Items[0].IsSelected) anchorIndex = clickedIndex;
        }
        int start = Math.Min(anchorIndex, clickedIndex);
        int end = Math.Max(anchorIndex, clickedIndex);
        bool any = false;
        for (int i = 0; i < Items.Count; i++)
        {
            var sel = i >= start && i <= end;
            if (Items[i].IsSelected != sel) Items[i].IsSelected = sel;
            any |= sel;
        }
        IsSelectionMode = any;
        clicked.IsRightClickHighlighted = true;
        _rightClickHighlightedItem = clicked;
        UpdateSelectionActionBar();
    }

    private int FindIndexById(string? id)
    {
        if (string.IsNullOrEmpty(id)) return -1;
        for (int i = 0; i < Items.Count; i++) if (Items[i].Id == id) return i;
        return -1;
    }

    // ── 빈 영역 클릭 (debit Root_PreviewMouseLeftButtonDown / 빈영역 러버밴드) ──
    private void Root_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 러버밴드 시작: 클릭이 버블 위가 아니라면(=빈 영역)
        if (e.OriginalSource is DependencyObject src && IsRubberBandableHit(src))
        {
            _rubberPendingOrigin = e.GetPosition(BubblesList);
            var sv = FindScrollViewer(BubblesList);
            _rubberOriginVOffset = sv?.VerticalOffset ?? 0;
            _rubberAdditiveSelection = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            _rubberSelectionBase = _rubberAdditiveSelection
                ? Items.Where(i => i.IsSelected).Select(i => i.Id).ToHashSet()
                : null;
            // 선택 모드 진입은 마우스 무브에서 (아직 안 움직이면 진짜 빈 영역 클릭일 수도)
            return;
        }
        // 버블 위 클릭이면 핸들러가 이미 처리함
    }

    private void Root_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_rubberActive)
        {
            UpdateRubberBand(e.GetPosition(BubblesList));
            e.Handled = true;
        }
        else if (_rubberPendingOrigin is Point origin && e.LeftButton == MouseButtonState.Pressed)
        {
            // 일정 거리 이상 움직였을 때 러버밴드 시작 (드래그 의도 확정)
            var cur = e.GetPosition(BubblesList);
            if (Math.Abs(cur.X - origin.X) > 4 || Math.Abs(cur.Y - origin.Y) > 4)
            {
                // devez StartRubberBand 정합: 캔버스 노출 + 인덱스 리셋 + 마우스 캡처 후 갱신.
                _rubberActive = true;
                _rubberTopIdx = -1;
                _rubberBottomIdx = -1;
                RubberBandCanvas.Visibility = Visibility.Visible;
                Mouse.Capture(this, CaptureMode.SubTree);
                UpdateRubberBand(cur);
            }
        }
    }

    private void Root_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_rubberActive)
        {
            EndRubberBand();
            e.Handled = true;
        }
        else if (_rubberPendingOrigin is Point origin)
        {
            // 드래그 없이 마우스 업 = 진짜 빈 영역 클릭. 선택 모드면 해제, 아니면 입력 포커스.
            _rubberPendingOrigin = null;
            if (_isSelectionMode && CountSelected() == 0)
            {
                IsSelectionMode = false;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => InputBox.Focus()),
                    System.Windows.Threading.DispatcherPriority.Input);
            }
        }
    }

    private bool IsRubberBandableHit(DependencyObject src)
    {
        while (src != null && !ReferenceEquals(src, BubblesList))
        {
            if (src is FrameworkElement fe && (fe.Name == "BubbleBorder" || fe.Name == "BubbleInner")) return false;
            if (src is System.Windows.Controls.Primitives.ScrollBar) return false;
            src = VisualTreeHelper.GetParent(src);
        }
        return src != null;
    }

    private void UpdateRubberBand(Point current)
    {
        if (_rubberPendingOrigin is not Point origin) return;
        _rubberLastPoint = current;

        double scrolled = (FindScrollViewer(BubblesList)?.VerticalOffset ?? 0) - _rubberOriginVOffset;
        var rect = new Rect(new Point(origin.X, origin.Y - scrolled), current);

        var visual = Rect.Intersect(rect, new Rect(0, 0, BubblesList.ActualWidth, BubblesList.ActualHeight));
        if (visual.IsEmpty)
        {
            RubberBandRect.Width = 0;
            RubberBandRect.Height = 0;
        }
        else
        {
            Canvas.SetLeft(RubberBandRect, visual.X);
            Canvas.SetTop(RubberBandRect, visual.Y);
            RubberBandRect.Width = visual.Width;
            RubberBandRect.Height = visual.Height;
        }

        int minHit = -1, maxHit = -1;
        for (int i = 0; i < Items.Count; i++)
        {
            if (BubblesList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container) continue;
            var bubble = FindBubbleBorder(container);
            if (bubble == null || bubble.ActualWidth <= 0) continue;
            Rect bounds;
            try
            {
                bounds = bubble.TransformToAncestor(BubblesList)
                    .TransformBounds(new Rect(0, 0, bubble.ActualWidth, bubble.ActualHeight));
            }
            catch { continue; }
            if (!rect.IntersectsWith(bounds)) continue;
            if (minHit < 0) minHit = i;
            maxHit = i;
        }

        bool aboveViewport = rect.Top < 0;
        bool belowViewport = rect.Bottom > BubblesList.ActualHeight;
        if (minHit >= 0)
        {
            _rubberTopIdx = aboveViewport && _rubberTopIdx >= 0 ? Math.Min(_rubberTopIdx, minHit) : minHit;
            _rubberBottomIdx = belowViewport && _rubberBottomIdx >= 0 ? Math.Max(_rubberBottomIdx, maxHit) : maxHit;
        }
        else if (!aboveViewport && !belowViewport)
        {
            _rubberTopIdx = _rubberBottomIdx = -1;
        }

        bool any = false;
        for (int i = 0; i < Items.Count; i++)
        {
            bool hit = _rubberTopIdx >= 0 && i >= _rubberTopIdx && i <= _rubberBottomIdx;
            bool sel = _rubberAdditiveSelection
                ? (_rubberSelectionBase?.Contains(Items[i].Id) ?? false) || hit
                : hit;
            if (Items[i].IsSelected != sel) Items[i].IsSelected = sel;
            if (sel) any = true;
        }
        if (any && !_isSelectionMode) IsSelectionMode = true;
        UpdateSelectionActionBar();
    }

    private void EndRubberBand()
    {
        _rubberActive = false;
        _rubberPendingOrigin = null;
        _rubberSelectionBase = null;
        if (ReferenceEquals(Mouse.Captured, this)) Mouse.Capture(null);
        RubberBandCanvas.Visibility = Visibility.Collapsed;
        RubberBandRect.Width = 0;
        RubberBandRect.Height = 0;
        if (CountSelected() == 0) IsSelectionMode = false;
    }

    private static Border? FindBubbleBorder(DependencyObject? from)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(from); i++)
        {
            var child = VisualTreeHelper.GetChild(from, i);
            if (child is Border b && b.Name == "BubbleBorder") return b;
            var r = FindBubbleBorder(child);
            if (r != null) return r;
        }
        return null;
    }

    // ── 키보드 (debit Root_PreviewKeyDown 슬림) ──
    private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // InputBox 에 포커스 있으면 거기서 처리. 중복 발화 방지.
        if (InputBox.IsKeyboardFocusWithin) return;

        if (e.Key == Key.Escape && _isSelectionMode)
        {
            IsSelectionMode = false;
            e.Handled = true;
            return;
        }
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (Items.Count == 0) return;
            foreach (var it in Items) it.IsSelected = true;
            IsSelectionMode = true;
            UpdateSelectionActionBar();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete && _isSelectionMode && CountSelected() > 0)
        {
            DeleteSelectedInternal();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && _isSelectionMode)
        {
            CopySelected();
            e.Handled = true;
            return;
        }
    }

    private void BubblesList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _isSelectionMode)
        {
            IsSelectionMode = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && CountSelected() > 0)
        {
            DeleteSelectedInternal();
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CopySelected();
            e.Handled = true;
        }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            foreach (var it in Items) it.IsSelected = true;
            IsSelectionMode = true;
            UpdateSelectionActionBar();
            e.Handled = true;
        }
    }

    // ── 액션바 / 컨텍스트 메뉴 핸들러 ──
    private void CancelSelection_Click(object sender, RoutedEventArgs e)
    {
        IsSelectionMode = false;
    }

    private void DeleteSelectedBtn_Click(object sender, RoutedEventArgs e) { }
    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        DeleteSelectedInternal();
    }

    private void DeleteSelectedInternal()
    {
        var toRemove = Items.Where(i => i.IsSelected).ToList();
        foreach (var it in toRemove) Items.Remove(it);
        IsSelectionMode = false;
    }

    private void BubbleDelete_Click(object sender, RoutedEventArgs e)
    {
        if (CountSelected() > 0)
        {
            DeleteSelectedInternal();
        }
        else if (sender is MenuItem { DataContext: TaskQueueItem item })
        {
            Items.Remove(item);
        }
    }

    private void BubbleCopy_Click(object sender, RoutedEventArgs e)
    {
        if (CountSelected() > 0) { CopySelected(); return; }
        if (sender is MenuItem { DataContext: TaskQueueItem item })
        {
            try { Clipboard.SetText(item.Text); } catch { }
        }
    }

    private void CopySelected()
    {
        var text = string.Join(Environment.NewLine, Items.Where(i => i.IsSelected).Select(i => i.Text));
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); } catch { }
    }

    /// <summary>컨텍스트 메뉴 열릴 때: 선택 수에 따라 메뉴 헤더를 '복사/삭제' ↔ '선택 N개 복사/삭제' 로 갱신.</summary>
    private void BubbleContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;
        var count = CountSelected();
        if (cm.Items.Count > 0 && cm.Items[0] is MenuItem copy)
            copy.Header = count > 1 ? $"선택 {count}개 복사" : "복사";
        if (cm.Items.Count > 2 && cm.Items[2] is MenuItem del)
            del.Header = count > 1 ? $"선택 {count}개 삭제" : "삭제";
    }

    /// <summary>외부 초기화 (필요 시).</summary>
    public void ClearAll() => Items.Clear();
}

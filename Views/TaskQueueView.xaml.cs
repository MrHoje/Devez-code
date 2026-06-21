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

    // ── 버블/입력 글꼴 크기 (Ctrl+휠, devez BubbleFontSize 정합 10~28) ──
    private double _bubbleFontSize = SettingsService.LoadTaskQueueBubbleFontSize();
    public double BubbleFontSize
    {
        get => _bubbleFontSize;
        set
        {
            var v = Math.Clamp(value, 10, 28);
            if (Math.Abs(_bubbleFontSize - v) < 0.01) return;
            _bubbleFontSize = v;
            OnPropertyChanged();
            SettingsService.SaveTaskQueueBubbleFontSize(v);
            ApplyDefaultInputHeightIfNotCustom(); // 사용자 지정 높이 없으면 2줄 기본 재계산
        }
    }

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
        ClearActionTarget(); // 선택 모드 진입/해제 시 '작업지시' 버튼 숨김
        if (!value)
        {
            foreach (var it in Items) it.IsSelected = false;
            _rightClickHighlightedItem = null;
            UpdateSelectionActionBar();
        }
    }

    private void UpdateSelectionActionBar()
    {
        // 선택 액션바('N개 선택' + 삭제)는 항상 숨김. 삭제는 컨텍스트 메뉴/Delete 키로만.
        SelectionActionBar.Visibility = Visibility.Collapsed;
    }

    private int CountSelected() => Items.Count(i => i.IsSelected);

    // ── 우클릭 하이라이트 (debit 우클릭 직후 한 버블 강조용) ──
    private TaskQueueItem? _rightClickHighlightedItem;

    // ── Shift 범위 선택용 anchor ──
    private string? _selectionAnchorItemId;

    // ── 선택 토글/우클릭 메뉴 (devez _pendingSelectionToggle / _lastContextMenuCloseTime) ──
    private TaskQueueItem? _pendingSelectionToggle;   // 마우스 업에서 적용할 토글(드래그와 충돌 방지)
    private TaskQueueItem? _contextMenuItem;           // 우클릭한 버블(메뉴 동작 대상)
    private DateTime _lastContextMenuCloseTime;        // 메뉴 닫은 직후 가짜 드래그/해제 차단(300ms)

    // ── 버블 드래그 병합 (devez 크로스윈도우 고스트의 패널 내 경량 버전) ──
    private TaskQueueItem? _pendingBubbleDragItem;     // 드래그 후보(임계 초과 시 시작)
    private Point _bubbleDragOrigin;                   // this 기준 시작 좌표
    private TaskQueueItem? _draggingBubbleItem;        // 드래그 중인 버블
    private bool _bubbleDragging;
    private TaskQueueItem? _mergeTargetItem;           // 현재 호버 중인 병합 도착지

    // ── '작업지시' 버튼 노출 대상(단일 클릭, 비선택모드) ──
    private TaskQueueItem? _actionTargetItem;

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

        Loaded += (_, _) =>
        {
            RestoreInputMinHeight();
            Dispatcher.BeginInvoke(new Action(() => InputBox.Focus()),
                System.Windows.Threading.DispatcherPriority.Input);
        };
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
    /// <summary>로드 중 플래그. Reload 의 Clear/Add 가 CollectionChanged→Save 를 유발해
    /// "빈 컬렉션 저장 → 프로젝트 큐 키 삭제 → 직후 로드 시 빈 데이터"로 저장본을 지우던 버그 차단.</summary>
    private bool _loading;

    private void Reload()
    {
        _actionTargetItem = null; // 프로젝트 전환 시 '작업지시' 버튼 대상 초기화
        _loading = true;
        Items.Clear();
        var saved = SettingsService.LoadTaskQueueItems(_projectPath);
        foreach (var (text, sortOrder) in saved)
            Items.Add(new TaskQueueItem { Text = text, SortOrder = sortOrder });
        _loading = false;

        IsSelectionMode = false;
        UpdateEmptyHint();
        UpdateSelectionActionBar();
        CountChanged?.Invoke();
    }

    private void Save()
    {
        if (_loading) return;
        SettingsService.SaveTaskQueueItems(_projectPath, Items.Select(i => (i.Text, i.SortOrder)));
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_loading) return;
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

    private const double InputMaxHeight = 180;

    /// <summary>기본(최소) 입력 높이 = 1줄(devez "min" 공식 fs*1.45+8, fs14 → 28).
    /// Ctrl+휠로 글꼴이 바뀌면 함께 변한다.</summary>
    private double NaturalInputHeight() => Math.Round(_bubbleFontSize * 1.45 + 8);

    /// <summary>Ctrl+휠 → 버블/입력 글꼴 크기 조절(10~28). devez MsgScroll_PreviewMouseWheel 정합.</summary>
    private void Root_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        BubbleFontSize += e.Delta > 0 ? 1 : -1;
    }

    /// <summary>입력창 상단 핸들 드래그 → 높이 조절(1줄~180), 로컬 저장. (devez InputResizeThumb 정합)</summary>
    private void InputResizeThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        var floor = NaturalInputHeight();
        var current = InputBox.MinHeight > 0 ? InputBox.MinHeight : floor;
        var newMin = Math.Clamp(current - e.VerticalChange, floor, InputMaxHeight);
        InputBox.MinHeight = newMin;
        // 기본(2줄)까지 줄이면 사용자 지정 해제(0 저장).
        SettingsService.SaveTaskQueueInputMinHeight(Math.Abs(newMin - floor) < 0.5 ? 0 : newMin);
    }

    private void RestoreInputMinHeight()
    {
        var floor = NaturalInputHeight();
        var saved = SettingsService.LoadTaskQueueInputMinHeight();
        InputBox.MinHeight = saved <= 0 ? floor : Math.Clamp(saved, floor, InputMaxHeight);
    }

    private void ApplyDefaultInputHeightIfNotCustom()
    {
        if (InputBox == null) return;
        if (SettingsService.LoadTaskQueueInputMinHeight() <= 0)
            InputBox.MinHeight = NaturalInputHeight();
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
    /// <summary>버블 좌클릭 Preview: 드래그(병합) 후보 준비. (devez DragHandle_PreviewMouseDown)
    /// Ctrl/Shift 는 선택 전용이라 드래그 안 함. 메뉴 닫은 직후 300ms 도 가짜 드래그 차단.</summary>
    private void Bubble_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not TaskQueueItem item) return;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0) return;
        if ((DateTime.Now - _lastContextMenuCloseTime).TotalMilliseconds < 300) return;
        _pendingBubbleDragItem = item;
        _bubbleDragOrigin = e.GetPosition(this);
    }

    /// <summary>버블 클릭 시 입력창에서 키보드 포커스를 빼 루트로 옮긴다.
    /// → 입력창에 텍스트가 있어도 Delete 가 (텍스트 편집이 아니라) 버블 삭제로 동작.
    /// 키 이벤트는 PreviewKeyDown(루트 터널링)으로 계속 Root_PreviewKeyDown 에 도달.</summary>
    private void DropInputFocus()
    {
        if (!InputBox.IsKeyboardFocusWithin) return;
        Focusable = true;
        Keyboard.Focus(this);
    }

    private void Bubble_LeftClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not TaskQueueItem item) return;
        DropInputFocus();

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
            // 선택 모드: 토글은 마우스 업으로 지연(드래그 시작 시 무효화 → 드래그/토글 충돌 방지).
            _pendingSelectionToggle = item;
            _selectionAnchorItemId = item.Id;
            e.Handled = true;
            return;
        }
        // 일반 모드: 하이라이트 + '작업지시' 버튼 토글(같은 버블 재클릭 → 숨김)
        _selectionAnchorItemId = item.Id;
        SetActionTarget(ReferenceEquals(_actionTargetItem, item) ? null : item);
        e.Handled = true;
    }

    /// <summary>'작업지시' 버튼 노출 대상 지정(이전 대상 해제). null 이면 모두 숨김.</summary>
    private void SetActionTarget(TaskQueueItem? item)
    {
        if (ReferenceEquals(_actionTargetItem, item)) return;
        if (_actionTargetItem != null) _actionTargetItem.IsActionTarget = false;
        _actionTargetItem = item;
        if (_actionTargetItem != null) _actionTargetItem.IsActionTarget = true;
    }

    private void ClearActionTarget() => SetActionTarget(null);

    /// <summary>'작업지시' 클릭 → 현재 활성 탭 세션에 이 버블 텍스트 입력+전송 후 큐에서 제거.</summary>
    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not TaskQueueItem item) return;
        var text = item.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        var ok = (Application.Current.MainWindow as DevezCode.MainWindow)?.SendTextToActiveSession(text) ?? false;
        if (!ok)
            ConfirmDialog.Alert("세션 없음", "현재 활성화된 세션이 없습니다.\n세션 탭을 먼저 선택하세요.");
        else
        {
            ClearActionTarget();
            Items.Remove(item);
        }
    }

    private void Bubble_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not TaskQueueItem item) return;
        DropInputFocus();
        ClearActionTarget();
        _contextMenuItem = item;
        // 우클릭은 드래그 후보가 아님(메뉴와 충돌 방지).
        _pendingBubbleDragItem = null;
        ClearPreviousRightClickHighlight(item);

        // 선택 모드 + 미선택 버블 우클릭 → 기존 선택 전체 해제, 이 버블만 선택
        if (_isSelectionMode && !item.IsSelected)
        {
            foreach (var it in Items) { it.IsSelected = false; it.IsRightClickHighlighted = false; }
            _rightClickHighlightedItem = null;
            item.IsSelected = true;
        }
        else if (!item.IsSelected)
        {
            // 일반 모드: 우클릭 버블만 잠깐 강조(메뉴 닫힐 때 해제). devez Bubble_RightClick 동일.
            item.IsRightClickHighlighted = true;
            _rightClickHighlightedItem = item;
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
            ClearActionTarget(); // 빈 영역 클릭 → '작업지시' 버튼 숨김
            // devez 정합: 선택 모드에서 빈 영역 클릭(Ctrl X) → 선택 모드 해제(전체 체크 해제).
            // 단, 컨텍스트 메뉴를 닫는 클릭(직후 300ms)은 해제하지 않는다.
            if (_isSelectionMode
                && (Keyboard.Modifiers & ModifierKeys.Control) == 0
                && (DateTime.Now - _lastContextMenuCloseTime).TotalMilliseconds >= 300)
            {
                IsSelectionMode = false;
            }
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
        // ── 버블 드래그(병합) 우선 처리 ──
        if (_bubbleDragging)
        {
            UpdateBubbleDrag(e.GetPosition(this));
            e.Handled = true;
            return;
        }
        if (_pendingBubbleDragItem != null && e.LeftButton == MouseButtonState.Pressed)
        {
            var cur = e.GetPosition(this);
            double thX = SystemParameters.MinimumHorizontalDragDistance * 2;
            double thY = SystemParameters.MinimumVerticalDragDistance * 2;
            if (Math.Abs(cur.X - _bubbleDragOrigin.X) > thX || Math.Abs(cur.Y - _bubbleDragOrigin.Y) > thY)
            {
                StartBubbleDrag(cur);
                e.Handled = true;
            }
            return;
        }

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
        // ── 버블 드래그 종료(병합 시도) ──
        if (_bubbleDragging)
        {
            EndBubbleDrag();
            e.Handled = true;
            return;
        }
        // 드래그 없이 버블에서 손 뗌 → 보류 토글 적용(선택 모드 클릭).
        _pendingBubbleDragItem = null;
        if (_pendingSelectionToggle is TaskQueueItem toggle)
        {
            _pendingSelectionToggle = null;
            toggle.IsSelected = !toggle.IsSelected;
            UpdateSelectionActionBar();
            e.Handled = true;
            return;
        }

        if (_rubberActive)
        {
            EndRubberBand();
            e.Handled = true;
        }
        else if (_rubberPendingOrigin is Point origin)
        {
            // 드래그 없이 마우스 업 = 진짜 빈 영역 클릭(선택 해제는 이미 다운에서 처리). 입력 포커스.
            _rubberPendingOrigin = null;
            Dispatcher.BeginInvoke(new Action(() => InputBox.Focus()),
                System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private bool IsRubberBandableHit(DependencyObject src)
    {
        while (src != null && !ReferenceEquals(src, BubblesList))
        {
            if (src is FrameworkElement fe && (fe.Name == "BubbleBorder" || fe.Name == "BubbleInner")) return false;
            if (src is System.Windows.Controls.Button) return false; // '작업지시' 버튼 등은 빈 영역 아님
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
        // 선택/하이라이트된 버블이 있으면 입력창 포커스와 무관하게 Delete/Esc/Ctrl+C 를 선처리.
        // (작업 큐는 입력창이 항상 포커스를 유지하므로, 포커스 가드로 막으면 Delete 가 영영 안 먹는다.)
        // 단, 입력창에 글자가 있으면(=텍스트 편집 중) Delete 는 가로채지 않는다.
        bool selActive = _isSelectionMode && CountSelected() > 0;
        bool inputBusy = InputBox.IsKeyboardFocusWithin && InputBox.Text.Length > 0;
        var delTargets = CurrentDeleteTargets();

        if (e.Key == Key.Escape && _isSelectionMode)
        {
            IsSelectionMode = false;
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete && delTargets.Count > 0 && !inputBusy)
        {
            DeleteItemsWithConfirm(delTargets);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && selActive)
        {
            CopySelected();
            e.Handled = true;
            return;
        }

        // 그 외 키는 입력창에 포커스가 있으면 거기서 처리(텍스트 편집 우선).
        if (InputBox.IsKeyboardFocusWithin) return;

        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (Items.Count == 0) return;
            foreach (var it in Items) it.IsSelected = true;
            IsSelectionMode = true;
            UpdateSelectionActionBar();
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

    /// <summary>Delete 키/액션바용: 선택 항목(없으면 하이라이트된 단일 버블)을 확인 후 삭제.</summary>
    private void DeleteSelectedInternal() => DeleteItemsWithConfirm(CurrentDeleteTargets());

    /// <summary>삭제 대상: 선택된 항목들. 없으면 좌/우클릭으로 강조된 단일 버블.</summary>
    private List<TaskQueueItem> CurrentDeleteTargets()
    {
        var sel = Items.Where(i => i.IsSelected).ToList();
        if (sel.Count > 0) return sel;
        if (_rightClickHighlightedItem != null && Items.Contains(_rightClickHighlightedItem))
            return new List<TaskQueueItem> { _rightClickHighlightedItem };
        return new List<TaskQueueItem>();
    }

    /// <summary>확인 다이얼로그(devez ConfirmDialog 정합) 후 대상 버블 삭제.</summary>
    private void DeleteItemsWithConfirm(IReadOnlyList<TaskQueueItem> targets)
    {
        if (targets.Count == 0) return;
        var msg = targets.Count == 1
            ? "이 작업을 삭제하시겠습니까?"
            : $"선택한 작업 {targets.Count}개를 삭제하시겠습니까?";
        if (!ConfirmDialog.Show("삭제 확인", msg, okLabel: "삭제", iconKey: "IconTrash2", danger: true))
            return;

        foreach (var it in targets) Items.Remove(it);
        if (_rightClickHighlightedItem != null && targets.Contains(_rightClickHighlightedItem))
            _rightClickHighlightedItem = null;
        if (_actionTargetItem != null && targets.Contains(_actionTargetItem))
            _actionTargetItem = null;
        IsSelectionMode = false;
    }

    private void BubbleDelete_Click(object sender, RoutedEventArgs e)
    {
        // 선택 항목이 있으면 그것들, 없으면 우클릭한 단일 버블.
        var targets = CountSelected() > 0
            ? Items.Where(i => i.IsSelected).ToList()
            : (sender is MenuItem { DataContext: TaskQueueItem item }
                ? new List<TaskQueueItem> { item }
                : new List<TaskQueueItem>());
        DeleteItemsWithConfirm(targets);
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

    /// <summary>컨텍스트 메뉴 열릴 때: 선택 수에 따라 헤더 갱신 + '줄별로 분리' 노출 결정.
    /// 메뉴 항목 순서: [0]복사 [1]줄별로분리 [2]구분선 [3]삭제.</summary>
    private void BubbleContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;
        var count = CountSelected();
        bool bulk = count > 1;
        if (cm.Items.Count > 0 && cm.Items[0] is MenuItem copy)
            copy.Header = bulk ? $"선택 {count}개 복사" : "복사";
        if (cm.Items.Count > 3 && cm.Items[3] is MenuItem del)
            del.Header = bulk ? $"선택 {count}개 삭제" : "삭제";
        // 줄별로 분리: 단일 대상이고 빈 줄 제외 2줄 이상일 때만(devez CanSplitMemo).
        if (cm.Items.Count > 1 && cm.Items[1] is MenuItem split)
        {
            bool showSplit = !bulk && _contextMenuItem != null && CanSplit(_contextMenuItem);
            split.Visibility = showSplit ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>메뉴 닫힘: 가짜 드래그 차단 타임스탬프 기록 + 우클릭 하이라이트 해제.</summary>
    private void BubbleContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        _lastContextMenuCloseTime = DateTime.Now;
        if (_rightClickHighlightedItem != null)
        {
            _rightClickHighlightedItem.IsRightClickHighlighted = false;
            _rightClickHighlightedItem = null;
        }
    }

    private void BubbleSplit_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMenuItem is TaskQueueItem item) SplitItemByLines(item);
    }

    // ── 버블 드래그 → 병합 (devez 크로스윈도우 고스트의 패널 내 경량 버전) ──
    private void StartBubbleDrag(Point cursorInThis)
    {
        var item = _pendingBubbleDragItem;
        _pendingBubbleDragItem = null;
        if (item == null) return;
        _draggingBubbleItem = item;
        _bubbleDragging = true;
        _pendingSelectionToggle = null; // 드래그 시작 → 클릭 토글 무효화
        ClearActionTarget();

        DragGhostText.Text = item.Text;
        int badge = (_isSelectionMode && item.IsSelected) ? CountSelected() : 0;
        if (badge >= 2)
        {
            DragGhostBadgeText.Text = badge.ToString();
            DragGhostBadge.Visibility = Visibility.Visible;
        }
        else DragGhostBadge.Visibility = Visibility.Collapsed;

        DragGhostLayer.Visibility = Visibility.Visible;
        Mouse.Capture(this, CaptureMode.SubTree);
        UpdateBubbleDrag(cursorInThis);
    }

    private void UpdateBubbleDrag(Point cursorInThis)
    {
        Canvas.SetLeft(DragGhost, cursorInThis.X + 12);
        Canvas.SetTop(DragGhost, cursorInThis.Y + 12);

        var target = FindBubbleItemUnderCursor(cursorInThis);
        if (ReferenceEquals(target, _mergeTargetItem)) return;
        if (_mergeTargetItem != null) _mergeTargetItem.IsMergeTarget = false;
        _mergeTargetItem = target;
        if (_mergeTargetItem != null) _mergeTargetItem.IsMergeTarget = true;
    }

    private void EndBubbleDrag()
    {
        var dragged = _draggingBubbleItem;
        var target = _mergeTargetItem;
        _bubbleDragging = false;
        _draggingBubbleItem = null;
        if (_mergeTargetItem != null) { _mergeTargetItem.IsMergeTarget = false; _mergeTargetItem = null; }
        _pendingBubbleDragItem = null;
        DragGhostLayer.Visibility = Visibility.Collapsed;
        if (ReferenceEquals(Mouse.Captured, this)) Mouse.Capture(null);

        if (dragged != null && target != null) MergeInto(target, dragged);
    }

    /// <summary>커서 아래 버블 항목을 찾아 병합 도착지로 유효한지 검증.
    /// 다중 드래그(선택모드+드래그버블 선택)면 선택 집합(소스)은 도착지 불가, 단일이면 자기 자신 불가.</summary>
    private TaskQueueItem? FindBubbleItemUnderCursor(Point cursorInThis)
    {
        var dragged = _draggingBubbleItem;
        if (dragged == null) return null;
        if (cursorInThis.X < 0 || cursorInThis.Y < 0
            || cursorInThis.X > ActualWidth || cursorInThis.Y > ActualHeight) return null;

        DependencyObject? hit;
        try { hit = InputHitTest(cursorInThis) as DependencyObject; }
        catch { return null; }
        var border = FindAncestorBubbleBorder(hit);
        if (border?.DataContext is not TaskQueueItem dest) return null;

        bool multi = _isSelectionMode && dragged.IsSelected;
        if (multi) { if (dest.IsSelected) return null; }
        else if (ReferenceEquals(dest, dragged)) return null;
        return dest;
    }

    private static Border? FindAncestorBubbleBorder(DependencyObject? node)
    {
        while (node != null)
        {
            if (node is Border b && b.Name == "BubbleBorder") return b;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node) : null;
        }
        return null;
    }

    /// <summary>도착지에 출발지 버블들을 합침. devez BuildMergedContent/SelectMergeSourceIds 정합.</summary>
    private void MergeInto(TaskQueueItem dest, TaskQueueItem dragged)
    {
        // 출발지: 선택 모드 + 드래그 버블이 선택됐으면 선택 전체, 아니면 드래그 1개. 도착지 제외.
        var sources = (_isSelectionMode && dragged.IsSelected)
            ? Items.Where(i => i.IsSelected && !ReferenceEquals(i, dest)).ToList()
            : new List<TaskQueueItem> { dragged };
        sources = sources.Where(s => !ReferenceEquals(s, dest))
                         .OrderBy(s => Items.IndexOf(s))   // messages 순서대로
                         .ToList();
        if (sources.Count == 0) return;

        var merged = BuildMergedContent(dest.Text, sources.Select(s => s.Text));
        if (merged == dest.Text) return;

        dest.Text = merged;
        foreach (var s in sources) Items.Remove(s);
        if (_isSelectionMode) IsSelectionMode = false;
        ResequenceSortOrders();
        Save();
    }

    /// <summary>도착지 내용 + 출발지 내용들을 줄바꿈 하나로 결합(trim·빈 줄 skip). devez 동일 규칙.</summary>
    private static string BuildMergedContent(string destContent, IEnumerable<string> sourceContents)
    {
        var result = destContent ?? "";
        foreach (var raw in sourceContents)
        {
            var text = (raw ?? "").Trim();
            if (text.Length == 0) continue;
            result = result.Length == 0 ? text : result + "\n" + text;
        }
        return result;
    }

    // ── 버블 줄별 분리 (devez SplitMemoByLinesAsync 의 로컬 동기 버전) ──
    /// <summary>content 를 줄바꿈으로 나눠 각 줄 trim·빈 줄 제외.</summary>
    private static List<string> SplitIntoLines(string? content) =>
        (content ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    /// <summary>빈 줄 제외 2줄 이상이면 분리 가능.</summary>
    private static bool CanSplit(TaskQueueItem item) => SplitIntoLines(item.Text).Count >= 2;

    /// <summary>버블을 줄별로 분리. 첫 줄은 원본 재사용, 나머지는 바로 뒤에 새 버블로 삽입.</summary>
    private void SplitItemByLines(TaskQueueItem item)
    {
        var lines = SplitIntoLines(item.Text);
        if (lines.Count < 2) return;
        int idx = Items.IndexOf(item);
        if (idx < 0) return;
        item.Text = lines[0];
        for (int k = 1; k < lines.Count; k++)
            Items.Insert(idx + k, new TaskQueueItem { Text = lines[k] });
        ResequenceSortOrders();
        Save();
    }

    /// <summary>현재 표시 순서대로 SortOrder 를 0,1,2… 재배열(저장/재로드 순서 보장).</summary>
    private void ResequenceSortOrders()
    {
        for (int i = 0; i < Items.Count; i++) Items[i].SortOrder = i;
    }

    /// <summary>외부 초기화 (필요 시).</summary>
    public void ClearAll() => Items.Clear();
}

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DevezCode.Views;

/// <summary>
/// 웹 스타일 리스트 재정렬 드래그 (devez ReorderDrag 이식, 핵심부).
/// 드래그 중 원본은 고스트로 커서를 따라가고, 나머지 항목은 시프트 애니메이션으로 자리를 비운다.
/// 호스트가 PreviewMouseMove→Update, MouseUp/LostCapture→FinishAsync 를 연결한다.
/// onCommit(source, hostTarget, hostSource): hostTarget 은 source 가 최종적으로 놓일 위치.
/// </summary>
internal sealed class ReorderDrag<T> where T : class
{
    private const double AnimMs = 160;
    private const double LiveReversalHysteresis = 6;

    // Left/Top/Width/Height: 그룹 전체 사각형. Primary*: 실제 대표 행의 드롭 사각형.
    private sealed record Slot(
        T Item,
        FrameworkElement Element,
        IReadOnlyList<FrameworkElement> Elements,
        double Left,
        double Top,
        double Width,
        double Height,
        double PrimaryLeft,
        double PrimaryTop,
        double PrimaryWidth,
        double PrimaryHeight);

    private readonly UIElement _coordHost;
    private readonly List<Slot> _slots;
    private readonly Func<T, int, int, Task> _onCommit;
    private readonly DragHelper.IGhost _ghost;
    private readonly T _source;
    private readonly int _sourceIndex;
    private readonly bool _exactFollow;
    private readonly bool _horizontal;
    private readonly int _columns;        // >1 이면 2열 컬럼 재정렬(명시적 컬럼 + 컬럼 내 세로 순서). 1이면 기존 1축 동작.
    private readonly double _gridMidX;    // coordHost 기준, 좌/우 컬럼을 가르는 X(컬럼 판정 기준).
    private readonly double _grabOffsetX; // 잡은 지점의 source 내부 오프셋 — 드래그 카드 중심 계산용
    private readonly double _grabOffsetY;
    private readonly Func<T, T, bool>? _canDropInto;
    private readonly Action<T?, FrameworkElement?>? _dropIntoPreviewChanged;
    private readonly Func<T, T, Task>? _onDropInto;
    private readonly Action<T?, FrameworkElement?, bool>? _reorderPreviewChanged;
    private readonly bool _commitUnchanged;
    private readonly bool _hitTestSlots;
    private readonly bool _suppressDisplacement;
    private readonly bool _useLiveLayoutPlaceholder;
    private readonly bool _useFixedLayoutPlaceholder;
    private readonly bool _useGridPlaceholder;
    private readonly bool _useLogicalHitTestBounds;
    private readonly Func<T, T, bool>? _useQuarterReorderHysteresis;
    private readonly double? _hitTestXOverride; // 고스트는 실제 포인터를 따르고 드롭 순서 판정 X만 고정.
    private int _targetIndex;             // 1축: host 인덱스 / 그리드: 목표 컬럼 내 삽입 위치
    private int _targetColumn;            // 그리드 전용: 목표 컬럼(0/1)
    private Slot? _dropIntoTarget;         // 중앙 50%: 자식 드롭 프리뷰/커밋 대상.
    private Slot? _reorderPreviewTarget;
    private bool _reorderPreviewAfter;
    private Slot? _directionalDropTarget;
    private DropEntrySide _directionalDropEntrySide;
    private double? _lastDropZoneAxisPosition;
    private double? _lastPointerAxisPosition;
    private int _lastTransitionDirection;
    private bool _finished;
    private bool _suppressed;             // 크로스 패널 드래그 중 반대 패널 위 → 이 리스트 프리뷰 억제.
    private bool _externalDropPreview;    // 폴더 등 외부 드롭 대상 위 → 재정렬 프리뷰를 원위치로 억제.
    private bool _needsReapply;           // 억제 해제(복귀) 직후 1회는 target 동일해도 강제 재적용(소스 자리 빈 채 고정 방지).

    private bool IsGrid => _columns > 1;
    private double AxisPos(Slot s) => _horizontal ? s.Left : s.Top;
    private double AxisSize(Slot s) => _horizontal ? s.Width : s.Height;
    private int ColumnOf(Slot s) => s.Left + s.Width / 2 >= _gridMidX ? 1 : 0;

    private ReorderDrag(UIElement coordHost, List<Slot> slots, T source, int sourceIndex,
        DragHelper.IGhost ghost, Func<T, int, int, Task> onCommit, bool exactFollow, bool horizontal,
        int columns, double gridMidX, double grabOffsetX, double grabOffsetY,
        Func<T, T, bool>? canDropInto, Action<T?, FrameworkElement?>? dropIntoPreviewChanged,
        Func<T, T, Task>? onDropInto, bool commitUnchanged,
        bool hitTestSlots, bool suppressDisplacement,
        bool useLiveLayoutPlaceholder, bool useFixedLayoutPlaceholder,
        bool useGridPlaceholder, bool useLogicalHitTestBounds,
        Action<T?, FrameworkElement?, bool>? reorderPreviewChanged,
        double? hitTestXOverride,
        Func<T, T, bool>? useQuarterReorderHysteresis)
    {
        _coordHost = coordHost; _slots = slots; _source = source; _sourceIndex = sourceIndex;
        _ghost = ghost; _onCommit = onCommit; _exactFollow = exactFollow; _horizontal = horizontal;
        _columns = columns; _gridMidX = gridMidX; _grabOffsetX = grabOffsetX; _grabOffsetY = grabOffsetY;
        _canDropInto = canDropInto; _dropIntoPreviewChanged = dropIntoPreviewChanged;
        _onDropInto = onDropInto; _commitUnchanged = commitUnchanged;
        _hitTestSlots = hitTestSlots; _suppressDisplacement = suppressDisplacement;
        _useLiveLayoutPlaceholder = useLiveLayoutPlaceholder;
        _useFixedLayoutPlaceholder = useFixedLayoutPlaceholder;
        _useGridPlaceholder = useGridPlaceholder;
        _useLogicalHitTestBounds = useLogicalHitTestBounds;
        _useQuarterReorderHysteresis = useQuarterReorderHysteresis;
        _reorderPreviewChanged = reorderPreviewChanged;
        _hitTestXOverride = hitTestXOverride;
        if (IsGrid)
        {
            // 드래그 임계값을 넘긴 직후 추가 MouseMove 없이 놓여도 원래 컬럼/위치가 유지되어야 한다.
            // -1로 시작하면 FinishAsync가 이를 0번 컬럼으로 정규화해 우측 카드가 좌측으로 이동할 수 있다.
            _targetColumn = ColumnOf(_slots[_sourceIndex]);
            _targetIndex = WithinColumnIndex(_sourceIndex, _targetColumn);
        }
        else
        {
            _targetIndex = sourceIndex;
            _targetColumn = -1;
        }
    }

    public T Source => _source;
    public int CurrentTargetColumn => _targetColumn;

    /// <summary>호스트가 컬렉션/열을 라이브 재배치할 때 현재 화면 위치에서 새 레이아웃 위치까지
    /// 2축 FLIP 애니메이션을 적용한다. 복잡한 2열+전체폭 폴더 배치는 단순 세로 shift로 표현할 수 없어 사용.</summary>
    public void AnimateLayoutChange(Action applyLayoutChange)
    {
        if (_finished)
        {
            applyLayoutChange();
            return;
        }

        _coordHost.UpdateLayout();
        var sourceElements = _slots[_sourceIndex].Elements.ToHashSet();
        var allElements = _slots
            .SelectMany(slot => slot.Elements)
            .Distinct()
            .ToList();
        var oldPositions = new Dictionary<FrameworkElement, Point>();
        foreach (var element in allElements)
        {
            if (sourceElements.Contains(element)) continue;
            try
            {
                oldPositions[element] = element.TransformToAncestor(_coordHost).Transform(new Point());
            }
            catch { /* 레이아웃 변경 중 분리된 컨테이너는 애니메이션에서 제외 */ }
        }

        // 진행 중 애니메이션의 현재 화면 위치는 위에서 캡처했다. 기존 변환을 지운 뒤 새 레이아웃을
        // 계산하고 그 차이만큼 역이동시켜, 연속 드래그에서도 순간이동 없이 이어 붙인다.
        foreach (var element in allElements) ResetPosition(element);
        applyLayoutChange();
        _coordHost.UpdateLayout();

        foreach (var (element, oldPosition) in oldPositions)
        {
            try
            {
                var newPosition = element.TransformToAncestor(_coordHost).Transform(new Point());
                AnimatePosition(element, oldPosition.X - newPosition.X, oldPosition.Y - newPosition.Y);
            }
            catch { /* 제거/재생성된 컨테이너는 최종 레이아웃에 그대로 둔다 */ }
        }
    }

    public static ReorderDrag<T>? TryStart(
        UIElement coordHost,
        IEnumerable<(T Item, FrameworkElement Element)> rows,
        T source,
        FrameworkElement sourceElement,
        Func<T, int, int, Task> onCommit,
        bool exactFollow = false,
        bool horizontal = false,
        int columns = 1,
        double gridMidX = 0,
        FrameworkElement? ghostSource = null,
        Func<T, T, bool>? canDropInto = null,
        Action<T?, FrameworkElement?>? dropIntoPreviewChanged = null,
        Func<T, T, Task>? onDropInto = null,
        bool commitUnchanged = false,
        Func<T, IReadOnlyList<FrameworkElement>>? groupedElements = null,
        bool hitTestSlots = false,
        Action<T?, FrameworkElement?, bool>? reorderPreviewChanged = null,
        bool suppressDisplacement = false,
        bool preserveRowOrder = false,
        double? hitTestXOverride = null,
        bool includeElementMarginsInBounds = false,
        bool useLiveLayoutPlaceholder = false,
        bool useFixedLayoutPlaceholder = false,
        bool useGridPlaceholder = false,
        bool useLogicalHitTestBounds = false,
        FrameworkElement? ghostBackgroundTarget = null,
        Brush? ghostBackground = null,
        Func<T, T, bool>? useQuarterReorderHysteresis = null)
    {
        var captured = new List<Slot>();
        foreach (var (item, el) in rows)
        {
            var elements = groupedElements?.Invoke(item)?
                .Where(element => element != null)
                .Distinct()
                .ToList() ?? new List<FrameworkElement> { el };
            if (!elements.Contains(el)) elements.Insert(0, el);

            Rect bounds = Rect.Empty;
            double primaryLeft, primaryTop;
            try
            {
                var primary = el.TransformToAncestor(coordHost).Transform(new Point(0, 0));
                primaryLeft = primary.X;
                primaryTop = primary.Y;
                foreach (var element in elements)
                {
                    var point = element.TransformToAncestor(coordHost).Transform(new Point(0, 0));
                    var margin = includeElementMarginsInBounds
                        ? element.Margin
                        : new Thickness();
                    var elementBounds = new Rect(
                        point.X - margin.Left,
                        point.Y - margin.Top,
                        Math.Max(1, element.ActualWidth + margin.Left + margin.Right),
                        Math.Max(1, element.ActualHeight + margin.Top + margin.Bottom));
                    bounds = bounds.IsEmpty ? elementBounds : Rect.Union(bounds, elementBounds);
                }
            }
            catch { continue; }

            captured.Add(new Slot(
                item,
                el,
                elements,
                bounds.Left,
                bounds.Top,
                Math.Max(1, bounds.Width),
                Math.Max(1, bounds.Height),
                primaryLeft,
                primaryTop,
                Math.Max(1, el.ActualWidth),
                Math.Max(1, el.ActualHeight)));
        }
        // 그리드(2열): 컬럼(좌→우) 우선, 그 안에서 위→아래. 1축: 해당 축 위치.
        if (!preserveRowOrder && columns > 1)
            captured.Sort((a, b) =>
            {
                int ca = a.Left + a.Width / 2 >= gridMidX ? 1 : 0;
                int cb = b.Left + b.Width / 2 >= gridMidX ? 1 : 0;
                return ca != cb ? ca.CompareTo(cb) : a.Top.CompareTo(b.Top);
            });
        else if (!preserveRowOrder)
            captured.Sort((a, b) =>
            {
                int primary = (horizontal ? a.Left : a.Top).CompareTo(horizontal ? b.Left : b.Top);
                if (primary != 0) return primary;
                return (horizontal ? a.Top : a.Left).CompareTo(horizontal ? b.Top : b.Left);
            });

        var srcIdx = captured.FindIndex(s => ReferenceEquals(s.Item, source));
        if (srcIdx < 0) return null; // 1개(패널 마지막 탭)여도 시작은 허용 — 로컬 재정렬은 무동작, 크로스 패널 이동은 별도 경로.

        // ghostSource: ghost 이미지로 캡처할 visual (null이면 sourceElement 사용).
        // groupedElements가 둘 이상이면 그룹 전체 영역을 한 장으로 캡처하고 모든 원본 행을 숨긴다.
        // 잡은 지점의 source 내부 오프셋(축) — 커서 raw 대신 '드래그 카드 중심'을 기준으로
        // 타깃을 판정하기 위함. 그래야 source 높이/잡은 위치와 무관하게 위/아래 모두 대칭으로
        // '이웃 카드 절반을 넘을 때' 순서가 바뀐다.
        var sourceSlot = captured[srcIdx];
        Point grabPt;
        DragHelper.IGhost? ghost;
        if (ghostSource == null && sourceSlot.Elements.Count > 1)
        {
            var pointer = Mouse.GetPosition(coordHost);
            grabPt = new Point(pointer.X - sourceSlot.Left, pointer.Y - sourceSlot.Top);
            ghost = DragHelper.BeginManualDrag(
                coordHost,
                new Rect(sourceSlot.Left, sourceSlot.Top, sourceSlot.Width, sourceSlot.Height),
                sourceSlot.Elements,
                grabPt);
        }
        else
        {
            grabPt = Mouse.GetPosition(sourceElement);
            ghost = DragHelper.BeginManualDrag(
                ghostSource ?? sourceElement,
                sourceElement,
                ghostBackgroundTarget,
                ghostBackground);
        }
        if (ghost == null) return null;

        double reorderGrabX = grabPt.X;
        double reorderGrabY = grabPt.Y;
        if (includeElementMarginsInBounds && sourceSlot.Elements.Count == 1)
        {
            reorderGrabX += sourceElement.Margin.Left;
            reorderGrabY += sourceElement.Margin.Top;
        }

        return new ReorderDrag<T>(coordHost, captured, source, srcIdx, ghost, onCommit, exactFollow, horizontal,
            columns, gridMidX, reorderGrabX, reorderGrabY, canDropInto, dropIntoPreviewChanged,
            onDropInto, commitUnchanged,
            hitTestSlots, suppressDisplacement,
            useLiveLayoutPlaceholder, useFixedLayoutPlaceholder,
            useGridPlaceholder, useLogicalHitTestBounds,
            reorderPreviewChanged, hitTestXOverride, useQuarterReorderHysteresis);
    }

    /// <summary>자식 드래그 시작 즉시 원래 자리를 접어 부모에서 빠져나오는 프리뷰를 표시.
    /// 실제 부모 관계 변경은 드롭 커밋 시 수행한다.</summary>
    public void ShowDetachedSourcePreview()
    {
        if (_finished || IsGrid) return;
        double shift = RowPitch();
        for (int i = 0; i < _slots.Count; i++)
        {
            if (i == _sourceIndex) { AnimateSlot(_slots[i], 0); continue; }
            AnimateSlot(_slots[i], i > _sourceIndex ? -shift : 0);
        }
        _needsReapply = true;
    }

    /// <summary>크로스 패널 드래그 중 커서가 반대 패널에 있을 때 호출 — 소스가 이 리스트에서 '나간' 것처럼
    /// 뒤 항목을 앞으로 당겨 빈자리를 메운다(압축). 고스트만 커서를 따라가고 이 리스트는 이 상태로 고정.
    /// off 로 돌아오면 원복하고 이후 Update 가 정상 재정렬 프리뷰를 재개한다.</summary>
    public void SuppressDisplacement(bool on)
    {
        if (_suppressed == on) return;
        _suppressed = on;
        // on=압축(소스를 끝으로 보낸 변위=뒤 항목 앞당김). off=억제 해제 — 뒤이어 호출되는 Update 가 커서 기준으로
        // 재적용하게 강제 플래그를 세운다(target 이 우연히 이전 값과 같아도 조기 return 안 되게 → 소스 자리 빈 채 고정 방지).
        if (on) { _targetIndex = _slots.Count - 1; ApplyDisplacement(); }
        else _needsReapply = true;
    }

    /// <summary>호스트가 별도 드롭 대상을 표시하는 동안 이 목록의 재정렬 프리뷰를 원위치로
    /// 되돌리고 고스트만 계속 따라가게 한다. 해제 직후에는 현재 포인터로 재정렬을 다시 계산한다.</summary>
    public void SetExternalDropPreview(bool on)
    {
        if (_finished || _externalDropPreview == on) return;
        _externalDropPreview = on;
        _lastPointerAxisPosition = null;
        _lastTransitionDirection = 0;

        if (!on)
        {
            _needsReapply = true;
            return;
        }

        ClearDropIntoTarget();
        ClearReorderPreview();
        if (IsGrid)
        {
            _targetColumn = ColumnOf(_slots[_sourceIndex]);
            _targetIndex = WithinColumnIndex(_sourceIndex, _targetColumn);
            ApplyGridDisplacement();
        }
        else
        {
            _targetIndex = _sourceIndex;
            ResetDisplacementPreview();
        }
        _needsReapply = false;
    }

    public void Update(MouseEventArgs e)
    {
        if (_finished) return;
        _ghost.MoveToMouse();
        if (_suppressed || _externalDropPreview) return; // 외부 드롭 대상 위 → 고스트만 이동.
        if (IsGrid)
        {
            // 2열: 마우스 X로 목표 컬럼을, 마우스 Y로 그 컬럼 안의 삽입 위치를 정한다.
            // 좌/우 컬럼은 각각 독립된 세로 리스트로 시프트 애니메이션한다 — 같은 컬럼이면 그 안에서
            // 재정렬, 다른 컬럼으로 넘기면 원래 컬럼은 빈자리를 위로 메우고 목표 컬럼은 자리를 연다.
            var p = e.GetPosition(_coordHost);
            double cx = p.X;
            double cy = p.Y;
            if (_useGridPlaceholder)
            {
                UpdateGridPlaceholder(cx, cy);
                return;
            }
            int newCol = cx >= _gridMidX ? 1 : 0;
            int newIdx = ComputeColumnTarget(newCol, cy);
            if (newCol == _targetColumn && newIdx == _targetIndex) return;
            _targetColumn = newCol; _targetIndex = newIdx;
            ApplyGridDisplacement();
            return;
        }

        var pointer = e.GetPosition(_coordHost);
        if (_hitTestXOverride is double hitTestX) pointer.X = hitTestX;

        // 2열 전체폭 폴더는 컬렉션 자체를 라이브 재배치하므로, 고정 X를 유지하면서
        // 마우스 Y가 다음 카드의 최종 레이아웃 중앙을 넘을 때 인접한 한 칸만 이동한다.
        if (_useLiveLayoutPlaceholder)
        {
            UpdatePointerTarget(pointer, liveLayout: true);
            return;
        }
        // 1열 카드 목록은 이동 중인 대상의 RenderTransform이나 앞 카드가 비운 자리를
        // 다시 판정하지 않고, 진행 방향의 다음 실제 카드 중앙을 고정 기준으로 사용한다.
        if (_useFixedLayoutPlaceholder)
        {
            UpdatePointerTarget(pointer, liveLayout: false);
            return;
        }

        if (TryGetDropZone(pointer, out var hovered, out var zone))
        {
            if (zone == DropZone.Into)
            {
                ClearReorderPreview();
                if (SetDropIntoTarget(hovered))
                {
                    _targetIndex = _sourceIndex;
                    ResetDisplacementPreview();
                }
                return;
            }

            ClearDropIntoTarget();
            SetReorderPreview(hovered, zone == DropZone.After);
            var zoneTarget = TargetIndexAround(hovered, after: zone == DropZone.After);
            if (!_needsReapply && zoneTarget == _targetIndex) return;
            _needsReapply = false;
            _targetIndex = zoneTarget;
            ApplyDisplacement();
            return;
        }

        if (_hitTestSlots)
        {
            ClearDropIntoTarget();
            if (!_suppressDisplacement
                && TryGetOuterDropTarget(pointer, out var outerTarget, out bool after))
            {
                SetReorderPreview(outerTarget, after);
                int outerIndex = TargetIndexAround(outerTarget, after);
                if (_needsReapply || outerIndex != _targetIndex)
                {
                    _needsReapply = false;
                    _targetIndex = outerIndex;
                    ApplyDisplacement();
                }
            }
            else if (CapturedPrimaryBounds(_slots[_sourceIndex]).Contains(pointer))
            {
                // 라이브 2열 이동으로 원래 컬럼이 비어도 시작 슬롯으로 돌아오면 원래 순서/열을 복원한다.
                ClearReorderPreview();
                _needsReapply = false;
                _targetIndex = _sourceIndex;
                ApplyDisplacement();
            }
            else if (!_suppressDisplacement)
            {
                // 1열 루트 목록의 원본 자리나 카드 사이 여백에서는 명시적 hit 대상이 없다.
                // 이때도 현재 드래그 카드 중심으로 재계산해야 직전 타깃이 남지 않는다.
                ClearReorderPreview();
                var gapCursor = _horizontal ? pointer.X : pointer.Y;
                var gapGrab = _horizontal ? _grabOffsetX : _grabOffsetY;
                var gapCenter = gapCursor - gapGrab + AxisSize(_slots[_sourceIndex]) / 2;
                int gapTarget = ComputeTargetIndex(gapCenter);
                if (_needsReapply || gapTarget != _targetIndex)
                {
                    _needsReapply = false;
                    _targetIndex = gapTarget;
                    ApplyDisplacement();
                }
            }
            else if (TryGetNearestCurrentTarget(pointer, out var nearestTarget, out bool nearestAfter))
            {
                // 2열의 컬럼 사이/짧은 컬럼 아래 빈 공간은 라이브 배치가 움직여도 변하지 않는
                // 현재 화면 위치를 기준으로 가장 가까운 항목 앞뒤를 선택한다.
                SetReorderPreview(nearestTarget, nearestAfter);
                int nearestIndex = TargetIndexAround(nearestTarget, nearestAfter);
                if (_needsReapply || nearestIndex != _targetIndex)
                {
                    _needsReapply = false;
                    _targetIndex = nearestIndex;
                    ApplyDisplacement();
                }
            }
            return;
        }
        ClearReorderPreview();
        ClearDropIntoTarget();
        var cursor = _horizontal ? pointer.X : pointer.Y;
        var grabOffset = _horizontal ? _grabOffsetX : _grabOffsetY;
        // 커서 raw 대신 드래그 중인 카드의 중심을 기준점으로 사용 — 위/아래 대칭 판정.
        var draggedCenter = cursor - grabOffset + AxisSize(_slots[_sourceIndex]) / 2;
        var newTarget = ComputeTargetIndex(draggedCenter);
        if (!_needsReapply && newTarget == _targetIndex) return;
        _needsReapply = false;
        _targetIndex = newTarget;
        ApplyDisplacement();
    }

    private void UpdatePointerTarget(Point pointer, bool liveLayout)
    {
        ClearDropIntoTarget();
        var source = _slots[_sourceIndex];
        double pointerPosition = _horizontal ? pointer.X : pointer.Y;
        double initialPointerPosition = AxisPos(source)
            + (_horizontal ? _grabOffsetX : _grabOffsetY);
        double previousPosition = _lastPointerAxisPosition ?? initialPointerPosition;
        _lastPointerAxisPosition = pointerPosition;
        int movementDirection = pointerPosition > previousPosition + 0.25
            ? 1
            : pointerPosition < previousPosition - 0.25 ? -1 : 0;
        var others = _slots
            .Where(slot => !ReferenceEquals(slot, source))
            .ToList();

        int newTarget = _targetIndex;
        if (movementDirection > 0 && newTarget < others.Count)
        {
            var target = others[newTarget];
            var bounds = liveLayout
                ? LogicalPrimaryBounds(target)
                : FixedDisplacedPrimaryBounds(target);
            bool quarterHysteresis = UsesQuarterReorderHysteresis(target);
            double threshold = _horizontal
                ? bounds.Left + bounds.Width * (quarterHysteresis ? 0.75 : 0.5)
                : bounds.Top + bounds.Height * (quarterHysteresis ? 0.75 : 0.5);
            if (!quarterHysteresis && _lastTransitionDirection < 0)
                threshold += LiveReversalHysteresis;
            if (pointerPosition >= threshold)
                newTarget++;
        }
        else if (movementDirection < 0 && newTarget > 0)
        {
            var target = others[newTarget - 1];
            var bounds = liveLayout
                ? LogicalPrimaryBounds(target)
                : FixedDisplacedPrimaryBounds(target);
            bool quarterHysteresis = UsesQuarterReorderHysteresis(target);
            double threshold = _horizontal
                ? bounds.Left + bounds.Width * (quarterHysteresis ? 0.25 : 0.5)
                : bounds.Top + bounds.Height * (quarterHysteresis ? 0.25 : 0.5);
            if (!quarterHysteresis && _lastTransitionDirection > 0)
                threshold -= LiveReversalHysteresis;
            if (pointerPosition <= threshold)
                newTarget--;
        }

        if (!_needsReapply && newTarget == _targetIndex) return;
        _needsReapply = false;
        _lastTransitionDirection = Math.Sign(newTarget - _targetIndex);
        _targetIndex = newTarget;
        SetReorderPreviewForTargetIndex(newTarget);
        if (!liveLayout)
            ApplyDisplacement();
    }

    private Rect FixedDisplacedPrimaryBounds(Slot slot)
    {
        // 애니메이션의 진행 중 좌표가 아니라 현재 target에서 도달할 최종 변위를 더한다.
        // 따라서 되돌아갈 때는 이미 밀려난 카드의 새 중앙이 안정된 기준점이 된다.
        var bounds = CapturedPrimaryBounds(slot);
        double offset = FixedDisplacement(slot);
        bounds.Offset(_horizontal ? offset : 0, _horizontal ? 0 : offset);
        return bounds;
    }

    private double FixedDisplacement(Slot slot)
    {
        int index = _slots.IndexOf(slot);
        double shift = RowPitch();
        if (_targetIndex < _sourceIndex && index >= _targetIndex && index < _sourceIndex)
            return shift;
        if (_targetIndex > _sourceIndex && index > _sourceIndex && index <= _targetIndex)
            return -shift;
        return 0;
    }

    private void SetReorderPreviewForTargetIndex(int targetIndex)
    {
        if (targetIndex == _sourceIndex)
        {
            ClearReorderPreview();
            return;
        }

        var otherSlots = _slots
            .Where(slot => !ReferenceEquals(slot, _slots[_sourceIndex]))
            .ToList();
        if (otherSlots.Count == 0)
        {
            ClearReorderPreview();
            return;
        }

        if (targetIndex <= 0)
            SetReorderPreview(otherSlots[0], false);
        else
            SetReorderPreview(otherSlots[Math.Min(targetIndex - 1, otherSlots.Count - 1)], true);
    }

    private enum DropZone { Before, Into, After }
    private enum DropEntrySide { Before, After }

    /// <summary>기본 자식 드롭은 상단 25%=앞, 중앙 50%=자식, 하단 25%=뒤.
    /// 방향형 대상은 진입한 쪽 75%=자식, 반대쪽 25%=재정렬로 판정한다.</summary>
    private bool TryGetDropZone(Point pointer, out Slot target, out DropZone zone)
    {
        target = null!;
        zone = DropZone.Before;
        if (_canDropInto == null && !_hitTestSlots) return false;

        double axisPosition = _horizontal ? pointer.X : pointer.Y;
        double? previousAxisPosition = _lastDropZoneAxisPosition;
        _lastDropZoneAxisPosition = axisPosition;

        foreach (var slot in _slots)
        {
            if (ReferenceEquals(slot, _slots[_sourceIndex])) continue;
            bool canDropInto = _canDropInto?.Invoke(_source, slot.Item) == true;
            if (!canDropInto && !_hitTestSlots) continue;
            // 일반 목록은 밀려난 현재 위치를 따르고, 2열 라이브 레이아웃은 FLIP 변위를 뺀
            // 최종 배치 위치를 쓴다. 방향 전환은 히스테리시스로 경계 왕복을 억제한다.
            var bounds = HitTestPrimaryBounds(slot);
            if (!bounds.Contains(pointer)) continue;

            double relative = _horizontal
                ? (pointer.X - bounds.Left) / bounds.Width
                : (pointer.Y - bounds.Top) / bounds.Height;
            bool directionalDrop = UsesQuarterReorderHysteresis(slot);
            if (directionalDrop && ReferenceEquals(_reorderPreviewTarget, slot))
            {
                bool after = ResolveReorderAfter(slot, relative >= 0.5, pointer);
                zone = after ? DropZone.After : DropZone.Before;
            }
            else if (directionalDrop)
            {
                if (!ReferenceEquals(_directionalDropTarget, slot))
                {
                    _directionalDropTarget = slot;
                    _directionalDropEntrySide = ResolveDropEntrySide(
                        bounds, axisPosition, previousAxisPosition, relative);
                }

                zone = _directionalDropEntrySide switch
                {
                    DropEntrySide.Before => relative > 0.75
                        ? DropZone.After
                        : canDropInto ? DropZone.Into : DropZone.Before,
                    DropEntrySide.After => relative < 0.25
                        ? DropZone.Before
                        : canDropInto ? DropZone.Into : DropZone.After,
                    _ => canDropInto ? DropZone.Into : DropZone.Before,
                };
            }
            else if (canDropInto)
            {
                _directionalDropTarget = null;
                zone = relative < 0.25
                    ? DropZone.Before
                    : relative > 0.75 ? DropZone.After : DropZone.Into;
            }
            else
            {
                _directionalDropTarget = null;
                bool after = ResolveReorderAfter(slot, relative >= 0.5, pointer);
                zone = after ? DropZone.After : DropZone.Before;
            }
            target = slot;
            return true;
        }

        // 재정렬로 이미 밀려난 대상과 커서 사이에 일시적인 빈 공간이 생겨도 상태를
        // 지우지 않는다. 실제 다른 슬롯에 들어갈 때까지 반대편 25% 경계를 계속 사용한다.
        if (_reorderPreviewTarget is { } reorderTarget
            && UsesQuarterReorderHysteresis(reorderTarget))
        {
            bool after = ResolveReorderAfter(reorderTarget, _reorderPreviewAfter, pointer);
            target = reorderTarget;
            zone = after ? DropZone.After : DropZone.Before;
            return true;
        }

        _directionalDropTarget = null;
        return false;
    }

    private DropEntrySide ResolveDropEntrySide(
        Rect bounds,
        double axisPosition,
        double? previousAxisPosition,
        double relative)
    {
        double axisStart = _horizontal ? bounds.Left : bounds.Top;
        double axisEnd = _horizontal ? bounds.Right : bounds.Bottom;
        if (previousAxisPosition is double previous)
        {
            if (previous <= axisStart) return DropEntrySide.Before;
            if (previous >= axisEnd) return DropEntrySide.After;
            if (axisPosition > previous + 0.25) return DropEntrySide.Before;
            if (axisPosition < previous - 0.25) return DropEntrySide.After;
        }

        return relative <= 0.5 ? DropEntrySide.Before : DropEntrySide.After;
    }

    private bool TryGetOuterDropTarget(Point pointer, out Slot target, out bool after)
    {
        target = null!;
        after = false;
        var candidates = _slots
            .Where(slot => !ReferenceEquals(slot, _slots[_sourceIndex]))
            .Select(slot => (Slot: slot, Bounds: HitTestPrimaryBounds(slot)))
            .ToList();
        if (candidates.Count == 0) return false;

        if (_horizontal)
        {
            double firstEdge = candidates.Min(candidate => candidate.Bounds.Left);
            double lastEdge = candidates.Max(candidate => candidate.Bounds.Right);
            if (pointer.X < firstEdge) { target = candidates[0].Slot; return true; }
            if (pointer.X <= lastEdge) return false;
            target = candidates[^1].Slot;
        }
        else
        {
            double firstEdge = candidates.Min(candidate => candidate.Bounds.Top);
            double lastEdge = candidates.Max(candidate => candidate.Bounds.Bottom);
            if (pointer.Y < firstEdge) { target = candidates[0].Slot; return true; }
            if (pointer.Y <= lastEdge) return false;
            target = candidates[^1].Slot;
        }
        after = true;
        return true;
    }

    private bool TryGetNearestCurrentTarget(Point pointer, out Slot target, out bool after)
    {
        target = null!;
        after = false;
        var candidates = _slots
            .Where(slot => !ReferenceEquals(slot, _slots[_sourceIndex]))
            .Select(slot => (Slot: slot, Bounds: HitTestPrimaryBounds(slot)))
            .ToList();
        if (candidates.Count == 0) return false;

        // 2열의 짧은 컬럼 아래에서는 같은 Y의 반대 컬럼 카드보다, 포인터가 속한 컬럼의
        // 마지막 카드를 우선해야 한다. 해당 축을 덮는 일반 폭 슬롯이 있을 때만 같은 lane으로 제한한다.
        // 전체 폭 폴더만 포인터를 덮거나 빈 컬럼처럼 기준 슬롯이 없으면 전체 중 가장 가까운 항목으로 폴백한다.
        bool HasPointerOnCrossAxis(Rect bounds)
        {
            return _horizontal
                ? pointer.Y >= bounds.Top && pointer.Y <= bounds.Bottom
                : pointer.X >= bounds.Left && pointer.X <= bounds.Right;
        }

        bool HasPointerOnMainAxis(Rect bounds)
        {
            return _horizontal
                ? pointer.X >= bounds.Left && pointer.X <= bounds.Right
                : pointer.Y >= bounds.Top && pointer.Y <= bounds.Bottom;
        }

        double minCrossSize = candidates.Min(candidate =>
            _horizontal ? candidate.Bounds.Height : candidate.Bounds.Width);
        bool hasSameLane = candidates.Any(candidate =>
            HasPointerOnCrossAxis(candidate.Bounds)
            && HasPointerOnMainAxis(candidate.Bounds)
            && (_horizontal ? candidate.Bounds.Height : candidate.Bounds.Width) <= minCrossSize * 1.5);
        double bestDistance = double.PositiveInfinity;
        foreach (var candidate in candidates)
        {
            var bounds = candidate.Bounds;
            if (hasSameLane && !HasPointerOnCrossAxis(bounds)) continue;
            double dx = pointer.X < bounds.Left
                ? bounds.Left - pointer.X
                : pointer.X > bounds.Right ? pointer.X - bounds.Right : 0;
            double dy = pointer.Y < bounds.Top
                ? bounds.Top - pointer.Y
                : pointer.Y > bounds.Bottom ? pointer.Y - bounds.Bottom : 0;
            double distance = dx * dx + dy * dy;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            target = candidate.Slot;
        }

        if (target == null) return false;
        var targetBounds = HitTestPrimaryBounds(target);
        after = _horizontal
            ? pointer.X >= targetBounds.Left + targetBounds.Width / 2
            : pointer.Y >= targetBounds.Top + targetBounds.Height / 2;
        after = ResolveReorderAfter(target, after, pointer);
        return true;
    }

    private bool ResolveReorderAfter(Slot target, bool proposedAfter, Point pointer)
    {
        if (!ReferenceEquals(_reorderPreviewTarget, target)) return proposedAfter;

        var bounds = HitTestPrimaryBounds(target);
        double axisPosition = _horizontal ? pointer.X : pointer.Y;
        double axisStart = _horizontal ? bounds.Left : bounds.Top;
        double axisSize = _horizontal ? bounds.Width : bounds.Height;
        if (UsesQuarterReorderHysteresis(target))
        {
            // 폴더 앞/뒤 재정렬이 시작된 뒤에는 반대편 25%까지 상태를 유지한다.
            // 레이아웃 이동으로 보더에 다시 닿아도 즉시 반전하지 않는다.
            return _reorderPreviewAfter
                ? axisPosition >= axisStart + axisSize * 0.25
                : axisPosition > axisStart + axisSize * 0.75;
        }

        double midpoint = axisStart + axisSize / 2;
        double hysteresis = Math.Min(8, axisSize * 0.2);

        // 현재 방향을 유지하다가 중앙선을 충분히 넘어선 경우에만 반전한다.
        // 경계 부근 1~2px 입력 흔들림과 애니메이션 프레임 변화로 인한 위/아래 왕복을 막는다.
        return _reorderPreviewAfter
            ? axisPosition >= midpoint - hysteresis
            : axisPosition > midpoint + hysteresis;
    }

    private bool UsesQuarterReorderHysteresis(Slot target)
        => _useQuarterReorderHysteresis?.Invoke(_source, target.Item) == true;

    public bool IsReorderPreviewTarget(T target)
        => _reorderPreviewTarget != null
           && ReferenceEquals(_reorderPreviewTarget.Item, target);

    private static Rect CapturedPrimaryBounds(Slot slot) => new(
        slot.PrimaryLeft,
        slot.PrimaryTop,
        slot.PrimaryWidth,
        slot.PrimaryHeight);

    private Rect CurrentPrimaryBounds(Slot slot)
    {
        try
        {
            var point = slot.Element.TransformToAncestor(_coordHost).Transform(new Point());
            return new Rect(
                point.X,
                point.Y,
                Math.Max(1, slot.Element.ActualWidth),
                Math.Max(1, slot.Element.ActualHeight));
        }
        catch
        {
            return CapturedPrimaryBounds(slot);
        }
    }

    private Rect HitTestPrimaryBounds(Slot slot)
    {
        return _useLogicalHitTestBounds
            ? LogicalPrimaryBounds(slot)
            : CurrentPrimaryBounds(slot);
    }

    private Rect LogicalPrimaryBounds(Slot slot)
    {
        var bounds = CurrentPrimaryBounds(slot);
        var transform = slot.Element.RenderTransform switch
        {
            TranslateTransform translate => translate,
            TransformGroup group => group.Children.OfType<TranslateTransform>().FirstOrDefault(),
            _ => null,
        };
        if (transform != null)
            bounds.Offset(-transform.X, -transform.Y);
        return bounds;
    }

    private int TargetIndexAround(Slot target, bool after)
    {
        int index = 0;
        foreach (var slot in _slots)
        {
            if (ReferenceEquals(slot, target)) return index + (after ? 1 : 0);
            if (!ReferenceEquals(slot, _slots[_sourceIndex])) index++;
        }
        return index;
    }

    private bool SetDropIntoTarget(Slot target)
    {
        if (ReferenceEquals(_dropIntoTarget, target)) return false;
        _dropIntoTarget = target;
        _dropIntoPreviewChanged?.Invoke(target.Item, target.Element);
        return true;
    }

    private void ClearDropIntoTarget()
    {
        if (_dropIntoTarget == null) return;
        _dropIntoTarget = null;
        _dropIntoPreviewChanged?.Invoke(null, null);
        _needsReapply = true;
    }

    private void SetReorderPreview(Slot target, bool after)
    {
        if (ReferenceEquals(_reorderPreviewTarget, target) && _reorderPreviewAfter == after)
        {
            _reorderPreviewChanged?.Invoke(target.Item, target.Element, after);
            return;
        }

        _reorderPreviewTarget = target;
        _reorderPreviewAfter = after;
        _reorderPreviewChanged?.Invoke(target.Item, target.Element, after);
    }

    private void ClearReorderPreview(bool notify = true)
    {
        if (_reorderPreviewTarget == null) return;
        _reorderPreviewTarget = null;
        _reorderPreviewAfter = false;
        if (notify) _reorderPreviewChanged?.Invoke(null, null, false);
    }

    private void ResetDisplacementPreview()
    {
        foreach (var slot in _slots) AnimateSlot(slot, 0);
    }

    private void UpdateGridPlaceholder(double cx, double cy)
    {
        int newColumn = cx >= _gridMidX ? 1 : 0;
        if (newColumn != _targetColumn)
        {
            _targetColumn = newColumn;
            _targetIndex = ComputeCapturedColumnTarget(newColumn, cy);
            _lastPointerAxisPosition = cy;
            _lastTransitionDirection = 0;
            ApplyGridDisplacement();
            return;
        }

        var source = _slots[_sourceIndex];
        double previousPosition = _lastPointerAxisPosition ?? (source.Top + _grabOffsetY);
        _lastPointerAxisPosition = cy;
        int movementDirection = cy > previousPosition + 0.25
            ? 1
            : cy < previousPosition - 0.25 ? -1 : 0;

        var targetSlots = _slots
            .Where(slot => !ReferenceEquals(slot, source) && ColumnOf(slot) == newColumn)
            .OrderBy(slot => slot.Top)
            .ToList();
        int newTarget = _targetIndex;
        if (movementDirection > 0 && newTarget < targetSlots.Count)
        {
            var target = targetSlots[newTarget];
            double threshold = target.Top + target.Height / 2 + GridDisplacement(target);
            if (_lastTransitionDirection < 0)
                threshold += LiveReversalHysteresis;
            if (cy >= threshold)
                newTarget++;
        }
        else if (movementDirection < 0 && newTarget > 0)
        {
            var target = targetSlots[newTarget - 1];
            double threshold = target.Top + target.Height / 2 + GridDisplacement(target);
            if (_lastTransitionDirection > 0)
                threshold -= LiveReversalHysteresis;
            if (cy <= threshold)
                newTarget--;
        }

        if (newTarget == _targetIndex) return;
        _lastTransitionDirection = Math.Sign(newTarget - _targetIndex);
        _targetIndex = newTarget;
        ApplyGridDisplacement();
    }

    private int ComputeCapturedColumnTarget(int column, double center)
    {
        int target = 0;
        foreach (var slot in _slots)
        {
            if (ReferenceEquals(slot, _slots[_sourceIndex]) || ColumnOf(slot) != column) continue;
            if (center >= slot.Top + slot.Height / 2) target++;
        }
        return target;
    }


    /// <summary>그리드: 목표 컬럼 안에서 마우스 Y가 들어갈 삽입 위치(0-based, source 제외).
    /// = 같은 컬럼의 (source 제외) 카드 중 중앙이 마우스보다 위에 있는 개수.</summary>
    private int ComputeColumnTarget(int column, double cy)
    {
        int target = 0;
        for (int i = 0; i < _slots.Count; i++)
        {
            if (i == _sourceIndex) continue;
            var s = _slots[i];
            if (ColumnOf(s) != column) continue;
            var bounds = CurrentPrimaryBounds(s);
            if (cy >= bounds.Top + bounds.Height / 2) target++;
        }
        return target;
    }

    /// <summary>해당 컬럼 안에서 slotIdx 카드의 0-based 위치(source 제외).</summary>
    private int WithinColumnIndex(int slotIdx, int column)
    {
        int w = 0;
        for (int i = 0; i < slotIdx; i++)
        {
            if (i == _sourceIndex) continue;
            if (ColumnOf(_slots[i]) == column) w++;
        }
        return w;
    }

    /// <summary>그리드(2열) 시프트 애니메이션. 좌/우 컬럼을 독립 세로 리스트로 다룬다.
    /// - 같은 컬럼 재정렬: 1열처럼 source 가 비집고 들어갈 자리만큼 사이 카드를 민다.
    /// - 다른 컬럼으로 이동: 원래 컬럼은 source 아래 카드를 위로 당겨 빈자리를 메우고,
    ///   목표 컬럼은 삽입 위치 이후 카드를 아래로 밀어 자리를 연다.</summary>
    private void ApplyGridDisplacement()
    {
        for (int i = 0; i < _slots.Count; i++)
        {
            if (i == _sourceIndex) { AnimateSlot(_slots[i], 0); continue; }
            AnimateSlot(_slots[i], GridDisplacement(_slots[i]));
        }
    }

    private double GridDisplacement(Slot slot)
    {
        // ApplyGridDisplacement와 드롭 임계값이 반드시 같은 최종 위치를 공유해야
        // 밀려난 카드의 원래 위치가 복귀 기준으로 재사용되지 않는다.
        var source = _slots[_sourceIndex];
        if (ReferenceEquals(slot, source)) return 0;

        int originColumn = ColumnOf(source);
        int column = ColumnOf(slot);
        int sourceWithin = WithinColumnIndex(_sourceIndex, originColumn);
        int within = WithinColumnIndex(_slots.IndexOf(slot), column);
        double sourceHeight = source.Height;
        if (column == originColumn && column == _targetColumn)
        {
            if (_targetIndex < sourceWithin && within >= _targetIndex && within < sourceWithin)
                return sourceHeight;
            if (_targetIndex > sourceWithin && within >= sourceWithin && within < _targetIndex)
                return -sourceHeight;
        }
        else if (column == originColumn)
        {
            if (within >= sourceWithin) return -sourceHeight;
        }
        else if (column == _targetColumn)
        {
            if (within >= _targetIndex) return sourceHeight;
        }
        return 0;
    }

    private int ComputeTargetIndex(double center)
    {
        if (_exactFollow)
        {
            // 드래그 카드의 진행 가장자리(아래 이웃엔 아랫변, 위 이웃엔 윗변)가 그 이웃의
            // 중점을 넘을 때 = 이웃을 절반 이상 덮었을 때만 넘어선 것으로 카운트.
            // 위·아래 모두 '이웃 절반'에서 대칭으로 순서가 바뀌고, 카드 높이가 달라도 동작.
            double half = AxisSize(_slots[_sourceIndex]) / 2;
            int target = 0;
            for (int i = 0; i < _slots.Count; i++)
            {
                if (i == _sourceIndex) continue;
                var bounds = CurrentPrimaryBounds(_slots[i]);
                double mid = _horizontal
                    ? bounds.Left + bounds.Width / 2
                    : bounds.Top + bounds.Height / 2;
                double edge = i < _sourceIndex ? center - half : center + half;
                if (edge >= mid) target++;
            }
            return target;
        }
        for (int i = 0; i < _slots.Count; i++)
        {
            if (i == _sourceIndex) continue;
            var bounds = CurrentPrimaryBounds(_slots[i]);
            double mid = _horizontal
                ? bounds.Left + bounds.Width / 2
                : bounds.Top + bounds.Height / 2;
            if (center < mid) return i;
        }
        return _slots.Count;
    }

    private double RowPitch()
    {
        if (_sourceIndex + 1 < _slots.Count)
        {
            var shift = AxisPos(_slots[_sourceIndex + 1]) - AxisPos(_slots[_sourceIndex]);
            if (shift >= 1) return shift;
        }
        double sourceSize = AxisSize(_slots[_sourceIndex]);
        if (_sourceIndex - 1 >= 0)
        {
            var aboveBottom = AxisPos(_slots[_sourceIndex - 1]) + AxisSize(_slots[_sourceIndex - 1]);
            var gap = AxisPos(_slots[_sourceIndex]) - aboveBottom;
            return sourceSize + Math.Max(0, gap);
        }
        return sourceSize;
    }

    private void ApplyDisplacement()
    {
        if (_suppressDisplacement) return;
        for (int i = 0; i < _slots.Count; i++)
        {
            if (i == _sourceIndex) { AnimateSlot(_slots[i], 0); continue; }
            AnimateSlot(_slots[i], FixedDisplacement(_slots[i]));
        }
    }

    public async Task FinishAsync(bool commit)
    {
        if (_finished) return;
        _finished = true;

        var dropIntoTarget = _dropIntoTarget;
        bool hadLiveReorderPreview = _reorderPreviewTarget != null;
        bool committed = false;
        try
        {
            if (!commit) return;
            if (dropIntoTarget != null)
            {
                if (_onDropInto != null)
                {
                    try
                    {
                        await _onDropInto(_source, dropIntoTarget.Item);
                        committed = true;
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ReorderDrag child commit failed: {ex}"); }
                }
                return;
            }

            if (IsGrid)
            {
                // 그리드: 프리뷰 변환을 유지한 채 모델 열/순서를 먼저 확정한다. 시각 상태를 먼저
                // 원복하면 저장·컬렉션 동기화 동안 원래 폴더 높이가 한 프레임 노출된다.
                try
                {
                    await _onCommit(_source, _targetColumn, _targetIndex);
                    committed = true;
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ReorderDrag grid commit failed: {ex}"); }
                return;
            }

            if (_commitUnchanged || _targetIndex != _sourceIndex)
            {
                int hostSource = SlotToHostIndex(_sourceIndex, skipSource: false);
                int hostTarget = SlotToHostIndex(_targetIndex, skipSource: !_exactFollow);
                try
                {
                    await _onCommit(_source, hostTarget, hostSource);
                    committed = true;
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ReorderDrag commit failed: {ex}"); }
            }
        }
        finally
        {
            ClearDropIntoTarget();
            // 라이브 순서 프리뷰가 성공적으로 커밋된 경우 null 콜백은 원순서를 다시 적용하므로
            // 내부 마킹만 지운다. 취소·실패 때만 콜백으로 원래 레이아웃을 복원한다.
            ClearReorderPreview(notify: !(committed && hadLiveReorderPreview));
            _ghost.Dispose();
            foreach (var slot in _slots)
                foreach (var element in slot.Elements)
                    if (_suppressDisplacement) ResetPosition(element);
                    else ResetAxis(element);
        }
    }

    private int SlotToHostIndex(int slotIdx, bool skipSource)
    {
        int count = 0;
        for (int i = 0; i < slotIdx && i < _slots.Count; i++)
        {
            if (skipSource && i == _sourceIndex) continue;
            count++;
        }
        return count;
    }

    private void AnimateSlot(Slot slot, double offset)
    {
        foreach (var element in slot.Elements) AnimateAxis(element, offset);
    }
    private void AnimateAxis(UIElement el, double offset)
    {
        var tt = EnsureTranslate(el);
        double cur = _horizontal ? tt.X : tt.Y;
        if (Math.Abs(cur - offset) < 0.5) return;
        var anim = new DoubleAnimation
        {
            To = offset,
            Duration = TimeSpan.FromMilliseconds(AnimMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        tt.BeginAnimation(_horizontal ? TranslateTransform.XProperty : TranslateTransform.YProperty,
            anim, HandoffBehavior.SnapshotAndReplace);
    }

    private void ResetAxis(UIElement el)
    {
        var tt = EnsureTranslate(el);
        var prop = _horizontal ? TranslateTransform.XProperty : TranslateTransform.YProperty;
        tt.BeginAnimation(prop, null);
        if (_horizontal) tt.X = 0; else tt.Y = 0;
    }

    private void AnimatePosition(UIElement el, double offsetX, double offsetY)
    {
        var tt = EnsureTranslate(el);
        AnimatePositionAxis(tt, TranslateTransform.XProperty, offsetX);
        AnimatePositionAxis(tt, TranslateTransform.YProperty, offsetY);
    }

    private static void AnimatePositionAxis(
        TranslateTransform transform,
        DependencyProperty property,
        double offset)
    {
        if (Math.Abs(offset) < 0.5) return;
        var animation = new DoubleAnimation
        {
            From = offset,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(AnimMs),
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        transform.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void ResetPosition(UIElement el)
    {
        var tt = EnsureTranslate(el);
        tt.BeginAnimation(TranslateTransform.XProperty, null);
        tt.BeginAnimation(TranslateTransform.YProperty, null);
        tt.X = 0;
        tt.Y = 0;
    }

    private static TranslateTransform EnsureTranslate(UIElement el)
    {
        if (el.RenderTransform is TranslateTransform t) return t;
        if (el.RenderTransform is TransformGroup g)
        {
            var existing = g.Children.OfType<TranslateTransform>().FirstOrDefault();
            if (existing != null) return existing;
            var added = new TranslateTransform();
            g.Children.Add(added);
            return added;
        }
        var nt = new TranslateTransform();
        if (el.RenderTransform != null && el.RenderTransform != Transform.Identity)
        {
            var grp = new TransformGroup();
            grp.Children.Add(el.RenderTransform);
            grp.Children.Add(nt);
            el.RenderTransform = grp;
        }
        else el.RenderTransform = nt;
        return nt;
    }
}

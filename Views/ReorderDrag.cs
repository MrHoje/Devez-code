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
    private readonly double? _hitTestXOverride; // 고스트는 실제 포인터를 따르고 드롭 순서 판정 X만 고정.
    private int _targetIndex;             // 1축: host 인덱스 / 그리드: 목표 컬럼 내 삽입 위치
    private int _targetColumn;            // 그리드 전용: 목표 컬럼(0/1)
    private Slot? _dropIntoTarget;         // 중앙 50%: 자식 드롭 프리뷰/커밋 대상.
    private Slot? _reorderPreviewTarget;
    private bool _reorderPreviewAfter;
    private bool _finished;
    private bool _suppressed;             // 크로스 패널 드래그 중 반대 패널 위 → 이 리스트 프리뷰 억제.
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
        Action<T?, FrameworkElement?, bool>? reorderPreviewChanged,
        double? hitTestXOverride)
    {
        _coordHost = coordHost; _slots = slots; _source = source; _sourceIndex = sourceIndex;
        _ghost = ghost; _onCommit = onCommit; _exactFollow = exactFollow; _horizontal = horizontal;
        _columns = columns; _gridMidX = gridMidX; _grabOffsetX = grabOffsetX; _grabOffsetY = grabOffsetY;
        _canDropInto = canDropInto; _dropIntoPreviewChanged = dropIntoPreviewChanged;
        _onDropInto = onDropInto; _commitUnchanged = commitUnchanged;
        _hitTestSlots = hitTestSlots; _suppressDisplacement = suppressDisplacement;
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
        double? hitTestXOverride = null)
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
                    var elementBounds = new Rect(
                        point.X,
                        point.Y,
                        Math.Max(1, element.ActualWidth),
                        Math.Max(1, element.ActualHeight));
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
            ghost = DragHelper.BeginManualDrag(ghostSource ?? sourceElement, sourceElement);
        }
        if (ghost == null) return null;

        return new ReorderDrag<T>(coordHost, captured, source, srcIdx, ghost, onCommit, exactFollow, horizontal,
            columns, gridMidX, grabPt.X, grabPt.Y, canDropInto, dropIntoPreviewChanged, onDropInto, commitUnchanged,
            hitTestSlots, suppressDisplacement, reorderPreviewChanged, hitTestXOverride);
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

    public void Update(MouseEventArgs e)
    {
        if (_finished) return;
        _ghost.MoveToMouse();
        if (_suppressed) return; // 반대 패널 위 → 이 리스트 프리뷰 억제(고스트만 이동).
        if (IsGrid)
        {
            // 2열: 드래그 카드 중심의 X로 목표 컬럼을, Y로 그 컬럼 안의 삽입 위치를 정한다.
            // 좌/우 컬럼은 각각 독립된 세로 리스트로 시프트 애니메이션한다 — 같은 컬럼이면 그 안에서
            // 재정렬, 다른 컬럼으로 넘기면 원래 컬럼은 빈자리를 위로 메우고 목표 컬럼은 자리를 연다.
            var p = e.GetPosition(_coordHost);
            var src = _slots[_sourceIndex];
            double cx = p.X - _grabOffsetX + src.Width / 2;
            double cy = p.Y - _grabOffsetY + src.Height / 2;
            int newCol = cx >= _gridMidX ? 1 : 0;
            int newIdx = ComputeColumnTarget(newCol, cy);
            if (newCol == _targetColumn && newIdx == _targetIndex) return;
            _targetColumn = newCol; _targetIndex = newIdx;
            ApplyGridDisplacement();
            return;
        }

        var pointer = e.GetPosition(_coordHost);
        if (_hitTestXOverride is double hitTestX) pointer.X = hitTestX;
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
            else if (TryGetNearestCapturedTarget(pointer, out var nearestTarget, out bool nearestAfter))
            {
                // 2열의 컬럼 사이/짧은 컬럼 아래 빈 공간은 라이브 배치가 움직여도 변하지 않는
                // 시작 슬롯을 기준으로 가장 가까운 항목 앞뒤를 선택한다.
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

    private enum DropZone { Before, Into, After }

    private Rect CurrentBounds(Slot slot)
    {
        if (!_hitTestSlots || !_suppressDisplacement)
            return new Rect(slot.PrimaryLeft, slot.PrimaryTop, slot.PrimaryWidth, slot.PrimaryHeight);

        try
        {
            var point = slot.Element.TransformToAncestor(_coordHost).Transform(new Point(0, 0));
            return new Rect(point.X, point.Y, Math.Max(1, slot.Element.ActualWidth), Math.Max(1, slot.Element.ActualHeight));
        }
        catch
        {
            return new Rect(slot.PrimaryLeft, slot.PrimaryTop, slot.PrimaryWidth, slot.PrimaryHeight);
        }
    }

    /// <summary>자식 드롭을 허용한 항목은 포인터 기준 상단 25%=앞, 중앙 50%=자식, 하단 25%=뒤.</summary>
    private bool TryGetDropZone(Point pointer, out Slot target, out DropZone zone)
    {
        target = null!;
        zone = DropZone.Before;
        if (_canDropInto == null && !_hitTestSlots) return false;

        foreach (var slot in _slots)
        {
            if (ReferenceEquals(slot, _slots[_sourceIndex])) continue;
            bool canDropInto = _canDropInto?.Invoke(_source, slot.Item) == true;
            if (!canDropInto && !_hitTestSlots) continue;
            // 시프트 애니메이션으로 대상 카드가 움직여도 드롭 구역까지 같이 도망가지 않게
            // 드래그 시작 시 캡처한 논리 슬롯을 사용한다. 중앙 진입 시 displacement가 원복되고
            // 실제 대상 SessionRow가 원래 자리로 돌아오며 보더 하이라이트된다.
            var bounds = CurrentBounds(slot);
            if (!bounds.Contains(pointer)) continue;

            double relative = _horizontal
                ? (pointer.X - bounds.Left) / bounds.Width
                : (pointer.Y - bounds.Top) / bounds.Height;
            zone = canDropInto
                ? relative < 0.25
                    ? DropZone.Before
                    : relative > 0.75 ? DropZone.After : DropZone.Into
                : relative < 0.5
                    ? DropZone.Before
                    : DropZone.After;
            target = slot;
            return true;
        }
        return false;
    }

    private bool TryGetOuterDropTarget(Point pointer, out Slot target, out bool after)
    {
        target = null!;
        after = false;
        var candidates = _slots
            .Where(slot => !ReferenceEquals(slot, _slots[_sourceIndex]))
            .Select(slot => (Slot: slot, Bounds: CurrentBounds(slot)))
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

    private bool TryGetNearestCapturedTarget(Point pointer, out Slot target, out bool after)
    {
        target = null!;
        after = false;
        var candidates = _slots
            .Where(slot => !ReferenceEquals(slot, _slots[_sourceIndex]))
            .ToList();
        if (candidates.Count == 0) return false;

        // 2열의 짧은 컬럼 아래에서는 같은 Y의 반대 컬럼 카드보다, 포인터가 속한 컬럼의
        // 마지막 카드를 우선해야 한다. 해당 축을 덮는 일반 폭 슬롯이 있을 때만 같은 lane으로 제한한다.
        // 전체 폭 폴더만 포인터를 덮거나 빈 컬럼처럼 기준 슬롯이 없으면 전체 중 가장 가까운 항목으로 폴백한다.
        bool HasPointerOnCrossAxis(Slot slot)
        {
            var bounds = CapturedPrimaryBounds(slot);
            return _horizontal
                ? pointer.Y >= bounds.Top && pointer.Y <= bounds.Bottom
                : pointer.X >= bounds.Left && pointer.X <= bounds.Right;
        }

        double minCrossSize = candidates.Min(slot => _horizontal ? slot.PrimaryHeight : slot.PrimaryWidth);
        bool hasSameLane = candidates.Any(slot =>
            HasPointerOnCrossAxis(slot)
            && (_horizontal ? slot.PrimaryHeight : slot.PrimaryWidth) <= minCrossSize * 1.5);
        double bestDistance = double.PositiveInfinity;
        foreach (var slot in candidates)
        {
            if (hasSameLane && !HasPointerOnCrossAxis(slot)) continue;
            var bounds = CapturedPrimaryBounds(slot);
            double dx = pointer.X < bounds.Left
                ? bounds.Left - pointer.X
                : pointer.X > bounds.Right ? pointer.X - bounds.Right : 0;
            double dy = pointer.Y < bounds.Top
                ? bounds.Top - pointer.Y
                : pointer.Y > bounds.Bottom ? pointer.Y - bounds.Bottom : 0;
            double distance = dx * dx + dy * dy;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            target = slot;
        }

        if (target == null) return false;
        after = _horizontal
            ? pointer.X >= target.PrimaryLeft + target.PrimaryWidth / 2
            : pointer.Y >= target.PrimaryTop + target.PrimaryHeight / 2;
        return true;
    }

    private static Rect CapturedPrimaryBounds(Slot slot) => new(
        slot.PrimaryLeft,
        slot.PrimaryTop,
        slot.PrimaryWidth,
        slot.PrimaryHeight);

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

    private void ClearReorderPreview()
    {
        if (_reorderPreviewTarget == null) return;
        _reorderPreviewTarget = null;
        _reorderPreviewAfter = false;
        _reorderPreviewChanged?.Invoke(null, null, false);
    }

    private void ResetDisplacementPreview()
    {
        foreach (var slot in _slots) AnimateSlot(slot, 0);
    }

    /// <summary>그리드: 목표 컬럼 안에서 드래그 카드 중심 Y 가 들어갈 삽입 위치(0-based, source 제외).
    /// = 같은 컬럼의 (source 제외) 카드 중 중심 Y 가 위에 있는 개수.</summary>
    private int ComputeColumnTarget(int column, double cy)
    {
        int target = 0;
        for (int i = 0; i < _slots.Count; i++)
        {
            if (i == _sourceIndex) continue;
            var s = _slots[i];
            if (ColumnOf(s) != column) continue;
            if (cy >= s.Top + s.Height / 2) target++;
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
        int originCol = ColumnOf(_slots[_sourceIndex]);
        double srcH = _slots[_sourceIndex].Height;
        int srcWithin = WithinColumnIndex(_sourceIndex, originCol);

        for (int i = 0; i < _slots.Count; i++)
        {
            if (i == _sourceIndex) { AnimateSlot(_slots[i], 0); continue; }
            int col = ColumnOf(_slots[i]);
            int w = WithinColumnIndex(i, col);
            double to = 0;
            if (col == originCol && col == _targetColumn)
            {
                // 같은 컬럼 내 재정렬 (w 는 source 제외 인덱스).
                if (_targetIndex < srcWithin && w >= _targetIndex && w < srcWithin) to = srcH;
                else if (_targetIndex > srcWithin && w >= srcWithin && w < _targetIndex) to = -srcH;
            }
            else if (col == originCol)
            {
                if (w >= srcWithin) to = -srcH; // source 가 떠난 컬럼: 아래 카드 위로 당김
            }
            else if (col == _targetColumn)
            {
                if (w >= _targetIndex) to = srcH; // 들어올 컬럼: 삽입 위치 이후 아래로 밂
            }
            AnimateSlot(_slots[i], to);
        }
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
                double mid = AxisPos(_slots[i]) + AxisSize(_slots[i]) / 2;
                double edge = i < _sourceIndex ? center - half : center + half;
                if (edge >= mid) target++;
            }
            return target;
        }
        for (int i = 0; i < _slots.Count; i++)
        {
            if (i == _sourceIndex) continue;
            if (center < AxisPos(_slots[i]) + AxisSize(_slots[i]) / 2) return i;
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
        double shift = RowPitch();
        for (int i = 0; i < _slots.Count; i++)
        {
            if (i == _sourceIndex) { AnimateSlot(_slots[i], 0); continue; }
            double to = 0;
            if (_targetIndex < _sourceIndex && i >= _targetIndex && i < _sourceIndex) to = shift;
            else if (_targetIndex > _sourceIndex && i > _sourceIndex && i <= _targetIndex) to = -shift;
            AnimateSlot(_slots[i], to);
        }
    }

    public async Task FinishAsync(bool commit)
    {
        if (_finished) return;
        _finished = true;

        var dropIntoTarget = _dropIntoTarget;
        ClearDropIntoTarget();
        ClearReorderPreview();

        _ghost.Dispose();
        foreach (var slot in _slots)
            foreach (var element in slot.Elements)
                if (_suppressDisplacement) ResetPosition(element);
                else ResetAxis(element);

        if (!commit) return;
        if (dropIntoTarget != null)
        {
            if (_onDropInto != null)
            {
                try { await _onDropInto(_source, dropIntoTarget.Item); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ReorderDrag child commit failed: {ex}"); }
            }
            return;
        }

        if (IsGrid)
        {
            // 그리드: (목표 컬럼, 컬럼 내 삽입 위치)를 그대로 전달. 변경 여부 판단은 commit 콜백이 한다.
            try { await _onCommit(_source, _targetColumn, _targetIndex); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ReorderDrag grid commit failed: {ex}"); }
            return;
        }

        if (_commitUnchanged || _targetIndex != _sourceIndex)
        {
            int hostSource = SlotToHostIndex(_sourceIndex, skipSource: false);
            int hostTarget = SlotToHostIndex(_targetIndex, skipSource: !_exactFollow);
            try { await _onCommit(_source, hostTarget, hostSource); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ReorderDrag commit failed: {ex}"); }
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

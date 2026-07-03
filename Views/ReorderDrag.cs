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

    // Left/Top/Width/Height: coordHost 기준 실제 사각형. 1D(축) 계산은 _horizontal 로 골라 쓴다.
    private sealed record Slot(T Item, FrameworkElement Element, double Left, double Top, double Width, double Height);

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
    private readonly int _splitBoundary;  // >=0 이면 slot [0,_splitBoundary)=좌 그룹, 나머지=우 그룹(RowPitch 가 그룹 경계=큰 간격을 안 넘게).
    private int _targetIndex;             // 1축: host 인덱스 / 그리드: 목표 컬럼 내 삽입 위치
    private int _targetColumn;            // 그리드 전용: 목표 컬럼(0/1)
    private bool _finished;
    private bool _suppressed;             // 크로스 패널 드래그 중 반대 패널 위 → 이 리스트 프리뷰 억제.

    private bool IsGrid => _columns > 1;
    private double AxisPos(Slot s) => _horizontal ? s.Left : s.Top;
    private double AxisSize(Slot s) => _horizontal ? s.Width : s.Height;
    private int ColumnOf(Slot s) => s.Left + s.Width / 2 >= _gridMidX ? 1 : 0;

    private ReorderDrag(UIElement coordHost, List<Slot> slots, T source, int sourceIndex,
        DragHelper.IGhost ghost, Func<T, int, int, Task> onCommit, bool exactFollow, bool horizontal,
        int columns, double gridMidX, double grabOffsetX, double grabOffsetY, int splitBoundary)
    {
        _coordHost = coordHost; _slots = slots; _source = source; _sourceIndex = sourceIndex;
        _ghost = ghost; _onCommit = onCommit; _exactFollow = exactFollow; _horizontal = horizontal;
        _columns = columns; _gridMidX = gridMidX; _grabOffsetX = grabOffsetX; _grabOffsetY = grabOffsetY;
        _splitBoundary = splitBoundary;
        _targetIndex = sourceIndex;
        _targetColumn = -1; // 그리드: 첫 Update 가 항상 displacement 를 적용하도록 미지정으로 시작.
    }

    public T Source => _source;

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
        int splitBoundary = -1)
    {
        var captured = new List<Slot>();
        foreach (var (item, el) in rows)
        {
            double left, top;
            try
            {
                var p = el.TransformToAncestor(coordHost).Transform(new Point(0, 0));
                left = p.X; top = p.Y;
            }
            catch { continue; }
            captured.Add(new Slot(item, el, left, top,
                Math.Max(1, el.ActualWidth), Math.Max(1, el.ActualHeight)));
        }
        // 그리드(2열): 컬럼(좌→우) 우선, 그 안에서 위→아래. 1축: 해당 축 위치.
        if (columns > 1)
            captured.Sort((a, b) =>
            {
                int ca = a.Left + a.Width / 2 >= gridMidX ? 1 : 0;
                int cb = b.Left + b.Width / 2 >= gridMidX ? 1 : 0;
                return ca != cb ? ca.CompareTo(cb) : a.Top.CompareTo(b.Top);
            });
        else
            captured.Sort((a, b) => (horizontal ? a.Left : a.Top).CompareTo(horizontal ? b.Left : b.Top));

        var srcIdx = captured.FindIndex(s => ReferenceEquals(s.Item, source));
        if (srcIdx < 0 || captured.Count < 2) return null;

        // ghostSource: ghost 이미지로 캡처할 visual (null이면 sourceElement 사용).
        // 슬롯에는 받침(Path) 자식이 포함되어 sourceElement(row) 자체로는 bitmap에 받침까지
        // 잡혀버리는 경우, 받침 없는 Border를 따로 지정해 ghost에서 받침을 제외한다 (devez 정합).
        // 잡은 지점의 source row 내부 오프셋(축) — 커서 raw 대신 '드래그 카드 중심'을 기준으로
        // 타깃을 판정하기 위함. 그래야 source 높이/잡은 위치와 무관하게 위/아래 모두 대칭으로
        // '이웃 카드 절반을 넘을 때' 순서가 바뀐다.
        var grabPt = Mouse.GetPosition(sourceElement);

        var ghost = DragHelper.BeginManualDrag(ghostSource ?? sourceElement, sourceElement);
        if (ghost == null) return null;

        return new ReorderDrag<T>(coordHost, captured, source, srcIdx, ghost, onCommit, exactFollow, horizontal,
            columns, gridMidX, grabPt.X, grabPt.Y, splitBoundary);
    }

    /// <summary>크로스 패널 드래그 중 커서가 반대 패널에 있을 때 호출 — 소스가 이 리스트에서 '나간' 것처럼
    /// 뒤 항목을 앞으로 당겨 빈자리를 메운다(압축). 고스트만 커서를 따라가고 이 리스트는 이 상태로 고정.
    /// off 로 돌아오면 원복하고 이후 Update 가 정상 재정렬 프리뷰를 재개한다.</summary>
    public void SuppressDisplacement(bool on)
    {
        if (_suppressed == on) return;
        _suppressed = on;
        if (on) { _targetIndex = _slots.Count - 1; ApplyDisplacement(); } // 압축=소스를 끝으로 보낸 변위(뒤 항목 앞당김)
        // off: 되돌리지 않고 둔다 — 뒤이어 호출되는 Update 가 커서 기준으로 즉시 재계산(원래자리 빈 채 안 남게).
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
        var cursor = _horizontal ? e.GetPosition(_coordHost).X : e.GetPosition(_coordHost).Y;
        var grabOffset = _horizontal ? _grabOffsetX : _grabOffsetY;
        // 커서 raw 대신 드래그 중인 카드의 중심을 기준점으로 사용 — 위/아래 대칭 판정.
        var draggedCenter = cursor - grabOffset + AxisSize(_slots[_sourceIndex]) / 2;
        var newTarget = ComputeTargetIndex(draggedCenter);
        if (newTarget == _targetIndex) return;
        _targetIndex = newTarget;
        ApplyDisplacement();
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
            if (i == _sourceIndex) { AnimateAxis(_slots[i].Element, 0); continue; }
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
            AnimateAxis(_slots[i].Element, to);
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

    /// <summary>슬롯 이동 피치. 분할 경계(_splitBoundary)를 건너뛰는 이웃(다른 그룹=먼 간격, 예: 두 패널 사이)은
    /// 피해 계산 — 그렇지 않으면 부풀린 피치로 이웃들이 과이동/겹침.</summary>
    private double RowPitch()
    {
        int p = _sourceIndex;
        bool SameGroup(int a, int b) => _splitBoundary < 0 || (a < _splitBoundary) == (b < _splitBoundary);
        if (p + 1 < _slots.Count && SameGroup(p, p + 1))
        {
            var shift = AxisPos(_slots[p + 1]) - AxisPos(_slots[p]);
            if (shift >= 1) return shift;
        }
        if (p - 1 >= 0 && SameGroup(p, p - 1))
        {
            var shift = AxisPos(_slots[p]) - AxisPos(_slots[p - 1]);
            if (shift >= 1) return shift;
        }
        double sourceSize = AxisSize(_slots[p]);
        if (p - 1 >= 0 && SameGroup(p, p - 1))
        {
            var aboveBottom = AxisPos(_slots[p - 1]) + AxisSize(_slots[p - 1]);
            var gap = AxisPos(_slots[p]) - aboveBottom;
            return sourceSize + Math.Max(0, gap);
        }
        return sourceSize;
    }

    private void ApplyDisplacement()
    {
        double shift = RowPitch();
        for (int i = 0; i < _slots.Count; i++)
        {
            if (i == _sourceIndex) { AnimateAxis(_slots[i].Element, 0); continue; }
            double to = 0;
            if (_targetIndex < _sourceIndex && i >= _targetIndex && i < _sourceIndex) to = shift;
            else if (_targetIndex > _sourceIndex && i > _sourceIndex && i <= _targetIndex) to = -shift;
            AnimateAxis(_slots[i].Element, to);
        }
    }

    public async Task FinishAsync(bool commit)
    {
        if (_finished) return;
        _finished = true;

        _ghost.Dispose();
        foreach (var s in _slots) ResetAxis(s.Element);

        if (!commit) return;

        if (IsGrid)
        {
            // 그리드: (목표 컬럼, 컬럼 내 삽입 위치)를 그대로 전달. 변경 여부 판단은 commit 콜백이 한다.
            try { await _onCommit(_source, _targetColumn, _targetIndex); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ReorderDrag grid commit failed: {ex}"); }
            return;
        }

        if (_targetIndex != _sourceIndex)
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

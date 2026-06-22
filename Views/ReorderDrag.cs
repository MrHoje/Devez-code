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

    private sealed record Slot(T Item, FrameworkElement Element, double Top, double Height);

    private readonly UIElement _coordHost;
    private readonly List<Slot> _slots;
    private readonly Func<T, int, int, Task> _onCommit;
    private readonly DragHelper.IGhost _ghost;
    private readonly T _source;
    private readonly int _sourceIndex;
    private readonly bool _exactFollow;
    private readonly bool _horizontal;
    private readonly double _grabOffset; // 잡은 지점의 source 내부 오프셋(축) — 드래그 카드 중심 계산용
    private int _targetIndex;
    private bool _finished;

    private ReorderDrag(UIElement coordHost, List<Slot> slots, T source, int sourceIndex,
        DragHelper.IGhost ghost, Func<T, int, int, Task> onCommit, bool exactFollow, bool horizontal, double grabOffset)
    {
        _coordHost = coordHost; _slots = slots; _source = source; _sourceIndex = sourceIndex;
        _ghost = ghost; _onCommit = onCommit; _exactFollow = exactFollow; _horizontal = horizontal;
        _grabOffset = grabOffset;
        _targetIndex = sourceIndex;
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
        FrameworkElement? ghostSource = null)
    {
        var captured = new List<Slot>();
        foreach (var (item, el) in rows)
        {
            double pos;
            try
            {
                var p = el.TransformToAncestor(coordHost).Transform(new Point(0, 0));
                pos = horizontal ? p.X : p.Y;
            }
            catch { continue; }
            double size = Math.Max(1, horizontal ? el.ActualWidth : el.ActualHeight);
            captured.Add(new Slot(item, el, pos, size));
        }
        captured.Sort((a, b) => a.Top.CompareTo(b.Top));

        var srcIdx = captured.FindIndex(s => ReferenceEquals(s.Item, source));
        if (srcIdx < 0 || captured.Count < 2) return null;

        // ghostSource: ghost 이미지로 캡처할 visual (null이면 sourceElement 사용).
        // 슬롯에는 받침(Path) 자식이 포함되어 sourceElement(row) 자체로는 bitmap에 받침까지
        // 잡혀버리는 경우, 받침 없는 Border를 따로 지정해 ghost에서 받침을 제외한다 (devez 정합).
        // 잡은 지점의 source row 내부 오프셋(축) — 커서 raw 대신 '드래그 카드 중심'을 기준으로
        // 타깃을 판정하기 위함. 그래야 source 높이/잡은 위치와 무관하게 위/아래 모두 대칭으로
        // '이웃 카드 절반을 넘을 때' 순서가 바뀐다.
        var grabPt = Mouse.GetPosition(sourceElement);
        double grabOffset = horizontal ? grabPt.X : grabPt.Y;

        var ghost = DragHelper.BeginManualDrag(ghostSource ?? sourceElement, sourceElement);
        if (ghost == null) return null;

        return new ReorderDrag<T>(coordHost, captured, source, srcIdx, ghost, onCommit, exactFollow, horizontal, grabOffset);
    }

    public void Update(MouseEventArgs e)
    {
        if (_finished) return;
        _ghost.MoveToMouse();
        var cursor = _horizontal ? e.GetPosition(_coordHost).X : e.GetPosition(_coordHost).Y;
        // 커서 raw 대신 드래그 중인 카드의 중심을 기준점으로 사용 — 위/아래 대칭 판정.
        var draggedCenter = cursor - _grabOffset + _slots[_sourceIndex].Height / 2;
        var newTarget = ComputeTargetIndex(draggedCenter);
        if (newTarget == _targetIndex) return;
        _targetIndex = newTarget;
        ApplyDisplacement();
    }

    private int ComputeTargetIndex(double center)
    {
        if (_exactFollow)
        {
            // 드래그 카드의 진행 가장자리(아래 이웃엔 아랫변, 위 이웃엔 윗변)가 그 이웃의
            // 중점을 넘을 때 = 이웃을 절반 이상 덮었을 때만 넘어선 것으로 카운트.
            // 위·아래 모두 '이웃 절반'에서 대칭으로 순서가 바뀌고, 카드 높이가 달라도 동작.
            double half = _slots[_sourceIndex].Height / 2;
            int target = 0;
            for (int i = 0; i < _slots.Count; i++)
            {
                if (i == _sourceIndex) continue;
                double mid = _slots[i].Top + _slots[i].Height / 2;
                double edge = i < _sourceIndex ? center - half : center + half;
                if (edge >= mid) target++;
            }
            return target;
        }
        for (int i = 0; i < _slots.Count; i++)
        {
            if (i == _sourceIndex) continue;
            if (center < _slots[i].Top + _slots[i].Height / 2) return i;
        }
        return _slots.Count;
    }

    private double RowPitch()
    {
        if (_sourceIndex + 1 < _slots.Count)
        {
            var shift = _slots[_sourceIndex + 1].Top - _slots[_sourceIndex].Top;
            if (shift >= 1) return shift;
        }
        double sourceSize = _slots[_sourceIndex].Height;
        if (_sourceIndex - 1 >= 0)
        {
            var aboveBottom = _slots[_sourceIndex - 1].Top + _slots[_sourceIndex - 1].Height;
            var gap = _slots[_sourceIndex].Top - aboveBottom;
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

        if (commit && _targetIndex != _sourceIndex)
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

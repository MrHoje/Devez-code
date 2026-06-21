namespace DevezCode.Models;

/// <summary>우측 패널 작업 큐의 한 항목(= 버블). doit MemoMessage 의 task queue 전용 슬림 버전.
/// - Content 만 가지며 태그/핀/별/코드블럭/시트/할일/타이머/첨부/URL/댓글/공유/일정등록/AI채팅 모두 제외.
/// - IsSelected / IsRightClickHighlighted: doit 채팅방의 다중선택/우클릭하이라이트 패턴 차용.</summary>
public sealed class TaskQueueItem : NotifyBase
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    private string _text = "";
    public string Text { get => _text; set => Set(ref _text, value); }

    private long _sortOrder;
    public long SortOrder { get => _sortOrder; set => Set(ref _sortOrder, value); }

    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    // ── 선택 (debit MemoMessage.IsSelected 패턴) ──
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value)) OnPropertyChanged(nameof(IsHighlighted)); }
    }

    // ── 우클릭 하이라이트 (선택 모드 진입 직전 한 버블만 잠깐 강조) ──
    private bool _isRightClickHighlighted;
    public bool IsRightClickHighlighted
    {
        get => _isRightClickHighlighted;
        set { if (Set(ref _isRightClickHighlighted, value)) OnPropertyChanged(nameof(IsHighlighted)); }
    }

    /// <summary>선택 또는 우클릭 하이라이트 중 하나라도 활성화되어 있으면 true.
    /// DataTemplate 의 SelectionRing/HoverRing 가시화에 사용.</summary>
    public bool IsHighlighted => IsSelected || IsRightClickHighlighted;
}

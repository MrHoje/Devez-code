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

    // ── 드래그 병합 도착지 표시 (devez IsMergeHoverIndicatorVisible 패턴) ──
    private bool _isMergeTarget;
    public bool IsMergeTarget
    {
        get => _isMergeTarget;
        set => Set(ref _isMergeTarget, value);
    }

    // ── 단일 클릭(비선택모드) 시 버블 아래 '작업지시' 버튼 노출 대상 ──
    private bool _isActionTarget;
    public bool IsActionTarget
    {
        get => _isActionTarget;
        set => Set(ref _isActionTarget, value);
    }

    // ── 인라인 수정모드 (devez MemoMessage.IsEditing 패턴 슬림) ──
    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set => Set(ref _isEditing, value);
    }

    // 수정모드 임시 편집 버퍼. 커밋 시 Text 로 반영, 취소 시 버림.
    private string _editText = "";
    public string EditText
    {
        get => _editText;
        set => Set(ref _editText, value);
    }
}

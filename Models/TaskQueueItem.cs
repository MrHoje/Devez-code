namespace DevezCode.Models;

/// <summary>우측 패널 작업 큐의 한 항목(= 버블). 텍스트 한 줄 + 생성 시각 + 정렬 순서.</summary>
public sealed class TaskQueueItem : NotifyBase
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    private string _text = "";
    public string Text { get => _text; set => Set(ref _text, value); }

    private long _sortOrder;
    public long SortOrder { get => _sortOrder; set => Set(ref _sortOrder, value); }

    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}

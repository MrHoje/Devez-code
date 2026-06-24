using System.Text.Json.Serialization;

namespace DevezCode.Models;

public sealed class TaskItem : NotifyBase
{
    public string Id { get; init; } = "";
    public string Description { get; init; } = "";

    private string _status = "pending";
    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    private string _priority = "medium";
    public string Priority
    {
        get => _priority;
        set => Set(ref _priority, value);
    }

    public string Source { get; init; } = "";

    [JsonIgnore]
    public bool IsInProgress => Status == "in_progress";

    [JsonIgnore]
    public bool IsCompleted => Status == "completed";

    [JsonIgnore]
    public bool IsCancelled => Status == "cancelled";

    [JsonIgnore]
    public bool IsPending => Status == "pending";

    [JsonIgnore]
    public string StatusPrefix => Status switch
    {
        "in_progress" => "[•] ",
        "completed" => "[✓] ",
        "cancelled" => "[-] ",
        _ => "[ ] "
    };

    [JsonIgnore]
    public double ItemOpacity => Status == "cancelled" ? 0.4 : 1.0;
}

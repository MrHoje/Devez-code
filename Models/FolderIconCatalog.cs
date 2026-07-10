namespace DevezCode.Models;

public sealed record FolderIconOption(string Key, string Label);

/// <summary>폴더에 사용할 수 있는 로컬 Lucide 아이콘 10개.</summary>
public static class FolderIconCatalog
{
    public const string DefaultKey = "IconFolder";

    public static IReadOnlyList<FolderIconOption> Options { get; } =
    [
        new("IconFolder", "폴더"),
        new("IconStar", "별"),
        new("IconFileText", "문서"),
        new("IconListTodo", "할 일"),
        new("IconKey", "열쇠"),
        new("IconAlarmClock", "알람"),
        new("IconSiren", "사이렌"),
        new("IconZap", "번개"),
        new("IconLightbulb", "전구"),
        new("IconSparkle", "AI"),
    ];

    private static readonly HashSet<string> ValidKeys =
        Options.Select(option => option.Key).ToHashSet(StringComparer.Ordinal);

    public static string Normalize(string? key) =>
        !string.IsNullOrWhiteSpace(key) && ValidKeys.Contains(key) ? key : DefaultKey;
}
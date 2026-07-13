using System.Text.Json.Serialization;

namespace DevezCode.Models;

public static class CommunityCategories
{
    public const string Improvement = "improvement";
    public const string Bug = "bug";
    public const string Other = "other";

    public static IReadOnlyList<CommunityCategoryOption> Options { get; } =
    [
        new(Improvement, "개선 요청"),
        new(Bug, "오류 제보"),
        new(Other, "기타"),
    ];

    public static string GetLabel(string value) => value switch
    {
        Improvement => "개선 요청",
        Bug => "오류 제보",
        _ => "기타",
    };
}

public sealed record CommunityCategoryOption(string Value, string Label);

public sealed class CommunityPostSummary
{
    [JsonPropertyName("post_id")]
    public Guid Id { get; init; }

    [JsonPropertyName("category")]
    public string Category { get; init; } = CommunityCategories.Other;

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("author_name")]
    public string AuthorName { get; init; } = "";

    [JsonPropertyName("is_private")]
    public bool IsPrivate { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("preview")]
    public string? Preview { get; init; }

    [JsonPropertyName("total_count")]
    public long TotalCount { get; init; }

    [JsonIgnore]
    public string CategoryLabel => CommunityCategories.GetLabel(Category);

    [JsonIgnore]
    public string DateLabel => CreatedAt.ToLocalTime().ToString("yyyy.MM.dd  HH:mm");

    [JsonIgnore]
    public string PreviewLabel => IsPrivate
        ? "비공개 글입니다."
        : string.IsNullOrWhiteSpace(Preview) ? "내용 없음" : Preview.ReplaceLineEndings(" ");
}

public sealed class CommunityPostDetail
{
    [JsonPropertyName("post_id")]
    public Guid Id { get; init; }

    [JsonPropertyName("category")]
    public string Category { get; init; } = CommunityCategories.Other;

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("body")]
    public string Body { get; init; } = "";

    [JsonPropertyName("author_name")]
    public string AuthorName { get; init; } = "";

    [JsonPropertyName("is_private")]
    public bool IsPrivate { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonIgnore]
    public string CategoryLabel => CommunityCategories.GetLabel(Category);

    [JsonIgnore]
    public string DateLabel => CreatedAt.ToLocalTime().ToString("yyyy.MM.dd  HH:mm");
}

public sealed class CommunityPostDraft
{
    public string Category { get; init; } = CommunityCategories.Improvement;
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string AuthorName { get; init; } = "";
    public string Password { get; init; } = "";
    public bool IsPrivate { get; init; }
}

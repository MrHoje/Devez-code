namespace DevezCode.Models;

/// <summary>한 사용량 윈도우(사용률 % + 초기화 시각).</summary>
public sealed class UsageWindow
{
    public double UsedPercent { get; init; }
    public DateTimeOffset? ResetsAt { get; init; }
}

/// <summary>codex(openai)·opencode-go 등 비-Claude provider 의 사용량 스냅샷.
/// Claude 는 <see cref="RateLimitSnapshot"/> 를 따로 쓰고, 이건 푸터의 추가 provider 칸에 쓴다.</summary>
public sealed class ProviderUsage
{
    /// <summary>provider 키 — "codex" | "opencode-go".</summary>
    public required string Provider { get; init; }

    public UsageWindow? Primary { get; init; }   // 단기(5h/hourly/rolling)
    public UsageWindow? Weekly  { get; init; }    // 주간
    public UsageWindow? Monthly { get; init; }    // opencode-go 만(툴팁 표기)

    /// <summary>플랜 라벨 등 부가 표시("OpenAI (Plus)"). 없으면 null.</summary>
    public string? PlanLabel { get; init; }
    /// <summary>오류/재로그인 안내 메시지. 정상이면 null.</summary>
    public string? Error { get; init; }

    public bool HasData => Primary != null || Weekly != null || Monthly != null;
}

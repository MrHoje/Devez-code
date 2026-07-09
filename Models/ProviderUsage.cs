namespace DevezCode.Models;

/// <summary>한 사용량 윈도우(사용률 % + 초기화 시각).</summary>
public sealed class UsageWindow
{
    public double UsedPercent { get; init; }
    public DateTimeOffset? ResetsAt { get; init; }
}

/// <summary>codex 초기화권(뱅크드 리셋) 1개 정보.</summary>
public sealed class ResetCredit
{
    /// <summary>"Full reset (Weekly + 5 hr)"</summary>
    public required string Title { get; init; }
    /// <summary>지급 시각.</summary>
    public DateTimeOffset? GrantedAt { get; init; }
    /// <summary>만료 시각. null 이면 만료 정보 없음.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>codex(openai)·opencode-go 등 비-Claude provider 의 사용량 스냅샷.
/// Claude 는 <see cref="RateLimitSnapshot"/> 를 따로 쓰고, 이건 푸터의 추가 provider 칸에 쓴다.</summary>
public sealed class ProviderUsage
{
    /// <summary>provider 키 — "codex" | "opencode-go" | "deepseek" | "grok".</summary>
    public required string Provider { get; init; }

    public UsageWindow? Primary { get; init; }   // 단기(5h/hourly/rolling)
    public UsageWindow? Weekly  { get; init; }    // 주간
    public UsageWindow? Monthly { get; init; }    // opencode-go 만(툴팁 표기)

    /// <summary>플랜 라벨 등 부가 표시("OpenAI (Plus)"). 없으면 null.</summary>
    public string? PlanLabel { get; init; }
    /// <summary>오류/재로그인 안내 메시지. 정상이면 null.</summary>
    public string? Error { get; init; }

    /// <summary>초기화권(뱅크드 리셋) 목록. codex 만 채워짐.</summary>
    public IReadOnlyList<ResetCredit> ResetCredits { get; init; } = Array.Empty<ResetCredit>();
    /// <summary>통화 잔액(DeepSeek 등 monetary-balance provider). 빈 배열이면 없음.</summary>
    public IReadOnlyList<BalanceInfo> Balances { get; init; } = Array.Empty<BalanceInfo>();
    public bool HasData => Primary != null || Weekly != null || Monthly != null || Balances.Count > 0;
}

/// <summary>통화 잔액 1개 통화 단위(DeepSeek).</summary>
public sealed class BalanceInfo
{
    /// <summary>"CNY" | "USD"</summary>
    public required string Currency { get; init; }
    /// <summary>총 잔액 문자열 ("110.00").</summary>
    public required string TotalBalance { get; init; }
    /// <summary>무료/프로모션 잔액 ("10.00").</summary>
    public required string GrantedBalance { get; init; }
    /// <summary>충전 잔액 ("100.00").</summary>
    public required string ToppedUpBalance { get; init; }
}

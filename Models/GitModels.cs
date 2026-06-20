namespace DevezCode.Models;

/// <summary>git status 한 줄 — 변경된 파일 하나.</summary>
public sealed class GitChange
{
    /// <summary>표시용 상태 글자(M/A/D/R/?/!).</summary>
    public string Status { get; init; } = "";
    /// <summary>저장소 루트 기준 상대 경로.</summary>
    public string Path { get; init; } = "";
    /// <summary>추적되지 않은(신규) 파일 여부 — diff 대신 파일 내용을 추가로 표시.</summary>
    public bool IsUntracked { get; init; }
    /// <summary>상태 글자 색(추가 녹색·삭제 빨강·수정 노랑·신규 회색).</summary>
    public string StatusColor => Status switch
    {
        "A" => "#3FB950",
        "D" => "#F85149",
        "M" => "#D29922",
        "R" => "#58A6FF",
        "?" => "#8B949E",
        _   => "#8B949E",
    };
}

/// <summary>diff 본문 한 줄과 그 종류.</summary>
public enum DiffLineKind { Context, Add, Del, Hunk, Header }

public sealed class DiffLine
{
    public string Text { get; init; } = "";
    public DiffLineKind Kind { get; init; }
}

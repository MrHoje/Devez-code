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

/// <summary>side-by-side diff 한쪽(좌=수정 전 / 우=수정 후) 셀의 종류.</summary>
public enum DiffCellKind { Empty, Context, Del, Add }

/// <summary>side-by-side diff 한 행 — 좌(수정 전)·우(수정 후) 셀을 함께 담는다.
/// IsHunk 면 hunk 머리글(@@ …) 한 줄을 양쪽에 걸쳐 표시.</summary>
public sealed class DiffRow
{
    public bool IsHunk { get; init; }
    public string HunkText { get; init; } = "";

    public string LeftNum { get; init; } = "";
    public string LeftText { get; init; } = "";
    public DiffCellKind LeftKind { get; init; }

    public string RightNum { get; init; } = "";
    public string RightText { get; init; } = "";
    public DiffCellKind RightKind { get; init; }
}

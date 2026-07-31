using System.ComponentModel;

namespace DevezCode.Models;

/// <summary>git status 한 줄 — 변경된 파일 하나.</summary>
public sealed class GitChange : INotifyPropertyChanged
{
    /// <summary>표시용 상태 글자(A/M/D/R/C/T/U). 신규 미추적 파일도 VS와 같이 A로 표시.</summary>
    public string Status { get; init; } = "";
    /// <summary>저장소 루트 기준 상대 경로.</summary>
    public string Path { get; init; } = "";
    /// <summary>추적되지 않은(신규) 파일 여부 — diff 대신 파일 내용을 추가로 표시.</summary>
    public bool IsUntracked { get; init; }
    /// <summary>스테이징 영역(Staged Changes) 소속이면 true, 작업트리(Changes)면 false.</summary>
    public bool IsStaged { get; init; }

    private bool _isSelected;
    /// <summary>목록에서 현재 선택된 파일인지(행 강조용).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>상태 글자 색.</summary>
    public string StatusColor => Status switch
    {
        "A" => "#3FB950",
        "D" => "#F85149",
        "M" => "#D29922",
        "R" => "#58A6FF",
        "C" => "#3FB950",
        "T" => "#D29922",
        "U" => "#F85149",
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

/// <summary>git status 분류 결과 — 스테이징/작업트리 두 목록.</summary>
public sealed class GitStatus
{
    public List<GitChange> Staged { get; init; } = new();
    public List<GitChange> Unstaged { get; init; } = new();
    public bool IsEmpty => Staged.Count == 0 && Unstaged.Count == 0;
}

/// <summary>현재 브랜치·업스트림·ahead/behind.</summary>
public sealed class BranchState
{
    public string? Branch { get; init; }
    public bool HasUpstream { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
}

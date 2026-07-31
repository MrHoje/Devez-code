using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;

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
    public string? Upstream { get; init; }
    public string? BaseBranch { get; init; }
    public bool HasUpstream { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
}

/// <summary>커밋 내역의 파일 변경 한 건.</summary>
public sealed class GitCommitFile
{
    public string Path { get; init; } = "";
    public int? Additions { get; init; }
    public int? Deletions { get; init; }
    public string Name => System.IO.Path.GetFileName(Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
    public string DirectoryName
    {
        get
        {
            var dir = System.IO.Path.GetDirectoryName(Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            return string.IsNullOrEmpty(dir) ? "" : dir.Replace('\\', '/') + "/";
        }
    }
    public string ChangeText => Additions.HasValue && Deletions.HasValue
        ? $"+{Additions} −{Deletions}"
        : "binary";
}

/// <summary>커밋 목록 한 행. 선택될 때 파일 통계를 지연 로드한다.</summary>
public sealed class GitCommitEntry : INotifyPropertyChanged
{
    public string Sha { get; init; } = "";
    public string ShortSha { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Body { get; init; } = "";
    public string AuthorName { get; init; } = "";
    public DateTimeOffset AuthoredAt { get; init; }
    public int ParentCount { get; init; }
    public string Refs { get; init; } = "";
    public bool IsMerge => ParentCount > 1;
    public bool IsHead => Refs.Contains("HEAD", StringComparison.Ordinal);
    public string RelativeTime => GitTimeText.Relative(AuthoredAt);
    public string AuthorInitial => string.IsNullOrWhiteSpace(AuthorName)
        ? "?"
        : AuthorName.Trim()[0].ToString().ToUpperInvariant();

    private bool _isDetailsLoading;
    public bool IsDetailsLoading
    {
        get => _isDetailsLoading;
        private set { if (_isDetailsLoading == value) return; _isDetailsLoading = value; OnChanged(nameof(IsDetailsLoading)); }
    }

    private bool _detailsLoaded;
    public bool DetailsLoaded
    {
        get => _detailsLoaded;
        private set { if (_detailsLoaded == value) return; _detailsLoaded = value; OnChanged(nameof(DetailsLoaded)); }
    }

    private string _detailBody = "";
    public string DetailBody
    {
        get => _detailBody;
        private set { if (_detailBody == value) return; _detailBody = value; OnChanged(nameof(DetailBody)); OnChanged(nameof(HasDetailBody)); }
    }
    public bool HasDetailBody => !string.IsNullOrWhiteSpace(DetailBody);

    private string _statsText = "";
    public string StatsText
    {
        get => _statsText;
        private set { if (_statsText == value) return; _statsText = value; OnChanged(nameof(StatsText)); }
    }

    private string _moreFilesText = "";
    public string MoreFilesText
    {
        get => _moreFilesText;
        private set { if (_moreFilesText == value) return; _moreFilesText = value; OnChanged(nameof(MoreFilesText)); OnChanged(nameof(HasMoreFiles)); }
    }
    public bool HasMoreFiles => !string.IsNullOrEmpty(MoreFilesText);

    public ObservableCollection<GitCommitFile> Files { get; } = new();

    private string _detailsError = "";
    public string DetailsError
    {
        get => _detailsError;
        private set { if (_detailsError == value) return; _detailsError = value; OnChanged(nameof(DetailsError)); OnChanged(nameof(HasDetailsError)); }
    }
    public bool HasDetailsError => !string.IsNullOrWhiteSpace(DetailsError);

    public void BeginDetails()
    {
        DetailsError = "";
        DetailsLoaded = false;
        IsDetailsLoading = true;
    }

    public void ApplyDetails(string body, IEnumerable<GitCommitFile> files)
    {
        DetailsError = "";
        DetailBody = body.Trim();
        Files.Clear();
        var allFiles = files.ToList();
        var additions = 0;
        var deletions = 0;
        foreach (var file in allFiles)
        {
            additions += file.Additions ?? 0;
            deletions += file.Deletions ?? 0;
        }
        foreach (var file in allFiles.Take(12)) Files.Add(file);
        MoreFilesText = allFiles.Count > Files.Count ? $"그 외 {allFiles.Count - Files.Count}개 파일" : "";
        StatsText = $"파일 {allFiles.Count}개  +{additions}  −{deletions}";
        IsDetailsLoading = false;
        DetailsLoaded = true;
    }

    public void FailDetails()
    {
        IsDetailsLoading = false;
        DetailsLoaded = true;
        DetailsError = "세부 정보를 불러오지 못했습니다.";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class GitHistoryPage
{
    public bool Ok { get; init; }
    public string Error { get; init; } = "";
    public List<GitCommitEntry> Commits { get; init; } = new();
    public bool HasMore { get; init; }
}

public enum PullRequestChecksState { None, Passing, Pending, Failing }

/// <summary>GitHub pull request 목록 한 행.</summary>
public sealed class GitPullRequestItem : INotifyPropertyChanged
{
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string State { get; init; } = "";
    public bool IsDraft { get; init; }
    public string AuthorLogin { get; init; } = "";
    public string HeadRefName { get; init; } = "";
    public string BaseRefName { get; init; } = "";
    public DateTimeOffset UpdatedAt { get; init; }
    public string Url { get; init; } = "";
    public string ReviewDecision { get; init; } = "";
    public int Additions { get; init; }
    public int Deletions { get; init; }
    public int ChangedFiles { get; init; }
    public int PassingChecks { get; init; }
    public int PendingChecks { get; init; }
    public int FailingChecks { get; init; }

    public bool IsOpen => State.Equals("OPEN", StringComparison.OrdinalIgnoreCase);
    public bool IsMerged => State.Equals("MERGED", StringComparison.OrdinalIgnoreCase);
    public bool IsClosed => !IsOpen && !IsMerged;
    public string StateText => IsDraft ? "초안" : IsOpen ? "열림" : IsMerged ? "병합됨" : "닫힘";
    public string NumberText => $"#{Number}";
    public string UpdatedText => GitTimeText.Relative(UpdatedAt);
    public string AuthorText => string.IsNullOrWhiteSpace(AuthorLogin) ? UpdatedText : $"{AuthorLogin} · {UpdatedText}";
    public string BranchText => $"{HeadRefName} → {BaseRefName}";
    public string ChangeText => $"파일 {ChangedFiles}개  +{Additions}  −{Deletions}";
    public PullRequestChecksState ChecksState => FailingChecks > 0
        ? PullRequestChecksState.Failing
        : PendingChecks > 0
            ? PullRequestChecksState.Pending
            : PassingChecks > 0
                ? PullRequestChecksState.Passing
                : PullRequestChecksState.None;
    public string ChecksText => ChecksState switch
    {
        PullRequestChecksState.Failing => $"체크 실패 {FailingChecks}",
        PullRequestChecksState.Pending => $"체크 진행 중 {PendingChecks}",
        PullRequestChecksState.Passing => $"체크 통과 {PassingChecks}",
        _ => "체크 없음",
    };
    public string ReviewText => ReviewDecision switch
    {
        "APPROVED" => "리뷰 승인됨",
        "CHANGES_REQUESTED" => "수정 요청됨",
        "REVIEW_REQUIRED" => "리뷰 필요",
        _ => "리뷰 대기",
    };
    private string _body = "";
    public string Body
    {
        get => _body;
        private set
        {
            if (_body == value) return;
            _body = value;
            PropertyChanged?.Invoke(this, new(nameof(Body)));
            PropertyChanged?.Invoke(this, new(nameof(HasBody)));
            PropertyChanged?.Invoke(this, new(nameof(HasNoBody)));
        }
    }
    public bool HasBody => !string.IsNullOrWhiteSpace(Body);
    public bool HasNoBody => DetailsLoaded && !IsDetailsLoading && !HasBody && !HasDetailsError;

    private string _detailsError = "";
    public string DetailsError
    {
        get => _detailsError;
        private set
        {
            if (_detailsError == value) return;
            _detailsError = value;
            PropertyChanged?.Invoke(this, new(nameof(DetailsError)));
            PropertyChanged?.Invoke(this, new(nameof(HasDetailsError)));
            PropertyChanged?.Invoke(this, new(nameof(HasNoBody)));
        }
    }
    public bool HasDetailsError => !string.IsNullOrWhiteSpace(DetailsError);

    private bool _isDetailsLoading;
    public bool IsDetailsLoading
    {
        get => _isDetailsLoading;
        private set
        {
            if (_isDetailsLoading == value) return;
            _isDetailsLoading = value;
            PropertyChanged?.Invoke(this, new(nameof(IsDetailsLoading)));
            PropertyChanged?.Invoke(this, new(nameof(HasNoBody)));
        }
    }

    private bool _detailsLoaded;
    public bool DetailsLoaded
    {
        get => _detailsLoaded;
        private set
        {
            if (_detailsLoaded == value) return;
            _detailsLoaded = value;
            PropertyChanged?.Invoke(this, new(nameof(DetailsLoaded)));
            PropertyChanged?.Invoke(this, new(nameof(HasNoBody)));
        }
    }

    public void BeginDetails()
    {
        DetailsError = "";
        DetailsLoaded = false;
        IsDetailsLoading = true;
    }
    public void ApplyBody(string body)
    {
        DetailsError = "";
        Body = body.Trim();
        IsDetailsLoading = false;
        DetailsLoaded = true;
    }
    public void FailDetails()
    {
        IsDetailsLoading = false;
        DetailsLoaded = true;
        DetailsError = "PR 본문을 불러오지 못했습니다.";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public enum GitHubLoadState { Ready, CliMissing, AuthenticationRequired, UnsupportedRemote, Failed }

public sealed class GitHubPullRequestResult
{
    public GitHubLoadState State { get; init; }
    public List<GitPullRequestItem> PullRequests { get; init; } = new();
    public string Message { get; init; } = "";
}

internal static class GitTimeText
{
    public static string Relative(DateTimeOffset value)
    {
        var elapsed = DateTimeOffset.Now - value.ToLocalTime();
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed.TotalSeconds < 60) return "방금 전";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}분 전";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}시간 전";
        if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays}일 전";
        return value.LocalDateTime.Year == DateTime.Now.Year
            ? value.LocalDateTime.ToString("M월 d일")
            : value.LocalDateTime.ToString("yyyy.M.d");
    }
}

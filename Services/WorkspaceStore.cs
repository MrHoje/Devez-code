using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>프로젝트/세션 트리를 %AppData%\DevezCode\workspace.json 에 저장·복원.</summary>
public static class WorkspaceStore
{
    private sealed class SessionDto { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string? Agent { get; set; } public bool Hidden { get; set; } public bool Locked { get; set; } public string? ParentId { get; set; } public bool ChildrenExpanded { get; set; } = true; }
    private sealed class BrowserDto { public string Id { get; set; } = ""; public string Name { get; set; } = "웹 브라우저"; }
    private sealed class ShortcutDto { public string Path { get; set; } = ""; public string Name { get; set; } = ""; public bool RunAsAdmin { get; set; } }
    private sealed class ProjectFolderDto { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string? Icon { get; set; } public int? RootOrder { get; set; } public bool IsExpanded { get; set; } = true; public string? ArchivedAt { get; set; } public bool TwoColumn { get; set; } = true; }
    private sealed class ProjectDto
    {
        public string Path { get; set; } = "";
        // 사용자 지정 표시 이름. null/빈값이면 폴더명(FromPath) 사용. 이름 변경 시 재시작 복원.
        public string? Name { get; set; }
        // 좌측 카드 접힘/펼침 상태 (기본 펼침). 재시작 시 복원.
        public bool IsExpanded { get; set; } = true;
        // 보관 시각(ISO-8601). null=활성, 값 있으면 보관함. devez archived_at 정합(로컬).
        public string? ArchivedAt { get; set; }
        // 2열 보기에서의 컬럼(0=좌, 1=우). 1열 보기에선 무시. 기본 0.
        public int Column { get; set; }
        public string? FolderId { get; set; }
        public int? RootOrder { get; set; }
        public List<SessionDto> Sessions { get; set; } = new();
        // 중앙 웹 브라우저 탭. ID가 settings.json 의 탭별 방문 기록 키와 연결된다.
        public List<BrowserDto> Browsers { get; set; } = new();
        // 프로젝트 메뉴에 등록한 바로가기 목록. 재시작 시 복원.
        public List<ShortcutDto> Files { get; set; } = new();
        // 숨김 세션 표시 여부 (카드 헤더 눈 아이콘 토글). 재시작 시 복원.
        public bool ShowHiddenSessions { get; set; } = true;
        // 직전에 열려 있던 파일 편집기 탭의 절대 경로 목록. 재시작 시 다시 탭으로 복원.
        public List<string> OpenFiles { get; set; } = new();
        // 저장 시점 전체 탭 순서(세션+문서+브라우저, "S:<id>"/"F:<path>"/"B:<id>"). 복원 시 이 순서로 Tabs 재배열.
        public List<string> TabOrder { get; set; } = new();
        // 마지막으로 활성화했던 탭 참조("S:<세션ID>"/"F:<파일경로>"/"B:<브라우저ID>"). 프로젝트 재선택 시 복원.
        public string? LastActiveTab { get; set; }
        // 이 프로젝트를 메인 패널에 열 때 분할을 함께 켤지 + 분할 파트너. 재시작/재선택 시 복원.
        public bool SplitEnabled { get; set; }
        public string? SplitPartnerProjectPath { get; set; }
        public string? SplitPartnerSessionId { get; set; }
        public string? SplitPartnerFilePath { get; set; }
        // 같은 프로젝트 분할 시 우측 패널 전체 탭 집합 + 우측 활성 탭. 재시작/재선택 시 우측 탭들을 복원.
        public List<string> SplitRightTabRefs { get; set; } = new();
        public string? SplitRightActiveRef { get; set; }
    }
    private sealed class WorkspaceDto { public List<ProjectDto> Projects { get; set; } = new(); public List<ProjectFolderDto> Folders { get; set; } = new(); }

    private static string WorkspacePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "workspace.json");

    // 세션 간 지시 릴레이(플러그인 /send-to)용 세션 목록. 플러그인이 이름→roomId 를 해석한다.
    private static string SessionsIndexPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "sessions-index.json");

    // JSON 키를 camelCase 로 그대로 내보내 send.js 가 별도 매핑 없이 읽게 한다.
    private sealed class SessionIndexEntryDto
    {
        public string roomId { get; set; } = "";
        public string name { get; set; } = "";
        public string projectPath { get; set; } = "";
        public string projectName { get; set; } = "";
    }

    /// <summary>활성 프로젝트의 세션 목록을 sessions-index.json 에 내보낸다.
    /// 플러그인 /send-to 가 대상 이름을 roomId 로 해석(같은 프로젝트 우선)하는 데 쓴다.
    /// 저장(추가/삭제/이름변경)마다 갱신되며, 시작 시 1회도 호출해 최신화한다.</summary>
    public static void ExportSessionsIndex(IEnumerable<ProjectItem> active)
    {
        try
        {
            var entries = active
                .SelectMany(p => p.Tabs.OfType<SessionItem>().Select(s => new SessionIndexEntryDto
                {
                    roomId = s.Id,
                    name = s.Name,
                    projectPath = p.Path,
                    projectName = p.Name,
                }))
                .ToList();
            AtomicFile.WriteAllText(SessionsIndexPath, JsonSerializer.Serialize(entries));
        }
        catch { /* non-critical — 릴레이 인덱스만 갱신 실패 */ }
    }

    /// <summary>직전 Load 가 손상으로 데이터를 못 읽고 빈 결과를 반환했는지.
    /// 이 상태에서 빈 트리로 Save 하면 격리해 둔 원본까지 영구 손실되므로 Save 를 막는다.</summary>
    private static bool _loadDegraded;

    /// <summary>직전 Load 가 손상 복구로 빈 트리를 반환했는가 — 이 경우 활성+보관 세션 목록이
    /// 실제로 비어있는 게 아니라 "일시적으로 못 읽은 것"이므로, 이 값에 의존해 방 존재 여부를
    /// 판단하는 로직(예: 유령 방 GC)은 이번 실행에서 건너뛰어야 한다.</summary>
    public static bool LastLoadDegraded => _loadDegraded;

    // Load/Save 직렬화 — 현재는 UI 스레드 전용이나, tmp/bak 고정 파일명을 쓰므로
    // 동시 진입 시 쓰기 충돌을 막기 위한 방어적 잠금.
    private static readonly object _lock = new();

    // 보관 프로젝트 컬렉션의 살아있는 참조. Load 가 채워 두면, 이후 활성만 받는
    // Save(active) 호출도 이 참조를 함께 직렬화해 보관 항목이 유실되지 않는다.
    // (호출부 대량 수정 없이 보관함을 영속하기 위한 장치.)
    private static IEnumerable<ProjectItem>? _archivedRef;
    public static ObservableCollection<ProjectFolderItem> ProjectFolders { get; } = new();

    /// <summary>보관 프로젝트 소스 등록 — 이후 모든 Save 가 이 항목들을 함께 기록한다.</summary>
    public static void SetArchivedSource(IEnumerable<ProjectItem> archived) => _archivedRef = archived;

    public static ObservableCollection<ProjectItem> Load() => Load(out _);

    /// <summary>활성 프로젝트를 반환하고, 보관(archived_at 있음) 프로젝트는 out 으로 분리해 돌려준다.</summary>
    public static ObservableCollection<ProjectItem> Load(out ObservableCollection<ProjectItem> archived)
    {
        lock (_lock) return LoadCore(out archived);
    }

    private static ObservableCollection<ProjectItem> LoadCore(out ObservableCollection<ProjectItem> archived)
    {
        var active = new ObservableCollection<ProjectItem>();
        archived = new ObservableCollection<ProjectItem>();
        _loadDegraded = false;
        ProjectFolders.Clear();

        // 본 파일 → .bak 순으로 읽되, 역직렬화까지 성공해야 유효로 인정.
        var text = AtomicFile.ReadValidated(WorkspacePath, IsParseable, out bool corrupted);
        if (text == null)
        {
            // corrupted=true: 파일은 있었으나 본/백업 모두 파싱 실패 → 손상 격리됨.
            // 빈 트리 Save 로 원본을 덮어쓰지 않도록 플래그.
            _loadDegraded = corrupted;
            SetArchivedSource(archived);
            return active;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<WorkspaceDto>(text);
            if (dto == null) { SetArchivedSource(archived); return active; }

            foreach (var folder in dto.Folders ?? new())
                ProjectFolders.Add(new ProjectFolderItem
                {
                    Id = folder.Id,
                    Name = folder.Name ?? "",
                    IconKey = FolderIconCatalog.Normalize(folder.Icon),
                    RootOrder = folder.RootOrder ?? int.MaxValue,
                    IsExpanded = folder.IsExpanded,
                    ArchivedAt = folder.ArchivedAt,
                    TwoColumn = folder.TwoColumn,
                });

            var folderIds = new HashSet<string>(ProjectFolders.Select(folder => folder.Id), StringComparer.Ordinal);
            foreach (var p in dto.Projects)
            {
                var proj = ProjectItem.FromPath(p.Path);
                if (!string.IsNullOrWhiteSpace(p.Name)) proj.Name = p.Name; // 사용자 지정 이름 복원
                proj.IsExpanded = p.IsExpanded;
                proj.ArchivedAt = p.ArchivedAt;
                proj.Column = p.Column;
                proj.FolderId = folderIds.Contains(p.FolderId ?? "") ? p.FolderId : null;
                proj.RootOrder = p.RootOrder ?? int.MaxValue;
                proj.ShowHiddenSessions = p.ShowHiddenSessions;
                foreach (var s in p.Sessions)
                    proj.Tabs.Add(new SessionItem { Id = s.Id, Name = s.Name, AgentId = s.Agent ?? "", Hidden = s.Hidden, IsLocked = s.Locked, ParentSessionId = s.ParentId, AreSessionChildrenExpanded = s.ChildrenExpanded });
                proj.NormalizeSessionTree();
                foreach (var b in p.Browsers ?? new())
                    proj.Tabs.Add(new BrowserTabItem { Id = b.Id, Name = string.IsNullOrWhiteSpace(b.Name) ? "웹 브라우저" : b.Name });
                foreach (var f in p.Files)
                    proj.AddShortcut(f.Path, f.Name, f.RunAsAdmin);
                proj.PendingOpenFiles = p.OpenFiles ?? new();   // 시작 시 RestoreFileTabs 가 1회 소비
                proj.PendingTabOrder = p.TabOrder ?? new();     // 세션+파일 복원 후 이 순서로 Tabs 재배열
                proj.LastActiveTabRef = p.LastActiveTab;
                proj.SplitEnabled = p.SplitEnabled;
                proj.SplitPartnerProjectPath = p.SplitPartnerProjectPath;
                proj.SplitPartnerSessionId = p.SplitPartnerSessionId;
                proj.SplitPartnerFilePath = p.SplitPartnerFilePath;
                proj.SplitRightTabRefs = p.SplitRightTabRefs ?? new();
                proj.SplitRightActiveRef = p.SplitRightActiveRef;
                (proj.IsArchived ? archived : active).Add(proj);
            }
        }
        catch { _loadDegraded = true; ProjectFolders.Clear(); archived = new ObservableCollection<ProjectItem>(); SetArchivedSource(archived); return new ObservableCollection<ProjectItem>(); }
        SetArchivedSource(archived);
        return active;
    }

    private static bool IsParseable(string text)
    {
        try { return JsonSerializer.Deserialize<WorkspaceDto>(text) != null; }
        catch { return false; }
    }

    public static void Save(IEnumerable<ProjectItem> projects)
    {
        // 순회 전에 스냅샷 — lock 구간 밖에서 컬렉션이 바뀌어도 안전(UI 스레드 전용이라 사실상 불변이나 방어적).
        var list = projects as ICollection<ProjectItem> ?? projects.ToList();
        lock (_lock) SaveCore(list);
    }

    /// <summary>활성 + 보관 프로젝트를 함께 저장. 보관 소스도 갱신한다.</summary>
    public static void Save(IEnumerable<ProjectItem> active, IEnumerable<ProjectItem> archived)
    {
        SetArchivedSource(archived as ICollection<ProjectItem> ?? archived.ToList());
        Save(active);
    }

    private static ProjectDto ToDto(ProjectItem p) => new ProjectDto
    {
        Path = p.Path,
        Name = p.Name,
        IsExpanded = p.IsExpanded,
        ArchivedAt = p.ArchivedAt,
        Column = p.Column,
        FolderId = p.FolderId,
        RootOrder = p.RootOrder,
        Sessions = p.Tabs.OfType<SessionItem>().Select(s => new SessionDto
        {
            Id = s.Id, Name = s.Name,
            Agent = string.IsNullOrEmpty(s.AgentId) ? null : s.AgentId,
            Hidden = s.Hidden, Locked = s.IsLocked, ParentId = s.ParentSessionId,
            ChildrenExpanded = s.AreSessionChildrenExpanded,
        }).ToList(),
        Browsers = p.Tabs.OfType<BrowserTabItem>().Select(b => new BrowserDto
        {
            Id = b.Id, Name = b.Name,
        }).ToList(),
        Files = p.Files.Select(f => new ShortcutDto { Path = f.FilePath, Name = f.Name, RunAsAdmin = f.RunAsAdmin }).ToList(),
        // 숨김 세션 표시 여부
        ShowHiddenSessions = p.ShowHiddenSessions,
        // 열린 파일 탭 경로 → 재시작 시 복원(Tabs 순서 그대로).
        OpenFiles = p.Tabs.OfType<FileTabItem>().Where(f => !f.IsDiff).Select(f => f.FilePath).ToList(),
        // 전체 탭 순서(세션+문서) → 복원 시 이 순서로 재배열해 문서의 끼임 위치 보존.
        TabOrder = p.Tabs.Select(t => t switch
        {
            SessionItem s => "S:" + s.Id,
            FileTabItem f => f.IsDiff ? "" : "F:" + f.FilePath,
            BrowserTabItem b => "B:" + b.Id,
            _ => "",
        }).Where(r => r.Length > 0).ToList(),
        LastActiveTab = p.LastActiveTabRef,
        SplitEnabled = p.SplitEnabled,
        SplitPartnerProjectPath = p.SplitPartnerProjectPath,
        SplitPartnerSessionId = p.SplitPartnerSessionId,
        SplitPartnerFilePath = p.SplitPartnerFilePath,
        SplitRightTabRefs = p.SplitRightTabRefs,
        SplitRightActiveRef = p.SplitRightActiveRef,
    };

    private static void SaveCore(ICollection<ProjectItem> list)
    {
        try
        {
            var archived = _archivedRef as ICollection<ProjectItem> ?? _archivedRef?.ToList();
            int archivedCount = archived?.Count ?? 0;
            // 손상 로드로 빈 시작한 상태에서 빈 트리 저장은 격리 원본까지 묻어버린다 — 스킵.
            // (사용자가 프로젝트를 추가하면 비어있지 않게 되어 정상 저장·재생성된다.)
            if (_loadDegraded && list.Count == 0 && archivedCount == 0) return;

            var projects = list.Select(ToDto).ToList();
            if (archived != null) projects.AddRange(archived.Select(ToDto));

            var dto = new WorkspaceDto
            {
                Projects = projects,
                Folders = ProjectFolders.Select(folder => new ProjectFolderDto
                {
                    Id = folder.Id,
                    Name = folder.Name,
                    Icon = folder.IconKey,
                    RootOrder = folder.RootOrder,
                    IsExpanded = folder.IsExpanded,
                    ArchivedAt = folder.ArchivedAt,
                    TwoColumn = folder.TwoColumn,
                }).ToList(),
            };
            AtomicFile.WriteAllText(WorkspacePath,
                JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
            _loadDegraded = false; // 정상 저장됨 — 이후 빈 가드 해제
            ExportSessionsIndex(list); // 세션 릴레이용 인덱스도 함께 갱신(추가/삭제/이름변경 반영)
        }
        catch { /* non-critical */ }
    }
}

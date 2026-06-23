using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>프로젝트/세션 트리를 %AppData%\DevezCode\workspace.json 에 저장·복원.</summary>
public static class WorkspaceStore
{
    private sealed class SessionDto { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string? Agent { get; set; } public bool Hidden { get; set; } }
    private sealed class ShortcutDto { public string Path { get; set; } = ""; public string Name { get; set; } = ""; public bool RunAsAdmin { get; set; } }
    private sealed class ProjectDto
    {
        public string Path { get; set; } = "";
        // 좌측 카드 접힘/펼침 상태 (기본 펼침). 재시작 시 복원.
        public bool IsExpanded { get; set; } = true;
        // 보관 시각(ISO-8601). null=활성, 값 있으면 보관함. devez archived_at 정합(로컬).
        public string? ArchivedAt { get; set; }
        // 2열 보기에서의 컬럼(0=좌, 1=우). 1열 보기에선 무시. 기본 0.
        public int Column { get; set; }
        public List<SessionDto> Sessions { get; set; } = new();
        // 프로젝트 메뉴에 등록한 바로가기 목록. 재시작 시 복원.
        public List<ShortcutDto> Files { get; set; } = new();
    }
    private sealed class WorkspaceDto { public List<ProjectDto> Projects { get; set; } = new(); }

    private static string WorkspacePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "workspace.json");

    /// <summary>직전 Load 가 손상으로 데이터를 못 읽고 빈 결과를 반환했는지.
    /// 이 상태에서 빈 트리로 Save 하면 격리해 둔 원본까지 영구 손실되므로 Save 를 막는다.</summary>
    private static bool _loadDegraded;

    // Load/Save 직렬화 — 현재는 UI 스레드 전용이나, tmp/bak 고정 파일명을 쓰므로
    // 동시 진입 시 쓰기 충돌을 막기 위한 방어적 잠금.
    private static readonly object _lock = new();

    // 보관 프로젝트 컬렉션의 살아있는 참조. Load 가 채워 두면, 이후 활성만 받는
    // Save(active) 호출도 이 참조를 함께 직렬화해 보관 항목이 유실되지 않는다.
    // (호출부 대량 수정 없이 보관함을 영속하기 위한 장치.)
    private static IEnumerable<ProjectItem>? _archivedRef;

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
            foreach (var p in dto.Projects)
            {
                var proj = ProjectItem.FromPath(p.Path);
                proj.IsExpanded = p.IsExpanded;
                proj.ArchivedAt = p.ArchivedAt;
                proj.Column = p.Column;
                foreach (var s in p.Sessions)
                    proj.Tabs.Add(new SessionItem { Id = s.Id, Name = s.Name, AgentId = s.Agent ?? "", Hidden = s.Hidden });
                foreach (var f in p.Files)
                    proj.AddShortcut(f.Path, f.Name, f.RunAsAdmin);
                (proj.IsArchived ? archived : active).Add(proj);
            }
        }
        catch { _loadDegraded = true; archived = new ObservableCollection<ProjectItem>(); SetArchivedSource(archived); return new ObservableCollection<ProjectItem>(); }
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
        IsExpanded = p.IsExpanded,
        ArchivedAt = p.ArchivedAt,
        Column = p.Column,
        // 파일 탭은 비영속: 세션만 저장 → 재시작 시 사라진다.
        Sessions = p.Tabs.OfType<SessionItem>().Select(s => new SessionDto
        {
            Id = s.Id, Name = s.Name,
            Agent = string.IsNullOrEmpty(s.AgentId) ? null : s.AgentId,
            Hidden = s.Hidden,
        }).ToList(),
        Files = p.Files.Select(f => new ShortcutDto { Path = f.FilePath, Name = f.Name, RunAsAdmin = f.RunAsAdmin }).ToList(),
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

            var dto = new WorkspaceDto { Projects = projects };
            AtomicFile.WriteAllText(WorkspacePath,
                JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
            _loadDegraded = false; // 정상 저장됨 — 이후 빈 가드 해제
        }
        catch { /* non-critical */ }
    }
}

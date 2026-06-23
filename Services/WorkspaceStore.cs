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

    public static ObservableCollection<ProjectItem> Load()
    {
        var result = new ObservableCollection<ProjectItem>();
        _loadDegraded = false;

        // 본 파일 → .bak 순으로 읽되, 역직렬화까지 성공해야 유효로 인정.
        var text = AtomicFile.ReadValidated(WorkspacePath, IsParseable, out bool corrupted);
        if (text == null)
        {
            // corrupted=true: 파일은 있었으나 본/백업 모두 파싱 실패 → 손상 격리됨.
            // 빈 트리 Save 로 원본을 덮어쓰지 않도록 플래그.
            _loadDegraded = corrupted;
            return result;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<WorkspaceDto>(text);
            if (dto == null) return result;
            foreach (var p in dto.Projects)
            {
                var proj = ProjectItem.FromPath(p.Path);
                proj.IsExpanded = p.IsExpanded;
                foreach (var s in p.Sessions)
                    proj.Tabs.Add(new SessionItem { Id = s.Id, Name = s.Name, AgentId = s.Agent ?? "", Hidden = s.Hidden });
                foreach (var f in p.Files)
                    proj.AddShortcut(f.Path, f.Name, f.RunAsAdmin);
                result.Add(proj);
            }
        }
        catch { _loadDegraded = true; return new ObservableCollection<ProjectItem>(); }
        return result;
    }

    private static bool IsParseable(string text)
    {
        try { return JsonSerializer.Deserialize<WorkspaceDto>(text) != null; }
        catch { return false; }
    }

    public static void Save(IEnumerable<ProjectItem> projects)
    {
        try
        {
            var list = projects as ICollection<ProjectItem> ?? projects.ToList();
            // 손상 로드로 빈 시작한 상태에서 빈 트리 저장은 격리 원본까지 묻어버린다 — 스킵.
            // (사용자가 프로젝트를 추가하면 비어있지 않게 되어 정상 저장·재생성된다.)
            if (_loadDegraded && list.Count == 0) return;

            var dto = new WorkspaceDto
            {
                Projects = list.Select(p => new ProjectDto
                {
                    Path = p.Path,
                    IsExpanded = p.IsExpanded,
                    // 파일 탭은 비영속: 세션만 저장 → 재시작 시 사라진다.
                    Sessions = p.Tabs.OfType<SessionItem>().Select(s => new SessionDto
                    {
                        Id = s.Id, Name = s.Name,
                        Agent = string.IsNullOrEmpty(s.AgentId) ? null : s.AgentId,
                        Hidden = s.Hidden,
                    }).ToList(),
                    Files = p.Files.Select(f => new ShortcutDto { Path = f.FilePath, Name = f.Name, RunAsAdmin = f.RunAsAdmin }).ToList(),
                }).ToList()
            };
            AtomicFile.WriteAllText(WorkspacePath,
                JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
            _loadDegraded = false; // 정상 저장됨 — 이후 빈 가드 해제
        }
        catch { /* non-critical */ }
    }
}

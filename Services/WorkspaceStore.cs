using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>프로젝트/세션 트리를 %AppData%\DevezCode\workspace.json 에 저장·복원.</summary>
public static class WorkspaceStore
{
    private sealed class SessionDto { public string Id { get; set; } = ""; public string Name { get; set; } = ""; }
    private sealed class ProjectDto { public string Path { get; set; } = ""; public List<SessionDto> Sessions { get; set; } = new(); }
    private sealed class WorkspaceDto { public List<ProjectDto> Projects { get; set; } = new(); }

    private static string WorkspacePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "workspace.json");

    public static ObservableCollection<ProjectItem> Load()
    {
        var result = new ObservableCollection<ProjectItem>();
        try
        {
            if (!File.Exists(WorkspacePath)) return result;
            var dto = JsonSerializer.Deserialize<WorkspaceDto>(File.ReadAllText(WorkspacePath));
            if (dto == null) return result;
            foreach (var p in dto.Projects)
            {
                var proj = ProjectItem.FromPath(p.Path);
                foreach (var s in p.Sessions)
                    proj.Sessions.Add(new SessionItem { Id = s.Id, Name = s.Name });
                result.Add(proj);
            }
        }
        catch { /* 손상 시 빈 워크스페이스 */ }
        return result;
    }

    public static void Save(IEnumerable<ProjectItem> projects)
    {
        try
        {
            var dto = new WorkspaceDto
            {
                Projects = projects.Select(p => new ProjectDto
                {
                    Path = p.Path,
                    Sessions = p.Sessions.Select(s => new SessionDto { Id = s.Id, Name = s.Name }).ToList()
                }).ToList()
            };
            Directory.CreateDirectory(Path.GetDirectoryName(WorkspacePath)!);
            File.WriteAllText(WorkspacePath,
                JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-critical */ }
    }
}

using System.Collections.Generic;
using System.IO;

namespace DevezCode.Models;

public enum QuickOpenKind
{
    Project,
    Session,
    File
}

public sealed class QuickOpenItem
{
    private readonly string _searchText;

    public QuickOpenKind Kind { get; }
    public string Title { get; }
    public string? Subtitle { get; }
    public ProjectItem? Project { get; }
    public SessionItem? Session { get; }
    public string? FilePath { get; }

    public QuickOpenItem(
        QuickOpenKind kind,
        string title,
        string? subtitle = null,
        ProjectItem? project = null,
        SessionItem? session = null,
        string? filePath = null)
    {
        Kind = kind;
        Title = title?.Trim() ?? "";
        Subtitle = string.IsNullOrWhiteSpace(subtitle) ? null : subtitle.Trim();
        Project = project;
        Session = session;
        FilePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath;
        _searchText = $"{Title} {Subtitle} {PathText(project?.Path)} {PathText(session?.Name)}"
                      .ToLowerInvariant();
    }

    public string KindText => Kind switch
    {
        QuickOpenKind.Project => "프로젝트",
        QuickOpenKind.Session => "세션",
        QuickOpenKind.File => "파일",
        _ => "항목"
    };

    public string SearchText => _searchText;

    private static string PathText(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value;
}

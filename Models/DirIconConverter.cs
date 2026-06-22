using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DevezCode.Models;

/// <summary>FileNode → 파일 종류별 아이콘 PathGeometry. 파일 탐색기 트리용.</summary>
public sealed class DirIconConverter : IValueConverter
{
    public static readonly DirIconConverter Instance = new();

    private static readonly HashSet<string> CodeExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".java", ".kt", ".kts", ".swift", ".go", ".rs",
        ".c", ".h", ".cpp", ".hpp", ".cc", ".py", ".rb", ".php", ".lua", ".dart", ".fs", ".fsi",
        ".vb", ".sql", ".razor", ".cshtml", ".xaml", ".xml", ".html", ".htm", ".css", ".scss", ".sass",
        ".less", ".vue", ".svelte"
    };

    private static readonly HashSet<string> ConfigExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".config", ".conf", ".ini", ".toml", ".yaml", ".yml", ".props", ".targets"
    };

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".svg", ".avif", ".tif", ".tiff"
    };

    private static readonly HashSet<string> DataExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".db", ".sqlite", ".sqlite3", ".mdb", ".accdb", ".csv", ".tsv", ".xls", ".xlsx"
    };

    private static readonly HashSet<string> ScriptExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1", ".psm1", ".bat", ".cmd", ".sh", ".bash", ".zsh", ".fish"
    };

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is FileNode node ? GetIconKey(node) : value is true ? "IconFolder" : "IconFileText";
        return Application.Current?.TryFindResource(key) as Geometry;
    }

    private static string GetIconKey(FileNode node)
    {
        if (node.IsDirectory) return "IconFolder";

        var name = node.Name.ToLowerInvariant();
        var ext = Path.GetExtension(name);

        if (name is ".gitignore" or ".gitattributes" or ".gitmodules" or ".girignore") return "IconGitBranch";
        if (name is ".editorconfig" or ".env" or ".npmrc" or ".prettierrc" or ".eslintrc") return "IconSettings";
        if (name is "dockerfile" or "makefile" or "justfile" or "rakefile") return "IconTerminal";
        if (name is "package.json" or "package-lock.json" or "pnpm-lock.yaml" or "yarn.lock" or "bun.lockb") return "IconBox";
        if (name.EndsWith("proj", StringComparison.OrdinalIgnoreCase) || name is "global.json" or "nuget.config") return "IconBoxes";

        if (ext is ".md" or ".markdown" or ".txt" or ".log" or ".rtf") return "IconFileText";
        if (ext is ".json" or ".jsonc") return "IconBraces";
        if (CodeExts.Contains(ext)) return "IconCode";
        if (ConfigExts.Contains(ext)) return "IconSettings";
        if (ImageExts.Contains(ext)) return "IconImage";
        if (DataExts.Contains(ext)) return "IconDatabase";
        if (ScriptExts.Contains(ext)) return "IconTerminal";
        if (ext is ".sln" or ".suo") return "IconBoxes";
        if (ext is ".zip" or ".7z" or ".rar" or ".tar" or ".gz") return "IconStorage";
        if (ext is ".key" or ".pem" or ".crt" or ".cer" or ".pfx") return "IconKey";

        return "IconFileText";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → Visibility (true=Visible, false=Collapsed).</summary>
public sealed class BoolVisibilityConverter : IValueConverter
{
    public static readonly BoolVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>IsExpanded(bool) → 아래/오른쪽 chevron 아이콘. 프로젝트 트리 펼침 표시용.</summary>
public sealed class ChevronConverter : IValueConverter
{
    public static readonly ChevronConverter Instance = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is true ? "IconChevronDown" : "IconChevronRight";
        return Application.Current?.TryFindResource(key) as Geometry;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
        var key = value switch
        {
            FileNode node    => GetIconKey(node.Name, node.IsDirectory),
            ScmTreeNode node => GetIconKey(node.Name, node.IsFolder),
            true             => "IconFolder",
            _                => "IconFileText",
        };
        return Application.Current?.TryFindResource(key) as Geometry;
    }

    private static string GetIconKey(string nodeName, bool isDirectory)
    {
        if (isDirectory) return "IconFolder";

        var name = nodeName.ToLowerInvariant();
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

/// <summary>
/// Visual Studio 2022 Image Library의 파일 형식 아이콘.
/// 밝은 테마는 원본, 다크 테마는 Visual Studio ImageThemingUtilities로 변환한 PNG를 사용한다.
/// </summary>
public sealed class VisualStudioFileIconConverter : IValueConverter
{
    public static readonly VisualStudioFileIconConverter LightInstance = new(dark: false);
    public static readonly VisualStudioFileIconConverter DarkInstance = new(dark: true);

    private const string PackRoot =
        "pack://application:,,,/Resources/Images/FileTypes/VisualStudio2022/";

    private static readonly Dictionary<string, string> Icons = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "CSFileNode.png",
        [".py"] = "PyFileNode.png",
        [".ts"] = "TSFileNode.png",
        [".tsx"] = "TSFileNode.png",
        [".js"] = "JSScript.png",
        [".jsx"] = "JSXScript.png",
        [".c"] = "CFile.png",
        [".h"] = "CFile.png",
        [".cpp"] = "CPPFileNode.png",
        [".cc"] = "CPPFileNode.png",
        [".cxx"] = "CPPFileNode.png",
        [".hpp"] = "CPPFileNode.png",
        [".hxx"] = "CPPFileNode.png",
        [".fs"] = "FSFileNode.png",
        [".fsi"] = "FSFileNode.png",
        [".fsx"] = "FSFileNode.png",
        [".vb"] = "VBFileNode.png",
        [".css"] = "CSSourceFile.png",
        [".scss"] = "CSSourceFile.png",
        [".sass"] = "CSSourceFile.png",
        [".less"] = "CSSourceFile.png",
        [".ps1"] = "PowershellFile.png",
        [".psm1"] = "PowershellFile.png",
        [".psd1"] = "PowershellFile.png",
        [".xaml"] = "WPFFile.png",
        [".razor"] = "CSRazorFile.png",
        [".cshtml"] = "CSRazorFile.png",
        [".png"] = "Image.png",
        [".jpg"] = "Image.png",
        [".jpeg"] = "Image.png",
        [".gif"] = "Image.png",
        [".webp"] = "Image.png",
        [".bmp"] = "Image.png",
        [".ico"] = "Image.png",
        [".svg"] = "Image.png",
        [".avif"] = "Image.png",
        [".tif"] = "Image.png",
        [".tiff"] = "Image.png",
        [".db"] = "DatabaseFile.png",
        [".sqlite"] = "DatabaseFile.png",
        [".sqlite3"] = "DatabaseFile.png",
        [".mdb"] = "DatabaseFile.png",
        [".accdb"] = "DatabaseFile.png",
        [".sql"] = "SQLDatabase.png",
        [".csproj"] = "CSProjectNode.png",
        [".fsproj"] = "FSProjectNode.png",
        [".vbproj"] = "VBProjectNode.png",
        [".pyproj"] = "PYProjectNode.png",
        [".json"] = "JsonFile.png",
        [".jsonc"] = "JsonFile.png",
        [".html"] = "HTMLFile.png",
        [".htm"] = "HTMLFile.png",
        [".xml"] = "XmlFile.png",
        [".yaml"] = "YamlFile.png",
        [".yml"] = "YamlFile.png",
        [".md"] = "MarkdownFile.png",
        [".markdown"] = "MarkdownFile.png",
        [".php"] = "PHPFile.png",
        [".java"] = "JavaSource.png",
        [".config"] = "ConfigurationFile.png",
        [".conf"] = "ConfigurationFile.png",
        [".ini"] = "ConfigurationFile.png",
        [".toml"] = "ConfigurationFile.png",
        [".props"] = "ConfigurationFile.png",
        [".targets"] = "ConfigurationFile.png",
        [".txt"] = "TextFile.png",
        [".log"] = "TextFile.png",
        [".rtf"] = "TextFile.png",
    };

    // 기존 파일명 전용 아이콘(package, git, .NET 구성)을 우선한다.
    private static readonly HashSet<string> UseFallbackIcon = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json", "package-lock.json", "pnpm-lock.yaml", "yarn.lock", "bun.lockb",
        "global.json", "nuget.config",
    };

    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _dark;

    private VisualStudioFileIconConverter(bool dark) => _dark = dark;

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var (name, isDirectory) = value switch
        {
            FileNode node    => (node.Name, node.IsDirectory),
            ScmTreeNode node => (node.Name, node.IsFolder),
            string text      => (text, false),
            _                => (string.Empty, true),
        };
        if (isDirectory || string.IsNullOrWhiteSpace(name) || UseFallbackIcon.Contains(name)) return null;

        var ext = Path.GetExtension(name);
        if (!Icons.TryGetValue(ext, out var fileName)) return null;
        return LoadIcon(_dark, fileName);
    }

    internal static ImageSource LoadIcon(bool dark, string fileName)
    {
        var uri = PackRoot + (dark ? "Dark/" : string.Empty) + fileName;

        lock (Cache)
        {
            if (Cache.TryGetValue(uri, out var cached)) return cached;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(uri, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            Cache[uri] = image;
            return image;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>트리 펼침 상태에 맞는 Visual Studio 폴더 아이콘.</summary>
public sealed class VisualStudioFolderIconConverter : IValueConverter
{
    public static readonly VisualStudioFolderIconConverter LightInstance = new(false);
    public static readonly VisualStudioFolderIconConverter DarkInstance = new(true);
    private const string PackRoot =
        "pack://application:,,,/Resources/Images/FileTypes/VisualStudio2017/";
    private static readonly Dictionary<string, ImageSource> Cache = [];
    private readonly bool _dark;

    private VisualStudioFolderIconConverter(bool dark) => _dark = dark;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => LoadIcon(_dark, value is true);

    private static ImageSource LoadIcon(bool dark, bool opened)
    {
        var uri = PackRoot
            + (dark ? "Dark/" : string.Empty)
            + (opened ? "FolderOpened.png" : "FolderClosed.png");

        lock (Cache)
        {
            if (Cache.TryGetValue(uri, out var cached)) return cached;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(uri, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            Cache[uri] = image;
            return image;
        }
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
        var key = value is true ? "IconChevronUp" : "IconChevronDown";
        return Application.Current?.TryFindResource(key) as Geometry;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

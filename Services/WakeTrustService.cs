using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevezCode.Services;

/// <summary>깨우기 전용 세션이 실행될 DevezCode 설치 경로의 CLI 신뢰 상태를 확인한다.</summary>
public static class WakeTrustService
{
    public static string InstallDirectory => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    public static bool IsTrusted(string agentId) => agentId.ToLowerInvariant() switch
    {
        "claude" => IsClaudeTrusted(),
        "codex" => IsCodexTrusted(),
        _ => false,
    };

    private static bool IsClaudeTrusted()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("projects", out var projects) ||
                projects.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var project in projects.EnumerateObject())
            {
                if (!IsSameOrAncestor(project.Name, InstallDirectory)) continue;
                if (project.Value.ValueKind == JsonValueKind.Object &&
                    project.Value.TryGetProperty("hasTrustDialogAccepted", out var accepted) &&
                    accepted.ValueKind == JsonValueKind.True)
                    return true;
            }
        }
        catch { /* 설정이 없거나 읽을 수 없으면 미신뢰 */ }
        return false;
    }

    private static bool IsCodexTrusted()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
        try
        {
            string? projectPath = null;
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("[projects.", StringComparison.Ordinal) && line.EndsWith(']'))
                {
                    projectPath = UnquoteTomlKey(line[10..^1].Trim());
                    continue;
                }

                if (projectPath != null && line.StartsWith("[", StringComparison.Ordinal))
                {
                    projectPath = null;
                    continue;
                }

                if (projectPath != null &&
                    Regex.IsMatch(line, """^trust_level\s*=\s*["']trusted["']\s*$""", RegexOptions.IgnoreCase) &&
                    IsSameOrAncestor(projectPath, InstallDirectory))
                    return true;
            }
        }
        catch { /* 설정이 없거나 읽을 수 없으면 미신뢰 */ }
        return false;
    }

    private static string UnquoteTomlKey(string value)
    {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1];
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try { return JsonSerializer.Deserialize<string>(value) ?? value[1..^1]; }
            catch { return value[1..^1]; }
        }
        return value;
    }

    private static bool IsSameOrAncestor(string candidate, string directory)
    {
        try
        {
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate.Replace('/', Path.DirectorySeparatorChar)));
            var child = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            var relative = Path.GetRelativePath(parent, child);
            return relative == "." ||
                   (!Path.IsPathRooted(relative) && relative != ".." &&
                    !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        }
        catch { return false; }
    }
}

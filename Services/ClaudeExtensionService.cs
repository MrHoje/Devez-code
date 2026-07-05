using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>Claude Code 스킬/서브에이전트 로컬 관리 — 파일 시스템 직접 조작(CLI 미경유).
/// 스킬:  <c>~/.claude/skills/&lt;name&gt;/SKILL.md</c> · 에이전트: <c>~/.claude/agents/&lt;name&gt;.md</c>.
/// 잠금(숨김)은 SKILL.md ↔ SKILL.md.off 로 파일명을 바꿔 Claude 의 스킬 탐색에서 제외한다.
/// v1 은 사용자 스코프(~/.claude)만 다룬다(프로젝트 스코프는 후속).</summary>
public static class ClaudeExtensionService
{
    private const string DisabledSuffix = ".off";   // SKILL.md.off = 잠금(숨김)

    private static string ClaudeHome
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    private static string SkillsDir => Path.Combine(ClaudeHome, "skills");
    private static string AgentsDir => Path.Combine(ClaudeHome, "agents");
    private static string PluginsDir => Path.Combine(ClaudeHome, "plugins");

    // ── 스킬 ──────────────────────────────────────────────────────
    // /skills 와 동일하게: 개인 스킬(~/.claude/skills) + 설치된 플러그인이 제공하는 스킬을 함께 보여준다.
    public static Task<List<ClaudeSkill>> SkillsAsync() => Task.Run(() =>
    {
        var personal = new List<ClaudeSkill>();
        try
        {
            if (Directory.Exists(SkillsDir))
                foreach (var dir in Directory.EnumerateDirectories(SkillsDir))
                {
                    var active = Path.Combine(dir, "SKILL.md");
                    var off = active + DisabledSuffix;
                    bool enabled = File.Exists(active);
                    var file = enabled ? active : (File.Exists(off) ? off : null);
                    if (file == null) continue;   // SKILL.md(.off) 없는 폴더는 스킬 아님
                    var (name, desc) = ParseFrontmatter(file);
                    personal.Add(new ClaudeSkill
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(dir) : name,
                        Description = desc,
                        Scope = "user",
                        Dir = dir,
                        FilePath = file,
                        Enabled = enabled,
                    });
                }
        }
        catch { }

        var plugin = new List<ClaudeSkill>();
        try
        {
            foreach (var (label, installPath) in InstalledPluginPaths())
            {
                var skillsRoot = Path.Combine(installPath, "skills");
                if (!Directory.Exists(skillsRoot)) continue;
                foreach (var dir in Directory.EnumerateDirectories(skillsRoot))
                {
                    var file = Path.Combine(dir, "SKILL.md");
                    if (!File.Exists(file)) continue;
                    var (name, desc) = ParseFrontmatter(file);
                    plugin.Add(new ClaudeSkill
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(dir) : name,
                        Description = desc,
                        Scope = label,           // 플러그인 표시명(배지)
                        Dir = dir,
                        FilePath = file,
                        Enabled = true,
                        IsPlugin = true,
                    });
                }
            }
        }
        catch { }

        // 개인 스킬 먼저, 그다음 플러그인 스킬(플러그인명 → 이름 순).
        return personal.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Concat(plugin.OrderBy(s => s.Scope, StringComparer.OrdinalIgnoreCase)
                          .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
    });

    /// <summary>installed_plugins.json 을 읽어 각 설치 플러그인의 (표시명, installPath) 를 돌려준다.
    /// 표시명은 "name@marketplace" 의 name 부분. installPath 가 존재하는 항목만.</summary>
    private static IEnumerable<(string label, string installPath)> InstalledPluginPaths()
    {
        var result = new List<(string, string)>();
        try
        {
            var jsonPath = Path.Combine(PluginsDir, "installed_plugins.json");
            if (!File.Exists(jsonPath)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (!doc.RootElement.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Object)
                return result;
            foreach (var prop in plugins.EnumerateObject())
            {
                var key = prop.Name;                        // "name@marketplace"
                var at = key.IndexOf('@');
                var label = at > 0 ? key.Substring(0, at) : key;
                if (prop.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in prop.Value.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Object
                        && entry.TryGetProperty("installPath", out var ip)
                        && ip.ValueKind == JsonValueKind.String)
                    {
                        var path = ip.GetString() ?? "";
                        if (Directory.Exists(path)) { result.Add((label, path)); break; }   // 첫(활성) 버전만
                    }
                }
            }
        }
        catch { }
        return result;
    }

    /// <summary>스킬 잠금(숨김) 토글. enable=true → SKILL.md, false → SKILL.md.off.
    /// 성공 시 갱신된 활성 파일 경로를 반환(실패 시 null).</summary>
    public static Task<string?> SetSkillEnabledAsync(ClaudeSkill skill, bool enable) => Task.Run<string?>(() =>
    {
        try
        {
            var active = Path.Combine(skill.Dir, "SKILL.md");
            var off = active + DisabledSuffix;
            if (enable)
            {
                if (File.Exists(active)) return active;      // 이미 활성
                if (!File.Exists(off)) return null;
                File.Move(off, active);
                return active;
            }
            else
            {
                if (File.Exists(off)) { if (File.Exists(active)) File.Delete(active); return off; }
                if (!File.Exists(active)) return null;
                File.Move(active, off);
                return off;
            }
        }
        catch { return null; }
    });

    // ── 에이전트 ──────────────────────────────────────────────────
    public static Task<List<ClaudeAgent>> AgentsAsync() => Task.Run(() =>
    {
        var list = new List<ClaudeAgent>();
        try
        {
            if (!Directory.Exists(AgentsDir)) return list;
            foreach (var file in Directory.EnumerateFiles(AgentsDir, "*.md", SearchOption.TopDirectoryOnly))
            {
                var (name, desc) = ParseFrontmatter(file);
                list.Add(new ClaudeAgent
                {
                    Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(file) : name,
                    Description = desc,
                    Scope = "user",
                    FilePath = file,
                });
            }
        }
        catch { }
        return list.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    });

    /// <summary>에이전트 .md 파일 삭제.</summary>
    public static Task<bool> DeleteAgentAsync(string path) => Task.Run(() =>
    {
        try { if (File.Exists(path)) File.Delete(path); return true; }
        catch { return false; }
    });

    // ── 파일 읽기/쓰기(에디터 공용) ───────────────────────────────
    public static Task<string> ReadAsync(string path) => Task.Run(() =>
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch { return ""; }
    });

    public static Task<bool> WriteAsync(string path, string content) => Task.Run(() =>
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, content);
            return true;
        }
        catch { return false; }
    });

    /// <summary>탐색기에서 스킬/에이전트 루트 폴더 열기(없으면 생성).</summary>
    public static void OpenFolder(bool skills)
    {
        try
        {
            var dir = skills ? SkillsDir : AgentsDir;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch { }
    }

    // ── frontmatter 파서 ──────────────────────────────────────────
    private static readonly Regex NameRx = new(@"^\s*name\s*:\s*(.+?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DescRx = new(@"^\s*description\s*:\s*(.+?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>YAML frontmatter(--- … ---) 에서 name·description 을 뽑는다. 없으면 빈 문자열.</summary>
    private static (string name, string desc) ParseFrontmatter(string path)
    {
        try
        {
            string name = "", desc = "";
            using var sr = new StreamReader(path);
            var first = sr.ReadLine();
            if (first == null || first.Trim() != "---") return (name, desc);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (line.Trim() == "---") break;
                if (name.Length == 0) { var m = NameRx.Match(line); if (m.Success) { name = Unquote(m.Groups[1].Value); continue; } }
                if (desc.Length == 0) { var m = DescRx.Match(line); if (m.Success) desc = Unquote(m.Groups[1].Value); }
            }
            return (name, desc);
        }
        catch { return ("", ""); }
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            s = s.Substring(1, s.Length - 2);
        return s.Trim();
    }
}

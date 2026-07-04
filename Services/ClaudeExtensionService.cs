using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    // ── 스킬 ──────────────────────────────────────────────────────
    public static Task<List<ClaudeSkill>> SkillsAsync() => Task.Run(() =>
    {
        var list = new List<ClaudeSkill>();
        try
        {
            if (!Directory.Exists(SkillsDir)) return list;
            foreach (var dir in Directory.EnumerateDirectories(SkillsDir))
            {
                var active = Path.Combine(dir, "SKILL.md");
                var off = active + DisabledSuffix;
                bool enabled = File.Exists(active);
                var file = enabled ? active : (File.Exists(off) ? off : null);
                if (file == null) continue;   // SKILL.md(.off) 없는 폴더는 스킬 아님
                var (name, desc) = ParseFrontmatter(file);
                list.Add(new ClaudeSkill
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
        return list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    });

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

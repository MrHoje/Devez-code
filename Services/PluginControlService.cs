using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>플러그인 컨트롤러의 에이전트 공통 백엔드. claude/gajae 는 CLI 로 완전 관리하고,
/// opencode 는 네이티브 on/off·목록 API 가 없어 config 파일(plugin 배열) + plugin 폴더 파일로 추가/삭제만 다룬다.</summary>
public static class PluginControlService
{
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    // ── 목록 ──────────────────────────────────────────────────────
    public static async Task<List<PluginItem>> ListAsync(string agentId) => agentId switch
    {
        "claude"   => await ListClaudeAsync(),
        "gajae"    => await ListGjcAsync(),
        "opencode" => ListOpenCode(),
        _          => new List<PluginItem>(),
    };

    private static async Task<List<PluginItem>> ListClaudeAsync()
    {
        var src = await ClaudePluginService.ListAsync();
        return src.Select(p => new PluginItem
        {
            AgentId = "claude", Id = p.Id, Name = p.Name, Source = p.Marketplace,
            Version = p.Version, Enabled = p.Enabled,
            Meta = string.Join("  ", new[] { p.McpText, p.UpdatedText }.Where(s => !string.IsNullOrEmpty(s))),
            CanToggle = true, CanUpdate = true, CanDetails = true,
        }).ToList();
    }

    // gjc plugin list --json → { npm:[], marketplace:[], gjc:[] }. 항목 스키마는 방어적으로 파싱
    // (id/name, version, enabled 후보 필드를 모두 시도). 현재 설치본이 없어 실데이터 검증은 추후.
    private static async Task<List<PluginItem>> ListGjcAsync()
    {
        var items = new List<PluginItem>();
        var json = await RunCaptureAsync("gjc", "plugin list --json");
        if (string.IsNullOrWhiteSpace(json)) return items;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
                foreach (var cat in new[] { "npm", "marketplace", "gjc" })
                    if (root.TryGetProperty(cat, out var arr) && arr.ValueKind == JsonValueKind.Array)
                        foreach (var el in arr.EnumerateArray())
                            items.Add(GjcItem(el, cat));
            else if (root.ValueKind == JsonValueKind.Array)
                foreach (var el in root.EnumerateArray()) items.Add(GjcItem(el, ""));
        }
        catch { }
        return items;
    }

    private static PluginItem GjcItem(JsonElement el, string cat)
    {
        // 문자열 항목이면 그 자체가 이름
        if (el.ValueKind == JsonValueKind.String)
            return new PluginItem { AgentId = "gajae", Id = el.GetString() ?? "", Name = el.GetString() ?? "",
                Source = cat, Enabled = true, CanToggle = true, CanUpdate = true, CanDetails = true };
        var id = FirstStr(el, "id", "name", "package");
        return new PluginItem
        {
            AgentId = "gajae", Id = id, Name = FirstStr(el, "name", "id", "package"),
            Source = string.IsNullOrEmpty(cat) ? FirstStr(el, "source", "kind") : cat,
            Version = FirstStr(el, "version", "ver"),
            Enabled = !el.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False,
            CanToggle = true, CanUpdate = true, CanDetails = true,
        };
    }

    private static List<PluginItem> ListOpenCode()
    {
        var items = new List<PluginItem>();
        // 1) config plugin 배열 (npm 모듈)
        foreach (var m in LoadOpenCodePluginArray())
            items.Add(new PluginItem { AgentId = "opencode", Id = m, Name = m, Source = "npm",
                Enabled = true, RemoveKind = "npm" });
        // 2) plugin/ · tui-plugins/ 폴더의 로컬 파일 (자동 로드)
        foreach (var (folder, label) in new[] { ("plugin", "폴더"), ("tui-plugins", "tui") })
        {
            var dir = Path.Combine(OpenCodeConfigDir(), folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(f);
                var isDevez = name.StartsWith("devez", StringComparison.OrdinalIgnoreCase);
                items.Add(new PluginItem { AgentId = "opencode", Id = f, Name = name, Source = label,
                    Enabled = true, RemoveKind = "file", IsProtected = isDevez });
            }
        }
        return items;
    }

    // ── claude/gjc 제어 ───────────────────────────────────────────
    public static async Task<string> EnableAsync(PluginItem p) => p.AgentId switch
    {
        "claude" => await ClaudePluginService.EnableAsync(p.Id),
        "gajae"  => await RunCaptureAsync("gjc", $"plugin enable {Q(p.Id)}"),
        _        => "",
    };
    public static async Task<string> DisableAsync(PluginItem p) => p.AgentId switch
    {
        "claude" => await ClaudePluginService.DisableAsync(p.Id),
        "gajae"  => await RunCaptureAsync("gjc", $"plugin disable {Q(p.Id)}"),
        _        => "",
    };
    public static async Task<string> DetailsAsync(PluginItem p) => p.AgentId switch
    {
        "claude" => await ClaudePluginService.DetailsAsync(p.Id),
        "gajae"  => await RunCaptureAsync("gjc", $"plugin features {Q(p.Id)}"),
        _        => "",
    };
    public static async Task<string> UpdateAsync(PluginItem p) => p.AgentId switch
    {
        "claude" => await ClaudePluginService.UpdateAsync(p.Id),
        "gajae"  => await RunCaptureAsync("gjc", $"plugin upgrade {Q(p.Id)}", 120000),
        _        => "",
    };
    public static async Task<string> UninstallAsync(PluginItem p) => p.AgentId switch
    {
        "claude"   => await ClaudePluginService.UninstallAsync(p.Id),
        "gajae"    => await RunCaptureAsync("gjc", $"plugin uninstall {Q(p.Id)}", 60000),
        "opencode" => OpenCodeRemove(p),
        _          => "",
    };

    // ── 설치/추가 ─────────────────────────────────────────────────
    /// <summary>설치. claude/gjc/opencode 모두 신뢰·npm 설치 프롬프트가 있을 수 있어 보이는 콘솔로 스폰.</summary>
    public static void SpawnInstall(string agentId, string target)
    {
        switch (agentId)
        {
            case "claude":   Spawn("claude", $"plugin install {Q(target)}"); break;
            case "gajae":    Spawn("gjc", $"plugin install {Q(target)}"); break;
            case "opencode": Spawn("opencode", $"plugin {Q(target)}"); break;  // npm 설치 + config 갱신
        }
    }

    // ── 마켓플레이스 (claude/gjc) ─────────────────────────────────
    public static async Task<List<ClaudeMarketplace>> MarketplacesAsync(string agentId)
    {
        if (agentId == "claude") return await ClaudePluginService.MarketplacesAsync();
        if (agentId == "gajae")
        {
            var list = new List<ClaudeMarketplace>();
            var json = await RunCaptureAsync("gjc", "plugin marketplace --json");
            if (string.IsNullOrWhiteSpace(json)) return list;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        var name = FirstStr(el, "name", "id");
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        list.Add(new ClaudeMarketplace
                        {
                            Name = name, Source = FirstStr(el, "source", "kind"),
                            Repo = FirstStr(el, "repo"), Url = FirstStr(el, "url", "source"),
                        });
                    }
            }
            catch { }
            return list;
        }
        return new List<ClaudeMarketplace>();
    }

    public static void SpawnMarketplaceAdd(string agentId, string source)
    {
        if (agentId == "claude") ClaudePluginService.SpawnMarketplaceAdd(source);
        else if (agentId == "gajae") Spawn("gjc", $"plugin marketplace add {Q(source)}");
    }

    public static async Task<string> MarketplaceUpdateAsync(string agentId, string? name = null) => agentId switch
    {
        "claude" => await ClaudePluginService.MarketplaceUpdateAsync(name),
        "gajae"  => await RunCaptureAsync("gjc", string.IsNullOrWhiteSpace(name)
                        ? "plugin marketplace update" : $"plugin marketplace update {Q(name!)}", 120000),
        _        => "",
    };

    public static async Task<string> MarketplaceRemoveAsync(string agentId, string name) => agentId switch
    {
        "claude" => await ClaudePluginService.MarketplaceRemoveAsync(name),
        "gajae"  => await RunCaptureAsync("gjc", $"plugin marketplace remove {Q(name)}"),
        _        => "",
    };

    // ── opencode 파일/설정 조작 ───────────────────────────────────
    private static string OpenCodeRemove(PluginItem p)
    {
        if (p.IsProtected) return "이 플러그인은 DevezCode 연동용이라 제거할 수 없습니다.";
        try
        {
            if (p.RemoveKind == "file")
            {
                if (File.Exists(p.Id)) File.Delete(p.Id);
                return "제거됨.";
            }
            // npm: config plugin 배열에서 제거
            RemoveOpenCodePluginFromConfig(p.Id);
            return "config 에서 제거됨. (node_modules 는 남아 있으나 로드되지 않습니다)";
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static string OpenCodeConfigDir() => Path.GetDirectoryName(OpenCodeMcpBackend.ConfigPath) ?? "";

    private static List<string> LoadOpenCodePluginArray()
    {
        var result = new List<string>();
        var path = OpenCodeMcpBackend.ConfigPath;
        if (!File.Exists(path)) return result;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("plugin", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.String) result.Add(el.GetString() ?? "");
        }
        catch { }
        return result.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    private static void RemoveOpenCodePluginFromConfig(string module)
    {
        var path = OpenCodeMcpBackend.ConfigPath;
        if (!File.Exists(path)) return;
        JsonElement root;
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(path))) root = doc.RootElement.Clone();
        if (root.ValueKind != JsonValueKind.Object) return;

        using var final = new MemoryStream();
        using (var w = new Utf8JsonWriter(final, new JsonWriterOptions { Indented = true, IndentSize = 2 }))
        {
            w.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.NameEquals("plugin"))
                {
                    w.WritePropertyName("plugin");
                    w.WriteStartArray();
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                        foreach (var el in prop.Value.EnumerateArray())
                            if (!(el.ValueKind == JsonValueKind.String &&
                                  string.Equals(el.GetString(), module, StringComparison.Ordinal)))
                                el.WriteTo(w);
                    w.WriteEndArray();
                    continue;
                }
                prop.WriteTo(w);
            }
            w.WriteEndObject();
        }
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Encoding.UTF8.GetString(final.ToArray()));
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
    }

    // ── 실행 헬퍼 ─────────────────────────────────────────────────
    private static string Q(string s) => "\"" + (s ?? "").Replace("\"", "") + "\"";

    private static void Spawn(string exe, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k {exe} {args}",
                UseShellExecute = true,
                CreateNoWindow = false,
            });
        }
        catch { }
    }

    private static async Task<string> RunCaptureAsync(string exe, string args, int timeoutMs = 30000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {exe} {args}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutMs);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return ""; }
            var raw = Ansi.Replace(await outTask, "");
            var err = Ansi.Replace(await errTask, "");
            return string.IsNullOrWhiteSpace(raw) ? err.Trim() : raw.Trim();
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static string FirstStr(JsonElement o, params string[] props)
    {
        if (o.ValueKind != JsonValueKind.Object) return "";
        foreach (var p in props)
            if (o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
        return "";
    }
}

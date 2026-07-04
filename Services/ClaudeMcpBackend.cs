using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>Claude Code 의 <c>~/.claude.json</c> 최상위 <c>mcpServers</c> 섹션.
/// user-level(전역) 만 다룬다. 프로젝트별 <c>projects[&lt;path&gt;].mcpServers</c> 와
/// <c>plugin:*</c> / <c>claude.ai *</c>(OAuth/플러그인 제공) 는 read-only 라서 여기선 안 건드림.</summary>
public sealed class ClaudeMcpBackend : IMcpBackend
{
    public string Id => "claude";
    public string DisplayName => "Claude Code";
    public string ConfigPathHint => ".claude.json (user-level)";

    public static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    public bool IsAvailable
    {
        get
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "claude",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                return p != null && p.WaitForExit(3000) && p.ExitCode == 0;
            }
            catch { return false; }
        }
    }

    /// <inheritdoc />
    public List<McpServer> Load()
    {
        var (root, _) = LoadRaw();
        var result = new List<McpServer>();
        if (root == null) return result;
        var r = root.Value;

        // 1) 사용자 추가 서버 (편집 가능)
        if (r.TryGetProperty("mcpServers", out var ms) && ms.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in ms.EnumerateObject())
            {
                var srv = ParseServer(prop.Name, prop.Value);
                if (srv != null) result.Add(srv);
            }
        }

        // 2) Anthropic 제공 first-party 서버 (claude.ai Figma, Notion …) — 바이너리에 하드코딩.
        //    URL 은 Claude Code 내부 카탈로그에 있고, OAuth 자격증명은
        //    ~/.claude/.credentials.json 의 mcpOAuth 섹션에 있다.
        var oauthTokens = LoadOAuthTokens();
        foreach (var fs in FirstPartyCatalog)
        {
            if (result.Any(s => s.Name == fs.Name)) continue;
            result.Add(new McpServer
            {
                Name = fs.Name,
                Type = McpServerType.Remote,
                Url = fs.Url,
                Enabled = true,
                IsReadOnly = true,
                ReadOnlyReason = "Anthropic 제공 (편집 불가)",
                Status = oauthTokens.Contains(fs.Name) ? McpLiveStatus.Connected : McpLiveStatus.NeedsAuth,
            });
        }

        // 3) 플러그인 제공 서버 (plugin:cloudflare:*, …) — ~/.claude/plugins/cache/<plugin>/<ver>/.mcp.json
        foreach (var srv in LoadPluginServers())
        {
            if (result.Any(s => s.Name == srv.Name)) continue;
            result.Add(srv);
        }
        return result;
    }

    // ── First-party 카탈로그 (Claude Code 가 기본 노출하는 서버) ─────────
    // 추후 새 서버 추가 시 Claude Code changelog / source 참고해 갱신.
    private record struct FirstPartyEntry(string Name, string Url)
    {
        public override string ToString() => $"{Name} ({Url})";
    }
    private static readonly FirstPartyEntry[] FirstPartyCatalog = new[]
    {
        new FirstPartyEntry("claude.ai Figma",                  "https://mcp.figma.com/mcp"),
        new FirstPartyEntry("claude.ai Notion",                 "https://mcp.notion.com/mcp"),
        new FirstPartyEntry("claude.ai Supabase",               "https://mcp.supabase.com/mcp"),
        new FirstPartyEntry("claude.ai Cloudflare Developer Platform", "https://mcp.cloudflare.com/mcp"),
        new FirstPartyEntry("claude.ai cloudflare-api",         "https://mcp.cloudflare.com/mcp"),
        new FirstPartyEntry("claude.ai cloudflare-builds",      "https://mcp.cloudflare.com/mcp"),
    };

    /// <summary>~/.claude/.credentials.json 의 mcpOAuth 섹션에서 first-party 서버의 인증 토큰 존재 여부.</summary>
    private static HashSet<string> LoadOAuthTokens()
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var credPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
        if (!File.Exists(credPath)) return tokens;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(credPath));
            // mcpOAuth: { "name|clientId": { "serverName": "...", ... }, ... }
            if (doc.RootElement.TryGetProperty("mcpOAuth", out var oauth) &&
                oauth.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in oauth.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object &&
                        prop.Value.TryGetProperty("serverName", out var sn) &&
                        sn.ValueKind == JsonValueKind.String)
                    {
                        tokens.Add(sn.GetString()!);
                    }
                }
            }
        }
        catch { /* 손상돼도 빈 집합 — Unknown 으로 표시 */ }
        return tokens;
    }

    /// <summary>~/.claude/plugins/cache/&lt;plugin&gt;/&lt;ver&gt;/.mcp.json 을 모두 읽어 합친다.
    /// 각 파일은 { "mcpServers": { "name": {...} } } 형식.</summary>
    private static IEnumerable<McpServer> LoadPluginServers()
    {
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "plugins", "cache");
        if (!Directory.Exists(cacheDir)) yield break;

        foreach (var pluginDir in Directory.EnumerateDirectories(cacheDir))
        {
            var pluginName = Path.GetFileName(pluginDir);
            // 각 plugin 폴더 안의 버전별 디렉터리를 훑는다.
            foreach (var verDir in Directory.EnumerateDirectories(pluginDir))
            {
                var mcpJson = Path.Combine(verDir, ".mcp.json");
                if (!File.Exists(mcpJson)) continue;
                List<McpServer>? list = null;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(mcpJson));
                    if (!doc.RootElement.TryGetProperty("mcpServers", out var ms) ||
                        ms.ValueKind != JsonValueKind.Object) continue;
                    list = new List<McpServer>();
                    foreach (var prop in ms.EnumerateObject())
                    {
                        var srv = ParseServer(prop.Name, prop.Value);
                        if (srv == null) continue;
                        srv.IsReadOnly = true;
                        srv.ReadOnlyReason = $"플러그인: {pluginName}";
                        // plugin: 접두사가 없으면 붙여서 표시 ( claude mcp list 출력과 일치)
                        if (!srv.Name.StartsWith("plugin:", StringComparison.Ordinal))
                            srv.Name = $"plugin:{pluginName}:{srv.Name}";
                        list.Add(srv);
                    }
                }
                catch { /* 손상 파일 무시 */ }
                if (list != null)
                    foreach (var s in list) yield return s;
            }
        }
    }

    /// <inheritdoc />
    public void Save(IEnumerable<McpServer> servers)
    {
        // read-only(first-party/plugin) 서버는 디스크에 다시 쓰지 않는다.
        // 사용자가 편집·삭제할 수 있는 영역은 user-level mcpServers 뿐.
        var editable = servers.Where(s => !s.IsReadOnly).ToList();

        var (root, _) = LoadRaw();
        using var final = new MemoryStream();
        using (var writer = new Utf8JsonWriter(final, new JsonWriterOptions { Indented = true, IndentSize = 2 }))
        {
            if (root.HasValue && root.Value.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var prop in root.Value.EnumerateObject())
                {
                    if (prop.NameEquals("mcpServers")) continue;
                    if (prop.NameEquals("projects")) continue;  // 따로 처리
                    prop.WriteTo(writer);  // 이름+값을 한 번에. 콤마/개행은 writer 의 Indented 옵션이 자동 처리.
                }
                WriteMcpObject(writer, editable);
                SyncProjectsDisabled(writer, root.Value, editable);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteStartObject();
                WriteMcpObject(writer, editable);
                writer.WriteEndObject();
            }
        }
        var dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, Encoding.UTF8.GetString(final.ToArray()));
        if (File.Exists(ConfigPath)) File.Replace(tmp, ConfigPath, null);
        else File.Move(tmp, ConfigPath);
    }

    /// <summary>projects[*].disabledMcpServers 와 enabled 상태를 동기화.
    /// Claude Code 의 mcpServers.X.enabled 는 무시되고 disabledMcpServers (서버 이름 배열) 만
    /// 존중되므로, 모든 프로젝트의 disabledMcpServers 를 editable 서버들의 Enabled 플래그에 맞춰
    /// 일괄 갱신한다. 프로젝트마다 따로 비활성화/활성화 토글 UI 가 없으므로 "전역 비활성화" 로
    /// 동작 ( 모든 프로젝트에서 숨김 ).</summary>
    private static void SyncProjectsDisabled(Utf8JsonWriter w, JsonElement root, IEnumerable<McpServer> editable)
    {
        // editable 에서 Enabled=false 인 이름들
        var disabledNames = editable
            .Where(s => !s.Enabled && !s.IsReadOnly)
            .Select(s => s.Name)
            .ToHashSet(StringComparer.Ordinal);

        // 기존 projects
        JsonElement projectsEl = default;
        bool hasProjects = root.TryGetProperty("projects", out projectsEl)
            && projectsEl.ValueKind == JsonValueKind.Object;

        w.WritePropertyName("projects");
        w.WriteStartObject();
        if (hasProjects)
        {
            foreach (var proj in projectsEl.EnumerateObject())
            {
                // 기존 disabledMcpServers 보존 + 새 disabledNames 가 있으면 추가
                var existing = new List<string>();
                if (proj.Value.TryGetProperty("disabledMcpServers", out var d) && d.ValueKind == JsonValueKind.Array)
                    foreach (var item in d.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            var n = item.GetString();
                            if (!string.IsNullOrEmpty(n)) existing.Add(n!);
                        }
                // editable 활성 서버 중 disabledMcpServers 에 있으면 제거
                var newDisabled = existing
                    .Where(n => !editable.Any(s => s.Enabled && s.Name == n))
                    .Concat(disabledNames.Where(n => !existing.Contains(n)))
                    .Distinct()
                    .ToList();

                // 프로젝트 객체 통째로 복사 ( 다른 필드 보존 ) 후 disabledMcpServers 만 갱신
                w.WritePropertyName(proj.Name);
                w.WriteStartObject();
                bool hasDisabled = false;
                foreach (var p in proj.Value.EnumerateObject())
                {
                    if (p.NameEquals("disabledMcpServers")) { hasDisabled = true; continue; }
                    p.WriteTo(w);
                }
                w.WritePropertyName("disabledMcpServers");
                w.WriteStartArray();
                foreach (var n in newDisabled) w.WriteStringValue(n);
                w.WriteEndArray();
                w.WriteEndObject();
            }
        }
        w.WriteEndObject();
    }

    /// <inheritdoc />
    public async Task RefreshStatusAsync(IEnumerable<McpServer> servers)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "claude",
                Arguments = "mcp list",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return;
            var outputTask = p.StandardOutput.ReadToEndAsync();
            var errTask    = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return; }
            var raw = await outputTask;
            var err = await errTask;
            ParseStatusInto(servers, raw, err);
        }
        catch { /* 실패 시 조용히 Unknown 유지 */ }
    }

    /// <inheritdoc />
    public IDisposable? WatchConfig(Action onChanged) => ConfigFileWatcher.Watch(ConfigPath, onChanged);

    // ── JSON 직렬화 ──────────────────────────────────────────────
    private static void WriteMcpObject(Utf8JsonWriter w, IEnumerable<McpServer> servers)
    {
        w.WritePropertyName("mcpServers");
        w.WriteStartObject();
        foreach (var s in servers)
        {
            if (string.IsNullOrWhiteSpace(s.Name)) continue;
            w.WritePropertyName(s.Name);
            WriteServer(w, s);
        }
        w.WriteEndObject();
    }

    private static void WriteServer(Utf8JsonWriter w, McpServer s)
    {
        w.WriteStartObject();
        if (s.Type == McpServerType.Local)
        {
            // Claude: "type":"stdio" + "command":"..." + "args":[...] + "env":{...}
            w.WriteString("type", "stdio");
            if (s.Command.Count > 0)
            {
                w.WriteString("command", s.Command[0]);
                if (s.Command.Count > 1)
                {
                    w.WritePropertyName("args");
                    w.WriteStartArray();
                    for (int i = 1; i < s.Command.Count; i++) w.WriteStringValue(s.Command[i]);
                    w.WriteEndArray();
                }
            }
            if (s.Environment.Count > 0)
            {
                w.WritePropertyName("env");
                w.WriteStartObject();
                foreach (var (k, v) in s.Environment) w.WriteString(k, v);
                w.WriteEndObject();
            }
        }
        else
        {
            // Claude: "type":"http" + "url":"..." + "headers":{...}
            w.WriteString("type", "http");
            w.WriteString("url", s.Url);
            if (s.Headers.Count > 0)
            {
                w.WritePropertyName("headers");
                w.WriteStartObject();
                foreach (var (k, v) in s.Headers) w.WriteString(k, v);
                w.WriteEndObject();
            }
        }
        // enabled/timeout은 Claude 스키마에 없음 (CLI 가 항상 활성으로 간주). 생략.
        w.WriteEndObject();
    }

    private static McpServer? ParseServer(string name, JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var type = el.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() : "stdio";

        var srv = new McpServer { Name = name };
        // Claude 의 type: "stdio" / "http" / "sse"
        if (type is "http" or "sse") srv.Type = McpServerType.Remote;

        if (srv.Type == McpServerType.Local)
        {
            // command (string) + args (array) → 단일 Command 리스트로 합침
            if (el.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.String)
                srv.Command.Add(cmd.GetString()!);
            if (el.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
                foreach (var a in args.EnumerateArray())
                    if (a.ValueKind == JsonValueKind.String) srv.Command.Add(a.GetString()!);
            if (el.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
                foreach (var kv in env.EnumerateObject())
                    srv.Environment[kv.Name] = kv.Value.ValueKind == JsonValueKind.String
                        ? kv.Value.GetString() ?? "" : kv.Value.ToString();
        }
        else
        {
            if (el.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                srv.Url = url.GetString() ?? "";
            if (el.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
                foreach (var kv in h.EnumerateObject())
                    srv.Headers[kv.Name] = kv.Value.ValueKind == JsonValueKind.String
                        ? kv.Value.GetString() ?? "" : kv.Value.ToString();
        }
        // Claude 는 enabled/disabled 별도 필드 없음. 글로벌 `disabledMcpServers` 목록으로 관리 — 우리는 건드리지 않음.
        srv.Enabled = true;
        return srv;
    }

    private static (JsonElement? root, byte[] raw) LoadRaw()
    {
        if (!File.Exists(ConfigPath)) return (null, Array.Empty<byte>());
        try
        {
            var bytes = File.ReadAllBytes(ConfigPath);
            using var doc = JsonDocument.Parse(bytes);
            return (doc.RootElement.Clone(), bytes);
        }
        catch { return (null, Array.Empty<byte>()); }
    }

    // ── 상태 파서 — `claude mcp list` 출력 ────────────────────────
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    /// <summary>실제 포맷 (1.17.x):
    /// <code>
    /// Checking MCP server health??
    /// claude.ai Figma: https://mcp.figma.com/mcp - ??Connected
    /// claude.ai Notion: https://mcp.notion.com/mcp - ??Connected
    /// plugin:cloudflare:cloudflare-api: https://mcp.cloudflare.com/mcp (HTTP) - ! Needs authentication
    /// chrome-devtools: npx -y chrome-devtools-mcp@latest - ??Connected
    /// </code>
    /// 이름 + 콜론 + (command|url) + 대시 + 상태 단어. plugin:* / claude.ai * 는 우리 관리 밖이므로 매칭돼도 무시.</summary>
    private static readonly Regex StatusLineRx = new(
        @"^\s*(?<name>[^:\s][^:]*?):\s+(?<detail>.+?)\s+-\s+(?<status>Connected|Failed|Needs authentication|Disabled)\s*$",
        RegexOptions.Compiled);

    private static void ParseStatusInto(IEnumerable<McpServer> servers, string rawOutput, string rawError)
    {
        var text = Ansi.Replace(rawOutput, "");
        var byName = servers.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            var m = StatusLineRx.Match(line);
            if (!m.Success) continue;
            var name = m.Groups["name"].Value.Trim();
            if (!byName.TryGetValue(name, out var srv)) continue;  // plugin:*/claude.ai * 는 무시
            srv.Status = m.Groups["status"].Value.ToLowerInvariant() switch
            {
                "connected" => McpLiveStatus.Connected,
                "disabled" => McpLiveStatus.Disabled,
                "needs authentication" => McpLiveStatus.NeedsAuth,
                "failed" => McpLiveStatus.Failed,
                _ => McpLiveStatus.Unknown,
            };
        }
    }

    // ── 런타임 제어 (라이브 대시보드용) ──────────────────────────────
    // claude mcp CLI: login/logout(OAuth) · get(상세) · list(상태). 제어는 모두 CLI 경유.

    /// <summary>어느 프로젝트에서든 disabledMcpServers 에 든 서버 이름의 합집합(=전역 비활성으로 간주).</summary>
    public static HashSet<string> LoadDisabledNames()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var (root, _) = LoadRaw();
        if (root == null || root.Value.ValueKind != JsonValueKind.Object) return set;
        if (!root.Value.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Object)
            return set;
        foreach (var proj in projects.EnumerateObject())
            if (proj.Value.TryGetProperty("disabledMcpServers", out var d) && d.ValueKind == JsonValueKind.Array)
                foreach (var it in d.EnumerateArray())
                    if (it.ValueKind == JsonValueKind.String)
                    {
                        var n = it.GetString();
                        if (!string.IsNullOrEmpty(n)) set.Add(n!);
                    }
        return set;
    }

    /// <summary>OAuth 인증 시작. 브라우저 플로우라 보이는 콘솔 창을 띄워 사용자가 완료하게 한다.
    /// (claude 는 .cmd shim 이라 cmd 경유. /k 로 결과를 남겨 사용자가 확인 후 닫음)</summary>
    public static void SpawnLogin(string name)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k claude mcp login \"{name.Replace("\"", "")}\"",
                UseShellExecute = true,
                CreateNoWindow = false,
            });
        }
        catch { /* CLI 미설치 등 — 호출부에서 상태 갱신 실패로 드러남 */ }
    }

    /// <summary>OAuth 자격증명 삭제. 비대화형 — stdout 캡처.</summary>
    public static Task<string> LogoutAsync(string name) => RunCaptureAsync($"logout \"{name.Replace("\"", "")}\"");

    /// <summary>서버 상세(claude mcp get). 명령·URL·툴 목록 등. 비대화형 — stdout 캡처.</summary>
    public static Task<string> GetAsync(string name) => RunCaptureAsync($"get \"{name.Replace("\"", "")}\"");

    private static async Task<string> RunCaptureAsync(string mcpArgs, int timeoutMs = 20000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c claude mcp {mcpArgs}",
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
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return ""; }
            var raw = Ansi.Replace(await outTask, "");
            var err = Ansi.Replace(await errTask, "");
            return string.IsNullOrWhiteSpace(raw) ? err.Trim() : raw.Trim();
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>서버를 전역(모든 프로젝트) 활성/비활성 토글. Claude 는 mcpServers.enabled 를 무시하고
    /// projects[*].disabledMcpServers(이름 배열)만 존중하므로, 모든 프로젝트의 그 목록을 갱신한다.
    /// first-party/plugin 서버도 이름 기준으로 동일하게 gate 되므로 uniform 하게 처리 가능.</summary>
    public static void SetServerEnabledGlobally(string name, bool enabled)
    {
        var (root, _) = LoadRaw();
        if (root == null || root.Value.ValueKind != JsonValueKind.Object) return;

        using var final = new MemoryStream();
        using (var w = new Utf8JsonWriter(final, new JsonWriterOptions { Indented = true, IndentSize = 2 }))
        {
            w.WriteStartObject();
            foreach (var prop in root.Value.EnumerateObject())
            {
                if (prop.NameEquals("projects")) continue; // 아래에서 갱신해 재작성
                prop.WriteTo(w);
            }
            w.WritePropertyName("projects");
            w.WriteStartObject();
            if (root.Value.TryGetProperty("projects", out var projects) &&
                projects.ValueKind == JsonValueKind.Object)
            {
                foreach (var proj in projects.EnumerateObject())
                {
                    w.WritePropertyName(proj.Name);
                    w.WriteStartObject();
                    var disabled = new List<string>();
                    foreach (var p in proj.Value.EnumerateObject())
                    {
                        if (p.NameEquals("disabledMcpServers"))
                        {
                            if (p.Value.ValueKind == JsonValueKind.Array)
                                foreach (var it in p.Value.EnumerateArray())
                                    if (it.ValueKind == JsonValueKind.String)
                                    {
                                        var n = it.GetString();
                                        if (!string.IsNullOrEmpty(n)) disabled.Add(n!);
                                    }
                            continue; // 아래에서 새로 씀
                        }
                        p.WriteTo(w);
                    }
                    if (enabled) disabled.RemoveAll(n => string.Equals(n, name, StringComparison.Ordinal));
                    else if (!disabled.Contains(name)) disabled.Add(name);
                    w.WritePropertyName("disabledMcpServers");
                    w.WriteStartArray();
                    foreach (var n in disabled) w.WriteStringValue(n);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }

        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, Encoding.UTF8.GetString(final.ToArray()));
        if (File.Exists(ConfigPath)) File.Replace(tmp, ConfigPath, null);
        else File.Move(tmp, ConfigPath);
    }
}

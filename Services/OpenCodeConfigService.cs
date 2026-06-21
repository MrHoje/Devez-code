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

/// <summary>opencode 글로벌 설정(~/.config/opencode/opencode.json) 의 <c>mcp</c> 섹션
/// 읽기/쓰기 + <c>opencode mcp list</c> 로 라이브 상태 조회.
/// 다른 필드( plugin, model, permission …) 는 건드리지 않고 보존한다.</summary>
public static class OpenCodeConfigService
{
    /// <summary>XDG_CONFIG_HOME 우선, 없으면 %USERPROFILE%\.config. opencode 1.17.x 가 그대로 쓴다.</summary>
    public static string ConfigPath
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var home = string.IsNullOrEmpty(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : xdg;
            return Path.Combine(home, "opencode", "opencode.json");
        }
    }

    /// <summary>파싱 + mcp 항목 추출. 파일이 없거나 mcp 섹션이 없으면 빈 목록 반환.</summary>
    public static List<McpServer> Load()
    {
        var (root, _) = LoadRaw();
        var result = new List<McpServer>();
        if (root == null) return result;
        var r = root.Value;

        if (r.TryGetProperty("mcp", out var mcp) && mcp.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in mcp.EnumerateObject())
            {
                var srv = ParseServer(prop.Name, prop.Value);
                if (srv != null) result.Add(srv);
            }
        }
        return result;
    }

    /// <summary>저장 — 기존 JSON 의 다른 필드( plugin, model, …) 를 그대로 두고 mcp 만 교체한다.
    /// 파일이 없으면 최소 JSON 으로 새로 만든다.</summary>
    public static void Save(IEnumerable<McpServer> servers)
    {
        var (root, rawBytes) = LoadRaw();

        // 단순화: System.Text.Json 의 들여쓰기 옵션에 맡기고, mcp 만 따로 만든 뒤 병합.
        // 더 안전한 방식: 원본 Element 를 직접 순회하며 mcp 만 교체.
        using var final = new MemoryStream();
        using (var writer = new Utf8JsonWriter(final, new JsonWriterOptions { Indented = true, IndentSize = 2 }))
        {
            if (root.HasValue && root.Value.ValueKind == JsonValueKind.Object)
            {
                bool first = true;
                writer.WriteStartObject();
                foreach (var prop in root.Value.EnumerateObject())
                {
                    if (prop.NameEquals("mcp")) continue;
                    if (!first) writer.WriteRawValue(",\n", false);
                    first = false;
                    prop.WriteTo(writer);
                }
                if (!first) writer.WriteRawValue(",\n", false);
                WriteMcpObject(writer, servers);
                writer.WriteEndObject();
            }
            else
            {
                WriteMcpObject(writer, servers);
            }
        }

        var dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // atomic write: 임시 파일 → 교체. 읽는 쪽( opencode )이 중간 상태를 보지 않게.
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, Encoding.UTF8.GetString(final.ToArray()));
        if (File.Exists(ConfigPath)) File.Replace(tmp, ConfigPath, null);
        else File.Move(tmp, ConfigPath);
    }

    /// <summary>기존에 없던 키를 추가할 때 (mcp 가 처음 생기는 경우) 호출.</summary>
    private static void WriteMcpObject(Utf8JsonWriter w, IEnumerable<McpServer> servers)
    {
        w.WritePropertyName("mcp");
        w.WriteStartObject();
        var first = true;
        foreach (var s in servers)
        {
            if (string.IsNullOrWhiteSpace(s.Name)) continue;
            if (!first) w.WriteRawValue(",\n", false);
            first = false;
            w.WritePropertyName(s.Name);
            WriteServer(w, s);
        }
        w.WriteEndObject();
    }

    private static void WriteObjectFiltered(Utf8JsonWriter w, JsonElement obj, string exceptKey) { }

    private static void WriteServer(Utf8JsonWriter w, McpServer s)
    {
        w.WriteStartObject();
        if (s.Type == McpServerType.Local)
        {
            w.WriteString("type", "local");
            w.WritePropertyName("command");
            w.WriteStartArray();
            foreach (var c in s.Command) w.WriteStringValue(c);
            w.WriteEndArray();
            if (s.Environment.Count > 0)
            {
                w.WritePropertyName("environment");
                w.WriteStartObject();
                foreach (var (k, v) in s.Environment) w.WriteString(k, v);
                w.WriteEndObject();
            }
        }
        else
        {
            w.WriteString("type", "remote");
            w.WriteString("url", s.Url);
            if (s.Headers.Count > 0)
            {
                w.WritePropertyName("headers");
                w.WriteStartObject();
                foreach (var (k, v) in s.Headers) w.WriteString(k, v);
                w.WriteEndObject();
            }
            if (s.OAuthMode == McpOAuthMode.Disabled)
            {
                w.WriteBoolean("oauth", false);
            }
            else if (s.OAuthMode == McpOAuthMode.Explicit)
            {
                w.WritePropertyName("oauth");
                w.WriteStartObject();
                if (!string.IsNullOrWhiteSpace(s.OAuthClientId)) w.WriteString("clientId", s.OAuthClientId);
                if (!string.IsNullOrWhiteSpace(s.OAuthClientSecret)) w.WriteString("clientSecret", s.OAuthClientSecret);
                if (!string.IsNullOrWhiteSpace(s.OAuthScope)) w.WriteString("scope", s.OAuthScope);
                w.WriteEndObject();
            }
        }
        if (!s.Enabled) w.WriteBoolean("enabled", false);
        if (s.TimeoutMs > 0) w.WriteNumber("timeout", s.TimeoutMs);
        w.WriteEndObject();
    }

    private static McpServer? ParseServer(string name, JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var type = el.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : "local";

        var srv = new McpServer { Name = name };
        if (type == "remote") srv.Type = McpServerType.Remote;

        if (srv.Type == McpServerType.Local)
        {
            if (el.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.Array)
                foreach (var c in cmd.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.String) srv.Command.Add(c.GetString()!);
            if (el.TryGetProperty("environment", out var env) && env.ValueKind == JsonValueKind.Object)
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
            if (el.TryGetProperty("oauth", out var oauth))
            {
                if (oauth.ValueKind == JsonValueKind.False) srv.OAuthMode = McpOAuthMode.Disabled;
                else if (oauth.ValueKind == JsonValueKind.Object)
                {
                    srv.OAuthMode = McpOAuthMode.Explicit;
                    if (oauth.TryGetProperty("clientId", out var cid) && cid.ValueKind == JsonValueKind.String) srv.OAuthClientId = cid.GetString();
                    if (oauth.TryGetProperty("clientSecret", out var cs) && cs.ValueKind == JsonValueKind.String) srv.OAuthClientSecret = cs.GetString();
                    if (oauth.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.String) srv.OAuthScope = sc.GetString();
                }
                // true 또는 누락 → Auto (키 자체를 안 쓰는 게 opencode 의 기본값)
            }
        }

        if (el.TryGetProperty("enabled", out var en) &&
            (en.ValueKind == JsonValueKind.False || en.ValueKind == JsonValueKind.True))
            srv.Enabled = en.GetBoolean();
        if (el.TryGetProperty("timeout", out var to) && to.ValueKind == JsonValueKind.Number && to.TryGetInt32(out var ms))
            srv.TimeoutMs = ms;

        return srv;
    }

    private static (JsonElement? root, byte[] raw) LoadRaw()
    {
        if (!File.Exists(ConfigPath)) return (null, Array.Empty<byte>());
        try
        {
            var bytes = File.ReadAllBytes(ConfigPath);
            // trailing whitespace/zero-byte 방어
            using var doc = JsonDocument.Parse(bytes);
            return (doc.RootElement.Clone(), bytes);
        }
        catch
        {
            return (null, Array.Empty<byte>());
        }
    }

    private static string? DetectIndent(byte[] raw)
    {
        if (raw.Length == 0) return null;
        var s = Encoding.UTF8.GetString(raw);
        // 이스케이프 quote 는 verbatim 문자열에서 "" 로 표현한다. \" 는 verbatim 의 이스케이프가 아니다.
        var m = Regex.Match(s, @"\n( +)\""");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>현재 디렉터리 컨텍스트로 <c>opencode mcp list</c> 를 돌려 각 서버의 라이브 상태를 채운다.
    /// ANSI 색코드와 박스문자는 정규식으로 벗겨낸다. 실패 시 Unknown 유지.</summary>
    public static async Task RefreshStatusAsync(IEnumerable<McpServer> servers, string? workingDir = null)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "opencode",
                Arguments = "mcp list",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            if (!string.IsNullOrEmpty(workingDir)) psi.WorkingDirectory = workingDir;

            using var p = Process.Start(psi);
            if (p == null) return;
            // mcp list 가 연결 시도를 하느라 10초 가까이 걸릴 수 있다.
            var outputTask = p.StandardOutput.ReadToEndAsync();
            var errTask    = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return; }
            var raw = await outputTask;
            var err = await errTask;
            ParseStatusInto(servers, raw, err);
        }
        catch
        {
            // opencode 가 PATH 에 없거나 실행 실패 — 조용히 Unknown 유지.
        }
    }

    private static readonly Regex Ansi = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex Box = new(@"[\u2500-\u257F\u2580-\u259F]", RegexOptions.Compiled);

    /// <summary><c>opencode mcp list</c> 출력에서 서버별 상태를 추출한다. 대략적인 패턴 매칭이지만
    /// "Name: foo" + "Status: connected|disabled|failed|needs_auth|needs_client_registration" 행을 찾는다.</summary>
    private static void ParseStatusInto(IEnumerable<McpServer> servers, string rawOutput, string rawError)
    {
        var text = Ansi.Replace(rawOutput, "");
        text = Box.Replace(text, "");
        // opencode 출력은 한 서버에 여러 줄( Name / Status / Command|URL / Error )
        // 서버 블록은 "Name: <name>" 으로 시작한다고 가정. CLI 버전이 바뀌면 패턴도 조정.
        var byName = servers.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var blocks = Regex.Split(text, @"(?=Name:\s+\S+)");
        foreach (var block in blocks)
        {
            var nameMatch = Regex.Match(block, @"Name:\s+(\S+)");
            if (!nameMatch.Success) continue;
            var name = nameMatch.Groups[1].Value.Trim();
            if (!byName.TryGetValue(name, out var srv)) continue;

            var statusMatch = Regex.Match(block, @"Status:\s+(\w+)");
            if (statusMatch.Success)
            {
                srv.Status = statusMatch.Groups[1].Value.ToLowerInvariant() switch
                {
                    "connected" => McpLiveStatus.Connected,
                    "disabled" => McpLiveStatus.Disabled,
                    "needs_auth" or "needsauth" => McpLiveStatus.NeedsAuth,
                    "failed" => McpLiveStatus.Failed,
                    "needs_client_registration" or "needsclientregistration" => McpLiveStatus.NeedsClientRegistration,
                    _ => McpLiveStatus.Unknown,
                };
            }
            var errMatch = Regex.Match(block, @"Error:\s*(.+)");
            srv.StatusMessage = errMatch.Success ? errMatch.Groups[1].Value.Trim() : "";
        }
    }
}

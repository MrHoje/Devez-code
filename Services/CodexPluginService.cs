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

/// <summary>Codex 플러그인/마켓플레이스 관리 — 전부 <c>codex plugin …</c> CLI 경유.
/// 모델은 Claude 팝업과 UI를 공유하기 위해 <see cref="ClaudePlugin"/> 계열을 재사용한다.
/// on/off 전용 CLI가 없어 <c>~/.codex/config.toml</c> 의 <c>[plugins."id"]</c> 를 직접 갱신한다.</summary>
public static class CodexPluginService
{
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    // ── 목록 ──────────────────────────────────────────────────────
    public static async Task<List<ClaudePlugin>> ListAsync()
    {
        var list = new List<ClaudePlugin>();
        var json = await RunCaptureAsync("plugin list --json");
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // 기본 출력은 { installed: [...] }. --available 와 혼용돼도 installed 만 본다.
            JsonElement arr;
            if (root.ValueKind == JsonValueKind.Array) arr = root;
            else if (!root.TryGetProperty("installed", out arr) || arr.ValueKind != JsonValueKind.Array)
                return list;

            var byId = new Dictionary<string, ClaudePlugin>(StringComparer.Ordinal);
            foreach (var el in arr.EnumerateArray())
            {
                // installed 배열에는 미설치 항목이 섞이지 않지만, 방어적으로 installed=false 스킵.
                if (el.TryGetProperty("installed", out var instFlag) && instFlag.ValueKind == JsonValueKind.False)
                    continue;
                var id = Str(el, "pluginId");
                if (string.IsNullOrWhiteSpace(id)) id = Str(el, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;

                bool enabled = !el.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False;
                if (byId.TryGetValue(id, out var exist))
                {
                    if (enabled) exist.Enabled = true;
                    continue;
                }

                var at = id.LastIndexOf('@');
                var name = Str(el, "name");
                if (string.IsNullOrEmpty(name)) name = at > 0 ? id.Substring(0, at) : id;
                var market = Str(el, "marketplaceName");
                if (string.IsNullOrEmpty(market)) market = at > 0 ? id.Substring(at + 1) : "";

                string path = "";
                string marketSrc = "";
                if (el.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object)
                    path = Str(src, "path");
                if (el.TryGetProperty("marketplaceSource", out var msrc) && msrc.ValueKind == JsonValueKind.Object)
                    marketSrc = Str(msrc, "source");

                // CLI 목록엔 설명 필드가 없음 → 로컬 plugin.json 에서 표시명·버전 보강.
                var dir = ResolvePluginDir(path, marketSrc, name);
                var meta = TryReadMeta(dir);
                if (meta != null)
                {
                    if (!string.IsNullOrEmpty(meta.DisplayName)) name = meta.DisplayName;
                    if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(dir)) path = dir;
                }
                var ver = Str(el, "version");
                if (string.IsNullOrEmpty(ver) && meta != null) ver = meta.Version;

                var p = new ClaudePlugin
                {
                    Id = id,
                    Name = name,
                    Marketplace = market,
                    Version = ver,
                    Scope = "",
                    InstallPath = path,
                    Enabled = enabled,
                };
                byId[id] = p;
                list.Add(p);
            }
        }
        catch { /* 파싱 실패 시 빈 목록 */ }
        return list;
    }

    /// <summary>설치 가능한 플러그인 카탈로그. <c>plugin list --available --json</c> 의 available[].
    /// CLI 는 description 을 주지 않으므로 로컬 <c>.codex-plugin/plugin.json</c>(또는 claude 형식)을 읽어 채운다.</summary>
    public static async Task<List<ClaudeAvailablePlugin>> AvailableAsync()
    {
        var json = await RunCaptureAsync("plugin list --available --json", 60000);
        if (string.IsNullOrWhiteSpace(json)) return new List<ClaudeAvailablePlugin>();
        // plugin.json 대량 디스크 읽기는 스레드풀에서 — UI 블로킹 방지.
        return await Task.Run(() => ParseAvailable(json));
    }

    private static List<ClaudeAvailablePlugin> ParseAvailable(string json)
    {
        var list = new List<ClaudeAvailablePlugin>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var installedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("installed", out var inst) && inst.ValueKind == JsonValueKind.Array)
                foreach (var el in inst.EnumerateArray())
                {
                    var id = Str(el, "pluginId");
                    if (string.IsNullOrEmpty(id)) id = Str(el, "id");
                    if (!string.IsNullOrEmpty(id)) installedIds.Add(id);
                }

            if (!root.TryGetProperty("available", out var avail) || avail.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var el in avail.EnumerateArray())
            {
                var pid = Str(el, "pluginId");
                if (string.IsNullOrEmpty(pid)) continue;
                var at = pid.LastIndexOf('@');
                var name = Str(el, "name");
                if (string.IsNullOrEmpty(name)) name = at > 0 ? pid.Substring(0, at) : pid;

                bool isInstalled = el.TryGetProperty("installed", out var iEl) && iEl.ValueKind == JsonValueKind.True
                    || installedIds.Contains(pid);

                string srcUrl = "", srcPath = "", marketSrc = "";
                if (el.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object)
                {
                    srcUrl = Str(src, "url");
                    srcPath = Str(src, "path");
                    if (string.IsNullOrEmpty(srcUrl) && srcPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        srcUrl = srcPath;
                }
                if (el.TryGetProperty("marketplaceSource", out var msrc) && msrc.ValueKind == JsonValueKind.Object)
                    marketSrc = Str(msrc, "source");

                var ver = Str(el, "version");
                var desc = Str(el, "description");

                // CLI 에 설명이 없으면 로컬 매니페스트에서 보강(공식 마켓은 거의 전부 로컬 캐시).
                var dir = ResolvePluginDir(srcPath, marketSrc, name);
                var meta = TryReadMeta(dir);
                if (meta != null)
                {
                    if (!string.IsNullOrEmpty(meta.DisplayName)) name = meta.DisplayName;
                    if (string.IsNullOrEmpty(desc))
                        desc = !string.IsNullOrEmpty(meta.Description) ? meta.Description : meta.LongDescription;
                    if (string.IsNullOrEmpty(ver)) ver = meta.Version;
                    if (string.IsNullOrEmpty(srcUrl)) srcUrl = meta.Homepage;
                }

                list.Add(new ClaudeAvailablePlugin
                {
                    Id = pid,
                    Name = name,
                    Marketplace = Str(el, "marketplaceName"),
                    Description = desc,
                    InstallCount = 0,
                    IsInstalled = isInstalled,
                    Version = ver,
                    SourceUrl = srcUrl,
                });
            }
        }
        catch { }
        // 설치됨 → 이름 순.
        list.Sort((a, b) =>
        {
            int c = b.IsInstalled.CompareTo(a.IsInstalled);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    public static async Task<List<ClaudeMarketplace>> MarketplacesAsync()
    {
        var list = new List<ClaudeMarketplace>();
        var json = await RunCaptureAsync("plugin marketplace list --json");
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement arr;
            if (root.ValueKind == JsonValueKind.Array) arr = root;
            else if (!root.TryGetProperty("marketplaces", out arr) || arr.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var el in arr.EnumerateArray())
            {
                var name = Str(el, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                string sourceType = "", source = "";
                if (el.TryGetProperty("marketplaceSource", out var ms) && ms.ValueKind == JsonValueKind.Object)
                {
                    sourceType = Str(ms, "sourceType");
                    source = Str(ms, "source");
                }
                if (string.IsNullOrEmpty(sourceType)) sourceType = Str(el, "sourceType");
                if (string.IsNullOrEmpty(source)) source = Str(el, "source");

                var rootPath = Str(el, "root");
                var m = new ClaudeMarketplace
                {
                    Name = name,
                    Source = string.IsNullOrEmpty(sourceType) ? "local" : sourceType,
                    InstallLocation = rootPath,
                };
                // git URL 이면 Url, owner/repo 형태면 Repo.
                if (source.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    || source.StartsWith("git@", StringComparison.OrdinalIgnoreCase)
                    || source.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
                    m.Url = source;
                else if (!string.IsNullOrEmpty(source))
                    m.Repo = source.TrimStart('\\', '?'); // \\?\ 로컬 경로 정리
                list.Add(m);
            }
        }
        catch { }
        return list;
    }

    // ── 플러그인 제어 ─────────────────────────────────────────────
    /// <summary>config.toml 의 <c>[plugins."id"] enabled</c> 를 true 로.</summary>
    public static Task<string> EnableAsync(string id) => Task.Run(() => SetEnabled(id, true));
    /// <summary>config.toml 의 <c>[plugins."id"] enabled</c> 를 false 로.</summary>
    public static Task<string> DisableAsync(string id) => Task.Run(() => SetEnabled(id, false));

    /// <summary>CLI <c>plugin details</c> 가 없어 설치 목록 + 로컬 <c>plugin.json</c> 으로 상세를 구성한다.</summary>
    public static async Task<string> DetailsAsync(string id)
    {
        var list = await ListAsync();
        var p = list.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (p == null) return "플러그인을 찾을 수 없습니다. (설치 목록에 없음)";

        var dir = ResolvePluginDir(p.InstallPath, null, p.Name);
        // list 시점 표시명이 바뀌었을 수 있어 id 의 name@ 부분으로 한 번 더.
        if (dir == null)
        {
            var at = p.Id.LastIndexOf('@');
            var rawName = at > 0 ? p.Id.Substring(0, at) : p.Name;
            dir = ResolvePluginDir(p.InstallPath, null, rawName);
        }
        var meta = await Task.Run(() => TryReadMeta(dir));

        var sb = new StringBuilder();
        sb.AppendLine($"이름: {(meta != null && !string.IsNullOrEmpty(meta.DisplayName) ? meta.DisplayName : p.Name)}");
        sb.AppendLine($"ID: {p.Id}");
        sb.AppendLine($"마켓플레이스: {p.Marketplace}");
        var ver = !string.IsNullOrWhiteSpace(p.Version) ? p.Version
            : (meta != null && !string.IsNullOrEmpty(meta.Version) ? meta.Version : "");
        sb.AppendLine($"버전: {(string.IsNullOrWhiteSpace(ver) ? "(없음)" : ver)}");
        sb.AppendLine($"상태: {(p.Enabled ? "enabled" : "disabled")}");
        if (meta != null)
        {
            if (!string.IsNullOrEmpty(meta.Author)) sb.AppendLine($"작성자: {meta.Author}");
            if (!string.IsNullOrEmpty(meta.Category)) sb.AppendLine($"카테고리: {meta.Category}");
            if (!string.IsNullOrEmpty(meta.Homepage)) sb.AppendLine($"홈페이지: {meta.Homepage}");
        }
        if (!string.IsNullOrWhiteSpace(p.InstallPath))
            sb.AppendLine($"경로: {p.InstallPath}");
        else if (!string.IsNullOrEmpty(dir))
            sb.AppendLine($"경로: {dir}");

        if (meta != null)
        {
            if (!string.IsNullOrEmpty(meta.Description))
            {
                sb.AppendLine();
                sb.AppendLine("요약");
                sb.AppendLine(meta.Description);
            }
            if (!string.IsNullOrEmpty(meta.LongDescription)
                && !string.Equals(meta.LongDescription, meta.Description, StringComparison.Ordinal))
            {
                sb.AppendLine();
                sb.AppendLine("설명");
                sb.AppendLine(meta.LongDescription);
            }
        }
        else
        {
            sb.AppendLine();
            sb.Append("※ 로컬 매니페스트(plugin.json)를 찾지 못해 기본 정보만 표시합니다.");
        }
        return sb.ToString();
    }

    // ── 로컬 매니페스트 (.codex-plugin / .claude-plugin) ──────────
    private sealed class PluginMeta
    {
        public string DisplayName = "";
        public string Description = "";
        public string LongDescription = "";
        public string Author = "";
        public string Homepage = "";
        public string Version = "";
        public string Category = "";
    }

    /// <summary>CLI path(절대/상대) + 마켓 루트로 플러그인 디렉터리를 해석한다.
    /// git URL 마켓 소스는 로컬 캐시 경로가 아니므로 무시한다.</summary>
    private static string? ResolvePluginDir(string? path, string? marketSource, string? pluginName = null)
    {
        static string Clean(string s)
        {
            s = (s ?? "").Trim();
            if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) s = s.Substring(4);
            return s;
        }
        static bool IsLocalFs(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            // http(s):// · git@ · ssh:// 는 파일 경로가 아님.
            if (s.Contains("://", StringComparison.Ordinal) || s.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        path = Clean(path ?? "");
        marketSource = Clean(marketSource ?? "");
        if (!IsLocalFs(marketSource)) marketSource = "";
        // path 자체가 URL 이면 디렉터리로 쓰지 않음(homepage 등과 혼동 방지).
        if (!IsLocalFs(path)) path = "";

        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                if (Path.IsPathRooted(path) && Directory.Exists(path)) return path;
            }
            catch { /* 잘못된 경로 문자 등 */ }

            if (!string.IsNullOrEmpty(marketSource))
            {
                try
                {
                    var combined = Path.GetFullPath(Path.Combine(
                        marketSource,
                        path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
                    if (Directory.Exists(combined)) return combined;
                }
                catch { }
            }
        }

        // path 없거나 실패 시 marketRoot/plugins/<name> 폴백.
        if (!string.IsNullOrEmpty(marketSource) && !string.IsNullOrEmpty(pluginName))
        {
            foreach (var sub in new[]
            {
                Path.Combine("plugins", pluginName),
                pluginName,
            })
            {
                try
                {
                    var c = Path.Combine(marketSource, sub);
                    if (Directory.Exists(c)) return c;
                }
                catch { }
            }
        }

        return null;
    }

    /// <summary>플러그인 폴더에서 매니페스트를 읽어 설명·표시명을 얻는다.
    /// 우선순위: .codex-plugin/plugin.json → .claude-plugin/plugin.json → plugin.json.</summary>
    private static PluginMeta? TryReadMeta(string? pluginDir)
    {
        if (string.IsNullOrEmpty(pluginDir) || !Directory.Exists(pluginDir)) return null;
        var candidates = new[]
        {
            Path.Combine(pluginDir, ".codex-plugin", "plugin.json"),
            Path.Combine(pluginDir, ".claude-plugin", "plugin.json"),
            Path.Combine(pluginDir, "plugin.json"),
        };
        foreach (var f in candidates)
        {
            if (!File.Exists(f)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var root = doc.RootElement;
                var meta = new PluginMeta
                {
                    DisplayName = Str(root, "name"),
                    Description = Str(root, "description"),
                    Version = Str(root, "version"),
                    Homepage = Str(root, "homepage"),
                };
                if (root.TryGetProperty("author", out var author))
                {
                    if (author.ValueKind == JsonValueKind.Object)
                        meta.Author = Str(author, "name");
                    else if (author.ValueKind == JsonValueKind.String)
                        meta.Author = author.GetString() ?? "";
                }
                if (root.TryGetProperty("interface", out var iface) && iface.ValueKind == JsonValueKind.Object)
                {
                    var dn = Str(iface, "displayName");
                    if (!string.IsNullOrEmpty(dn)) meta.DisplayName = dn;
                    var sd = Str(iface, "shortDescription");
                    if (!string.IsNullOrEmpty(sd)) meta.Description = sd;
                    meta.LongDescription = Str(iface, "longDescription");
                    if (string.IsNullOrEmpty(meta.Description) && !string.IsNullOrEmpty(meta.LongDescription))
                        meta.Description = meta.LongDescription;
                    var dev = Str(iface, "developerName");
                    if (!string.IsNullOrEmpty(dev)) meta.Author = dev;
                    meta.Category = Str(iface, "category");
                    var web = Str(iface, "websiteURL");
                    if (!string.IsNullOrEmpty(web)) meta.Homepage = web;
                }
                return meta;
            }
            catch { /* 다음 후보 */ }
        }
        return null;
    }

    /// <summary>개별 플러그인 update CLI 없음 → 해당 마켓플레이스 스냅샷을 upgrade.</summary>
    public static Task<string> UpdateAsync(string id)
    {
        var at = (id ?? "").LastIndexOf('@');
        var market = at > 0 ? id!.Substring(at + 1) : null;
        if (string.IsNullOrWhiteSpace(market))
            return Task.FromResult("마켓플레이스를 알 수 없어 업데이트할 수 없습니다.");
        return MarketplaceUpdateAsync(market);
    }

    public static Task<string> UninstallAsync(string id)
        => RunCaptureAsync($"plugin remove {Q(id)} --json", timeoutMs: 60000);

    public static Task<CliResult> InstallAsync(string pluginAtMarket)
        => RunCaptureExAsync($"plugin add {Q(pluginAtMarket)} --json", timeoutMs: 180000);

    // ── 마켓플레이스 제어 ─────────────────────────────────────────
    public static Task<string> MarketplaceRemoveAsync(string name)
        => RunCaptureAsync($"plugin marketplace remove {Q(name)} --json");

    public static Task<string> MarketplaceUpdateAsync(string? name = null)
        => RunCaptureAsync(
            string.IsNullOrWhiteSpace(name)
                ? "plugin marketplace upgrade --json"
                : $"plugin marketplace upgrade {Q(name!)} --json",
            timeoutMs: 120000);

    public static Task<CliResult> MarketplaceAddAsync(string source)
        => RunCaptureExAsync($"plugin marketplace add {Q(source)} --json", timeoutMs: 120000);

    // Claude 와 동일한 결과 타입 별칭.
    public readonly record struct CliResult(bool Ok, string Text);

    // ── enable/disable (config.toml) ──────────────────────────────
    private static string SetEnabled(string pluginId, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return "플러그인 ID 가 비어 있습니다.";
        var path = CodexMcpBackend.ConfigPath;
        try
        {
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            var header = $"[plugins.\"{pluginId}\"]";
            // 기존 섹션: 헤더부터 다음 [ 섹션 직전까지.
            var sectionRx = new Regex(
                @"^[ \t]*\[plugins\.""" + Regex.Escape(pluginId) + @"""\][ \t]*\r?\n(?:(?!^[ \t]*\[).*\r?\n?)*",
                RegexOptions.Multiline);

            string newSection = header + Environment.NewLine + "enabled = " + (enabled ? "true" : "false") + Environment.NewLine;

            string next;
            if (sectionRx.IsMatch(text))
            {
                next = sectionRx.Replace(text, m =>
                {
                    // 섹션 본문에서 enabled 줄만 교체, 없으면 헤더 바로 아래에 추가.
                    var body = m.Value;
                    var enRx = new Regex(@"(?m)^[ \t]*enabled[ \t]*=[ \t]*(true|false)[ \t]*\r?$", RegexOptions.IgnoreCase);
                    if (enRx.IsMatch(body))
                        return enRx.Replace(body, "enabled = " + (enabled ? "true" : "false"), 1);
                    // 헤더 다음 줄에 삽입.
                    var nl = body.Contains("\r\n") ? "\r\n" : "\n";
                    var firstNl = body.IndexOf('\n');
                    if (firstNl < 0) return body.TrimEnd() + nl + "enabled = " + (enabled ? "true" : "false") + nl;
                    return body.Substring(0, firstNl + 1) + "enabled = " + (enabled ? "true" : "false") + nl + body.Substring(firstNl + 1);
                }, 1);
            }
            else
            {
                var sb = new StringBuilder(text.TrimEnd());
                if (sb.Length > 0) sb.AppendLine().AppendLine();
                sb.Append(newSection);
                next = sb.ToString();
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicFile.WriteAllText(path, next);
            return enabled ? "enabled = true" : "enabled = false";
        }
        catch (Exception ex) { return "설정 변경 실패: " + ex.Message; }
    }

    // ── 실행 헬퍼 ─────────────────────────────────────────────────
    private static string Q(string s) => "\"" + (s ?? "").Replace("\"", "") + "\"";

    private static async Task<string> RunCaptureAsync(string args, int timeoutMs = 30000)
        => (await RunCaptureExAsync(args, timeoutMs)).Text;

    private static async Task<CliResult> RunCaptureExAsync(string args, int timeoutMs = 30000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c codex {args}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = await Task.Run(() => Process.Start(psi));
            if (p == null) return new CliResult(false, "codex CLI 를 실행할 수 없습니다.");
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            using var cts = new System.Threading.CancellationTokenSource(timeoutMs);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                return new CliResult(false, "시간이 초과되어 중단했습니다.");
            }
            var raw = Ansi.Replace(await outTask, "").Trim();
            var err = Ansi.Replace(await errTask, "").Trim();
            bool ok = p.ExitCode == 0;
            string text;
            if (ok)
                text = raw.Length > 0 ? raw : err;
            else
            {
                text = (raw.Length > 0 && err.Length > 0) ? raw + "\n" + err
                     : err.Length > 0 ? err : raw;
                if (text.Length == 0) text = $"실패 (종료 코드 {p.ExitCode}).";
            }
            return new CliResult(ok, text);
        }
        catch (Exception ex) { return new CliResult(false, ex.Message); }
    }

    private static string Str(JsonElement o, string prop)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
}

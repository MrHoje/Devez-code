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
                if (el.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object)
                    path = Str(src, "path");

                var p = new ClaudePlugin
                {
                    Id = id,
                    Name = name,
                    Marketplace = market,
                    Version = Str(el, "version"),
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

    /// <summary>설치 가능한 플러그인 카탈로그. <c>plugin list --available --json</c> 의 available[].</summary>
    public static async Task<List<ClaudeAvailablePlugin>> AvailableAsync()
    {
        var list = new List<ClaudeAvailablePlugin>();
        var json = await RunCaptureAsync("plugin list --available --json", 60000);
        if (string.IsNullOrWhiteSpace(json)) return list;
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

                string srcUrl = "", srcPath = "";
                if (el.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object)
                {
                    srcUrl = Str(src, "url");
                    srcPath = Str(src, "path");
                    if (string.IsNullOrEmpty(srcUrl) && srcPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        srcUrl = srcPath;
                }

                var ver = Str(el, "version");
                list.Add(new ClaudeAvailablePlugin
                {
                    Id = pid,
                    Name = name,
                    Marketplace = Str(el, "marketplaceName"),
                    Description = Str(el, "description"),
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

    /// <summary>CLI 상세 명령이 없어 list 결과로 요약 텍스트를 만든다.</summary>
    public static async Task<string> DetailsAsync(string id)
    {
        var list = await ListAsync();
        var p = list.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (p == null) return "플러그인을 찾을 수 없습니다. (설치 목록에 없음)";
        var sb = new StringBuilder();
        sb.AppendLine($"이름: {p.Name}");
        sb.AppendLine($"ID: {p.Id}");
        sb.AppendLine($"마켓플레이스: {p.Marketplace}");
        sb.AppendLine($"버전: {(string.IsNullOrWhiteSpace(p.Version) ? "(없음)" : p.Version)}");
        sb.AppendLine($"상태: {(p.Enabled ? "enabled" : "disabled")}");
        if (!string.IsNullOrWhiteSpace(p.InstallPath))
            sb.AppendLine($"경로: {p.InstallPath}");
        sb.AppendLine();
        sb.Append("※ Codex 는 `plugin details` CLI 가 없어 목록 정보로 표시합니다.");
        return sb.ToString();
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

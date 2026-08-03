using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>Claude Code 플러그인/마켓플레이스 관리 — 전부 <c>claude plugin …</c> CLI 경유.
/// 비대화형으로 끝나는 명령(list/enable/disable/details/update/uninstall/marketplace)은 stdout 캡처,
/// 신뢰/설정 프롬프트가 뜰 수 있는 명령(install/marketplace add)은 보이는 콘솔로 띄운다.</summary>
public static class ClaudePluginService
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
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            // 같은 플러그인이 scope(user/project/local) 별로 중복 반환됨(예: superpowers user+project).
            // Id 기준으로 하나로 합친다 — 어느 scope든 enabled 면 enabled 로 본다.
            var byId = new Dictionary<string, ClaudePlugin>(StringComparer.Ordinal);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var id = Str(el, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                bool enabled = el.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
                if (byId.TryGetValue(id, out var exist))
                {
                    if (enabled) exist.Enabled = true;   // 어느 scope든 켜져 있으면 켜짐으로
                    continue;
                }
                var at = id.LastIndexOf('@');
                var p = new ClaudePlugin
                {
                    Id = id,
                    Name = at > 0 ? id.Substring(0, at) : id,
                    Marketplace = at > 0 ? id.Substring(at + 1) : "",
                    Version = Str(el, "version"),
                    Scope = Str(el, "scope"),
                    InstallPath = Str(el, "installPath"),
                    Enabled = enabled,
                    InstalledAt = Date(el, "installedAt"),
                    LastUpdated = Date(el, "lastUpdated"),
                };
                if (el.TryGetProperty("mcpServers", out var ms) && ms.ValueKind == JsonValueKind.Object)
                    foreach (var srv in ms.EnumerateObject()) p.McpServerNames.Add(srv.Name);
                byId[id] = p;
                list.Add(p);
            }
        }
        catch { /* 파싱 실패 시 빈 목록 */ }

        // 마켓플레이스 카탈로그(--available)에서 각 플러그인의 최신 버전(source.ref)을 얻어 최신 여부 판정.
        await AnnotateLatestAsync(list);
        return list;
    }

    /// <summary>--available 카탈로그의 각 항목 source.ref(예: "v1.5.5")를 최신 버전으로 보고 설치본과 비교.
    /// ref 가 semver 로 파싱될 때만 판정한다(main/sha 는 확인 불가 → 표시 안 함).</summary>
    private static async Task AnnotateLatestAsync(List<ClaudePlugin> installed)
    {
        if (installed.Count == 0) return;
        try
        {
            var json = await RunCaptureAsync("plugin list --available --json", 45000);
            if (string.IsNullOrWhiteSpace(json)) return;
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("available", out var avail) || avail.ValueKind != JsonValueKind.Array)
                return;
            // pluginId → ref 문자열
            var latest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var el in avail.EnumerateArray())
            {
                var pid = Str(el, "pluginId");
                if (string.IsNullOrEmpty(pid)) continue;
                if (el.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object)
                {
                    var r = Str(src, "ref");
                    if (!string.IsNullOrEmpty(r)) latest[pid] = r;
                }
            }
            foreach (var p in installed)
            {
                if (!latest.TryGetValue(p.Id, out var refStr)) continue;
                var lv = ParseVer(refStr);
                var cv = ParseVer(p.Version);
                if (lv == null || cv == null) continue;   // 비교 불가(main/sha 등) → 표시 안 함
                p.LatestVersion = TrimV(refStr);
                p.UpdateAvailable = lv > cv;
            }
        }
        catch { /* 실패 시 최신 정보 없이 진행 */ }
    }

    private static readonly Regex VerRx = new(@"^\d+(\.\d+)*", RegexOptions.Compiled);
    private static string TrimV(string s) => s.TrimStart('v', 'V');
    private static Version? ParseVer(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = VerRx.Match(TrimV(s.Trim()));
        if (!m.Success) return null;
        var t = m.Value;
        if (!t.Contains('.')) t += ".0";
        return Version.TryParse(t, out var v) ? v : null;
    }

    /// <summary>설치 가능한 플러그인 카탈로그(Discover). --available 의 available[] 를 인기(installCount) 순으로.
    /// 이미 설치된 항목은 IsInstalled=true 로 표시.</summary>
    public static async Task<List<ClaudeAvailablePlugin>> AvailableAsync()
    {
        var list = new List<ClaudeAvailablePlugin>();
        var json = await RunCaptureAsync("plugin list --available --json", 45000);
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var installedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("installed", out var inst) && inst.ValueKind == JsonValueKind.Array)
                foreach (var el in inst.EnumerateArray())
                {
                    var id = Str(el, "id");
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
                int count = el.TryGetProperty("installCount", out var ic) && ic.ValueKind == JsonValueKind.Number
                            && ic.TryGetInt32(out var n) ? n : 0;
                // source(객체) 에서 url·ref 를 뽑는다(문자열 source 인 경우는 무시).
                string srcUrl = "", srcRef = "";
                if (el.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object)
                { srcUrl = Str(src, "url"); srcRef = Str(src, "ref"); }
                var ver = Str(el, "version");
                if (string.IsNullOrEmpty(ver)) ver = srcRef;
                list.Add(new ClaudeAvailablePlugin
                {
                    Id = pid,
                    Name = name,
                    Marketplace = Str(el, "marketplaceName"),
                    Description = Str(el, "description"),
                    InstallCount = count,
                    IsInstalled = installedIds.Contains(pid),
                    Version = ver,
                    SourceUrl = srcUrl,
                });
            }
        }
        catch { }
        list.Sort((a, b) => b.InstallCount.CompareTo(a.InstallCount));
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
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var name = Str(el, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                list.Add(new ClaudeMarketplace
                {
                    Name = name,
                    Source = Str(el, "source"),
                    Repo = Str(el, "repo"),
                    Url = Str(el, "url"),
                    InstallLocation = Str(el, "installLocation"),
                });
            }
        }
        catch { }
        return list;
    }

    // ── 플러그인 제어 ─────────────────────────────────────────────
    public static Task<string> EnableAsync(string id)  => RunCaptureAsync($"plugin enable {Q(id)}");
    public static Task<string> DisableAsync(string id) => RunCaptureAsync($"plugin disable {Q(id)}");
    public static Task<string> DetailsAsync(string id) => RunCaptureAsync($"plugin details {Q(id)}");
    public static Task<string> UpdateAsync(string id)  => RunCaptureAsync($"plugin update {Q(id)}", timeoutMs: 120000);
    /// <summary>-y: 비TTY 프롬프트 스킵. --prune 은 붙이지 않아 의존성은 보존.</summary>
    public static Task<string> UninstallAsync(string id) => RunCaptureAsync($"plugin uninstall {Q(id)} -y", timeoutMs: 60000);

    // ── 마켓플레이스 제어 ─────────────────────────────────────────
    public static Task<string> MarketplaceRemoveAsync(string name) => RunCaptureAsync($"plugin marketplace remove {Q(name)}");
    public static Task<string> MarketplaceUpdateAsync(string? name = null)
        => RunCaptureAsync(string.IsNullOrWhiteSpace(name) ? "plugin marketplace update" : $"plugin marketplace update {Q(name!)}", timeoutMs: 120000);

    /// <summary>설치(인라인 캡처) — 성공/실패까지 반환. install 은 대화형 프롬프트가 없어 캡처 가능.</summary>
    public static Task<CliResult> InstallAsync(string pluginAtMarket) => RunCaptureExAsync($"plugin install {Q(pluginAtMarket)}", timeoutMs: 180000);
    /// <summary>설치 — 보이는 콘솔로 띄운다(레거시/대체 경로).</summary>
    public static void SpawnInstall(string pluginAtMarket) => SpawnConsole($"plugin install {Q(pluginAtMarket)}");
    /// <summary>마켓플레이스 추가(인라인 캡처) — 성공/실패까지 반환.</summary>
    public static Task<CliResult> MarketplaceAddAsync(string source) => RunCaptureExAsync($"plugin marketplace add {Q(source)}", timeoutMs: 120000);
    /// <summary>마켓플레이스 추가 — 보이는 콘솔로 띄운다(레거시/대체 경로).</summary>
    public static void SpawnMarketplaceAdd(string source) => SpawnConsole($"plugin marketplace add {Q(source)}");

    /// <summary>앱 시작 시 설치된 devez-marketplace와 사용 중인 hoje-code 플러그인만 <b>완전 조용히</b>(창·모달·알림 없이) 최신화한다.
    /// 내부적으로 <see cref="MarketplaceUpdateAsync"/>/<see cref="UpdateAsync"/> 를 재사용하며, 둘 다
    /// <c>cmd /c</c> + <c>CreateNoWindow=true</c> 라 콘솔이 뜨지 않는다. 실패해도 삼키고 넘어간다(fire-and-forget).
    /// 시작 지연을 주지 않으려 호출부는 await 하지 않고 버린다.</summary>
    public static async Task UpdateDevezSilentlyAsync()
    {
        try
        {
            var marketplaces = await MarketplacesAsync();
            if (!marketplaces.Any(m => string.Equals(m.Name, "devez-marketplace", StringComparison.OrdinalIgnoreCase)))
                return;

            await MarketplaceUpdateAsync("devez-marketplace");

            var plugins = await ListAsync();
            if (plugins.Any(p => p.Enabled && string.Equals(p.Id, "hoje-code@devez-marketplace", StringComparison.OrdinalIgnoreCase)))
                await UpdateAsync("hoje-code@devez-marketplace");
        }
        catch { /* 오프라인/CLI 미설치 등: 조용히 무시 */ }
    }

    // ── 실행 헬퍼 ─────────────────────────────────────────────────
    private static string Q(string s) => "\"" + (s ?? "").Replace("\"", "") + "\"";

    private static void SpawnConsole(string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k claude {args}",
                UseShellExecute = true,
                CreateNoWindow = false,
            });
        }
        catch { /* CLI 미설치 등 */ }
    }

    /// <summary>CLI 실행 결과. Ok=성공(종료 코드 0), Text=표시용 출력(실패 시 stderr 포함).</summary>
    public readonly record struct CliResult(bool Ok, string Text);

    private static async Task<string> RunCaptureAsync(string args, int timeoutMs = 30000)
        => (await RunCaptureExAsync(args, timeoutMs)).Text;

    /// <summary>성공/실패까지 구분해 캡처. 실패(종료 코드≠0) 시 stderr 를 반드시 포함한다
    /// (기존엔 stdout 이 있으면 stderr 를 버려 설치 실패 메시지가 안 보였다).</summary>
    private static async Task<CliResult> RunCaptureExAsync(string args, int timeoutMs = 30000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c claude {args}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            // Process.Start 는 동기 호출이라 UI 스레드에서 수십 ms 블로킹될 수 있다(cmd+claude 기동).
            // 스레드풀로 오프로드해 팝업 오픈 애니메이션 등이 끊기지 않게 한다.
            using var p = await Task.Run(() => Process.Start(psi));
            if (p == null) return new CliResult(false, "claude CLI 를 실행할 수 없습니다.");
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            // WaitForExit(int) 은 UI 스레드를 동기 블로킹하므로 사용 금지. WaitForExitAsync + 타임아웃 토큰으로 비동기 대기.
            using var cts = new System.Threading.CancellationTokenSource(timeoutMs);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return new CliResult(false, "시간이 초과되어 중단했습니다."); }
            var raw = Ansi.Replace(await outTask, "").Trim();
            var err = Ansi.Replace(await errTask, "").Trim();
            bool ok = p.ExitCode == 0;
            string text;
            if (ok)
                text = raw.Length > 0 ? raw : err;
            else
            {
                // 실패: stdout·stderr 모두 살려서 보여준다.
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

    private static DateTime? Date(JsonElement o, string prop)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            && DateTime.TryParse(v.GetString(), out var dt) ? dt : null;
}

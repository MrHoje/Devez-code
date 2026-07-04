using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var id = Str(el, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var at = id.LastIndexOf('@');
                var p = new ClaudePlugin
                {
                    Id = id,
                    Name = at > 0 ? id.Substring(0, at) : id,
                    Marketplace = at > 0 ? id.Substring(at + 1) : "",
                    Version = Str(el, "version"),
                    Scope = Str(el, "scope"),
                    InstallPath = Str(el, "installPath"),
                    Enabled = el.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True,
                    InstalledAt = Date(el, "installedAt"),
                    LastUpdated = Date(el, "lastUpdated"),
                };
                if (el.TryGetProperty("mcpServers", out var ms) && ms.ValueKind == JsonValueKind.Object)
                    foreach (var srv in ms.EnumerateObject()) p.McpServerNames.Add(srv.Name);
                list.Add(p);
            }
        }
        catch { /* 파싱 실패 시 빈 목록 */ }
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

    /// <summary>설치 — 신뢰/설정 프롬프트가 뜰 수 있어 보이는 콘솔로 띄운다.</summary>
    public static void SpawnInstall(string pluginAtMarket) => SpawnConsole($"plugin install {Q(pluginAtMarket)}");
    /// <summary>마켓플레이스 추가 — 신뢰 프롬프트가 뜰 수 있어 보이는 콘솔로 띄운다.</summary>
    public static void SpawnMarketplaceAdd(string source) => SpawnConsole($"plugin marketplace add {Q(source)}");

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

    private static async Task<string> RunCaptureAsync(string args, int timeoutMs = 30000)
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

    private static string Str(JsonElement o, string prop)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static DateTime? Date(JsonElement o, string prop)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            && DateTime.TryParse(v.GetString(), out var dt) ? dt : null;
}

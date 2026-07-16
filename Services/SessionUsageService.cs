using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using DevezCode.Services.Terminal;

namespace DevezCode.Services;

/// <summary>세션 대화 로그(JSONL)에서 입/출력 토큰 사용량을 누적 집계한다.
/// claude(~/.claude/projects/*/&lt;sid&gt;.jsonl 의 message.usage)와 codex(token_count 이벤트의
/// total_token_usage) 만 정확 지원 — 두 CLI 가 API 응답 usage 를 그대로 기록하므로 공급자 청구 토큰과 동일하다.
/// 부하 최소화: claude 는 파일 끝에 붙은 새 줄만 증분 파싱(방별 offset·누적 캐시), codex 는 파일 끝
/// 마지막 token_count 한 줄만 tail 로 읽는다. 상태는 %AppData%\DevezCode\usage.json 에 영속(재시작 시 이어읽기).</summary>
public static class SessionUsageService
{
    /// <summary>방(세션) 하나의 누적 토큰. Codex 는 CacheWrite* 가 0, CacheRead 에 cached_input_tokens 를 담는다.
    /// 그래서 InputTotal·Cost 공식이 claude/codex 공통으로 성립한다.</summary>
    public readonly record struct UsageTotals(
        long InputNew, long CacheWrite5m, long CacheWrite1h, long CacheRead, long Output,
        string? Model, string AgentLabel)
    {
        public long InputTotal => InputNew + CacheWrite5m + CacheWrite1h + CacheRead;
        public bool HasData => InputNew > 0 || CacheWrite5m > 0 || CacheWrite1h > 0 || CacheRead > 0 || Output > 0;
    }

    private sealed class Entry
    {
        public string? Sid;      // 이 방이 현재 가리키는 CLI 세션ID. 바뀌면(포크/재개) offset·누적을 리셋
        public long Offset;      // claude: 다음에 이어읽을 byte 위치. codex: 미사용
        public long LastLen;     // 마지막으로 읽었을 때 파일 크기(변화 감지용)
        public UsageTotals Totals;
    }

    private static readonly ConcurrentDictionary<string, Entry> _cache = new();
    private static readonly object _saveLock = new();
    private static long _lastSaveTicks;
    private static bool _loaded;

    // ── 단가표 (per 1M tokens). Claude 는 claude-api 스킬 기준 정확값. Codex(GPT)는 근사치 — "예상" 표기. ──
    private readonly record struct Price(double InPerM, double OutPerM);
    private static Price? PriceFor(string? model)
    {
        if (string.IsNullOrEmpty(model)) return null;
        var m = model.ToLowerInvariant();
        if (m.Contains("fable") || m.Contains("mythos")) return new Price(10, 50);
        if (m.Contains("opus")) return new Price(5, 25);
        if (m.Contains("sonnet")) return new Price(3, 15);
        if (m.Contains("haiku")) return new Price(1, 5);
        if (m.Contains("gpt-5") || m.Contains("codex") || m.Contains("gpt5")) return new Price(1.25, 10); // GPT-5 계열 근사
        return null;
    }

    /// <summary>비용($) 추정. 캐시 write 5m=1.25×·1h=2×, read=0.1× (입력 단가 기준). 단가 미상 모델이면 null.</summary>
    public static double? EstimateCost(in UsageTotals t)
    {
        if (PriceFor(t.Model) is not { } p) return null;
        double inR = p.InPerM / 1_000_000.0;
        double outR = p.OutPerM / 1_000_000.0;
        return t.InputNew * inR
             + t.CacheWrite5m * inR * 1.25
             + t.CacheWrite1h * inR * 2.0
             + t.CacheRead * inR * 0.1
             + t.Output * outR;
    }

    /// <summary>지원 에이전트인지 (정확 집계 가능). 그 외는 표시하지 않는다.</summary>
    public static bool IsSupported(string agentId) => agentId is "claude" or "codex";

    /// <summary>이 방의 최신 누적 사용량. 미지원/데이터 없음이면 null. 백그라운드 스레드에서 호출 권장(파일 IO).</summary>
    public static UsageTotals? Read(string roomId, string agentId, string? cwd)
    {
        EnsureLoaded();
        try
        {
            return agentId switch
            {
                "claude" => ReadClaude(roomId, cwd),
                "codex" => ReadCodex(roomId),
                _ => null,
            };
        }
        catch { return null; }
    }

    // ── claude: 변경 시에만 전체 스캔 + message.id 로 중복 제거 ──
    // (claude 는 한 응답을 여러 JSONL 줄로 쪼개 기록 — 같은 message.id 가 2~3번 나온다. 줄마다 합산하면
    //  2~3배 뻥튀기되므로 반드시 message.id 로 dedup. 파일 크기 불변이면 재파싱 스킵해 부하 억제.)
    private static UsageTotals? ReadClaude(string roomId, string? cwd)
    {
        var sid = SettingsService.LoadClaudeCodeRoomSession(roomId);
        var path = TerminalSessionManager.FindClaudeTranscriptPath(cwd, sid);
        if (path == null) return null;

        var entry = _cache.GetOrAdd(roomId, _ => new Entry { Totals = new UsageTotals(0, 0, 0, 0, 0, null, "Claude") });
        lock (entry)
        {
            long len = new FileInfo(path).Length;
            if (entry.Sid == sid && len == entry.LastLen && entry.LastLen > 0) // 변화 없음 → 캐시 그대로
                return entry.Totals.HasData ? entry.Totals : null;

            var acc = ParseClaudeFull(path);
            entry.Sid = sid;
            entry.LastLen = len;
            entry.Totals = acc;
            SaveThrottled();
            return acc.HasData ? acc : null;
        }
    }

    private static UsageTotals ParseClaudeFull(string path)
    {
        long inNew = 0, cw5 = 0, cw1 = 0, cr = 0, outp = 0;
        string? model = null;
        var seen = new HashSet<string>();
        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs, Encoding.UTF8))
            text = sr.ReadToEnd();
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0) continue;
            try
            {
                using var d = JsonDocument.Parse(line);
                if (!d.RootElement.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;
                if (!msg.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) continue;
                // 같은 응답이 여러 줄로 기록됨 → message.id 로 1회만 집계
                if (msg.TryGetProperty("id", out var idv) && idv.ValueKind == JsonValueKind.String)
                    if (!seen.Add(idv.GetString()!)) continue;
                if (msg.TryGetProperty("model", out var mo) && mo.ValueKind == JsonValueKind.String) model = mo.GetString();
                inNew += GetLong(u, "input_tokens");
                cr += GetLong(u, "cache_read_input_tokens");
                outp += GetLong(u, "output_tokens");
                if (u.TryGetProperty("cache_creation", out var cc) && cc.ValueKind == JsonValueKind.Object)
                {
                    cw5 += GetLong(cc, "ephemeral_5m_input_tokens");
                    cw1 += GetLong(cc, "ephemeral_1h_input_tokens");
                }
                else
                {
                    cw5 += GetLong(u, "cache_creation_input_tokens"); // 구 포맷 폴백(티어 미구분 → 5m 취급)
                }
            }
            catch { /* 부분 줄/비 JSON 무시 */ }
        }
        return new UsageTotals(inNew, cw5, cw1, cr, outp, model, "Claude");
    }

    // ── codex: 파일 끝 마지막 token_count 이벤트 한 줄만 (누적값 내장) ──
    private static UsageTotals? ReadCodex(string roomId)
    {
        var sid = SettingsService.LoadCodexRoomSession(roomId);
        var path = TerminalSessionManager.FindCodexTranscriptPath(sid);
        if (path == null) return null;

        long len = new FileInfo(path).Length;
        var tail = ReadTail(path, 128 * 1024);
        string? last = null, model = null;
        foreach (var line in tail.Split('\n'))
        {
            if (line.Contains("\"token_count\"")) last = line;
            if (model == null)
            {
                int mi = line.IndexOf("\"model\":\"", StringComparison.Ordinal);
                if (mi >= 0)
                {
                    int s = mi + 9, e = line.IndexOf('"', s);
                    if (e > s) model = line.Substring(s, e - s);
                }
            }
        }
        if (last == null) return null;
        try
        {
            using var d = JsonDocument.Parse(last);
            var info = d.RootElement.GetProperty("payload").GetProperty("info").GetProperty("total_token_usage");
            long input = GetLong(info, "input_tokens");
            long cached = GetLong(info, "cached_input_tokens");
            long output = GetLong(info, "output_tokens");
            var t = new UsageTotals(Math.Max(0, input - cached), 0, 0, cached, output, model ?? "gpt-5-codex", "Codex");
            _cache[roomId] = new Entry { Sid = sid, LastLen = len, Totals = t, Offset = -1 };
            return t.HasData ? t : null;
        }
        catch { return null; }
    }

    // ── 표시 문자열 ──

    /// <summary>헤더 인라인: "↓570k ↑6.5k $0.42". 단가 미상이면 $ 생략.</summary>
    public static string FormatInline(in UsageTotals t)
    {
        var sb = new StringBuilder();
        sb.Append('↓').Append(Compact(t.InputTotal)).Append("  ↑").Append(Compact(t.Output));
        if (EstimateCost(t) is { } c) sb.Append("  (").Append(FormatCost(c)).Append(')');
        return sb.ToString();
    }

    /// <summary>툴팁: 전체 분해 + 예상 비용.</summary>
    public static string FormatTooltip(in UsageTotals t)
    {
        var sb = new StringBuilder();
        sb.Append(t.AgentLabel);
        if (!string.IsNullOrEmpty(t.Model)) sb.Append("  ·  ").Append(t.Model);
        sb.Append('\n');
        sb.Append($"입력(신규)   {t.InputNew:N0}\n");
        if (t.CacheWrite5m > 0) sb.Append($"캐시 생성 5m {t.CacheWrite5m:N0}\n");
        if (t.CacheWrite1h > 0) sb.Append($"캐시 생성 1h {t.CacheWrite1h:N0}\n");
        sb.Append($"캐시 읽기    {t.CacheRead:N0}\n");
        sb.Append($"입력 합계    {t.InputTotal:N0}\n");
        sb.Append($"출력         {t.Output:N0}");
        if (EstimateCost(t) is { } c)
        {
            sb.Append($"\n예상 비용    {FormatCost(c)}");
            sb.Append("\n※ 실제 청구액이 아니라 토큰 사용량으로 계산한 추정치입니다.");
        }
        return sb.ToString();
    }

    private static string Compact(long n)
    {
        if (n >= 1_000_000) return (n / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        if (n >= 1_000) return (n / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        return n.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatCost(double c)
        => c > 0 && c < 0.01 ? "<$0.01" : "$" + c.ToString("0.00", CultureInfo.InvariantCulture);

    // ── 삭제 시 정리 (세션 삭제 경로에서 호출) ──
    public static void Remove(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        if (_cache.TryRemove(sessionId, out _)) Save();
    }

    // ── 영속 (usage.json) ──
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "usage.json");

    private sealed class Dto
    {
        public string? Sid { get; set; }
        public long Offset { get; set; }
        public long LastLen { get; set; }
        public long InputNew { get; set; }
        public long CacheWrite5m { get; set; }
        public long CacheWrite1h { get; set; }
        public long CacheRead { get; set; }
        public long Output { get; set; }
        public string? Model { get; set; }
        public string? AgentLabel { get; set; }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_saveLock)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                var dict = JsonSerializer.Deserialize<Dictionary<string, Dto>>(File.ReadAllText(FilePath));
                if (dict == null) return;
                foreach (var (rid, v) in dict)
                    _cache[rid] = new Entry
                    {
                        Sid = v.Sid,
                        Offset = v.Offset,
                        LastLen = v.LastLen,
                        Totals = new UsageTotals(v.InputNew, v.CacheWrite5m, v.CacheWrite1h, v.CacheRead, v.Output,
                            v.Model, v.AgentLabel ?? "Claude"),
                    };
            }
            catch { /* 손상 시 무시 — 재스캔 */ }
        }
    }

    private static void SaveThrottled()
    {
        long now = Environment.TickCount64;
        if (now - _lastSaveTicks < 5000) return;
        _lastSaveTicks = now;
        Save();
    }

    /// <summary>현재 캐시를 usage.json 에 원자적으로 기록. 앱 종료 시에도 호출 가능.</summary>
    public static void Save()
    {
        lock (_saveLock)
        {
            try
            {
                var dict = new Dictionary<string, Dto>();
                foreach (var (rid, e) in _cache)
                {
                    var t = e.Totals;
                    dict[rid] = new Dto
                    {
                        Sid = e.Sid, Offset = e.Offset, LastLen = e.LastLen,
                        InputNew = t.InputNew, CacheWrite5m = t.CacheWrite5m, CacheWrite1h = t.CacheWrite1h,
                        CacheRead = t.CacheRead, Output = t.Output, Model = t.Model, AgentLabel = t.AgentLabel,
                    };
                }
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(dict));
                if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
            }
            catch { /* 영속 실패는 무해 — 다음에 재스캔 */ }
        }
    }

    private static long GetLong(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;

    private static string ReadTail(string path, int maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long len = fs.Length;
        long start = Math.Max(0, len - maxBytes);
        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[len - start];
        int read = fs.Read(buf, 0, buf.Length);
        return Encoding.UTF8.GetString(buf, 0, read);
    }
}

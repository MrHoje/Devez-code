using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using DevezCode.Services.Terminal;

namespace DevezCode.Services;

/// <summary>세션 대화 로그(JSONL)에서 입/출력 토큰 사용량을 누적 집계한다.
/// claude(~/.claude/projects/*/&lt;sid&gt;.jsonl 의 message.usage)와 codex/devezvibe(token_count 이벤트의
/// total_token_usage)를 정확 지원 — 세 CLI 가 API 응답 usage 를 그대로 기록하므로 공급자 청구 토큰과 동일하다.
/// 부하 최소화: claude 는 파일 끝에 붙은 새 줄만 증분 파싱(방별 offset·누적 캐시), codex/devezvibe 는 파일 끝
/// 마지막 token_count 한 줄만 tail 로 읽는다. 상태는 %AppData%\DevezCode\usage.json 에 영속(재시작 시 이어읽기).</summary>
public static class SessionUsageService
{
    /// <summary>방(세션) 하나의 누적 토큰. Codex 는 CacheRead=cached_input_tokens, CacheWrite5m=cache_write_input_tokens
    /// (GPT-5.6+ write 과금 ×1.25 = DefW5m 과 동일), CacheWrite1h=0. InputTotal·Cost 공식이 claude/codex 공통 성립.</summary>
    public readonly record struct UsageTotals(
        long InputNew, long CacheWrite5m, long CacheWrite1h, long CacheRead, long Output,
        string? Model, string AgentLabel)
    {
        /// <summary>비용($) 추정치. 파싱 시 모델별 단가로 계산해 담는다(세션 중 모델 변경 대응 — 모델별 버킷 합산).
        /// 단가 미상 모델뿐이면 null.</summary>
        public double? Cost { get; init; }
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
    // settings.json 의 UsagePricing(SettingsService.LoadUsagePricing) 이 있으면 그게 우선(덮어쓰기/신규 모델).
    // 갱신 절차 문서: .knowledge/토큰사용량-단가-갱신.md
    // 표준 캐시 배수(입력 단가 대비). 규칙에서 생략 시 이 값 사용. Anthropic/OpenAI 공통 현행값.
    private const double DefW5m = 1.25, DefW1h = 2.0, DefRead = 0.1;

    private readonly record struct Price(double InPerM, double OutPerM, double W5mMult, double W1hMult, double ReadMult);
    private static Price? PriceFor(string? model)
    {
        if (string.IsNullOrEmpty(model)) return null;
        var m = model.ToLowerInvariant();
        try
        {
            foreach (var r in SettingsService.LoadUsagePricing())
                if (!string.IsNullOrEmpty(r.Match) && m.Contains(r.Match.ToLowerInvariant()))
                    return new Price(r.InPerM, r.OutPerM,
                        r.CacheWrite5m ?? DefW5m, r.CacheWrite1h ?? DefW1h, r.CacheRead ?? DefRead);
        }
        catch { /* 설정 로드 실패 시 내장 기본값으로 */ }
        // 내장 기본값(설정에 없을 때) — 캐시 배수는 표준값. 구체적 모델을 먼저 검사(contains 매칭).
        if (m.Contains("fable") || m.Contains("mythos")) return new Price(10, 50, DefW5m, DefW1h, DefRead);
        if (m.Contains("opus")) return new Price(5, 25, DefW5m, DefW1h, DefRead);
        if (m.Contains("sonnet")) return new Price(3, 15, DefW5m, DefW1h, DefRead); // Sonnet 5 정가(인트로 $2/$10 은 2026-08 까지 — 정가 기준 표시)
        if (m.Contains("haiku")) return new Price(1, 5, DefW5m, DefW1h, DefRead);
        // GPT-5.6 티어별(2026-07-30 인하 반영). cached read ×0.1, cache write ×1.25(5.6부터 write 과금).
        if (m.Contains("gpt-5.6-terra")) return new Price(2, 12, DefW5m, DefW1h, DefRead);
        if (m.Contains("gpt-5.6-luna")) return new Price(0.2, 1.2, DefW5m, DefW1h, DefRead);
        if (m.Contains("gpt-5.6")) return new Price(5, 30, DefW5m, DefW1h, DefRead); // sol + 티어 미표기 폴백
        if (m.Contains("gpt-5.5")) return new Price(5, 30, DefW5m, DefW1h, DefRead);
        if (m.Contains("gpt-5.3-codex")) return new Price(1.75, 14, DefW5m, DefW1h, DefRead);
        if (m.Contains("gpt-5") || m.Contains("codex") || m.Contains("gpt5")) return new Price(1.25, 10, DefW5m, DefW1h, DefRead); // 구 GPT-5/gpt-5-codex
        return null;
    }

    /// <summary>파싱 시 계산해 둔 비용 추정치. (모델별 단가 합산은 ParseClaudeFull/ReadCodexLike 에서 수행)</summary>
    public static double? EstimateCost(in UsageTotals t) => t.Cost;

    /// <summary>단일 모델·토큰 묶음의 비용($). 캐시 배수는 모델 규칙별(Price)에서 온다. 단가 미상이면 null.</summary>
    private static double? CostOf(string? model, long inNew, long cw5, long cw1, long cr, long outp)
    {
        if (PriceFor(model) is not { } p) return null;
        double inR = p.InPerM / 1_000_000.0, outR = p.OutPerM / 1_000_000.0;
        return inNew * inR + cw5 * inR * p.W5mMult + cw1 * inR * p.W1hMult + cr * inR * p.ReadMult + outp * outR;
    }

    /// <summary>지원 에이전트인지 (정확 집계 가능). 그 외는 표시하지 않는다.</summary>
    public static bool IsSupported(string agentId) => agentId is "claude" or "codex" or "devezvibe";

    /// <summary>메모리/usage.json 에 남아 있는 이 방의 마지막 집계값(경로 미해석 시 폴백 표시용). 없으면 null.</summary>
    private static UsageTotals? LastKnown(string roomId)
        => _cache.TryGetValue(roomId, out var e) && e.Totals.HasData ? e.Totals : null;

    /// <summary>이 방의 최신 누적 사용량. 미지원/데이터 없음이면 null. 백그라운드 스레드에서 호출 권장(파일 IO).</summary>
    public static UsageTotals? Read(string roomId, string agentId, string? cwd)
    {
        EnsureLoaded();
        try
        {
            return agentId switch
            {
                "claude" => ReadClaude(roomId, cwd),
                "codex" => ReadCodexLike(roomId, SettingsService.LoadCodexRoomSession(roomId), "Codex"),
                "devezvibe" => ReadDevezVibe(roomId, cwd),
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
        return ReadClaude(roomId, cwd, SettingsService.LoadClaudeCodeRoomSession(roomId), "Claude");
    }

    /// <summary>Claude 백엔드를 선택한 dvz 방은 Claude transcript를 읽는다.
    /// Codex rollout만 읽으면 <c>claude:UUID</c> 경로를 해석하지 못해 사용량이 숨겨진다.</summary>
    private static UsageTotals? ReadDevezVibe(string roomId, string? cwd)
    {
        var sid = SettingsService.LoadDevezVibeRoomSession(roomId);
        return sid?.StartsWith("claude:", StringComparison.Ordinal) == true
            ? ReadClaude(roomId, cwd, DevezVibeStateService.StripBackendPrefix(sid), "Devez Vibe · Claude")
            : ReadCodexLike(roomId, sid, "Devez Vibe");
    }

    private static UsageTotals? ReadClaude(string roomId, string? cwd, string? sid, string agentLabel)
    {
        var path = TerminalSessionManager.FindClaudeTranscriptPath(cwd, sid);
        if (path == null) return LastKnown(roomId); // 재시작 직후 등 경로 미해석 → 마지막 저장값이라도 표시

        var entry = _cache.GetOrAdd(roomId, _ => new Entry { Totals = new UsageTotals(0, 0, 0, 0, 0, null, agentLabel) });
        lock (entry)
        {
            long len = new FileInfo(path).Length;
            if (entry.Sid == sid && len == entry.LastLen && entry.LastLen > 0) // 변화 없음 → 캐시 그대로
                return entry.Totals.HasData ? entry.Totals : null;

            var acc = ParseClaudeFull(path, agentLabel);
            entry.Sid = sid;
            entry.LastLen = len;
            entry.Totals = acc;
            SaveThrottled();
            return acc.HasData ? acc : null;
        }
    }

    private static UsageTotals ParseClaudeFull(string path, string agentLabel)
    {
        long inNew = 0, cw5 = 0, cw1 = 0, cr = 0, outp = 0;      // 표시용 전체 합계(모델 무관)
        var byModel = new Dictionary<string, long[]>();          // 비용 정확도용 모델별 버킷 [in,cw5,cw1,cr,out]
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
                var mdl = msg.TryGetProperty("model", out var mo) && mo.ValueKind == JsonValueKind.String ? mo.GetString()! : "?";
                long li = GetLong(u, "input_tokens");
                long lr = GetLong(u, "cache_read_input_tokens");
                long lo = GetLong(u, "output_tokens");
                long l5, l1;
                if (u.TryGetProperty("cache_creation", out var cc) && cc.ValueKind == JsonValueKind.Object)
                { l5 = GetLong(cc, "ephemeral_5m_input_tokens"); l1 = GetLong(cc, "ephemeral_1h_input_tokens"); }
                else { l5 = GetLong(u, "cache_creation_input_tokens"); l1 = 0; } // 구 포맷 폴백(티어 미구분 → 5m)
                inNew += li; cr += lr; outp += lo; cw5 += l5; cw1 += l1;
                if (!byModel.TryGetValue(mdl, out var b)) byModel[mdl] = b = new long[5];
                b[0] += li; b[1] += l5; b[2] += l1; b[3] += lr; b[4] += lo;
            }
            catch { /* 부분 줄/비 JSON 무시 */ }
        }

        // 모델별 단가로 비용 합산 — 세션 중 모델을 바꿔 써도 정확. 단가 미상 모델은 비용에서 제외(0 기여).
        double cost = 0; bool anyPriced = false;
        foreach (var (mdl, b) in byModel)
            if (CostOf(mdl, b[0], b[1], b[2], b[3], b[4]) is { } c) { cost += c; anyPriced = true; }

        // 표시 모델 라벨: 실제 토큰을 쓴 모델이 하나면 그 이름, 여럿이면 "여러 모델(a, b)".
        var used = byModel.Keys.Where(k => k != "?").ToList();
        string? label = used.Count switch
        {
            0 => byModel.Count > 0 ? "?" : null,
            1 => used[0],
            _ => "여러 모델(" + string.Join(", ", used.Select(ShortModel)) + ")",
        };
        return new UsageTotals(inNew, cw5, cw1, cr, outp, label, agentLabel) { Cost = anyPriced ? cost : null };
    }

    private static string ShortModel(string m)
    {
        var s = m.ToLowerInvariant();
        if (s.Contains("opus")) return "Opus";
        if (s.Contains("sonnet")) return "Sonnet";
        if (s.Contains("haiku")) return "Haiku";
        if (s.Contains("fable")) return "Fable";
        if (s.Contains("gpt-5.6-sol")) return "5.6 Sol";
        if (s.Contains("gpt-5.6-terra")) return "5.6 Terra";
        if (s.Contains("gpt-5.6-luna")) return "5.6 Luna";
        return m;
    }

    // ── codex/devezvibe: 파일 끝 마지막 token_count 이벤트 한 줄만 (누적값 내장) ──
    private static UsageTotals? ReadCodexLike(string roomId, string? sid, string agentLabel)
    {
        var path = TerminalSessionManager.FindCodexTranscriptPath(sid);
        if (path == null) return LastKnown(roomId); // 재시작 직후 등 경로 미해석 → 마지막 저장값

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
        if (last == null) return LastKnown(roomId); // tail 128KB 안에 token_count 없음(마지막 응답 큼) → 직전 집계값 유지(순간 미표시 방지)
        try
        {
            using var d = JsonDocument.Parse(last);
            var info = d.RootElement.GetProperty("payload").GetProperty("info").GetProperty("total_token_usage");
            long input = GetLong(info, "input_tokens");
            long cached = GetLong(info, "cached_input_tokens");
            long cw = GetLong(info, "cache_write_input_tokens"); // GPT-5.6부터 write 과금(×1.25) — CacheWrite5m 버킷으로 계산
            long output = GetLong(info, "output_tokens");
            long inNew = Math.Max(0, input - cached - cw); // input_tokens 는 cached/write 포함 총계
            var t = new UsageTotals(inNew, cw, 0, cached, output, model ?? "gpt-5-codex", agentLabel)
            { Cost = CostOf(model ?? "gpt-5-codex", inNew, cw, 0, cached, output) };
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
        => "※ 실제 청구액이 아니라 토큰 사용량으로 계산한 추정치입니다.";

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
        public double? Cost { get; set; }
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
                            v.Model, v.AgentLabel ?? "Claude") { Cost = v.Cost },
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
                        Cost = t.Cost,
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

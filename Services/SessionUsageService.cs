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
/// total_token_usage)를 집계한다. 비용은 기록된 모델·사용량을 현재 API 단가로 환산한 추정치다.
/// 파일이 변경될 때 재집계하고, 상태는 %AppData%\DevezCode\usage.json 에 영속한다.</summary>
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
        public bool CostIsPartial { get; init; }
        public long InputTotal => InputNew + CacheWrite5m + CacheWrite1h + CacheRead;
        public bool HasData => InputNew > 0 || CacheWrite5m > 0 || CacheWrite1h > 0 || CacheRead > 0 || Output > 0;
    }

    private sealed class Entry
    {
        public string? Sid;      // 이 방이 현재 가리키는 CLI 세션ID. 바뀌면(포크/재개) offset·누적을 리셋
        public long Offset;      // claude: 다음에 이어읽을 byte 위치. codex: 미사용
        public long LastLen;     // 마지막으로 읽었을 때 파일 크기(변화 감지용)
        public long LastWriteTicks;
        public UsageTotals Totals;
    }

    private static readonly ConcurrentDictionary<string, Entry> _cache = new();
    private static readonly object _saveLock = new();
    private static long _lastSaveTicks;
    private static bool _loaded;

    // ── 단가표 (per 1M tokens). 현재 API 정가로 환산하며 실제 구독 청구액과 다르다. ──
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
                if (!string.IsNullOrEmpty(r.Match) && m.Contains(r.Match.ToLowerInvariant())
                    && ValidRate(r.InPerM) && ValidRate(r.OutPerM)
                    && ValidRate(r.CacheWrite5m ?? DefW5m) && ValidRate(r.CacheWrite1h ?? DefW1h) && ValidRate(r.CacheRead ?? DefRead))
                    return new Price(r.InPerM, r.OutPerM,
                        r.CacheWrite5m ?? DefW5m, r.CacheWrite1h ?? DefW1h, r.CacheRead ?? DefRead);
        }
        catch { /* 설정 로드 실패 시 내장 기본값으로 */ }
        // 내장 기본값(설정에 없을 때) — 캐시 배수는 표준값. 구체적 모델을 먼저 검사(contains 매칭).
        if (m.Contains("fable") || m.Contains("mythos")) return new Price(10, 50, DefW5m, DefW1h, DefRead);
        if (m.Contains("opus")) return new Price(5, 25, DefW5m, DefW1h, DefRead);
        if (m.Contains("sonnet")) return new Price(3, 15, DefW5m, DefW1h, DefRead); // Sonnet 5 정가(인트로 $2/$10 은 2026-08 까지 — 정가 기준 표시)
        if (m.Contains("haiku")) return new Price(1, 5, DefW5m, DefW1h, DefRead);
        // OpenAI 공식 표준 단가, 2026-09-09 확인. 장문·서비스 등급은 요청별로 적용.
        if (m.Contains("gpt-6-astra")) return new Price(10, 50, DefW5m, DefW1h, DefRead);
        // GPT-5.6 티어별(2026-07-30 인하 반영). cached read ×0.1, cache write ×1.25(5.6부터 write 과금).
        if (m.Contains("gpt-5.6-terra")) return new Price(2, 12, DefW5m, DefW1h, DefRead);
        if (m.Contains("gpt-5.6-luna")) return new Price(0.2, 1.2, DefW5m, DefW1h, DefRead);
        if (m.Contains("gpt-5.6")) return new Price(4, 20, DefW5m, DefW1h, DefRead); // sol + 공식 별칭, 행사 가격(최소 2026-11-21까지)
        if (m.Contains("gpt-5.5")) return new Price(5, 30, DefW5m, DefW1h, DefRead);
        if (m.Contains("gpt-5.3-codex")) return new Price(1.75, 14, DefW5m, DefW1h, DefRead);
        if (m.Contains("gpt-5") || m.Contains("codex") || m.Contains("gpt5")) return new Price(1.25, 10, DefW5m, DefW1h, DefRead); // 구 GPT-5/gpt-5-codex
        return null;
    }

    private static bool ValidRate(double value) => double.IsFinite(value) && value >= 0;

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
        if (s.Contains("gpt-6-astra")) return "6 Astra";
        if (s.Contains("gpt-5.6-sol")) return "5.6 Sol";
        if (s.Contains("gpt-5.6-terra")) return "5.6 Terra";
        if (s.Contains("gpt-5.6-luna")) return "5.6 Luna";
        return m;
    }

    // ── codex/devezvibe: 요청별 기록 우선, 구버전은 누적 token_count 차이로 복원 ──
    private static UsageTotals? ReadCodexLike(string roomId, string? sid, string agentLabel)
    {
        var path = TerminalSessionManager.FindCodexTranscriptPath(sid);
        if (path == null) return LastKnown(roomId); // 재시작 직후 등 경로 미해석 → 마지막 저장값
        var entry = _cache.GetOrAdd(roomId, _ => new Entry());
        lock (entry)
        {
            var file = new FileInfo(path);
            long len = file.Length, ticks = file.LastWriteTimeUtc.Ticks;
            if (entry.Sid == sid && entry.LastLen == len && entry.LastWriteTicks == ticks)
                return entry.Totals.HasData ? entry.Totals : null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var totals = ParseCodexUsage(reader, agentLabel);
            entry.Sid = sid;
            entry.LastLen = len;
            entry.LastWriteTicks = ticks;
            entry.Offset = -1;
            entry.Totals = totals;
            return totals.HasData ? totals : null;
        }
    }

    private readonly record struct CodexTokens(long Input, long Cached, long Write, long Output)
    {
        public bool HasData => Input > 0 || Output > 0;
        public static CodexTokens operator -(CodexTokens a, CodexTokens b)
            => new(a.Input - b.Input, a.Cached - b.Cached, a.Write - b.Write, a.Output - b.Output);
        public bool IsValid => Input >= 0 && Output >= 0 && Cached >= 0 && Write >= 0
            && Cached <= Input && Write <= Input - Cached && Output <= long.MaxValue - Input;
    }

    private static bool TryCodexTokens(JsonElement obj, string name, out CodexTokens tokens)
    {
        tokens = default;
        if (!obj.TryGetProperty(name, out var u) || u.ValueKind != JsonValueKind.Object) return false;
        bool Number(string key, bool required, out long value)
        {
            value = 0;
            return u.TryGetProperty(key, out var v)
                ? v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out value) && value >= 0
                : !required;
        }
        if (!Number("input_tokens", true, out var input) || !Number("output_tokens", true, out var output)
            || !Number("cached_input_tokens", false, out var cached) || !Number("cache_write_input_tokens", false, out var write)) return false;
        tokens = new(input, cached, write, output);
        return tokens.IsValid;
    }

    private static string? StringProperty(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    internal static UsageTotals ParseCodexUsage(TextReader reader, string agentLabel)
    {
        string? model = null, tier = null;
        long input = 0, cached = 0, write = 0, output = 0;
        double cost = 0;
        bool priced = false, partial = false, modern = false, pendingRecord = false;
        CodexTokens previous = default, modernSum = default, latestRequest = default;
        var responses = new HashSet<string>(StringComparer.Ordinal);
        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(CodexTokens usage, bool requestKnown)
        {
            if (!usage.HasData) return;
            // 손상된 로그의 정수 넘침이 표시와 추정 비용을 음수로 만들지 않게 한다.
            long ni, nc, nw, no;
            try { checked { ni = input + usage.Input; nc = cached + usage.Cached; nw = write + usage.Write; no = output + usage.Output; } }
            catch (OverflowException) { partial = true; return; }
            input = ni; cached = nc; write = nw; output = no;
            models.Add(model ?? "?");
            if (PriceFor(model) is not { } p) { partial = true; return; }
            var m = model!.ToLowerInvariant();
            bool longContext = requestKnown && usage.Input > 272_000
                && (m.Contains("gpt-6-astra") || m.Contains("gpt-5.6"));
            var serviceTier = tier?.ToLowerInvariant();
            double speed = serviceTier switch { "priority" or "fast" => 2, "flex" or "batch" => 0.5, _ => 1 };
            partial |= serviceTier is not (null or "" or "default" or "standard" or "priority" or "fast" or "flex" or "batch");
            double inputCost = ((usage.Input - usage.Cached - usage.Write) + usage.Cached * p.ReadMult + usage.Write * p.W5mMult) * p.InPerM;
            double requestCost = (inputCost * (longContext ? 2 : 1) + usage.Output * p.OutPerM * (longContext ? 1.5 : 1)) * speed / 1_000_000;
            if (!double.IsFinite(cost + requestCost)) { partial = true; return; }
            cost += requestCost;
            priced = true;
            partial |= !requestKnown;
        }

        void Record(JsonElement payload)
        {
            var id = StringProperty(payload, "response_id");
            if (string.IsNullOrEmpty(id) || !TryCodexTokens(payload, "usage", out var usage)) { partial = true; return; }
            modern = true;
            if (!responses.Add(id)) return;
            pendingRecord = true;
            latestRequest = usage;
            Add(usage, true);
            try
            {
                checked { modernSum = new(modernSum.Input + usage.Input, modernSum.Cached + usage.Cached,
                    modernSum.Write + usage.Write, modernSum.Output + usage.Output); }
                if (payload.TryGetProperty("thread_token_usage", out _))
                    partial |= !TryCodexTokens(payload, "thread_token_usage", out var cumulative) || cumulative != modernSum;
            }
            catch (OverflowException) { partial = true; }
        }

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) continue;
                switch (StringProperty(root, "type"))
                {
                    case "turn_context":
                        model = StringProperty(payload, "model") ?? model;
                        if (payload.TryGetProperty("service_tier", out _)) tier = StringProperty(payload, "service_tier");
                        break;
                    case "token_usage_record":
                        Record(payload);
                        break;
                    case "compacted":
                        if (payload.TryGetProperty("latest_token_usage_record", out var latest) && latest.ValueKind == JsonValueKind.Object) Record(latest);
                        break;
                    case "event_msg":
                        if (StringProperty(payload, "type") == "thread_settings_applied"
                            && payload.TryGetProperty("thread_settings", out var settings) && settings.ValueKind == JsonValueKind.Object)
                        {
                            model = StringProperty(settings, "model") ?? model;
                            tier = StringProperty(settings, "service_tier");
                        }
                        else if (StringProperty(payload, "type") == "token_count"
                            && payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                            && TryCodexTokens(info, "total_token_usage", out var total))
                        {
                            var delta = total - previous;
                            bool hasLast = TryCodexTokens(info, "last_token_usage", out var last);
                            if (total != previous && !modern)
                            {
                                if (!delta.IsValid) delta = total; // 재개·요약 등으로 누적이 초기화된 구간
                                Add(delta, hasLast && delta == last);
                            }
                            else if (modern)
                            {
                                // 새 요청 기록이 빠졌는데 구 누적만 증가했다면 완전한 집계로 표시하지 않는다.
                                if (total != previous && (!pendingRecord || (hasLast && last.HasData && last != latestRequest)))
                                    partial = true;
                                pendingRecord = false;
                            }
                            previous = total;
                        }
                        break;
                }
            }
            catch (JsonException) { partial = true; /* 작성 중인 마지막 줄은 다음 변경 때 다시 읽는다. */ }
            catch (InvalidOperationException) { partial = true; /* 손상된 줄로 사용량을 놓쳤을 수 있다. */ }
        }
        string? label = models.Count switch { 0 => null, 1 => models.First(), _ => "여러 모델(" + string.Join(", ", models.Select(ShortModel)) + ")" };
        return new UsageTotals(input - cached - write, write, 0, cached, output, label, agentLabel)
            { Cost = priced ? cost : null, CostIsPartial = partial };
    }

    // ── 표시 문자열 ──

    /// <summary>헤더 인라인: "↓570k ↑6.5k $0.42". 단가 미상이면 $ 생략.</summary>
    public static string FormatInline(in UsageTotals t)
    {
        var sb = new StringBuilder();
        sb.Append('↓').Append(Compact(t.InputTotal)).Append("  ↑").Append(Compact(t.Output));
        if (EstimateCost(t) is { } c) sb.Append(t.CostIsPartial ? "  (~" : "  (").Append(FormatCost(c)).Append(')');
        return sb.ToString();
    }

    /// <summary>툴팁: 전체 분해 + 예상 비용.</summary>
    public static string FormatTooltip(in UsageTotals t)
        => "※ 실제 청구액이 아니라 토큰 사용량으로 계산한 추정치입니다."
            + (t.CostIsPartial ? "\n일부 사용량의 단가 또는 요청 정보가 불완전해 비용이 실제와 차이가 날 수 있습니다." : "")
            + (!t.AgentLabel.Contains("Claude", StringComparison.OrdinalIgnoreCase)
                ? "\n처리 등급이 기록되지 않은 요청은 표준 요율로 계산하며, 도구 사용료는 포함하지 않습니다." : "");

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
        public bool CostIsPartial { get; set; }
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
                            v.Model, v.AgentLabel ?? "Claude") { Cost = v.Cost, CostIsPartial = v.CostIsPartial },
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
                        CostIsPartial = t.CostIsPartial,
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

}

using System.Text.Json;
using System.Diagnostics;
using DevezCode.Services;
using DevezCode.Services.Terminal;
using Totals = DevezCode.Services.SessionUsageService.UsageTotals;

if (args is ["--replay", var path])
{
    var timer = Stopwatch.StartNew();
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(stream);
    var totals = SessionUsageService.ParseCodexUsage(reader, "검사");
    timer.Stop();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        totals.InputTotal, totals.Output, totals.CacheRead, totals.CacheWrite5m, totals.Model,
        totals.Cost, totals.CostIsPartial, FormatInline = SessionUsageService.FormatInline(totals), elapsedMillis = timer.ElapsedMilliseconds
    }));
    return 0;
}

var failures = new List<string>();
int passed = 0;
void Test(string name, Action check)
{
    SettingsService.Pricing = [];
    try { check(); passed++; Console.WriteLine($"통과: {name}"); }
    catch (Exception ex) { failures.Add(name); Console.Error.WriteLine($"실패: {name}: {ex.Message}"); }
    finally { SettingsService.Pricing = []; }
}

Test("아스트라 기본 단가와 캐시 쓰기 및 추론 출력 중복 제외", () =>
{
    var t = Parse(Context(), Event(1000, 100, 200, 100, 80));
    Equal(700L, t.InputNew); Equal(200L, t.CacheRead); Equal(100L, t.CacheWrite5m);
    Equal(100L, t.Output); Cost(.01345, t); Equal(false, t.CostIsPartial);
    True(SessionUsageService.FormatInline(t).Contains("$0.01"), "상단 비용 표시 없음");
});

foreach (var (model, price) in new[] { ("gpt-5.6-sol", .006), ("gpt-5.6-terra", .0032), ("gpt-5.6-luna", .00032) })
    Test($"모델 기본 단가 {model}", () => Cost(price, Parse(Context(model), Event(1000, 100))));

Test("장문 경계 272000은 표준", () => Cost(2.725, Parse(Context(), Event(272000, 100))));
Test("장문 경계 272001은 입력 두 배 출력 한 배 반", () => Cost(5.44752, Parse(Context(), Event(272001, 100))));
Test("장문 캐시 읽기와 쓰기에도 입력 할증", () =>
    Cost(4.97752, Parse(Context(), Event(272001, 100, 40000, 50000))));
Test("누적 입력이 커도 짧은 개별 요청은 표준", () =>
    Cost(3.015, Parse(Context(), Event(150000, 100), Event(300000, 300, lastInput: 150000, lastOutput: 200))));
Test("장문 요청 한 건만 할증", () =>
    Cost(5.46252, Parse(Context(), Event(1000, 100), Event(273001, 200, lastInput: 272001, lastOutput: 100))));

foreach (var (tier, multiplier) in new[] { ("priority", 2.0), ("fast", 2.0), ("flex", .5), ("batch", .5), ("default", 1.0) })
    Test($"서비스 등급 배수 {tier}", () => Cost(.015 * multiplier, Parse(Context(tier: tier), Event(1000, 100))));
Test("장문과 우선 처리 할증 동시 적용", () => Cost(10.89504, Parse(Context(tier: "priority"), Event(272001, 100))));
Test("새 문맥에서 등급을 명시적으로 비우면 표준", () =>
    Cost(.045, Parse(Context(tier: "priority"), Event(1000, 100), Context(), Event(2000, 200, lastInput: 1000, lastOutput: 100))));

Test("모델 변경 이전과 이후 단가 분리", () =>
{
    var t = Parse(Context(), Event(1000, 100), Context("gpt-5.6-luna"), Event(2000, 200, lastInput: 1000, lastOutput: 100));
    Cost(.01532, t); Equal(2000L, t.InputTotal); True(t.Model?.Contains("여러 모델") == true, "혼합 모델 표시 없음");
});
Test("동일 누적 이벤트는 중복 과금하지 않음", () =>
    Cost(.015, Parse(Context(), Event(1000, 100), Event(1000, 100))));
Test("동일 누적 이벤트는 모델 변경 후에도 중복 제외", () =>
    Cost(.015, Parse(Context(), Event(1000, 100), Context("gpt-5.6-sol"), Event(1000, 100))));
Test("누적 초기화 이후 구간 합산", () =>
{
    var t = Parse(Context(), Event(1000, 100), Event(200, 20));
    Equal(1200L, t.InputTotal); Equal(120L, t.Output); Cost(.018, t);
});

Test("잘못된 줄과 불완전 마지막 줄은 이전 집계 보존", () =>
    Cost(.03, Parse("잘못된 JSON", "[]", "null", Context(), Event(1000, 100), "{\"payload\":", Event(2000, 200), "{")));
Test("모델 누락 시 임의 단가 적용하지 않음", () =>
{
    var t = Parse(Event(1000, 100)); Equal(1000L, t.InputTotal); Equal<double?>(null, t.Cost);
    True(!SessionUsageService.FormatInline(t).Contains('$'), "모델 미상에 비용 표시");
});
Test("알 수 없는 모델 단가는 미확인", () => Equal<double?>(null, Parse(Context("unknown-model"), Event(1000, 100)).Cost));
Test("일반 대화에 삽입된 모델과 이벤트는 무시", () =>
{
    var fake = JsonSerializer.Serialize(new { type = "response_item", payload = new { type = "message", model = "gpt-6-astra", text = "token_count", info = new { total_token_usage = Usage(9999, 999) } } });
    var t = Parse(fake, Event(1000, 100)); Equal(1000L, t.InputTotal); Equal<double?>(null, t.Cost);
});
Test("일부 모델 미상은 알려진 비용과 부분 집계 표시", () =>
{
    var t = Parse(Context(), Event(1000, 100), Context("unknown-model"), Event(2000, 200, lastInput: 1000, lastOutput: 100));
    Cost(.015, t); Equal(2000L, t.InputTotal); Equal(true, t.CostIsPartial);
    True(SessionUsageService.FormatInline(t).Contains("(~$"), "상단 부분 비용 표시 없음");
    True(SessionUsageService.FormatTooltip(t).Contains("일부"), "도움말 부분 비용 설명 없음");
});
Test("모델과 토큰이 마지막 128킬로바이트 밖에 있어도 복원", () =>
    Cost(.015, Parse(Context(), Event(1000, 100), JsonSerializer.Serialize(new { type = "response_item", payload = new { text = new string('가', 140000) } }))));
Test("외부 단가와 캐시 배수가 내장 단가보다 우선", () =>
{
    SettingsService.Pricing = [new() { Match = "gpt-6-astra", InPerM = 20, OutPerM = 40, CacheRead = .2, CacheWrite5m = 2 }];
    Cost(.0228, Parse(Context(), Event(1000, 100, 200, 100)));
});
Test("빈 기록은 사용량 없음", () => { var t = Parse(); Equal(false, t.HasData); Equal<double?>(null, t.Cost); });
foreach (var invalid in new[] { "-1", "9223372036854775808", "1.2", "\"123\"", "null" })
    Test($"잘못된 토큰 숫자가 음수 비용이나 예외를 만들지 않음 {invalid}", () =>
    {
        var bad = Event(1000, 100).Replace("\"input_tokens\":1000", "\"input_tokens\":" + invalid);
        var t = Parse(Context(), bad);
        True(t.InputTotal >= 0 && t.InputNew >= 0 && t.CacheRead >= 0 && t.Output >= 0, "음수 토큰");
        True(t.Cost is null || double.IsFinite(t.Cost.Value) && t.Cost >= 0, "잘못된 비용");
    });
Test("캐시 합계가 입력을 초과해도 표시 입력이 증가하지 않음", () =>
{
    var t = Parse(Context(), Event(100, 10, 200, 300));
    True(t.InputTotal <= 100, "캐시 오류로 전체 입력 부풀림");
});

Test("최신 요청 기록 기본 단가와 캐시 및 추론 출력", () =>
    Cost(.01345, Parse(Context(), Record("r1", 1000, 100, 200, 100, 80))));
Test("최신 요청 기록이 누적값과 중복되지 않음", () =>
{
    var t = Parse(Context(), Record("r1", 1000, 100), Event(1000, 100), Record("r2", 2000, 200), Event(3000, 300, lastInput: 2000, lastOutput: 200));
    Equal(3000L, t.InputTotal); Cost(.045, t); Equal(false, t.CostIsPartial);
});
Test("최신 요청 기록 응답 식별자 중복 제외", () =>
    Cost(.015, Parse(Context(), Record("r1", 1000, 100), Record("r1", 1000, 100))));
Test("문맥 요약에 복제된 최신 요청은 중복 제외", () =>
    Cost(.015, Parse(Context(), Record("r1", 1000, 100), Compacted("r1", 1000, 100))));
Test("문맥 요약에서 처음 발견한 요청 비용 복원", () =>
    Cost(.03, Parse(Context(), Compacted("summary", 2000, 200))));
Test("요약 후 초기화된 누적값 대신 모든 요청 비용 합산", () =>
{
    var t = Parse(Context(), Record("r1", 1000, 100), Event(1000, 100), Record("summary", 2000, 200), Compacted("summary", 2000, 200), Record("r2", 300, 30), Event(300, 30));
    Equal(3300L, t.InputTotal); Cost(.0495, t);
});
Test("구형 기록 이후 최신 기록으로 전환", () =>
    Cost(.045, Parse(Context(), Event(1000, 100), Record("r2", 2000, 200), Event(3000, 300))));
Test("최신 요청 기록 장문 경계 및 고속 할증", () =>
    Cost(10.89504, Parse(Context(tier: "priority"), Record("r1", 272001, 100))));
Test("최신 요청별 작은 입력은 누적값이 커도 표준", () =>
    Cost(3.01, Parse(Context(), Record("r1", 150000, 100), Record("r2", 150000, 100))));
Test("설정 이벤트에서 모델과 처리 등급 복원", () =>
    Cost(.03, Parse(Settings("gpt-6-astra", "priority"), Record("r1", 1000, 100))));
Test("문맥에서 등급 필드가 없으면 앞선 설정 유지", () =>
    Cost(.03, Parse(Settings("gpt-6-astra", "priority"), JsonSerializer.Serialize(new { type = "turn_context", payload = new { model = "gpt-6-astra" } }), Record("r1", 1000, 100))));
Test("새 설정 이벤트에서 등급 누락이면 표준으로 초기화", () =>
    Cost(.045, Parse(Settings("gpt-6-astra", "priority"), Record("r1", 1000, 100), Settings("gpt-6-astra"), Record("r2", 1000, 100))));
Test("설정 이벤트 모델 교체 시 요청별 비용 보존", () =>
    Cost(.01532, Parse(Settings("gpt-6-astra"), Record("r1", 1000, 100), Settings("gpt-5.6-luna"), Record("r2", 1000, 100))));
Test("응답 식별자 없는 최신 기록은 부분 집계로 알림", () =>
{
    var t = Parse(Context(), Record("r1", 1000, 100), Record(null, 2000, 200));
    Cost(.015, t); Equal(true, t.CostIsPartial);
});
Test("누적 차이와 마지막 요청이 다르면 장문 할증 없이 부분 추정", () =>
{
    var t = Parse(Context(), Event(300000, 300, lastInput: 1000, lastOutput: 100));
    Cost(3.015, t); Equal(true, t.CostIsPartial);
});
Test("요청 합계 정수 범위 초과는 음수 대신 부분 집계", () =>
{
    var t = Parse(Context(), Record("r1", long.MaxValue - 1, 0), Record("r2", 10, 0));
    Equal(long.MaxValue - 1, t.InputTotal); Equal(true, t.CostIsPartial);
    True(t.Cost is { } cost && double.IsFinite(cost) && cost > 0, "합계 범위 초과 후 비용 손상");
});

foreach (var reported in new[] { 500L, 2000L })
    Test($"요청 합계와 기록된 전체 합계가 다르면 부분 추정 {reported}", () =>
    {
        var t = Parse(Context(), Record("r1", 1000, 100, threadInput: reported, threadOutput: 100));
        Cost(.015, t); Equal(true, t.CostIsPartial);
        True(SessionUsageService.FormatInline(t).Contains("(~$"), "불확실한 비용을 근사값으로 표시하지 않음");
        True(SessionUsageService.FormatTooltip(t).Contains("실제와 차이가 날 수 있습니다"), "양방향 차이 안내 없음");
    });
Test("요청 합계와 기록된 전체 합계가 같으면 온전한 추정", () =>
{
    var t = Parse(Context(), Record("r1", 1000, 100, threadInput: 1000, threadOutput: 100), Record("r2", 2000, 200, threadInput: 3000, threadOutput: 300));
    Cost(.045, t); Equal(false, t.CostIsPartial);
});
Test("요청 기록 없이 누적값만 증가하면 부분 추정", () =>
{
    var t = Parse(Context(), Record("r1", 1000, 100), Event(1000, 100), Event(2000, 200));
    Cost(.015, t); Equal(true, t.CostIsPartial);
});
Test("손상된 JSON 줄 때문에 빠진 요청 가능성 안내", () =>
{
    var t = Parse(Context(), Record("r1", 1000, 100), "{");
    Cost(.015, t); Equal(true, t.CostIsPartial);
});
foreach (var tier in new[] { "auto", "unknown-tier" })
    Test($"미확정 처리 등급은 표준 비용과 부분 추정 {tier}", () =>
    {
        var t = Parse(Context(tier: tier), Record("r1", 1000, 100));
        Cost(.015, t); Equal(true, t.CostIsPartial);
    });

Test("클로드 캐시 읽기와 쓰기 및 한 시간 요율 보존", () =>
{
    var line = JsonSerializer.Serialize(new { message = new { id = "m1", model = "claude-sonnet-5", usage = new { input_tokens = 1000, cache_read_input_tokens = 200, cache_creation = new { ephemeral_5m_input_tokens = 100, ephemeral_1h_input_tokens = 50 }, output_tokens = 100 } } });
    using var fixture = new TranscriptFixture(line);
    var t = ParseClaude(fixture.FirstPath);
    Equal(1350L, t.InputTotal); Equal(100L, t.CacheWrite5m); Equal(50L, t.CacheWrite1h); Equal(200L, t.CacheRead); Cost(.005235, t);
});
Test("클로드 같은 응답 식별자가 반복돼도 한 번만 과금", () =>
{
    var line = JsonSerializer.Serialize(new { message = new { id = "m1", model = "claude-opus-4-8", usage = new { input_tokens = 1000, cache_read_input_tokens = 0, output_tokens = 100 } } });
    using var fixture = new TranscriptFixture(line + "\n" + line);
    var t = ParseClaude(fixture.FirstPath);
    Equal(1000L, t.InputTotal); Equal(100L, t.Output); Cost(.0075, t);
});

foreach (var agent in new[] { "codex", "devezvibe" })
    Test($"실제 파일 조회부터 상단 표시까지 연결 {agent}", () =>
    {
        using var fixture = new TranscriptFixture(Context() + "\n" + Record("r1", 1000, 100));
        var t = SessionUsageService.Read(fixture.Room, agent, null) ?? throw new Exception("파일 사용량 없음");
        Cost(.015, t); Equal(1000L, t.InputTotal);
        Equal(agent == "codex" ? "Codex" : "Devez Vibe", t.AgentLabel);
        True(SessionUsageService.FormatInline(t).Contains("$0.02"), "상단 비용 연결 실패");
    });
Test("파일이 바뀌지 않으면 다시 열지 않고 캐시 사용", () =>
{
    using var fixture = new TranscriptFixture(Context() + "\n" + Record("r1", 1000, 100));
    var first = SessionUsageService.Read(fixture.Room, "codex", null);
    using var held = new FileStream(fixture.FirstPath, FileMode.Open, FileAccess.Read, FileShare.None);
    var second = SessionUsageService.Read(fixture.Room, "codex", null);
    True(first.HasValue, "최초 사용량 없음"); Equal(first, second);
});
Test("같은 길이 파일 교체도 수정시각이 바뀌면 다시 계산", () =>
{
    using var fixture = new TranscriptFixture(Context() + "\n" + Record("r1", 1000, 100));
    var first = SessionUsageService.Read(fixture.Room, "codex", null) ?? throw new Exception("최초 사용량 없음");
    var original = new FileInfo(fixture.FirstPath);
    long length = original.Length; var modified = original.LastWriteTimeUtc;
    File.WriteAllText(fixture.FirstPath, Context() + "\n" + Record("r1", 2000, 200));
    File.SetLastWriteTimeUtc(fixture.FirstPath, modified.AddSeconds(2));
    Equal(length, new FileInfo(fixture.FirstPath).Length);
    var second = SessionUsageService.Read(fixture.Room, "codex", null) ?? throw new Exception("교체 후 사용량 없음");
    Cost(.015, first); Cost(.03, second);
});
Test("부분 줄 뒤 나머지가 기록되면 누락 사용량 복구", () =>
{
    var next = Record("r2", 2000, 200);
    using var fixture = new TranscriptFixture(Context() + "\n" + Record("r1", 1000, 100) + "\n" + next[..^12]);
    var first = SessionUsageService.Read(fixture.Room, "devezvibe", null) ?? throw new Exception("최초 사용량 없음");
    File.AppendAllText(fixture.FirstPath, next[^12..]);
    var second = SessionUsageService.Read(fixture.Room, "devezvibe", null) ?? throw new Exception("추가 후 사용량 없음");
    Cost(.015, first); Cost(.045, second); Equal(3000L, second.InputTotal);
});
Test("세션 변경 시 같은 길이와 시각이어도 이전 모델과 비용 제거", () =>
{
    using var fixture = new TranscriptFixture(Context() + "\n" + Record("r1", 1000, 100));
    var first = SessionUsageService.Read(fixture.Room, "codex", null) ?? throw new Exception("최초 사용량 없음");
    fixture.Switch(Context("future-test") + "\n" + Record("r1", 2000, 200));
    File.SetLastWriteTimeUtc(fixture.SecondPath, File.GetLastWriteTimeUtc(fixture.FirstPath));
    Equal(new FileInfo(fixture.FirstPath).Length, new FileInfo(fixture.SecondPath).Length);
    var second = SessionUsageService.Read(fixture.Room, "codex", null) ?? throw new Exception("전환 후 사용량 없음");
    Cost(.015, first); Equal<double?>(null, second.Cost); Equal("future-test", second.Model); Equal(2000L, second.InputTotal);
});

Console.WriteLine($"검사 결과: 통과 {passed}, 실패 {failures.Count}");
return failures.Count == 0 ? 0 : 1;

static Totals Parse(params string[] lines) => SessionUsageService.ParseCodexUsage(new StringReader(string.Join('\n', lines)), "검사");
static Totals ParseClaude(string path) => (Totals)typeof(SessionUsageService)
    .GetMethod("ParseClaudeFull", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
    .Invoke(null, [path, "Claude"])!;
static string Context(string model = "gpt-6-astra", string? tier = null) => JsonSerializer.Serialize(new
{
    type = "turn_context", payload = new { model, service_tier = tier }
});
static object Usage(long input, long output, long cached = 0, long write = 0, long reasoning = 0) => new
{
    input_tokens = input, cached_input_tokens = cached, cache_write_input_tokens = write,
    output_tokens = output, reasoning_output_tokens = reasoning, total_tokens = input + output
};
static string Settings(string model, string? tier = null)
{
    var settings = new Dictionary<string, object> { ["model"] = model };
    if (tier != null) settings["service_tier"] = tier;
    return JsonSerializer.Serialize(new { type = "event_msg", payload = new { type = "thread_settings_applied", thread_settings = settings } });
}
static object RecordPayload(string? id, long input, long output, long cached = 0, long write = 0, long reasoning = 0, long? threadInput = null, long? threadOutput = null)
{
    var payload = new Dictionary<string, object?>
    {
        ["response_id"] = id, ["usage"] = Usage(input, output, cached, write, reasoning), ["turn_token_usage"] = Usage(input, output)
    };
    if (threadInput is { } totalInput && threadOutput is { } totalOutput)
        payload["thread_token_usage"] = Usage(totalInput, totalOutput, cached, write);
    return payload;
}
static string Record(string? id, long input, long output, long cached = 0, long write = 0, long reasoning = 0, long? threadInput = null, long? threadOutput = null) => JsonSerializer.Serialize(new
{
    type = "token_usage_record", payload = RecordPayload(id, input, output, cached, write, reasoning, threadInput, threadOutput)
});
static string Compacted(string id, long input, long output) => JsonSerializer.Serialize(new
{
    type = "compacted", payload = new { latest_token_usage_record = RecordPayload(id, input, output) }
});
static string Event(long input, long output, long cached = 0, long write = 0, long reasoning = 0, long? lastInput = null, long? lastOutput = null) => JsonSerializer.Serialize(new
{
    type = "event_msg", payload = new
    {
        type = "token_count", info = new
        {
            total_token_usage = Usage(input, output, cached, write, reasoning),
            last_token_usage = Usage(lastInput ?? input, lastOutput ?? output, cached, write, reasoning)
        }
    }
});
static void Cost(double expected, Totals actual)
{
    if (actual.Cost is not { } value || Math.Abs(expected - value) > 1e-9)
        throw new Exception($"예상 비용 {expected}, 실제 {actual.Cost}");
}
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"예상 {expected}, 실제 {actual}");
}
static void True(bool condition, string message) { if (!condition) throw new Exception(message); }

sealed class TranscriptFixture : IDisposable
{
    public string Room { get; } = "usage-test-" + Guid.NewGuid().ToString("N");
    private readonly string directory = Path.Combine(Path.GetTempPath(), "DevezCode-usage-test-" + Guid.NewGuid().ToString("N"));
    private readonly string firstSid = Guid.NewGuid().ToString("N");
    private readonly string secondSid = Guid.NewGuid().ToString("N");
    public string FirstPath => Path.Combine(directory, "first.jsonl");
    public string SecondPath => Path.Combine(directory, "second.jsonl");
    public TranscriptFixture(string content)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FirstPath, content);
        Map(firstSid, FirstPath);
    }
    public void Switch(string content)
    {
        File.WriteAllText(SecondPath, content);
        Map(secondSid, SecondPath);
    }
    private void Map(string sid, string path)
    {
        SettingsService.CodexSessions[Room] = sid;
        SettingsService.VibeSessions[Room] = sid;
        TerminalSessionManager.CodexPaths[sid] = path;
    }
    public void Dispose()
    {
        SettingsService.CodexSessions.Remove(Room);
        SettingsService.VibeSessions.Remove(Room);
        TerminalSessionManager.CodexPaths.Remove(firstSid);
        TerminalSessionManager.CodexPaths.Remove(secondSid);
        File.Delete(FirstPath);
        File.Delete(SecondPath);
        Directory.Delete(directory);
    }
}

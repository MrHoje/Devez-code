using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DevezCode.Models;
using DevezCode.Services.Terminal;

namespace DevezCode.Services.Mcp;

/// <summary>
/// 외부 에이전트(hermes 등)가 세션 상태를 "조회"하고 세션에 입력을 "주입"할 수 있게
/// 앱 내부에 띄우는 MCP(Model Context Protocol) HTTP 서버.
///
/// - 전송: Streamable HTTP (단순 요청/응답 → application/json 반환). SSE(GET) 는 미지원(405).
/// - 바인딩: 루프백 전용(http://127.0.0.1:&lt;port&gt;/) → 로컬 프로세스만 접근 가능(관리자 권한 불필요).
/// - 엔드포인트 URL 은 %AppData%\DevezCode\mcp\endpoint.txt 에 기록해 hermes 가 발견하게 한다.
/// - 세션 접근/주입은 DiscordBotService 와 동일 패턴: _projects 열거 + TerminalSessionManager.Instance.
///   (SessionItem 의 bool/string 속성 읽기와 TerminalSession.Write 는 스레드-세이프하게 취급)
/// 주입은 살아있는 ConPTY 에 써야 하므로 앱이 실행 중일 때만 동작한다.
/// </summary>
public sealed class McpServerService : IDisposable
{
    private const string ProtocolVersion = "2025-06-18";
    private const string ServerName = "devezcode";

    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private HttpListener? _listener;
    private volatile bool _running;
    private ObservableCollection<ProjectItem>? _projects;

    /// <summary>세션 컬렉션 주입(MainWindow 가 호출). DiscordBotService.SetProjects 와 동일 역할.</summary>
    public void SetProjects(ObservableCollection<ProjectItem> projects) => _projects = projects;

    public void Start()
    {
        if (_running) return;

        int basePort = 8765;
        var env = Environment.GetEnvironmentVariable("DEVEZCODE_MCP_PORT");
        if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env, out var p) && p is > 0 and < 65536)
            basePort = p;

        HttpListener? listener = null;
        int port = basePort;
        for (int i = 0; i < 20; i++, port++)
        {
            try
            {
                var l = new HttpListener();
                l.Prefixes.Add($"http://127.0.0.1:{port}/");
                l.Start();
                listener = l;
                break;
            }
            catch { /* 포트 사용 중 → 다음 포트 시도 */ }
        }
        if (listener == null)
        {
            Debug.WriteLine("[MCP] 서버 시작 실패: 사용 가능한 포트 없음");
            return;
        }

        _listener = listener;
        _running = true;
        WriteEndpointFile(port);
        Debug.WriteLine($"[MCP] http://127.0.0.1:{port}/mcp 리슨 시작");

        _ = AcceptLoopAsync();
    }

    /// <summary>hermes 가 엔드포인트를 발견하도록 URL 을 파일로 기록.</summary>
    private static void WriteEndpointFile(int port)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "mcp");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "endpoint.txt"), $"http://127.0.0.1:{port}/mcp");
        }
        catch { /* 기록 실패는 치명적이지 않음 */ }
    }

    private async Task AcceptLoopAsync()
    {
        var listener = _listener;
        if (listener == null) return;
        while (_running)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; } // Stop/Dispose → 루프 종료
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            // SSE(GET)/세션 종료(DELETE) 는 미지원 — 단순 요청/응답만.
            if (req.HttpMethod == "GET")
            {
                ctx.Response.StatusCode = 405;
                ctx.Response.AddHeader("Allow", "POST");
                ctx.Response.Close();
                return;
            }
            if (req.HttpMethod != "POST")
            {
                ctx.Response.StatusCode = 405;
                ctx.Response.Close();
                return;
            }

            string body;
            using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                body = await reader.ReadToEndAsync();

            JsonNode? parsed = null;
            try { parsed = JsonNode.Parse(body); } catch { }

            // 배치(JSON-RPC array) 지원.
            if (parsed is JsonArray arr)
            {
                var outArr = new JsonArray();
                foreach (var item in arr)
                    if (item is JsonObject o)
                    {
                        var r = await ProcessOneAsync(o);
                        if (r != null) outArr.Add(r);
                    }
                await WriteJsonAsync(ctx, outArr.Count == 0 ? null : outArr);
                return;
            }

            if (parsed is not JsonObject reqObj)
            {
                await WriteJsonAsync(ctx, ErrorEnvelope(null, -32700, "Parse error"));
                return;
            }

            var resp = await ProcessOneAsync(reqObj);
            await WriteJsonAsync(ctx, resp); // resp==null(알림) → 202 빈 응답
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MCP] 처리 오류: {ex.Message}");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    /// <summary>JSON-RPC 요청 하나를 처리. 알림(id 없음)이면 null 반환(응답 없음).</summary>
    private async Task<JsonObject?> ProcessOneAsync(JsonObject req)
    {
        var method = req["method"]?.GetValue<string>();
        bool isNotification = req["id"] == null && !req.ContainsKey("id");
        var idNode = req["id"];

        if (method == null)
            return isNotification ? null : ErrorEnvelope(idNode, -32600, "Invalid Request");

        // 알림(notifications/*)은 응답하지 않는다.
        if (method.StartsWith("notifications/")) return null;

        try
        {
            switch (method)
            {
                case "initialize":
                    var clientVer = req["params"]?["protocolVersion"]?.GetValue<string>();
                    return ResultEnvelope(idNode, new JsonObject
                    {
                        ["protocolVersion"] = clientVer ?? ProtocolVersion,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = "1.0.0" },
                    });

                case "ping":
                    return ResultEnvelope(idNode, new JsonObject());

                case "tools/list":
                    return ResultEnvelope(idNode, new JsonObject { ["tools"] = ToolDefinitions() });

                case "tools/call":
                    var name = req["params"]?["name"]?.GetValue<string>();
                    var args = req["params"]?["arguments"] as JsonObject;
                    var (text, isError) = await CallToolAsync(name, args);
                    return ResultEnvelope(idNode, new JsonObject
                    {
                        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
                        ["isError"] = isError,
                    });

                default:
                    return isNotification ? null : ErrorEnvelope(idNode, -32601, $"Method not found: {method}");
            }
        }
        catch (Exception ex)
        {
            return ErrorEnvelope(idNode, -32603, "Internal error: " + ex.Message);
        }
    }

    // ── 도구 정의 ──────────────────────────────────────────────

    private static JsonArray ToolDefinitions() => new()
    {
        new JsonObject
        {
            ["name"] = "list_sessions",
            ["description"] = "모든 세션의 목록과 상태(진행중/완료/대기/종료)를 반환한다. "
                + "각 세션: id, name(세션명), project, agent, status, busy, alive, waitingChoice, lastPrompt(마지막 보낸 프롬프트).",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["activeOnly"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "true 면 살아있는(실행 중) 세션만 반환. 기본 false.",
                    },
                },
            },
        },
        new JsonObject
        {
            ["name"] = "get_session",
            ["description"] = "세션 하나의 상세 상태와 마지막 assistant 응답(lastReply)을 반환한다. "
                + "status 가 idle(완료)이면 그 세션의 마지막 응답 메시지를 읽을 수 있다.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["query"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "세션 ID 또는 세션명(부분 일치 허용).",
                    },
                },
                ["required"] = new JsonArray { "query" },
            },
        },
        new JsonObject
        {
            ["name"] = "send_message",
            ["description"] = "실행 중인 세션에 텍스트(프롬프트/명령)를 주입한다. 세션이 살아있어야 한다. "
                + "submit=true(기본)면 입력 후 Enter 로 제출한다.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["query"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "대상 세션의 ID 또는 세션명(부분 일치 허용).",
                    },
                    ["text"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "주입할 텍스트.",
                    },
                    ["submit"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "입력 후 Enter 제출 여부. 기본 true.",
                    },
                },
                ["required"] = new JsonArray { "query", "text" },
            },
        },
    };

    // ── 도구 실행 ──────────────────────────────────────────────

    private async Task<(string text, bool isError)> CallToolAsync(string? name, JsonObject? args)
    {
        switch (name)
        {
            case "list_sessions":
                return (ToolListSessions(GetBool(args, "activeOnly") ?? false), false);

            case "get_session":
            {
                var q = GetString(args, "query");
                if (string.IsNullOrWhiteSpace(q)) return ("query 가 필요합니다.", true);
                var hit = Resolve(q!);
                if (hit == null) return ($"세션을 찾을 수 없습니다: {q}", true);
                return (SessionToNode(hit.Value.p, hit.Value.s, includeReply: true).ToJsonString(JsonOpts), false);
            }

            case "send_message":
            {
                var q = GetString(args, "query");
                var text = GetString(args, "text");
                if (string.IsNullOrWhiteSpace(q)) return ("query 가 필요합니다.", true);
                if (text == null) return ("text 가 필요합니다.", true);
                bool submit = GetBool(args, "submit") ?? true;

                var hit = Resolve(q!);
                if (hit == null) return ($"세션을 찾을 수 없습니다: {q}", true);

                var s = hit.Value.s;
                var live = TerminalSessionManager.Instance.Get(s.Id);
                if (live is not { IsAlive: true })
                    return ($"세션이 실행 중이 아니라 주입할 수 없습니다: {s.Name} ({s.Id})", true);

                // DiscordBotService.OnMessageReceived 와 동일: 텍스트와 Enter 를 분리해
                // alt-screen TUI 가 텍스트를 등록한 뒤 제출하게 한다(빈 제출 방지).
                bool inline = AgentRegistry.Find(AgentId(s))?.InlineTui == true;
                live.Write(text);
                if (submit)
                {
                    await Task.Delay(inline ? 500 : 250);
                    live.Write("\r");
                }

                var res = new JsonObject
                {
                    ["sent"] = true,
                    ["submitted"] = submit,
                    ["sessionId"] = s.Id,
                    ["name"] = s.Name,
                };
                return (res.ToJsonString(JsonOpts), false);
            }

            default:
                return ($"알 수 없는 도구: {name}", true);
        }
    }

    private string ToolListSessions(bool activeOnly)
    {
        var arr = new JsonArray();
        foreach (var (p, s) in EnumerateSessions())
        {
            if (activeOnly && !s.IsAlive) continue;
            arr.Add(SessionToNode(p, s, includeReply: false));
        }
        return arr.ToJsonString(JsonOpts);
    }

    // ── 세션 열거/해석/직렬화 ────────────────────────────────────

    private List<(ProjectItem p, SessionItem s)> EnumerateSessions()
    {
        var list = new List<(ProjectItem, SessionItem)>();
        var projects = _projects;
        if (projects == null) return list;
        foreach (var p in projects.ToArray())
            foreach (var s in p.Tabs.OfType<SessionItem>().ToArray())
                list.Add((p, s));
        return list;
    }

    /// <summary>query 로 세션 해석: ID 완전일치 → 세션명 완전일치 → 세션명 부분일치. 동점이면 살아있는 것 우선.</summary>
    private (ProjectItem p, SessionItem s)? Resolve(string query)
    {
        var all = EnumerateSessions();
        var byId = all.FirstOrDefault(x => string.Equals(x.s.Id, query, StringComparison.Ordinal));
        if (byId.s != null) return byId;

        var exact = all.Where(x => string.Equals(x.s.Name, query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return exact.FirstOrDefault(x => x.s.IsAlive) is { s: not null } a ? a : exact[0];

        var contains = all.Where(x => x.s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (contains.Count > 0) return contains.FirstOrDefault(x => x.s.IsAlive) is { s: not null } c ? c : contains[0];

        return null;
    }

    private static string AgentId(SessionItem s)
        => string.IsNullOrWhiteSpace(s.AgentId) ? AgentRegistry.DefaultAgentId : s.AgentId;

    private static string Status(SessionItem s)
        => !s.IsAlive ? "dead" : s.IsWaitingChoice ? "waiting" : s.IsBusy ? "running" : "idle";

    private static JsonObject SessionToNode(ProjectItem p, SessionItem s, bool includeReply)
    {
        var agent = AgentId(s);
        var node = new JsonObject
        {
            ["id"] = s.Id,
            ["name"] = s.Name,
            ["project"] = p.Name,
            ["agent"] = agent,
            ["status"] = Status(s),
            ["alive"] = s.IsAlive,
            ["busy"] = s.IsBusy,
            ["waitingChoice"] = s.IsWaitingChoice,
            ["lastPrompt"] = s.LastMessage ?? "",
        };
        if (includeReply)
            node["lastReply"] = AgentReplyService.TryGetLastAssistantReply(s.Id, agent);
        return node;
    }

    // ── JSON-RPC / HTTP 헬퍼 ────────────────────────────────────

    private static JsonObject ResultEnvelope(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    };

    private static JsonObject ErrorEnvelope(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private static async Task WriteJsonAsync(HttpListenerContext ctx, JsonNode? payload)
    {
        var resp = ctx.Response;
        if (payload == null)
        {
            // 알림 → 응답 본문 없음.
            resp.StatusCode = 202;
            resp.Close();
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString(JsonOpts));
        resp.StatusCode = 200;
        resp.ContentType = "application/json; charset=utf-8";
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes);
        resp.Close();
    }

    private static bool? GetBool(JsonObject? args, string key)
    {
        try { return args?[key]?.GetValue<bool>(); } catch { return null; }
    }

    private static string? GetString(JsonObject? args, string key)
    {
        try { return args?[key]?.GetValue<string>(); } catch { return null; }
    }

    public void Dispose()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;
    }
}

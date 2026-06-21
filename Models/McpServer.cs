using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace DevezCode.Models;

/// <summary>opencode MCP 서버 한 개. config JSON 의 <c>mcp.&lt;name&gt;</c> 와 1:1 매핑.
/// type 에 따라 command+environment 또는 url+headers+oauth 중 한 쪽만 사용된다.</summary>
public sealed class McpServer : INotifyPropertyChanged
{
    /// <summary>opencode config 의 키 이름. 고유해야 한다.</summary>
    public string Name { get; set; } = "";

    public McpServerType Type { get; set; } = McpServerType.Local;

    // ── Local ─────────────────────────────────────────────────────
    /// <summary>로컬 MCP 의 실행 커맨드(예: ["npx","-y","@modelcontextprotocol/server-filesystem","C:/work"]).
    /// 빈 배열이면 유효하지 않은 것으로 간주.</summary>
    public List<string> Command { get; set; } = new();

    /// <summary>로컬 MCP 환경변수 (KEY=VALUE). nullable 한 항목 제거는 Save 직전에 한다.</summary>
    public Dictionary<string, string> Environment { get; set; } = new();

    // ── Remote ────────────────────────────────────────────────────
    public string Url { get; set; } = "";

    /// <summary>원격 MCP HTTP 헤더. (Authorization 등)</summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>OAuth 동작 모드. null=auto(헤더로 clientId 없으면 자동 탐지), false=사용 안 함,
    /// 그 외=명시 설정. opencode schema 의 McpOAuthConfig 와 매핑.</summary>
    public McpOAuthMode? OAuthMode { get; set; }

    /// <summary>OAuthMode 가 Explicit 일 때만 사용.</summary>
    public string? OAuthClientId { get; set; }
    public string? OAuthClientSecret { get; set; }
    public string? OAuthScope { get; set; }

    // ── 공통 ──────────────────────────────────────────────────────
    public bool Enabled { get; set; } = true;

    /// <summary>연결 타임아웃(ms). 0 이면 기본값 사용(=필드 미출력).</summary>
    public int TimeoutMs { get; set; }

    // ── 라이브 상태(설정 파일에 저장되지 않음, opencode mcp list 로 채움) ──
    private McpLiveStatus _status = McpLiveStatus.Unknown;
    [JsonIgnore]
    public McpLiveStatus Status
    {
        get => _status;
        set { if (_status != value) { _status = value; OnPropertyChanged(); } }
    }

    private string _statusMessage = "";
    [JsonIgnore]
    public string StatusMessage
    {
        get => _statusMessage;
        set { if (_statusMessage != value) { _statusMessage = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    /// <summary>command 텍스트박스용 단일 문자열 (입력 시 Command 로 split).</summary>
    [JsonIgnore]
    public string CommandText
    {
        get => string.Join(" ", Command.Select(EscapeArg));
        set
        {
            Command = SplitArgs(value);
            OnPropertyChanged();
        }
    }

    /// <summary>환경변수 사전 ↔ "KEY=VALUE" 한 줄 텍스트.</summary>
    [JsonIgnore]
    public string EnvironmentText
    {
        get => string.Join(System.Environment.NewLine, Environment.Select(kv => $"{kv.Key}={kv.Value}"));
        set
        {
            Environment = ParseKvLines(value);
            OnPropertyChanged();
        }
    }

    /// <summary>헤더 사전 ↔ "KEY=VALUE" 한 줄 텍스트.</summary>
    [JsonIgnore]
    public string HeadersText
    {
        get => string.Join(System.Environment.NewLine, Headers.Select(kv => $"{kv.Key}={kv.Value}"));
        set
        {
            Headers = ParseKvLines(value);
            OnPropertyChanged();
        }
    }

    private static Dictionary<string, string> ParseKvLines(string? text)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var raw in text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;  // "=value" 도 키 없음으로 간주
            var key = line.Substring(0, eq).Trim();
            var val = line.Substring(eq + 1).Trim();
            if (key.Length == 0) continue;
            result[key] = val;
        }
        return result;
    }

    /// <summary>command 한 줄 (UI 표시용). 따옴표로 묶인 인자는 그대로 묶어 보여준다.</summary>
    public static string EscapeArg(string a)
    {
        if (string.IsNullOrEmpty(a)) return "\"\"";
        if (a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
        return "\"" + a.Replace("\"", "\\\"") + "\"";
    }

    /// <summary>"npx -y \"@foo bar\"" 같은 입력을 토큰으로 분리. 큰따옴표 묶음 + 백슬래시 이스케이프 지원.</summary>
    public static List<string> SplitArgs(string input)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(input)) return result;
        var cur = new System.Text.StringBuilder();
        bool inQuote = false;
        for (int i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (inQuote)
            {
                if (c == '\\' && i + 1 < input.Length)
                {
                    cur.Append(input[++i]);
                }
                else if (c == '"') inQuote = false;
                else cur.Append(c);
            }
            else
            {
                if (c == '"') inQuote = true;
                else if (char.IsWhiteSpace(c))
                {
                    if (cur.Length > 0) { result.Add(cur.ToString()); cur.Clear(); }
                }
                else cur.Append(c);
            }
        }
        if (cur.Length > 0) result.Add(cur.ToString());
        return result;
    }

    /// <summary>저장 직전 빈 키/빈 값·null 항목을 제거해 config 노이즈를 줄인다.</summary>
    public void Sanitize()
    {
        if (Type == McpServerType.Local)
        {
            Url = "";
            Headers = new Dictionary<string, string>();
            OAuthMode = null;
            OAuthClientId = null; OAuthClientSecret = null; OAuthScope = null;
        }
        else
        {
            Command = new List<string>();
            Environment = new Dictionary<string, string>();
        }

        Environment = Environment
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value != null)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        Headers = Headers
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value != null)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        if (OAuthMode != McpOAuthMode.Explicit)
        {
            OAuthClientId = null; OAuthClientSecret = null; OAuthScope = null;
        }
    }

    public McpServer Clone() => new()
    {
        Name = Name,
        Type = Type,
        Command = new List<string>(Command),
        Environment = new Dictionary<string, string>(Environment),
        Url = Url,
        Headers = new Dictionary<string, string>(Headers),
        OAuthMode = OAuthMode,
        OAuthClientId = OAuthClientId,
        OAuthClientSecret = OAuthClientSecret,
        OAuthScope = OAuthScope,
        Enabled = Enabled,
        TimeoutMs = TimeoutMs,
        Status = Status,
        StatusMessage = StatusMessage,
    };
}

public enum McpServerType { Local, Remote }

public enum McpOAuthMode
{
    /// <summary>JSON 에 "oauth": false 로 기록 (OAuth 비활성).</summary>
    Disabled,
    /// <summary>JSON 에 키 자체를 생략 (자동 — 헤더에 clientId 없으면 동적 등록 시도).</summary>
    Auto,
    /// <summary>JSON 에 McpOAuthConfig 객체로 기록.</summary>
    Explicit,
}

/// <summary><c>opencode mcp list</c> 가 알려주는 실시간 연결 상태.
/// config 파일에는 없으며, 매번 CLI 를 호출해 갱신한다.</summary>
public enum McpLiveStatus
{
    Unknown,        // 아직 조회 안 함
    Connected,      // 초록
    Disabled,       // 회색
    NeedsAuth,      // 노랑 (OAuth 필요)
    Failed,         // 빨강
    NeedsClientRegistration, // 주황 (동적 클라이언트 등록 필요)
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using DevezCode.Services.Terminal;

namespace DevezCode.Services;

/// <summary>세션 대화를 사람이 읽는 마크다운으로 내보낸다(옵션 A: user/assistant 텍스트만 —
/// 툴 호출·결과·thinking·시스템 메시지·이미지는 제외). claude/opencode/gajae 각 저장 포맷을 파싱.
/// 활성 세션이면 파일이 잠겨 있어 FileShare.ReadWrite 로 읽는다.</summary>
public static class SessionExporter
{
    /// <summary>세션 마크다운 생성. 대화가 없거나 미지원이면 null. (opencode 는 CLI export 를 스폰하므로
    /// 호출부는 백그라운드 스레드에서 부르는 게 좋다.)</summary>
    public static string? BuildMarkdown(string roomId, string agentId, string sessionName, string? cwd)
    {
        if (agentId == "grok")
            return ExportGrokMarkdown(roomId);

        var turns = agentId switch
        {
            "claude"   => FromClaude(roomId, cwd),
            "opencode" => FromOpenCode(roomId),
            "gajae"    => FromGajae(roomId),
            "codex"    => FromCodex(roomId),
            "kimi"     => FromKimi(roomId),
            _          => new List<(string role, string text)>(),
        };
        if (turns.Count == 0) return null;

        var label = AgentLabel(agentId);
        var sb = new StringBuilder();
        sb.Append("# ").Append(string.IsNullOrWhiteSpace(sessionName) ? "세션" : sessionName)
          .Append("  ·  ").Append(label).Append('\n');
        sb.Append("_내보낸 시각: ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append("_\n\n");
        foreach (var (role, text) in turns)
        {
            var t = text.Trim();
            if (t.Length == 0) continue;
            sb.Append(role == "user" ? "### 🧑 나\n\n" : $"### 🤖 {label}\n\n");
            sb.Append(t).Append("\n\n");
        }
        return sb.ToString();
    }

    private static string AgentLabel(string a) => a switch
    {
        "claude" => "Claude", "opencode" => "OpenCode", "gajae" => "가재코드",
        "codex" => "Codex", "grok" => "Grok", "kimi" => "Kimi", _ => a,
    };

    // ── grok: 최신 CLI는 transcript를 SQLite에 저장하므로 공식 export 명령을 사용 ──
    private static string? ExportGrokMarkdown(string roomId)
    {
        var sid = SettingsService.LoadGrokRoomSession(roomId);
        if (string.IsNullOrWhiteSpace(sid)) return null;

        var agent = AgentRegistry.Find("grok");
        var executablePath = agent == null ? null : AgentRegistry.ResolvePath(agent);
        if (string.IsNullOrWhiteSpace(executablePath)) return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("export");
            psi.ArgumentList.Add(sid);

            using var process = Process.Start(psi);
            if (process == null) return null;
            var output = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, __) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(20000))
            {
                try { process.Kill(true); } catch { }
                return null;
            }
            var markdown = output.ToString().Trim();
            return markdown.Length == 0 ? null : markdown + "\n";
        }
        catch { return null; }
    }

    // ── codex: ~/.codex/sessions/**/rollout-*-<sid>.jsonl (type=response_item, payload.type=message,
    //    role=user|assistant, content=[{type:input_text|output_text, text}]). role=developer(시스템) 제외. ──
    private static List<(string role, string text)> FromCodex(string roomId)
    {
        var turns = new List<(string, string)>();
        var sid = SettingsService.LoadCodexRoomSession(roomId);
        var path = TerminalSessionManager.FindCodexTranscriptPath(sid);
        if (path == null) return turns;
        foreach (var line in ReadLinesShared(path))
        {
            try
            {
                using var d = JsonDocument.Parse(line);
                var o = d.RootElement;
                if (!TryStr(o, "type", out var t) || t != "response_item") continue;
                if (!o.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object) continue;
                if (!TryStr(p, "type", out var pt) || pt != "message") continue;
                if (!TryStr(p, "role", out var role) || (role != "user" && role != "assistant")) continue;
                if (!p.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
                var sb = new StringBuilder();
                foreach (var c in content.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.Object && TryStr(c, "type", out var ct)
                        && (ct == "input_text" || ct == "output_text") && TryStr(c, "text", out var txt))
                        sb.Append(txt).Append('\n');
                var text = sb.ToString().Trim();
                if (text.Length == 0) continue;
                // codex 가 첫 user 턴에 주입하는 컨텍스트(AGENTS.md·환경·지침)는 대화가 아니므로 제외.
                if (role == "user" && IsInjectedCodexContext(text)) continue;
                turns.Add((role, text));
            }
            catch { }
        }
        return turns;
    }

    // ── kimi(kimi-code): <sessionDir>/agents/main/wire.jsonl. 레코드 type=context.append_message /
    //    context.append_loop_event 등에 message={role, content:[{type:"text",text}]} 가 담긴다.
    //    fold 로직(reduceWireRecords)을 복제하지 않고, role=user|assistant 메시지를 재귀 수집 후 연속 중복만 제거. ──
    private static List<(string role, string text)> FromKimi(string roomId)
    {
        var turns = new List<(string, string)>();
        var sid = SettingsService.LoadKimiRoomSession(roomId) ?? KimiHookService.LoadTrackedSessionId(roomId);
        var path = TerminalSessionManager.FindKimiWirePath(sid);
        if (path == null) return turns;

        var collected = new List<(string, string)>();
        foreach (var line in ReadLinesShared(path))
        {
            try
            {
                using var d = JsonDocument.Parse(line);
                CollectKimiMessages(d.RootElement, collected);
            }
            catch { }
        }
        // 연속 중복 제거(fold 재생/재개로 같은 메시지가 반복될 수 있음).
        foreach (var t in collected)
            if (turns.Count == 0 || turns[^1] != t) turns.Add(t);
        return turns;
    }

    private static void CollectKimiMessages(JsonElement el, List<(string role, string text)> acc)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryStr(el, "role", out var role) && (role == "user" || role == "assistant")
                    && el.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    var text = ExtractKimiContentText(content);
                    if (!string.IsNullOrWhiteSpace(text)) acc.Add((role, text));
                    return; // 메시지 내부는 더 파고들지 않음
                }
                foreach (var prop in el.EnumerateObject()) CollectKimiMessages(prop.Value, acc);
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) CollectKimiMessages(item, acc);
                break;
        }
    }

    private static string ExtractKimiContentText(JsonElement content)
    {
        var sb = new StringBuilder();
        foreach (var b in content.EnumerateArray())
            if (b.ValueKind == JsonValueKind.Object && TryStr(b, "type", out var bt) && bt == "text"
                && TryStr(b, "text", out var txt))
                sb.Append(txt).Append('\n');
        return sb.ToString().Trim();
    }

    private static bool IsInjectedCodexContext(string text) =>
        text.StartsWith("# AGENTS.md", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("<environment_context", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("<permissions", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("<user_instructions", StringComparison.OrdinalIgnoreCase);

    // ── claude: %USERPROFILE%\.claude\projects\<enc>\<sid>.jsonl (type=user/assistant, content=str|[text]) ──
    private static List<(string role, string text)> FromClaude(string roomId, string? cwd)
    {
        var turns = new List<(string, string)>();
        var sid = SettingsService.LoadClaudeCodeRoomSession(roomId);
        var path = TerminalSessionManager.FindClaudeTranscriptPath(cwd, sid);
        if (path == null) return turns;
        foreach (var line in ReadLinesShared(path))
        {
            var turn = ParseTurn(line, requireType: true, typeIsMessageMarker: false);
            if (turn != null) turns.Add(turn.Value);
        }
        return turns;
    }

    // ── gajae(gjc): 방 dir 최신 jsonl (type=="message" + role, content=[text]; model_change 등 메타 제외) ──
    private static List<(string role, string text)> FromGajae(string roomId)
    {
        var turns = new List<(string, string)>();
        var path = TerminalSessionManager.FindLatestGajaeTranscriptPath(roomId);
        if (path == null) return turns;
        foreach (var line in ReadLinesShared(path))
        {
            var turn = ParseTurn(line, requireType: true, typeIsMessageMarker: true);
            if (turn != null) turns.Add(turn.Value);
        }
        return turns;
    }

    /// <summary>jsonl 한 줄에서 (role, text) 추출. claude/gjc 공용.
    /// typeIsMessageMarker=false(claude): type 이 곧 role(user/assistant).
    /// true(gjc): type=="message" 인 줄만, role 은 message.role/role 필드에서.</summary>
    private static (string role, string text)? ParseTurn(string line, bool requireType, bool typeIsMessageMarker)
    {
        try
        {
            using var d = JsonDocument.Parse(line);
            var o = d.RootElement;
            if (!TryStr(o, "type", out var type)) { if (requireType) return null; type = ""; }

            string role;
            if (typeIsMessageMarker)
            {
                if (type != "message") return null;
                var m0 = o.TryGetProperty("message", out var mm) && mm.ValueKind == JsonValueKind.Object ? mm : o;
                if (!TryStr(m0, "role", out role)) TryStr(o, "role", out role);
            }
            else
            {
                role = type; // claude: type == role
            }
            if (role != "user" && role != "assistant") return null;

            var msg = o.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object ? m : o;
            var text = ExtractContentText(msg);
            return string.IsNullOrWhiteSpace(text) ? null : (role, text);
        }
        catch { return null; }
    }

    // ── opencode: `opencode export <sid>` → JSON {messages:[{info:{role}, parts:[{type,text}]}]} ──
    private static List<(string role, string text)> FromOpenCode(string roomId)
    {
        var turns = new List<(string, string)>();
        var sid = SettingsService.LoadOpenCodeRoomSession(roomId);
        if (string.IsNullOrWhiteSpace(sid)) return turns;
        var json = RunCapture("opencode", "export " + sid);
        if (string.IsNullOrWhiteSpace(json)) return turns;
        try
        {
            using var d = JsonDocument.Parse(json);
            if (!d.RootElement.TryGetProperty("messages", out var msgs) || msgs.ValueKind != JsonValueKind.Array)
                return turns;
            foreach (var msg in msgs.EnumerateArray())
            {
                var info = msg.TryGetProperty("info", out var inf) && inf.ValueKind == JsonValueKind.Object ? inf : msg;
                if (!TryStr(info, "role", out var role) || (role != "user" && role != "assistant")) continue;
                if (!msg.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array) continue;
                var sb = new StringBuilder();
                foreach (var p in parts.EnumerateArray())
                    if (TryStr(p, "type", out var pt) && pt == "text" && TryStr(p, "text", out var txt))
                        sb.Append(txt).Append('\n');
                var text = sb.ToString().Trim();
                if (text.Length > 0) turns.Add((role, text));
            }
        }
        catch { }
        return turns;
    }

    /// <summary>content(문자열 or [{type,text},…] 배열)에서 text 블록만 이어붙인다(tool_use/tool_result/thinking/image 제외).</summary>
    private static string ExtractContentText(JsonElement msg)
    {
        if (msg.ValueKind != JsonValueKind.Object || !msg.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var b in c.EnumerateArray())
                if (b.ValueKind == JsonValueKind.Object && TryStr(b, "type", out var bt) && bt == "text"
                    && TryStr(b, "text", out var txt))
                    sb.Append(txt).Append('\n');
            return sb.ToString().Trim();
        }
        return "";
    }

    private static bool TryStr(JsonElement o, string prop, out string val)
    {
        val = "";
        if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty(prop, out var v)
            && v.ValueKind == JsonValueKind.String) { val = v.GetString() ?? ""; return true; }
        return false;
    }

    private static IEnumerable<string> ReadLinesShared(string path)
    {
        // 활성 세션이면 에이전트 프로세스가 파일을 열어둔 상태 → FileShare.ReadWrite 로 읽어야 한다(포크와 동일 이슈).
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        string? line;
        while ((line = sr.ReadLine()) != null)
            if (line.Length > 0) yield return line;
    }

    /// <summary>cmd /k 없이 실행(shim/.cmd 해석 위해 cmd /c 경유) 후 stdout 캡처. 타임아웃 시 kill.</summary>
    private static string? RunCapture(string exe, string args, int timeoutMs = 20000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {exe} {args}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var sb = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) sb.Append(e.Data).Append('\n'); };
            p.ErrorDataReceived += (_, __) => { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return null; }
            return sb.ToString();
        }
        catch { return null; }
    }
}

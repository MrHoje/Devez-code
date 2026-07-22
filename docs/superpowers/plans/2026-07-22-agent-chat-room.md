# 에이전트 채팅방 GUI (claude, 빌드1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** DevezCode 안에 claude 헤드리스(stream-json) 상주 프로세스를 감싸는 채팅 메신저 뷰를 추가한다(터미널 숨김, A+ 표시, 구독 인증, `--permission-mode auto` 전용).

**Architecture:** 방(SessionItem)에 `IsChatRoom` 플래그를 추가해 기존 세션 인프라(사이드바·세션매니저·저장)를 그대로 타고, 활성화 시에만 터미널 대신 `ChatRoomView`(WebView2 채팅 HTML)로 라우팅한다. 백엔드는 `ClaudeChatProcess`(Process + stdin/stdout stream-json) → `ClaudeStreamAdapter`(JSONL→ChatEvent) → JS 브릿지.

**Tech Stack:** .NET 9 WPF, WebView2(가상호스트 `chat.devezcode.local`), claude CLI 2.1.217 stream-json (스파이크 검증됨: `docs/superpowers/specs/2026-07-22-agent-chat-room-design.md`).

## Global Constraints

- **빌드·재시작은 프로젝트 CLAUDE.md 절차 준수** — DevezCode 프로세스 강제 종료 금지, 본 세션이 DevezCode 내부인지 확인, 정상 종료 대기 후 `dotnet build -c Release`.
- 스폰 env에서 반드시 제거: `ANTHROPIC_API_KEY`(구독 강제·과금 방지), `CLAUDECODE`, `CLAUDE_CODE_CHILD_SESSION`, `CLAUDE_CODE_ENTRYPOINT`, `CLAUDE_CODE_SESSION_ID`, `CLAUDE_CODE_SSE_PORT`(transcript 영속·resume).
- 권한 모드는 `--permission-mode auto` 고정. 승인버튼·codex·이미지 첨부는 범위 밖(스펙 §12).
- 테스트 프로젝트 없음 → 검증 = Release 빌드 성공 + 실앱 스모크(핵심 로직 위주 경량 검증).
- UI는 `AppStyles.xaml` 전역 스타일, `.knowledge/텍스트렌더링규칙.md`·`.knowledge/컨트롤추가규칙.md` 준수(커서 Arrow).
- 각 태스크 끝 = 빌드 성공 확인 후 commit(푸시는 마지막에).

---

### Task 1: ChatEvent 모델 + ClaudeStreamAdapter (순수 파싱)

**Files:**
- Create: `Services/Chat/ChatEvent.cs`
- Create: `Services/Chat/ClaudeStreamAdapter.cs`

**Interfaces (Produces):**
- `ChatEvent` — `Kind`(enum `ChatEventKind`: `SessionInit, AssistantTextDelta, AssistantText, ToolUse, ToolResult, RateLimit, Result, ParseError`), `SessionId`, `Text`, `ToolName`, `ToolUseId`, `ToolInputJson`, `IsError`, `ResultUsageJson`, `TotalCostUsd`
- `ClaudeStreamAdapter.Parse(string jsonlLine) : ChatEvent?` — 관심 없는 라인은 null(hook_* 등)

- [ ] **Step 1: ChatEvent 작성**

```csharp
namespace DevezCode.Services.Chat;

public enum ChatEventKind
{
    SessionInit, AssistantTextDelta, AssistantText, ToolUse, ToolResult,
    RateLimit, Result, ParseError
}

/// <summary>claude stream-json 한 줄을 UI가 그릴 수 있는 최소 단위로 정규화한 이벤트.</summary>
public sealed class ChatEvent
{
    public ChatEventKind Kind { get; init; }
    public string? SessionId { get; init; }
    public string? Text { get; init; }          // 텍스트 델타/전문, result 요약
    public string? ToolName { get; init; }      // tool_use
    public string? ToolUseId { get; init; }     // tool_use ↔ tool_result 매칭
    public string? ToolInputJson { get; init; } // tool_use input 원본(JSON)
    public bool IsError { get; init; }          // tool_result 실패
    public string? ResultUsageJson { get; init; }
    public double? TotalCostUsd { get; init; }
}
```

- [ ] **Step 2: ClaudeStreamAdapter 작성**

스파이크에서 관측된 실제 이벤트(스펙 §4) 기준. `System.Text.Json` 사용.

```csharp
using System.Text.Json;

namespace DevezCode.Services.Chat;

/// <summary>claude --output-format stream-json JSONL 라인 → ChatEvent 정규화.
/// 관심 없는 라인(system/hook_* 등)은 null. 파싱 실패는 ParseError 로 보고(라인 유실 진단용).</summary>
public static class ClaudeStreamAdapter
{
    public static ChatEvent? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return new ChatEvent { Kind = ChatEventKind.ParseError, Text = Trunc(line, 200) }; }

        using (doc)
        {
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            var sid = root.TryGetProperty("session_id", out var s) ? s.GetString() : null;

            switch (type)
            {
                case "system":
                    var sub = root.TryGetProperty("subtype", out var st) ? st.GetString() : null;
                    return sub == "init" ? new ChatEvent { Kind = ChatEventKind.SessionInit, SessionId = sid } : null;

                case "stream_event": // 실시간 텍스트 델타만 추출(타이핑 효과)
                    if (root.TryGetProperty("event", out var ev)
                        && ev.TryGetProperty("type", out var et) && et.GetString() == "content_block_delta"
                        && ev.TryGetProperty("delta", out var d)
                        && d.TryGetProperty("text", out var dt))
                        return new ChatEvent { Kind = ChatEventKind.AssistantTextDelta, SessionId = sid, Text = dt.GetString() };
                    return null;

                case "assistant": // 확정 블록: text(델타 최종본 겸 폴백) / tool_use
                    if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content))
                        return null;
                    foreach (var block in content.EnumerateArray())
                    {
                        var bt = block.TryGetProperty("type", out var b) ? b.GetString() : null;
                        if (bt == "tool_use")
                            return new ChatEvent
                            {
                                Kind = ChatEventKind.ToolUse, SessionId = sid,
                                ToolName = block.GetProperty("name").GetString(),
                                ToolUseId = block.GetProperty("id").GetString(),
                                ToolInputJson = block.GetProperty("input").GetRawText(),
                            };
                        if (bt == "text")
                            return new ChatEvent { Kind = ChatEventKind.AssistantText, SessionId = sid, Text = block.GetProperty("text").GetString() };
                    }
                    return null;

                case "user": // tool_result 회신
                    if (root.TryGetProperty("message", out var um) && um.TryGetProperty("content", out var uc)
                        && uc.ValueKind == JsonValueKind.Array)
                        foreach (var block in uc.EnumerateArray())
                            if (block.TryGetProperty("type", out var ub) && ub.GetString() == "tool_result")
                                return new ChatEvent
                                {
                                    Kind = ChatEventKind.ToolResult, SessionId = sid,
                                    ToolUseId = block.TryGetProperty("tool_use_id", out var tid) ? tid.GetString() : null,
                                    IsError = block.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True,
                                    Text = block.TryGetProperty("content", out var tc) ? Trunc(tc.ToString(), 4000) : null,
                                };
                    return null;

                case "rate_limit_event":
                    return new ChatEvent { Kind = ChatEventKind.RateLimit, SessionId = sid,
                        Text = root.TryGetProperty("rate_limit_info", out var rl) ? rl.GetRawText() : null };

                case "result":
                    return new ChatEvent
                    {
                        Kind = ChatEventKind.Result, SessionId = sid,
                        Text = root.TryGetProperty("result", out var r) ? r.GetString() : null,
                        IsError = root.TryGetProperty("is_error", out var re) && re.ValueKind == JsonValueKind.True,
                        ResultUsageJson = root.TryGetProperty("usage", out var u) ? u.GetRawText() : null,
                        TotalCostUsd = root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null,
                    };

                default: return null;
            }
        }
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];
}
```

- [ ] **Step 3: 빌드 확인 + 커밋**

```powershell
dotnet build -c Release --nologo -v quiet
```
Expected: 성공.
```bash
git add Services/Chat && git commit -m "feat: 채팅방 ChatEvent 모델 + claude stream-json 어댑터"
```

---

### Task 2: ClaudeChatProcess (상주 프로세스 래퍼)

**Files:**
- Create: `Services/Chat/ClaudeChatProcess.cs`

**Interfaces:**
- Consumes: `ClaudeStreamAdapter.Parse`, `AgentRegistry.Find("claude")`/`ResolvePath`
- Produces: `ClaudeChatProcess(string workingDir, string? resumeSessionId)`,
  `event Action<ChatEvent> EventReceived`(백그라운드 스레드), `event Action<int> Exited`,
  `bool SendUserMessage(string text)`, `void Stop()`(프로세스 종료), `bool IsAlive`, `string? SessionId`

- [ ] **Step 1: 구현**

핵심 규칙: env 위생(Global Constraints), UTF-8 고정, `.cmd` 셔밍 대응, 읽기 스레드에서 라인 단위 파싱.

```csharp
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DevezCode.Services.Chat;

/// <summary>claude 헤드리스 상주 프로세스(stream-json 양방향). 채팅방당 1개.
/// 구독 인증 사용을 위해 API 키 env 를 제거하고, 중첩세션 마커도 제거한다(transcript 영속·resume).</summary>
public sealed class ClaudeChatProcess : IDisposable
{
    public event Action<ChatEvent>? EventReceived; // 백그라운드 스레드에서 발생
    public event Action<int>? Exited;

    public bool IsAlive => _proc is { HasExited: false };
    public string? SessionId { get; private set; }

    private Process? _proc;
    private readonly object _writeLock = new();

    public ClaudeChatProcess(string workingDir, string? resumeSessionId)
    {
        var agent = Services.AgentRegistry.Find("claude") ?? Services.AgentRegistry.GetDefault();
        var exe = Services.AgentRegistry.ResolvePath(agent) ?? agent.Command;

        var args = "-p --input-format stream-json --output-format stream-json --verbose --permission-mode auto";
        if (!string.IsNullOrEmpty(resumeSessionId)) args += $" --resume {resumeSessionId}";

        var psi = new ProcessStartInfo
        {
            WorkingDirectory = Directory.Exists(workingDir) ? workingDir : Environment.CurrentDirectory,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        // claude 가 PATH 상 .cmd/.ps1 셔밍이면 직접 실행 불가 → cmd /c 로 감싼다.
        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        { psi.FileName = "cmd.exe"; psi.Arguments = $"/c \"\"{exe}\" {args}\""; }
        else { psi.FileName = exe; psi.Arguments = args; }

        // env 위생 — API 과금 방지 + 중첩세션 판정 방지(스펙 §7)
        foreach (var k in new[] { "ANTHROPIC_API_KEY", "CLAUDECODE", "CLAUDE_CODE_CHILD_SESSION",
                 "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_SSE_PORT" })
            psi.Environment.Remove(k);

        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.Exited += (_, _) => Exited?.Invoke(_proc?.ExitCode ?? -1);
        _proc.Start();
        StartReadLoop(_proc.StandardOutput);
        StartDrain(_proc.StandardError); // stderr 버퍼 막힘 방지 + diag
    }

    private void StartReadLoop(StreamReader stdout)
    {
        new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = stdout.ReadLine()) != null)
                {
                    var ev = ClaudeStreamAdapter.Parse(line);
                    if (ev == null) continue;
                    if (ev.Kind == ChatEventKind.SessionInit && ev.SessionId != null) SessionId = ev.SessionId;
                    EventReceived?.Invoke(ev);
                }
            }
            catch (Exception) { /* 파이프 닫힘 = 종료 경로 */ }
        }) { IsBackground = true, Name = "ChatProc-Read" }.Start();
    }

    private static void StartDrain(StreamReader stderr)
    {
        new Thread(() =>
        {
            try { string? l; while ((l = stderr.ReadLine()) != null) DevezCode.Services.DiagLog.Write($"chat stderr: {l}"); }
            catch (Exception) { }
        }) { IsBackground = true, Name = "ChatProc-Err" }.Start();
    }

    public bool SendUserMessage(string text)
    {
        if (!IsAlive) return false;
        var json = JsonSerializer.Serialize(new
        { type = "user", message = new { role = "user", content = text } });
        lock (_writeLock)
        {
            try { _proc!.StandardInput.WriteLine(json); _proc.StandardInput.Flush(); return true; }
            catch (Exception) { return false; }
        }
    }

    /// <summary>턴 취소/방 닫기 — 프로세스 종료. 다음 전송 시 --resume 으로 새로 뜬다.</summary>
    public void Stop()
    {
        try { if (IsAlive) _proc!.Kill(entireProcessTree: true); } catch (Exception) { }
    }

    public void Dispose() { Stop(); _proc?.Dispose(); _proc = null; }
}
```

주의: `DiagLog` 시그니처는 기존 사용처(`TerminalSession.cs:98`) 참고해 맞춘다.

- [ ] **Step 2: 빌드 확인 + 커밋**

```powershell
dotnet build -c Release --nologo -v quiet
```
```bash
git add Services/Chat/ClaudeChatProcess.cs && git commit -m "feat: claude 헤드리스 상주 프로세스 래퍼(env 위생·stream-json)"
```

---

### Task 3: ChatSessionManager (방↔프로세스 + 세션id 영속)

**Files:**
- Create: `Services/Chat/ChatSessionManager.cs`

**Interfaces:**
- Consumes: `ClaudeChatProcess`, `SettingsService.LoadClaudeCodeRoomSession(roomId)` / `SaveClaudeCodeRoomSession(roomId, sessionId)` (`Services/SettingsService.cs:562,565`), `SettingsService.LoadClaudeCodeRoomDir(roomId)`
- Produces: `static ChatSessionManager.GetOrCreate(string roomId) : ClaudeChatProcess`,
  `Get(roomId)`, `CloseRoom(roomId)`, `DisposeAll()`

- [ ] **Step 1: 구현**

```csharp
namespace DevezCode.Services.Chat;

/// <summary>roomId → ClaudeChatProcess. TerminalSessionManager 와 대칭인 채팅 전용 매니저.
/// SessionInit 이벤트로 얻은 CLI session_id 를 SettingsService 에 저장해 재시작 후 --resume 한다.</summary>
public static class ChatSessionManager
{
    private static readonly Dictionary<string, ClaudeChatProcess> _procs = new();
    private static readonly object _lock = new();

    public static ClaudeChatProcess GetOrCreate(string roomId)
    {
        lock (_lock)
        {
            if (_procs.TryGetValue(roomId, out var p) && p.IsAlive) return p;
            _procs.Remove(roomId);

            var dir = Services.SettingsService.LoadClaudeCodeRoomDir(roomId)
                      ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var resume = Services.SettingsService.LoadClaudeCodeRoomSession(roomId);
            var proc = new ClaudeChatProcess(dir, resume);
            proc.EventReceived += ev =>
            {
                if (ev.Kind == ChatEventKind.SessionInit && ev.SessionId != null)
                    Services.SettingsService.SaveClaudeCodeRoomSession(roomId, ev.SessionId);
            };
            _procs[roomId] = proc;
            return proc;
        }
    }

    public static ClaudeChatProcess? Get(string roomId)
    { lock (_lock) return _procs.TryGetValue(roomId, out var p) ? p : null; }

    public static void CloseRoom(string roomId)
    { lock (_lock) { if (_procs.Remove(roomId, out var p)) p.Dispose(); } }

    public static void DisposeAll()
    { lock (_lock) { foreach (var p in _procs.Values) p.Dispose(); _procs.Clear(); } }
}
```

- [ ] **Step 2: 앱 종료 시 정리** — `App.xaml.cs`의 기존 종료 처리(OnExit 또는 MainWindow Closing에서 `TerminalSessionManager` 정리하는 지점 검색)에 `ChatSessionManager.DisposeAll();` 한 줄 추가.

- [ ] **Step 3: 빌드 확인 + 커밋**

```bash
git add Services/Chat/ChatSessionManager.cs App.xaml.cs && git commit -m "feat: 채팅방 프로세스 매니저 + 세션id 영속"
```

---

### Task 4: 채팅 웹 UI (chat.html / chat.css / chat.js)

**Files:**
- Create: `Resources/Chat/web/chat.html`, `Resources/Chat/web/chat.css`, `Resources/Chat/web/chat.js`
- Modify: `DevezCode.csproj` — Content 항목 추가(기존 L43 `Resources\Markdown\web\**` 패턴):

```xml
<Content Include="Resources\Chat\web\**">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</Content>
```

**Interfaces (C#↔JS 브릿지 프로토콜):**
- C#→JS (`PostWebMessageAsJson`): `{type:"userEcho", text}` / `{type:"delta", text}` / `{type:"toolUse", id, name, summary, detail}` / `{type:"toolResult", id, isError}` / `{type:"turnDone", usage, cost}` / `{type:"busy", on}` / `{type:"theme", dark}` / `{type:"history", items:[...]}`
- JS→C# (`window.chrome.webview.postMessage`): `{type:"pageReady"}` / `{type:"send", text}` / `{type:"stop"}`

- [ ] **Step 1: chat.html 골격**

```html
<!DOCTYPE html>
<html><head><meta charset="utf-8">
<link rel="stylesheet" href="chat.css">
</head>
<body>
  <div id="scroll"><div id="messages"></div></div>
  <div id="composer">
    <textarea id="input" rows="1" placeholder="메시지 입력… (Enter=전송, Shift+Enter=줄바꿈)"></textarea>
    <button id="stopBtn" class="hidden" title="중지">■</button>
    <button id="sendBtn" title="전송">➤</button>
  </div>
  <script src="chat.js"></script>
</body></html>
```

- [ ] **Step 2: chat.js 핵심 로직**

요구사항(코드는 구현 시 상세화하되 아래 동작은 필수):
- `postMessage({type:"pageReady"})` 를 load 시 1회.
- `delta` 수신 → 현재 열린 AI 말풍선에 텍스트 append(없으면 새 말풍선). 스트리밍 중 자동 스크롤(사용자가 위로 스크롤 중이면 고정 해제).
- `toolUse` → 접이식 카드 `<details><summary>🔧 {name} {summary}</summary><pre>{detail}</pre></details>`. `id` 를 data-attr 로 보관.
- `toolResult` → 해당 카드 summary 에 ✅/❌ 배지.
- `turnDone` → 진행 말풍선 닫기 + usage 뱃지, `busy` off.
- 전송: Enter(조합 중 아닐 때)=send, Shift+Enter=줄바꿈. IME `isComposing` 체크 필수(한글).
- AI 말풍선 텍스트는 turnDone 시점에 marked 없이 **경량 마크다운**(코드펜스·굵게·인라인코드만 정규식 처리) — 외부 라이브러리 추가 금지(오프라인 번들 원칙).
- 다크/라이트: `theme` 메시지로 body class 토글, 색은 CSS 변수.

- [ ] **Step 3: chat.css** — 말풍선(user=오른쪽 강조색, AI=왼쪽 표면색), 카드, 컴포저. 색상 값은 `C:\source\devez` 디자인 시스템 팔레트 준수(AppStyles.xaml 의 Primary/Surface/Line 브러시 값을 CSS 변수로 미러).

- [ ] **Step 4: 빌드 확인(Content 복사) + 커밋**

```powershell
dotnet build -c Release --nologo -v quiet
Test-Path "bin\Resources\Chat\web\chat.html"   # True 여야 함
```
```bash
git add Resources/Chat DevezCode.csproj && git commit -m "feat: 채팅방 웹 UI(chat.html/js/css) + csproj Content"
```

---

### Task 5: ChatRoomView (WebView2 호스트 + 브릿지)

**Files:**
- Create: `Views/ChatRoomView.cs` (XAML 없이 코드 전용 — `MarkdownWysiwygHost.cs` 를 템플릿으로)

**Interfaces:**
- Consumes: `ChatSessionManager.GetOrCreate/Get/CloseRoom`, `ChatEvent`, `MarkdownWysiwygHost.cs` 의 WebView2 패턴(SharedEnvironment L41, 가상호스트 매핑 L80-82, `?v=ticks` L87-89, WebMessageReceived L84)
- Produces: `ChatRoomView(string roomId)` : `Border`(또는 `ContentControl`) — WorkspacePaneView 가 컨테이너에 부착. `void FocusInput()`, `void DisposeView()`

- [ ] **Step 1: 구현 요점**

`MarkdownWysiwygHost.cs` 초기화 흐름 복제 후 차이만:
- 가상호스트: `const string VirtualHost = "chat.devezcode.local"`, webRoot = `Resources/Chat/web`, `chat.html` 탐색.
- `CoreWebView2Environment` 는 **MarkdownWysiwygHost.SharedEnvironment 재사용**(중복 환경 생성 금지).
- `WebMessageReceived`:
  - `pageReady` → 저장된 히스토리 렌더(빌드1: 생략 가능, §비고) + `theme` 전송 + 포커스.
  - `send` → `ChatSessionManager.GetOrCreate(roomId)` 에 이벤트 핸들러 최초 1회 배선 후 `SendUserMessage(text)`. 성공 시 JS에 `userEcho`, `busy:true`.
  - `stop` → `Get(roomId)?.Stop()` + `busy:false`.
- `ClaudeChatProcess.EventReceived` → **Dispatcher 마샬** 후 매핑:
  - `AssistantTextDelta` → `delta`
  - `ToolUse` → `toolUse` (summary: `ToolInputJson` 에서 `file_path` 나 `command` 필드 추출해 짧게; detail: `ToolInputJson` pretty-print)
  - `ToolResult` → `toolResult`
  - `Result` → `turnDone` (usage: `ResultUsageJson` 에서 input/output 토큰, cost: `TotalCostUsd`)
  - `RateLimit` 중 `"status":"rejected"` 포함 시 시스템 말풍선으로 한도 안내
  - 프로세스 `Exited` (busy 중) → 시스템 말풍선 "프로세스 종료됨 — 다시 보내면 이어집니다" + `busy:false`
- 이벤트 핸들러 중복 배선 방지: 방당 현재 프로세스 참조를 필드로 두고 프로세스가 바뀔 때만 재배선.

- [ ] **Step 2: 빌드 확인 + 커밋**

```bash
git add Views/ChatRoomView.cs && git commit -m "feat: ChatRoomView — WebView2 채팅 호스트 + 이벤트 브릿지"
```

---

### Task 6: 모델·영속·생성 플로우

**Files:**
- Modify: `Models/WorkspaceModels.cs` — `SessionItem`(L51)에 플래그+뷰 홀더 추가
- Modify: `Services/WorkspaceStore.cs` — `SessionDto`(L11)에 `Chat` 필드, `ToDto`(L219-229)·로드(L166-167) 반영
- Modify: `Views/AgentPickerDialog.xaml(.cs)` — claude 선택 시 "채팅 UI로 열기(베타)" 체크박스
- Modify: `Views/WorkspacePaneView.xaml.cs` — `AddSession`(L909) 채팅 선택 반영

- [ ] **Step 1: SessionItem 확장**

```csharp
// SessionItem 내부에 추가
/// <summary>채팅 UI 방(true)이면 터미널 대신 ChatRoomView 로 연다. claude 전용(빌드1).</summary>
public bool IsChatRoom { get; set; }

/// <summary>채팅방 뷰 인스턴스(FileTabItem.Editor 패턴 — 모델이 뷰를 소유, 직렬화 안 함).</summary>
public Views.ChatRoomView? ChatView { get; set; }
```

- [ ] **Step 2: WorkspaceStore 영속** — `SessionDto` record에 `bool Chat = false` 추가, `ToDto` 에서 `s.IsChatRoom` 기록, 로드 루프에서 `IsChatRoom = dto.Chat` 복원. (기존 workspace.json 과의 하위호환: 필드 없으면 기본 false — record 기본값으로 충족.)

- [ ] **Step 3: AgentPickerDialog 체크박스** — XAML 하단에 `CheckBox x:Name="ChatUiCheck"`(내용 "채팅 UI로 열기 (베타, claude 전용)") 추가, claude 항목이 하이라이트/선택될 때만 Visible. 코드는 `public bool ChatUiChecked => ChatUiCheck.IsChecked == true;` 노출, `Pick(...)` 반환은 유지하되 `out bool chatUi` 오버로드 또는 정적 `LastPickChatUi` 프로퍼티로 전달(호출부 L922 만 수정 — 다른 호출처 있는지 grep 확인 후 최소 변경).

- [ ] **Step 4: AddSession 반영** — L936 부근:

```csharp
var session = new SessionItem { Name = sessionName, AgentId = agentId, IsChatRoom = chatUi && agentId == "claude" };
```
(available.Count==1 로 피커 안 뜬 경우 chatUi=false.)

- [ ] **Step 5: 빌드 + 커밋**

```bash
git add Models/WorkspaceModels.cs Services/WorkspaceStore.cs Views/AgentPickerDialog.xaml* Views/WorkspacePaneView.xaml.cs
git commit -m "feat: 채팅방 플래그 모델·영속·생성 플로우(피커 체크박스)"
```

---

### Task 7: WorkspacePaneView 뷰 호스팅 통합

**Files:**
- Modify: `Views/WorkspacePaneView.xaml` — L485-487 형제로 `<ContentControl x:Name="ChatHostContainer" Visibility="Collapsed"/>` 추가
- Modify: `Views/WorkspacePaneView.xaml.cs` — 활성화·파킹·닫기 배선

- [ ] **Step 1: 활성화 라우팅** — 세션 활성화 진입점(`OpenSession`/`ActivateSession` ~L1090)에 최상단 분기:

```csharp
if (session.IsChatRoom) { ActivateChatRoom(session); return; }
```

`ActivateChatRoom` 은 `ActivateBrowserTab`(L1287-1311) 패턴 복제:

```csharp
private void ActivateChatRoom(SessionItem session)
{
    session.ChatView ??= new ChatRoomView(session.Id);
    var view = session.ChatView;
    if (view.Parent is ContentControl prev && !ReferenceEquals(prev, ChatHostContainer)) prev.Content = null;
    if (!ReferenceEquals(ChatHostContainer.Content, view)) ChatHostContainer.Content = view;
    _activeTab = session;
    UpdateEmptyState();
    view.FocusInput();
}
```
(탭 선택 상태·사이드바 하이라이트 등 기존 ActivateSession 이 하던 공통 처리 중 터미널 무관 부분은 그대로 수행 — 구현 시 ActivateSession 본문을 읽고 터미널 배선 직전에 분기 지점을 잡는다.)

- [ ] **Step 2: UpdateEmptyState 분기** — `UpdateEmptyState()`(L3300) 의 `is SessionItem` 분기를 채팅 인지로 수정:

```csharp
if (_activeTab is SessionItem { IsChatRoom: true })
{
    ParkTerminalHost(); ParkFileEditorHost(); ParkBrowserHost();
    ChatHostContainer.Visibility = Visibility.Visible;
}
else if (_activeTab is SessionItem)
{
    ChatHostContainer.Visibility = Visibility.Collapsed;
    // ...기존 본문 그대로...
}
else ... // 나머지 분기들에도 ChatHostContainer.Visibility = Collapsed 추가
```
(채팅뷰도 WebView2=airspace 이므로 파킹 패턴이 필요해지면 `.knowledge/webview2-airspace-패널리사이즈-깜빡임.md` 참조해 Park/Unpark 로 승격. 빌드1은 Visibility 토글로 시작.)

- [ ] **Step 3: 기타 `is SessionItem` 스위치 확인** — L241-243, L352-354, L850-851, L2248-2250, L2470-2476, L2631-2639 를 훑어 터미널 전제(예: `ShowTerminal(roomId)` 호출) 코드가 채팅방에도 타는 곳에 `!s.IsChatRoom` 가드 추가. 특히:
  - 세션 닫기/삭제 경로 → `ChatSessionManager.CloseRoom(roomId)` + `session.ChatView = null` 추가, `SessionUsageService.Remove` 는 기존 흐름 재사용.
  - 세션 재시작/포크/그레이스풀 종료(터미널 전용) → 채팅방이면 스킵 또는 CloseRoom.

- [ ] **Step 4: 빌드 + 커밋**

```bash
git add Views/WorkspacePaneView.xaml Views/WorkspacePaneView.xaml.cs
git commit -m "feat: 채팅방 뷰 호스팅 — 활성화 라우팅·파킹·정리 배선"
```

---

### Task 8: 스모크 테스트 (실앱 E2E) + 마무리

- [ ] **Step 1: CLAUDE.md 빌드·재시작 절차로 앱 기동** (본 세션이 DevezCode 내부인지 먼저 확인; 내부라면 사용자에게 재시작 요청하고 대기)

- [ ] **Step 2: 수동 스모크 시나리오**
  1. 새 세션 → 피커에서 claude + "채팅 UI" 체크 → 방 생성됨(세션 목록에 표시)
  2. "현재 디렉토리에 hello.txt 만들어줘" 전송 → user 말풍선, 델타 스트리밍, 🔧 Write 카드, 완료 후 usage 뱃지, 파일 실제 생성
  3. 후속 질문 → 같은 프로세스로 맥락 유지
  4. 앱 재시작 → 방 재진입 → 이전 대화 이어 질문(resume) 동작
  5. 작업 중 Stop → busy 해제, 재전송 시 새 프로세스+resume
  6. 방 닫기/삭제 → claude 프로세스 종료 확인(작업관리자)
  7. 터미널 방(일반 세션)·파일탭·브라우저탭 전환 시 채팅뷰 파킹 정상(잔상 없음)

- [ ] **Step 3: 커밋 + 푸시**

```bash
git push
```

---

## 비고 / 알려진 한계 (빌드1)

- **히스토리 렌더 없음**: 방 재진입 시 과거 말풍선 복원은 빌드1 생략(resume 으로 맥락은 유지). 백로그: claude transcript(.jsonl) 파싱해 복원.
- 이미지 첨부·승인버튼·codex·슬래시커맨드 UI = 스펙 §12 백로그.
- 상주 프로세스가 유휴로 오래 있으면 CLI 자체 타임아웃 가능성 → Exited 이벤트로 감지·재스폰(SendUserMessage 실패 시 GetOrCreate 재시도 한 번).

## Self-Review 결과

- 스펙 §1-§11 커버: 모델(6)·프로세스(2,3)·UI(4,5)·통합(7)·usage(5 turnDone)·env 위생(2)·resume(3)·Stop(2,5)·스모크(8). 승인버튼·codex 는 스펙에서 범위 밖.
- 타입 일관성: `ChatEvent`/`ClaudeChatProcess`/`ChatSessionManager`/`ChatRoomView` 시그니처 태스크 간 일치 확인.
- 남은 불확실성은 각 태스크에 "구현 시 확인" 으로 명시(DiagLog 시그니처, ActivateSession 내부, 피커 호출처 grep).

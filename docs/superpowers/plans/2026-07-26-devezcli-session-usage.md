# DevezCLI Session Usage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show DevezCLI input tokens, output tokens, and estimated cost in the session header with the same behavior and format as Codex.

**Architecture:** Reuse the existing Codex rollout JSONL parser because DevezCLI sessions are Codex threads stored in the same format. Parameterize the parser with the session ID and display label, then route both `codex` and `devezcli` through it; the existing header UI will activate through `IsSupported`.

**Tech Stack:** C# 13, .NET 9, WPF, JSONL via `System.Text.Json`

## Global Constraints

- Keep the header format exactly `↓입력  ↑출력  ($예상금액)`.
- Use `SettingsService.LoadDevezCliRoomSession` for the DevezCLI session ID.
- Reuse Codex token, cache, GPT pricing, formatting, tooltip, and refresh behavior.
- Hide the usage area when no usable rollout data exists.
- Do not change footer plan usage, pricing data, DevezCLI log formats, hooks, or session tracking.
- Never force-stop DevezCode. Do not build while the current Codex session is running inside DevezCode; request a normal app close first.

---

### Task 1: Route DevezCLI through the Codex usage parser

**Files:**
- Modify: `Services/SessionUsageService.cs`
- Modify: `.knowledge/토큰사용량-단가-갱신.md`

**Interfaces:**
- Consumes: `SettingsService.LoadCodexRoomSession(string roomId)`, `SettingsService.LoadDevezCliRoomSession(string roomId)`, `TerminalSessionManager.FindCodexTranscriptPath(string? sessionId)`
- Produces: `SessionUsageService.IsSupported("devezcli") == true` and `SessionUsageService.Read(roomId, "devezcli", cwd)` returning `UsageTotals` with `AgentLabel == "Devez CLI"`

- [ ] **Step 1: Record the pre-change routing check**

Run:

```powershell
rg -n 'IsSupported|ReadCodex|\"codex\" =>|\"devezcli\" =>' Services\SessionUsageService.cs
```

Expected: `IsSupported` and `Read` contain `claude`/`codex`, while `devezcli` is absent.

- [ ] **Step 2: Parameterize the Codex rollout reader**

Change the support predicate and dispatch to:

```csharp
public static bool IsSupported(string agentId) => agentId is "claude" or "codex" or "devezcli";

public static UsageTotals? Read(string roomId, string agentId, string? cwd)
{
    EnsureLoaded();
    try
    {
        return agentId switch
        {
            "claude" => ReadClaude(roomId, cwd),
            "codex" => ReadCodexLike(
                roomId,
                SettingsService.LoadCodexRoomSession(roomId),
                "Codex"),
            "devezcli" => ReadCodexLike(
                roomId,
                SettingsService.LoadDevezCliRoomSession(roomId),
                "Devez CLI"),
            _ => null,
        };
    }
    catch { return null; }
}
```

Rename and parameterize the current Codex reader:

```csharp
private static UsageTotals? ReadCodexLike(string roomId, string? sid, string agentLabel)
{
    var path = TerminalSessionManager.FindCodexTranscriptPath(sid);
    if (path == null) return LastKnown(roomId);

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
    if (last == null) return LastKnown(roomId);
    try
    {
        using var d = JsonDocument.Parse(last);
        var info = d.RootElement.GetProperty("payload").GetProperty("info").GetProperty("total_token_usage");
        long input = GetLong(info, "input_tokens");
        long cached = GetLong(info, "cached_input_tokens");
        long cw = GetLong(info, "cache_write_input_tokens");
        long output = GetLong(info, "output_tokens");
        long inNew = Math.Max(0, input - cached - cw);
        var t = new UsageTotals(inNew, cw, 0, cached, output, model ?? "gpt-5-codex", agentLabel)
        {
            Cost = CostOf(model ?? "gpt-5-codex", inNew, cw, 0, cached, output)
        };
        _cache[roomId] = new Entry { Sid = sid, LastLen = len, Totals = t, Offset = -1 };
        return t.HasData ? t : null;
    }
    catch { return null; }
}
```

Update these comments:

- File summary: Codex and DevezCLI use the shared `token_count.total_token_usage` parser.
- `IsSupported` summary: Claude, Codex, and DevezCLI are accurately supported.
- Reader section heading: `codex/devezcli: 파일 끝 마지막 token_count 이벤트 한 줄만`.

- [ ] **Step 3: Update the targeted knowledge entry**

In `.knowledge/토큰사용량-단가-갱신.md`, change the parser description from Codex-only to Codex/DevezCLI and state that both use the shared `token_count.total_token_usage` rollout parser with agent-specific session ID loaders.

- [ ] **Step 4: Run lightweight source verification**

Run:

```powershell
rg -n 'IsSupported|ReadCodexLike|LoadDevezCliRoomSession|Devez CLI' Services\SessionUsageService.cs
git diff --check -- Services\SessionUsageService.cs .knowledge\토큰사용량-단가-갱신.md
```

Expected: DevezCLI appears in support and dispatch, both agents call `ReadCodexLike`, and `git diff --check` exits successfully.

- [ ] **Step 5: Build after DevezCode is normally closed**

First check:

```powershell
Get-Process -Name DevezCode -ErrorAction SilentlyContinue
```

If the process exists, stop and ask the user to close DevezCode normally. Do not call `Stop-Process`, `taskkill`, or `Process.Kill`.

After the process is gone, run:

```powershell
dotnet build -c Release --nologo -v quiet
```

Expected: build succeeds with zero errors.

- [ ] **Step 6: Restart and perform the focused UI check**

Only after a successful build:

```powershell
Start-Process "bin\DevezCode.exe"
```

Open a DevezCLI session that has completed at least one response. Expected: the session header shows `↓…  ↑…  ($…)`; switching to Codex preserves its existing display, and a DevezCLI session without token data keeps the area hidden.

- [ ] **Step 7: Commit and push only the feature files**

```powershell
git add Services/SessionUsageService.cs .knowledge/토큰사용량-단가-갱신.md
git commit -m "feat: show DevezCLI session token usage"
git push
```

Before committing, confirm unrelated pre-existing hook and tracking changes are not staged.

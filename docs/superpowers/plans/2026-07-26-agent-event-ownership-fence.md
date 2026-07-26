# Agent Event Ownership Fence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent nested or cross-agent CLI processes from changing another DevezCode room's prompt, spinner, waiting state, tracked session, or completion history.

**Architecture:** Add a fail-closed tracking-agent marker at every DevezCode launch boundary, reject mismatched events both in producers and in `MainWindow`, and preserve one root native session per room for globally installed hooks. Keep existing provider-specific authoritative turn completion signals.

**Tech Stack:** C# 13 / .NET 9 WPF, PowerShell 5.1, Windows cmd, Node.js OpenCode plugin, Rust Devez CLI.

## Global Constraints

- Do not force-close, kill, build, publish, deploy, or restart the running DevezCode process.
- State files must continue to use temp-file plus rename atomic writes.
- A room ID without a matching `DEVEZCODE_TRACKING_AGENT` must fail closed.
- Rejected events must not change UI state, persisted session IDs, notifications, or completion history.
- Prompt bodies must not be written to rejection diagnostics.
- Existing resume, clear/new-session, fork, external-terminal, and automatic re-entry paths must retain their behavior.
- Code changes are committed and pushed only after the available non-destructive verification passes.

---

### Task 1: Central App Event Ownership Gate

**Files:**
- Create: `Services/AgentEventOwnership.cs`
- Create: `Tests/DevezCode.Tracking.Tests/DevezCode.Tracking.Tests.csproj`
- Create: `Tests/DevezCode.Tracking.Tests/Program.cs`
- Modify: `DevezCode.csproj`
- Modify: `MainWindow.xaml.cs:275-665`
- Modify: `Services/AgentLastMessageService.cs:41,270-278`

**Interfaces:**
- Produces: `AgentEventOwnership.IsMatch(string? sessionAgentId, string sourceAgentId) : bool`
- Produces: `MainWindow.FindOwnedSession(string roomId, string sourceAgentId, string eventName) : SessionItem?`
- Changes: `AgentLastMessageService.LastPromptChanged` to `Action<string, string, string>` carrying `(workingDir, agentId, message)`.

- [ ] **Step 1: Write the failing ownership tests**

`Program.cs` must assert:

```csharp
Check(AgentEventOwnership.IsMatch("claude", "claude"), "same agent");
Check(AgentEventOwnership.IsMatch("CoDeX", "codex"), "case-insensitive");
Check(!AgentEventOwnership.IsMatch("claude", "codex"), "cross-agent");
Check(!AgentEventOwnership.IsMatch("", "codex"), "missing owner fails closed");
Check(!AgentEventOwnership.IsMatch("codex", ""), "missing source fails closed");
```

The test project links `../../Services/AgentEventOwnership.cs` and does not reference the WPF project. `DevezCode.csproj` excludes `Tests/**/*.cs` from its default compile glob.

- [ ] **Step 2: Run the test and confirm RED**

Run:

```powershell
dotnet run --project Tests\DevezCode.Tracking.Tests\DevezCode.Tracking.Tests.csproj
```

Expected: compile failure because `Services/AgentEventOwnership.cs` does not exist.

- [ ] **Step 3: Implement the pure ownership predicate**

```csharp
namespace DevezCode.Services;

internal static class AgentEventOwnership
{
    internal static bool IsMatch(string? sessionAgentId, string sourceAgentId)
        => !string.IsNullOrWhiteSpace(sessionAgentId)
           && !string.IsNullOrWhiteSpace(sourceAgentId)
           && string.Equals(sessionAgentId, sourceAgentId, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 4: Run the focused test and confirm GREEN**

Run the command from Step 2.

Expected: exit code 0 and five passing checks.

- [ ] **Step 5: Route every room event through `FindOwnedSession`**

The helper resolves the session, applies the pure predicate, and rate-limits diagnostic output:

```csharp
private SessionItem? FindOwnedSession(string roomId, string sourceAgentId, string eventName)
{
    var session = FindSession(roomId);
    if (session != null && AgentEventOwnership.IsMatch(session.AgentId, sourceAgentId))
        return session;
    LogRejectedAgentEvent(roomId, session?.AgentId, sourceAgentId, eventName);
    return null;
}
```

Apply it to Claude, Codex, OpenCode, Gajae, Grok, Kimi, Devez CLI, and Antigravity Message/Busy/Waiting/SessionChanged handlers, including Claude's `TurnEndMarker`. Do not call `MarkSessionActivity`, mutate `IsBusy`, save session IDs, or emit completion records for a rejected event.

- [ ] **Step 6: Make generic last-message polling agent-specific**

Change `LastPromptChanged` to emit its `agentId`. In `MainWindow`, update only sessions whose project path and `AgentId` both match.

- [ ] **Step 7: Run focused tests and static coverage checks**

```powershell
dotnet run --project Tests\DevezCode.Tracking.Tests\DevezCode.Tracking.Tests.csproj
rg -n "FindSession\\((id|roomId)\\)" MainWindow.xaml.cs
```

Expected: tests pass; remaining direct `FindSession` calls in event subscriptions are individually reviewed and are not agent state sources.

- [ ] **Step 8: Commit**

```powershell
git add DevezCode.csproj Services\AgentEventOwnership.cs Services\AgentLastMessageService.cs MainWindow.xaml.cs Tests\DevezCode.Tracking.Tests
git commit -m "fix(tracking): gate room events by agent owner"
```

### Task 2: Launch Tracking-Agent Marker

**Files:**
- Modify: `Services/Terminal/TerminalSessionManager.cs:145-270,337-930,1453-1700`
- Modify: `Services/ExternalSessionService.cs:233-310,447-465`
- Modify: `Tests/DevezCode.Tracking.Tests/Program.cs`

**Interfaces:**
- Produces: `TrackingEnvironment.VariableName = "DEVEZCODE_TRACKING_AGENT"`
- Produces: `TrackingEnvironment.CmdSetLine(string agentId) : string`
- Consumes: agent IDs from `AgentRegistry`.

- [ ] **Step 1: Add failing tests for environment formatting**

Add assertions for:

```csharp
Check(TrackingEnvironment.IsExpected("codex", "codex"), "matching marker");
Check(!TrackingEnvironment.IsExpected("claude", "codex"), "mismatched marker");
Check(!TrackingEnvironment.IsExpected(null, "codex"), "missing marker");
Check(TrackingEnvironment.CmdSetLine("kimi") ==
      "set \"DEVEZCODE_TRACKING_AGENT=kimi\"\r\n", "cmd marker");
```

- [ ] **Step 2: Run and confirm RED**

Run the focused test project. Expected: compile failure for the missing `TrackingEnvironment`.

- [ ] **Step 3: Implement `TrackingEnvironment`**

Add the constants and pure helpers beside `AgentEventOwnership` without reading process-global environment inside the pure methods.

- [ ] **Step 4: Inject and restore the process environment**

Around `new TerminalSession(...)`, save both previous values and restore them in `finally`:

```csharp
var previousRoom = Environment.GetEnvironmentVariable("DEVEZCODE_ROOM_ID");
var previousAgent = Environment.GetEnvironmentVariable(TrackingEnvironment.VariableName);
Environment.SetEnvironmentVariable("DEVEZCODE_ROOM_ID", roomId);
Environment.SetEnvironmentVariable(TrackingEnvironment.VariableName, agent.Id);
```

Never blindly clear a pre-existing parent value.

- [ ] **Step 5: Put the marker in every generated launch batch**

Add `set "DEVEZCODE_TRACKING_AGENT=<agentId>"` alongside the existing room assignment for Codex, Claude, OpenCode including fork, Grok, Kimi, Antigravity, Devez CLI, and any direct fallback batch. Gajae receives `gajae` in the process environment even though it does not consume the room variable.

- [ ] **Step 6: Put the marker in external-terminal launches**

Pass `agent.Id` into `BuildRunnerScript` and set:

```powershell
$env:DEVEZCODE_TRACKING_AGENT = '<agentId>'
```

before the external agent executable starts.

- [ ] **Step 7: Run tests and inspect generated-script call sites**

```powershell
dotnet run --project Tests\DevezCode.Tracking.Tests\DevezCode.Tracking.Tests.csproj
rg -n "DEVEZCODE_ROOM_ID" Services\Terminal\TerminalSessionManager.cs Services\ExternalSessionService.cs
```

Expected: every agent launch that assigns a room also assigns a tracking agent.

- [ ] **Step 8: Commit**

```powershell
git add Services\AgentEventOwnership.cs Services\Terminal\TerminalSessionManager.cs Services\ExternalSessionService.cs Tests\DevezCode.Tracking.Tests\Program.cs
git commit -m "fix(tracking): tag root agent launches"
```

### Task 3: Producer-Side Cross-Agent Rejection

**Files:**
- Create: `Tests/TrackingHooks.Tests.ps1`
- Modify: `Services/Terminal/TerminalSessionManager.cs:1892-2555`
- Modify: `Resources/Hooks/codex-hook.ps1`
- Modify: `Resources/Hooks/codex-state-hook.cmd`
- Modify: `Resources/Hooks/grok-hook.ps1`
- Modify: `Resources/Hooks/grok-state-hook.cmd`
- Modify: `Resources/Hooks/kimi-hook.ps1`
- Modify: `Resources/Hooks/kimi-state-hook.cmd`
- Modify: `Resources/Hooks/antigravity-hook.cmd`
- Modify: `Resources/Plugins/opencode-room-tracker.js`

**Interfaces:**
- Consumes: `DEVEZCODE_TRACKING_AGENT`
- Rule: each producer accepts only its exact lowercase agent ID.

- [ ] **Step 1: Write failing hook guard tests**

`TrackingHooks.Tests.ps1` creates an isolated temporary `APPDATA`, sets a room, sends the smallest valid hook payload, and verifies:

```powershell
Invoke-CodexHook -TrackingAgent 'claude'
Assert-NotExists "$tempAppData\DevezCode\codex\busy\room-test.txt"

Invoke-CodexHook -TrackingAgent 'codex'
Assert-FileValue "$tempAppData\DevezCode\codex\busy\room-test.txt" 'running'
```

Repeat the mismatch/no-write assertion for Kimi, Grok, Antigravity, and their cmd state hooks. The test must restore all environment variables and delete only its own temporary directory.

- [ ] **Step 2: Run and confirm RED**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tests\TrackingHooks.Tests.ps1
```

Expected: at least the Codex mismatch case fails because current hooks only check room ID.

- [ ] **Step 3: Add fail-closed guards to PowerShell hooks**

Immediately after draining/parsing required stdin:

```powershell
if ($env:DEVEZCODE_TRACKING_AGENT -ne 'codex') { exit 0 }
```

Use the corresponding agent ID for Grok and Kimi. Claude's generated room and busy hook scripts must require `claude`; update `HookAssetsHealthy()` tokens so stale generated assets are replaced.

- [ ] **Step 4: Add fail-closed guards to cmd hooks**

After required JSON stdout for Antigravity and before state writes:

```bat
if /i not "%DEVEZCODE_TRACKING_AGENT%"=="antigravity" (
  "%SystemRoot%\System32\more.com" >nul 2>nul
  exit /b 0
)
```

Use the corresponding agent ID for Codex, Grok, and Kimi.

- [ ] **Step 5: Guard the OpenCode plugin before loading modules**

The plugin returns `{}` unless:

```javascript
process.env.DEVEZCODE_TRACKING_AGENT === "opencode"
```

This check stays beside the room check and before filesystem/client initialization.

- [ ] **Step 6: Run hook tests and asset-health scans**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tests\TrackingHooks.Tests.ps1
rg -n "DEVEZCODE_TRACKING_AGENT" Resources\Hooks Resources\Plugins Services\Terminal\TerminalSessionManager.cs
```

Expected: all hook tests pass and every room-aware producer contains the marker guard.

- [ ] **Step 7: Commit**

```powershell
git add Resources\Hooks Resources\Plugins Services\Terminal\TerminalSessionManager.cs Tests\TrackingHooks.Tests.ps1
git commit -m "fix(tracking): reject inherited cross-agent hooks"
```

### Task 4: Same-Agent Root Session Fences

**Files:**
- Modify: `Resources/Hooks/codex-hook.ps1`
- Modify: `Resources/Hooks/kimi-hook.ps1`
- Modify: `Resources/Hooks/kimi-state-hook.cmd`
- Modify: `Resources/Plugins/opencode-room-tracker.js`
- Modify: `Services/CodexHookService.cs`
- Modify: `Services/KimiHookService.cs`
- Modify: `Services/Terminal/TerminalSessionManager.cs`
- Modify: `Tests/TrackingHooks.Tests.ps1`

**Interfaces:**
- Produces per room: `sessions/<room>.root.txt`, `sessions/<room>.ended.txt`, and where process identity is available `sessions/<room>.owner.txt`.
- Consumes native session IDs from hook payloads.

- [ ] **Step 1: Add failing nested-session tests**

For Codex, create two resumable fake rollout files, send root `SessionStart`, then a second startup `SessionStart`, and assert the tracked/root files still contain the first ID. Send UserPromptSubmit and Stop for the second ID and assert no busy/lastmsg change.

For Kimi, send `SessionStart` for `session_root`, then `session_child`; assert only `session_root` owns UserPromptSubmit and Stop.

- [ ] **Step 2: Run and confirm RED**

Run `Tests/TrackingHooks.Tests.ps1`.

Expected: Codex and Kimi child sessions currently replace or mutate the room.

- [ ] **Step 3: Fence Codex session ownership**

- Seed `root.txt` from a valid saved/resume session before launch.
- First valid SessionStart claims an empty root.
- A different startup/resume SessionStart cannot replace a live root.
- Explicit Codex `clear` transition or a SessionStart after the root's SessionEnd may replace it.
- UserPromptSubmit, tool state, Stop, session persistence, and C# event emission require current ID equals root ID.
- Keep the existing turn ID mutex/active marker inside this session fence.

- [ ] **Step 4: Fence Kimi session ownership**

Use root and ended files with the same first-claim/explicit-end transition. `kimi-state-hook.cmd` must parse the event session ID when available and fail closed when it differs from root. If a lightweight event has no session ID, it may mutate state only while the root process has an active main turn marker created by root UserPromptSubmit.

- [ ] **Step 5: Fence OpenCode across processes**

On plugin initialization, atomically claim `sessions/<room>.owner.txt` with `process.pid`. If it contains a different live PID, return `{}` before registering handlers. If the PID is stale, replace it. Re-entry by a new top-level process is allowed only after the old owner is dead. Remove the file on a normal root process shutdown only when it still contains the current PID.

- [ ] **Step 6: Recheck existing fences**

Run focused tests for Grok and Antigravity to prove a second session ID cannot replace their root before an accepted end transition. Gajae remains isolated by its room-specific `--session-dir`; document this in the test output rather than adding an unused hook.

- [ ] **Step 7: Run all tracking tests**

```powershell
dotnet run --project Tests\DevezCode.Tracking.Tests\DevezCode.Tracking.Tests.csproj
powershell -NoProfile -ExecutionPolicy Bypass -File Tests\TrackingHooks.Tests.ps1
```

Expected: all checks pass.

- [ ] **Step 8: Commit**

```powershell
git add Resources\Hooks Resources\Plugins Services\CodexHookService.cs Services\KimiHookService.cs Services\Terminal\TerminalSessionManager.cs Tests
git commit -m "fix(tracking): fence nested root sessions"
```

### Task 5: Devez CLI Producer Ownership

**Files in `C:\Source\DevezCLI`:**
- Modify: `src/devezcode.rs`

**Interfaces:**
- Consumes: `DEVEZCODE_TRACKING_AGENT=devezcli`
- Produces: exclusive owner file `%APPDATA%\DevezCode\devezcli\owners\<room>.txt`.

- [ ] **Step 1: Write failing Rust tests**

Extract pure checks and owner-state helpers so tests assert:

```rust
assert!(tracking_agent_matches(Some("devezcli")));
assert!(!tracking_agent_matches(Some("claude")));
assert!(!tracking_agent_matches(None));
```

Add a temporary-directory test where the first reporter owns a room, a second reporter is rejected, and replacing the owner token makes the old reporter's next write fail closed.

- [ ] **Step 2: Run and confirm RED**

```powershell
cargo test devezcode::tests
```

Expected: compile failure for missing ownership helpers or failing behavior assertion.

- [ ] **Step 3: Implement marker and owner checks**

`init()` returns without a reporter unless the tracking agent matches. A reporter atomically claims an owner token before writing transient idle state. Every later write verifies the token still owns the room. `finish()` removes the owner file only if it still owns it.

- [ ] **Step 4: Clear stale ownership before top-level launch**

In DevezCode's `TryBuildDevezCliDirectLaunch`, remove only the target room's owner file immediately before starting the new top-level Devez CLI process. The new process then claims it; an already-running nested process cannot.

- [ ] **Step 5: Run Rust tests**

```powershell
cargo test devezcode::tests
```

Expected: all Devez CLI tracking tests pass.

- [ ] **Step 6: Commit and push the Devez CLI repository**

```powershell
git add src\devezcode.rs
git commit -m "fix(tracking): isolate DevezCode room ownership"
git push
```

### Task 6: Integrated Verification and Documentation

**Files:**
- Modify: `.knowledge/에이전트추가규칙.md`
- Create or modify: `.knowledge/세션-추적-이벤트-소유권.md`
- Modify: `CLAUDE.md` knowledge index only if a new knowledge file is created.

**Interfaces:**
- Documents `DEVEZCODE_TRACKING_AGENT`, app-side agent gate, and root session ownership requirements for future agents.

- [ ] **Step 1: Run non-destructive verification**

```powershell
dotnet run --project Tests\DevezCode.Tracking.Tests\DevezCode.Tracking.Tests.csproj
powershell -NoProfile -ExecutionPolicy Bypass -File Tests\TrackingHooks.Tests.ps1
git diff --check
git status --short
```

Do not run the DevezCode Release build while this session is hosted by the running DevezCode process.

- [ ] **Step 2: Review every event source**

```powershell
rg -n "MessageChanged \\+=|BusyChanged \\+=|WaitingChoiceChanged \\+=|SessionChanged \\+=|TurnEndMarker \\+=" MainWindow.xaml.cs
rg -n "DEVEZCODE_ROOM_ID" Services Resources
```

Expected: every room event is owner-gated and every room-aware producer is tracking-agent-gated.

- [ ] **Step 3: Update the knowledge base**

Document:

- never key trusted state by room ID alone;
- require room ID + expected agent + root native session;
- require turn ID where supplied;
- external/re-entry launch paths must carry the same marker;
- same-agent nested processes need a shared owner fence, not only an in-process variable.

- [ ] **Step 4: Inspect the final diff for unrelated changes**

```powershell
git diff --stat
git diff --check
git status --short
```

Expected: only tracking, tests, and knowledge files from this plan.

- [ ] **Step 5: Commit and push DevezCode**

```powershell
git add .
git commit -m "docs: record agent event ownership rules"
git push
```

- [ ] **Step 6: Report the build constraint**

State that focused logic, hook, and Rust tests passed, while the DevezCode Release build was intentionally not run because the current session is hosted inside the running app and project rules prohibit closing or building it without explicit safe shutdown.

# Kimi Code CLI Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** DevezCode에 Kimi Code CLI(`@moonshot-ai/kimi-code` v0.27.0)를 6번째 에이전트로 Tier 3 풀 통합한다 (등록·세션영속·상태추적·포크·사용량·모델도크·테마·내보내기·클리너).

**Architecture:** codex형 훅(config `[[hooks]]`, stdin JSON, cmd.exe/windowsHide) + grok형 TOML 편집 + 앱 레벨 재진입. 세션추적은 훅(SessionStart→id) 1차 + `session_index.jsonl` workDir 매칭 2차 폴백. 상태파일은 `%APPDATA%\DevezCode\kimi\{sessions,busy,waiting,lastmsg}\<room>.txt` 원자적 쓰기.

**Tech Stack:** C# / .NET (WPF), single `DevezCode.csproj`, ConPTY 터미널, WebView2 xterm. 테스트 프레임워크 없음.

## 검증 방식 (이 프로젝트 특례 — 사용자 CLAUDE.md 우선)
- 테스트 프로젝트가 없으므로 각 태스크의 "테스트"는 **①`dotnet build -c Release` 성공 + ②명시된 수동 검증**이다.
- **본 작업 세션이 DevezCode(PID 1428) 내부에서 실행 중** → 빌드/재시작이 세션을 끊는다. 따라서 **빌드·실행·수동검증은 사용자가 직접** 수행한다(코드 작성·커밋은 세션이 수행). CLAUDE.md 최우선 규칙(강제종료 금지) 준수.
- 순수 로직은 개념 검증(입력→기대출력)을 각 태스크에 명시. 필요 시 `dotnet run` 대신 앱 실행 중 실동작으로 확인.

## Global Constraints
- 훅 명령: `cmd.exe /d /c call <8.3-safe path> <arg>` (codex `BuildFastStateHookCommand` 재사용). `.ps1`은 `powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File <8.3path>`.
- 상태파일 쓰기는 **반드시 원자적**(temp→`Move-Item -Force`/`File.Move(overwrite)`). 직접 `Set-Content`/truncate 금지 (`.knowledge/훅-상태파일-원자적쓰기.md`).
- 전역 훅/스크립트는 `DEVEZCODE_ROOM_ID` 없으면 **즉시 no-op**.
- 세션 복원은 전용 분기에서 **항상 `-S <sessionId>`**(cwd 무관). `--work-dir` 없으므로 배치에서 `cd /d <workDir>` 필수.
- 커밋 규칙: 각 태스크 코드 작성 후 커밋. 빌드 성공 확인은 사용자 검증 체크포인트에서. AGENTS.md/CLAUDE.md 동기화 규칙 준수.
- 디자인: `AppStyles.xaml` 전역 스타일 사용, 인라인 스타일 남발 금지. 클릭 컨트롤 커서 규칙(`.knowledge/컨트롤추가규칙.md`), 텍스트 렌더링 규칙(`.knowledge/텍스트렌더링규칙.md`).

---

## 참조 앵커 (grep으로 실위치 확인 — 라인은 참고용, 변동됨)

| 대상 | grep 앵커 | 템플릿(codex/grok) |
|---|---|---|
| AgentRegistry 배열 | `Id = "codex"` | AgentRegistry.cs:49-62 (codex), 63-74 (grok) |
| 아이콘 스위치 | `"codex"       =>` | AgentImageConverter.cs:20-29 |
| 런치 디스패치 | `agent.Id == "codex"` / `agent.Id == "grok"` | TerminalSessionManager.cs:148,156 |
| 스냅샷 | `TrySnapshotRoomSession` (switch agent.Id) | :2505+ |
| 종료 | `GracefulExitPlan` | :2602+ |
| 방삭제 정리 | `RemoveClaudeCodeRoomDir` | SettingsService.cs:396 |
| 아티팩트 정리 | `PurgeAppOwnedRoomArtifacts` | TerminalSessionManager.cs:2764+ |
| 자동재진입 | `IsAutoReenterRoom` | TerminalHostView.cs:89-99 |
| 포크 화이트리스트 | `agentId != "claude"` | WorkspacePaneView.xaml.cs:944 |
| 훅서비스 필드/구독 | `_codexHook` | MainWindow.xaml.cs:89,380-420,547,632 |
| usage 필드/카드 | `_codex` / `AddProviderCard` | MainWindow.xaml.cs:71,909 |
| 클리너 enum | `CleanerAgentKind` | SessionCleanerService.cs:19 |
| IME B형 | `pinsLogicalCaret` | terminal.html:533 |
| csproj Content/Resource | `codex-hook.ps1` / `codex.png` | DevezCode.csproj:54-64,100 |

---

# Phase 1 — 코어 뼈대 (Tier 1-2 + 훅 인프라)

### Task 1: 에이전트 등록 + 아이콘 (피커 표시 = Tier 1)

**Files:**
- Modify: `Services/AgentRegistry.cs` (grep `Id = "grok"` 다음에 새 항목)
- Modify: `Models/AgentImageConverter.cs` (grep `"codex"       =>`)
- Create: `Resources/Images/ShellPresets/kimi.png` (50×50 투명 배경; 임시로 codex.png 복사 후 교체 가능)
- Modify: `DevezCode.csproj` (grep `codex.png` 옆에 Resource 추가)

**Interfaces:**
- Produces: `AgentRegistry.All` 에 `Id="kimi"` AgentDef; 모든 이후 태스크가 이 id로 분기.

- [ ] **Step 1:** `AgentRegistry.All` 배열에 추가:
```csharp
new()
{
    Id = "kimi", DisplayName = "Kimi", Provider = "Moonshot AI",
    ExeNames = new[] { "kimi.cmd", "kimi.ps1", "kimi.exe", "kimi" },
    Command = "kimi",
    InstallCommand = "npm install -g @moonshot-ai/kimi-code",
    UpdateCommand = "kimi upgrade",
    ResumeFlag = "-c",       // Tier-1 generic 폴백 전용(전용 분기는 -S <id>)
    SupportsHooks = false,   // 전용 agent.Id=="kimi" 분기 사용(grok과 동일 방식)
    InlineTui = true,        // alt-screen 미사용(?1049 없음) — 로딩 오버레이 해제 기준
},
```
- [ ] **Step 2:** `AgentImageConverter.Convert` 스위치에 `"kimi" => "kimi.png",` 추가.
- [ ] **Step 3:** csproj에 `<Resource Include="Resources\Images\ShellPresets\kimi.png" />` 추가. kimi.png 없으면 codex.png를 임시 복사(추후 실제 아이콘 교체 TODO).
- [ ] **Step 4 (검증·사용자):** 빌드 → 앱 실행 → 설정 다이얼로그에 "Kimi"(Moonshot AI) 표시 + 설치 감지(kimi.cmd) → 새 세션 피커에 아이콘과 함께 뜸.
- [ ] **Step 5 (커밋):** `git add -A && git commit -m "feat(kimi): register Kimi agent + icon (Tier 1)"`

---

### Task 2: 훅 스크립트 (stdin JSON → 상태파일)

**Files:**
- Create: `Resources/Hooks/kimi-hook.ps1`
- Create: `Resources/Hooks/kimi-state-hook.cmd`
- Modify: `DevezCode.csproj` (grep `codex-hook.ps1` 옆에 Content 2개)
- 참조(읽기): `Resources/Hooks/codex-hook.ps1`, `Resources/Hooks/codex-state-hook.cmd`

**Interfaces:**
- Produces: stdin JSON(`hookEventName`,`sessionId`,`cwd`,+event fields)을 받아 `%APPDATA%\DevezCode\kimi\{sessions,busy,waiting,lastmsg}\<DEVEZCODE_ROOM_ID>.txt` 를 원자적으로 씀. KimiHookService(Task 4)가 이 파일을 감시.

- [ ] **Step 1:** `kimi-state-hook.cmd` 작성 — 인자 `%1`=상태(working/waiting-on/waiting-off). codex-state-hook.cmd 구조 그대로, 단 대상 폴더 `kimi`. 핵심 로직(ASCII 전용, 한글 주석 금지):
  - `if not defined DEVEZCODE_ROOM_ID goto :eof`
  - `%1==working` → busy/<room>.txt 에 `running` 원자적 기록
  - `%1==waiting-on` → waiting/<room>.txt 에 `waiting`
  - `%1==waiting-off` → waiting/<room>.txt 삭제(또는 빈값 원자적)
  - 원자적: `%TEMP%\...tmp` 작성 후 `move /y`.
- [ ] **Step 2:** `kimi-hook.ps1` 작성 — stdin에서 JSON 읽어 `hookEventName` 분기. codex-hook.ps1의 `Write-State`(temp+`Move-Item -Force`) 헬퍼 재사용. 핵심:
```powershell
$room = $env:DEVEZCODE_ROOM_ID
if ([string]::IsNullOrEmpty($room)) { exit 0 }
$raw = [Console]::In.ReadToEnd()
try { $j = $raw | ConvertFrom-Json } catch { exit 0 }
$ev = $j.hookEventName
$base = Join-Path $env:APPDATA 'DevezCode\kimi'
switch ($ev) {
  'SessionStart'     { if ($j.sessionId) { Write-State (Join-Path $base "sessions\$room.txt") $j.sessionId } }
  'UserPromptSubmit' {
      Write-State (Join-Path $base "busy\$room.txt") 'running'
      # prompt 는 ContentPart[] — 텍스트만 이어붙임
      $txt = ($j.prompt | ForEach-Object { if ($_.text) { $_.text } elseif ($_ -is [string]) { $_ } }) -join ' '
      if ($txt) { Write-State (Join-Path $base "lastmsg\$room.txt") ($txt.Trim()) }
  }
  { $_ -in 'Stop','StopFailure' } { Write-State (Join-Path $base "busy\$room.txt") 'idle' }
}
exit 0
```
  - `Write-State`: `$tmp=$path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'; [IO.File]::WriteAllText($tmp,$val,[Text.UTF8Encoding]::new($false)); Move-Item -Force $tmp $path` (부모 폴더 없으면 생성).
- [ ] **Step 3:** csproj에 두 파일 `<Content ...><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></Content>` 추가.
- [ ] **Step 4 (검증·사용자):** 빌드만. (실동작 검증은 Task 6 이후 실제 실행에서.)
- [ ] **Step 5 (커밋):** `git commit -m "feat(kimi): hook scripts (ps1 + fast state cmd)"`

---

### Task 3: KimiHookInstaller (config.toml `[[hooks]]` 멱등 주입)

**Files:**
- Create: `Services/KimiHookInstaller.cs`
- 참조(읽기): `Services/CodexHookInstaller.cs`(스크립트 설치·8.3경로·멱등), `Services/GrokMcpBackend.cs`(TOML 섹션 재작성), `Services/AtomicFile.cs`

**Interfaces:**
- Produces: `KimiHookInstaller.EnsureInstalled()` (스크립트 복사 + config.toml `[[hooks]]` 주입, 멱등), `KimiHookInstaller.HookAssetsHealthy()`.
- Consumes: Task 2 스크립트.

- [ ] **Step 1:** 상수 — 홈 `KimiHome = $KIMI_CODE_HOME ?? ~/.kimi-code`, `ConfigTomlPath = <home>/config.toml`. 스크립트 설치 경로 `%LOCALAPPDATA%\DevezCode\kimi\hook.ps1` / `state-hook.cmd`.
- [ ] **Step 2:** `EnsureScriptInstalled()` — codex 방식(임베디드/소스 폴백 읽어 `AtomicFile.WriteAllText`, 내용 같으면 skip).
- [ ] **Step 3:** `[[hooks]]` 주입 — **핵심 로직**(TomlParser는 array-of-tables 미지원이라 수동):
  - 대상 이벤트→명령 맵:
    - `SessionStart`,`UserPromptSubmit`,`Stop`,`StopFailure` → `powershell ... -File <8.3 hook.ps1>`
    - `PreToolUse`,`PostToolUse` → `<8.3 state-hook.cmd> working` (matcher=".*")
    - `PermissionRequest` → `... state-hook.cmd waiting-on`
    - `PermissionResult` → `... state-hook.cmd waiting-off`
  - config.toml 읽기 → **우리 소유 `[[hooks]]` 블록만 제거**(마커 주석 `# devezcode-managed` 로 식별) → 우리 블록 재생성 append → `AtomicFile` 로 원자적 쓰기. GrokMcpBackend.StripMcpSections 패턴 응용(단 `[[hooks]]` double-bracket + 마커 기반 제거).
  - 각 블록 형식:
    ```toml
    # devezcode-managed
    [[hooks]]
    event = "UserPromptSubmit"
    command = "cmd.exe /d /c call C:\\PROGRA~1\\...\\hook.ps1..."  # 실제로는 powershell -File
    timeout = 15
    ```
  - 멱등: 이미 동일 내용이면 디스크 쓰기 생략.
- [ ] **Step 4:** `EnsureInstalled()` = EnsureScriptInstalled + 위 주입. 예외는 삼켜 다음 시작에 재시도.
- [ ] **Step 5 (검증·사용자):** 앱 실행 후 `~/.kimi-code/config.toml` 열어 `# devezcode-managed [[hooks]]` 7블록 존재 확인 + `kimi doctor` 가 "valid" 반환(파싱 유효성).
- [ ] **Step 6 (커밋):** `git commit -m "feat(kimi): hook installer (config.toml [[hooks]] idempotent)"`

---

### Task 4: KimiHookService (상태파일 감시 → 이벤트)

**Files:**
- Create: `Services/KimiHookService.cs`
- 참조(읽기): `Services/CodexHookService.cs`

**Interfaces:**
- Produces: 이벤트 `MessageChanged(room,msg)`, `BusyChanged(room,bool)`, `WaitingChoiceChanged(room,bool)`, `KimiSessionChanged(room,sessionId)`; `Start()`, `Dispose()`. 정적 헬퍼 `LoadTrackedSessionId(room)`.
- Consumes: Task 2 상태파일.

- [ ] **Step 1:** CodexHookService 복제 — 폴더 `%APPDATA%\DevezCode\kimi\{lastmsg,busy,waiting,sessions}`. 4개 FileSystemWatcher(Changed/Created/Renamed).
- [ ] **Step 2:** `Start()` 에서 stale busy/waiting 삭제(sessions 보존). 초기 emit(lastmsg,sessions).
- [ ] **Step 3:** Emit 핸들러 — busy `running`→true/`idle`→false(빈파일 120ms settle 재확인), waiting `waiting`→true, session GUID/`session_...` 형식 검증 후 emit. `TryRead`(FileShare.ReadWrite 재시도).
- [ ] **Step 4:** `KimiSessionChanged` 검증 — sessionId 유효(sessions 디렉터리 존재)일 때만 emit.
- [ ] **Step 5 (검증·사용자):** 빌드만(배선은 Task 9).
- [ ] **Step 6 (커밋):** `git commit -m "feat(kimi): hook service (busy/waiting/lastmsg/session watcher)"`

---

### Task 5: SettingsService — 방별 세션 저장

**Files:**
- Modify: `Services/SettingsService.cs` (grep `CodexRoomSessions`, `RemoveClaudeCodeRoomDir`)

**Interfaces:**
- Produces: `KimiRoomSessions` dict + `LoadKimiRoomSession(room)`, `SaveKimiRoomSession(room,id)`, `ClearKimiRoomSession(room)`; `ShowFooterKimi` + Load/Save.

- [ ] **Step 1:** SettingsData에 `public Dictionary<string,string> KimiRoomSessions { get; set; } = new();`
- [ ] **Step 2:** Load/Save(id 비었으면 저장 안 함)/Clear 정적 메서드 (codex 패턴).
- [ ] **Step 3:** `RemoveClaudeCodeRoomDir` 에 `changed |= Current.KimiRoomSessions.Remove(roomId);` 추가.
- [ ] **Step 4:** `ShowFooterKimi`(기본 false) + `LoadShowFooterKimi`/`SaveShowFooterKimi`.
- [ ] **Step 5:** 클리너 스냅샷 튜플(`LoadManagedSessionSnapshot`)에 Kimi 값 추가(있으면).
- [ ] **Step 6 (검증·사용자):** 빌드.
- [ ] **Step 7 (커밋):** `git commit -m "feat(kimi): settings room-session store + footer toggle field"`

---

### Task 6: 런치 분기 (TryBuildKimiDirectLaunch)

**Files:**
- Modify: `Services/Terminal/TerminalSessionManager.cs` (grep `agent.Id == "grok"` 다음에 분기; `CanSafelyResumeRoom`)

**Interfaces:**
- Consumes: Task 3 `EnsureInstalled`, Task 5 `LoadKimiRoomSession`, Task 4 `LoadTrackedSessionId`.
- Produces: `agent.Id=="kimi"` → `TryBuildKimiDirectLaunch(roomId, ccDir, out inject)`; `KimiLaunchDir()`.

- [ ] **Step 1:** 디스패치에 추가(grok 분기 다음):
```csharp
if (ccDir != null && agent.Id == "kimi")
{
    startDir = ccDir;
    var direct = TryBuildKimiDirectLaunch(roomId, ccDir, out inject);
    if (direct != null) commandLine = direct;
}
```
- [ ] **Step 2:** `TryBuildKimiDirectLaunch` 작성:
  - `KimiHookInstaller.EnsureInstalled();` (+ Task 13에서 theme apply 추가)
  - kimi exe 경로 해석(ExeNames/PATH).
  - **세션 id 선택**: `LoadKimiRoomSession(room)` 유효(=`~/.kimi-code/sessions/**/<id>/state.json` 존재)면 사용 → 없으면 `session_index.jsonl`에서 workDir==ccDir 매칭 최신 id 폴백 → 없으면 신규.
    - `session_index.jsonl` 은 append-only, 같은 id 나중 줄 우선. workDir 비교는 대소문자·슬래시 정규화.
  - `MarkAgentRoomLaunched(roomId,"kimi")`.
  - 배치 작성 `KimiLaunchDir()\<room>.cmd`:
    ```bat
    @echo off
    cd /d "<ccDir>"
    set "DEVEZCODE_ROOM_ID=<roomId>"
    kimi -S <sessionId>      REM 또는 신규면: kimi
    exit
    ```
    반환 `cmd.exe /c "<batch>"`. 예외 시 폴백 inject.
  - env 백업: `Environment.SetEnvironmentVariable("DEVEZCODE_ROOM_ID", roomId)` 세션 생성 직전 set, finally clear (codex/grok 패턴).
- [ ] **Step 3:** `CanSafelyResumeRoom` switch에 `"kimi" => <session dir 존재 확인> != null`.
- [ ] **Step 4:** `KimiLaunchDir()` = `%APPDATA%\DevezCode\kimi\launch`.
- [ ] **Step 5 (검증·사용자):** 앱에서 kimi 새 세션 생성 → kimi TUI 기동(InlineTui: 로딩 오버레이가 첫 출력에 해제되는지 확인 — 안 되면 InlineTui 재검토) → 대화 1회 → `%APPDATA%\DevezCode\kimi\sessions\<room>.txt` 에 session id 기록됨(훅 발화 확인) → busy/lastmsg 파일도 생성됨. **여기서 훅 end-to-end 최초 검증.**
- [ ] **Step 6 (커밋):** `git commit -m "feat(kimi): direct launch (cd + -S resume + hook env)"`

---

### Task 7: 스냅샷 + 정리

**Files:**
- Modify: `Services/Terminal/TerminalSessionManager.cs` (grep `TrySnapshotRoomSession`, `PurgeAppOwnedRoomArtifacts`, 고스트 GC dir 수집부)

**Interfaces:**
- Consumes: Task 4 `LoadTrackedSessionId`, Task 5 `SaveKimiRoomSession`.

- [ ] **Step 1:** `TrySnapshotRoomSession` switch에 `"kimi"` 케이스: `KimiHookService.LoadTrackedSessionId(room)` 읽어 세션 디렉터리 존재 검증 후 `SaveKimiRoomSession`.
- [ ] **Step 2:** `PurgeAppOwnedRoomArtifacts` 에 kimi `{sessions,busy,waiting,lastmsg}\<room>.txt` + `launch\<room>.cmd` 삭제 추가.
- [ ] **Step 3:** 고스트 GC/클리너 보호 dir 수집부에 kimi 디렉터리 추가.
- [ ] **Step 4 (검증·사용자):** 대화 후 앱 **정상 종료**→재시작 → 종료 직전 세션으로 복원(스냅샷). 방 삭제 후 같은 이름 재생성 → 예전 대화 안 이어짐(정리).
- [ ] **Step 5 (커밋):** `git commit -m "feat(kimi): snapshot on shutdown + artifact purge"`

---

### Task 8: 종료 + 앱 레벨 재진입

**Files:**
- Modify: `Services/Terminal/TerminalSessionManager.cs` (grep `GracefulExitPlan`; `IsKimiBusyRunning` 신규)
- Modify: `Views/TerminalHostView.cs` (grep `IsAutoReenterRoom`)

**Interfaces:**
- Consumes: busy 상태파일.

- [ ] **Step 1:** `GracefulExitPlan` 에 `if (agent == "kimi") return ("\x04", 2, IsKimiBusyRunning(roomId));` (Ctrl+D×2, busy면 Esc선행).
- [ ] **Step 2:** `IsKimiBusyRunning(roomId)` = `%APPDATA%\DevezCode\kimi\busy\<room>.txt`=="running".
- [ ] **Step 3:** `TerminalHostView.IsAutoReenterRoom` 반환식에 `|| a=="kimi"` 추가(IsShuttingDown 가드는 기존).
- [ ] **Step 4 (검증·사용자):** TUI 내부에서 Ctrl+D×2 종료 → 앱이 같은 세션으로 자동 재진입(`-S <id>`). 앱 종료 중에는 재진입 안 됨(IsShuttingDown). 10초 3회 제한 동작.
- [ ] **Step 5 (커밋):** `git commit -m "feat(kimi): graceful exit (Ctrl+D x2) + app-level reenter"`

---

# Phase 2 — 상태 UI 배선

### Task 9: MainWindow 배선 (스피너/❗/lastmsg/완료토스트)

**Files:**
- Modify: `MainWindow.xaml.cs` (grep `_codexHook`, `_grokHook`, `NotifyIfSessionFinished`, `HasBusyOrWaitingTrackingFile`)

**Interfaces:**
- Consumes: Task 4 KimiHookService 이벤트, Task 5 SaveKimiRoomSession.

- [ ] **Step 1:** 필드 `private readonly KimiHookService _kimiHook = new();`
- [ ] **Step 2:** 시작부 `KimiHookInstaller.EnsureInstalled(); _kimiHook.Start();` / Dispose에 `_kimiHook.Dispose();`
- [ ] **Step 3:** 구독(codex 블록 복제):
  - `MessageChanged` → `s.LastMessage=msg`, 팬 갱신.
  - `BusyChanged` → `MarkSessionActivity`, `s.IsBusy=busy`, `IsWaitingChoice=false`, `NotifyIfSessionFinished(s, was, busy)`, `UpdateSessionBusyDisplay`.
  - `WaitingChoiceChanged` → `s.IsWaitingChoice`, `NotifyIfSessionWaiting(s,...,1500)`.
  - `KimiSessionChanged` → 검증 후 `SettingsService.SaveKimiRoomSession`.
- [ ] **Step 4:** `HasBusyOrWaitingTrackingFile` 에 "kimi" 경로 추가(재진입/종료 판정용).
- [ ] **Step 5 (검증·사용자):** 프롬프트 전송→스피너 ON→응답완료→OFF+완료토스트. 권한/선택지 질문→❗. 헤더에 마지막 프롬프트. **Tier 3 상태추적 완결.**
- [ ] **Step 6 (커밋):** `git commit -m "feat(kimi): wire busy/waiting/lastmsg + finish toast"`

> **Phase 1-2 완료 = Tier 3 코어. 사용자 실기 검증(§13 T1~T3 + 한글 IME 기초) 후 Phase 3.**

---

# Phase 3 — 선택 기능

### Task 10: 세션 포크

**Files:**
- Modify: `Services/Terminal/TerminalSessionManager.cs` (`TryForkKimiSession` 신규)
- Modify: `Views/WorkspacePaneView.xaml.cs` (grep `agentId != "claude"` 화이트리스트; fork 저장 if 체인)

**Interfaces:**
- Produces: `TryForkKimiSession(sourceSessionId)` → new sessionId(`session_<uuid>`).

- [ ] **Step 1:** `TryForkKimiSession` — 원본 `~/.kimi-code/sessions/<wdKey>/<srcId>/` 디렉터리 전체를 새 `session_<newGuid>/` 로 복사(활성 파일 `FileShare.ReadWrite`). `state.json` 의 `agents.*.homedir` 경로를 새 id로 치환 + `forkedFrom=<srcId>` 추가 + `createdAt/updatedAt` 갱신. `session_index.jsonl` 에 새 줄 append(sessionId/sessionDir/workDir).
- [ ] **Step 2:** 화이트리스트에 `&& agentId != "kimi"` 조건 추가(허용).
- [ ] **Step 3:** fork srcSid 획득 + `TryForkKimiSession` 호출 + `SaveKimiRoomSession(newRoom, newId)` 분기 추가.
- [ ] **Step 4 (검증·사용자):** 세션 우클릭 포크 → 새 세션이 원본 복사본으로 시작(`kimi -S <newId>` 로 대화 이어짐), 원본 무변.
- [ ] **Step 5 (커밋):** `git commit -m "feat(kimi): session fork (dir copy + state/index rewrite)"`

---

### Task 11: 사용량 푸터 (정액제 %)

**Files:**
- Create: `Services/KimiCredentialStore.cs`, `Services/KimiUsageService.cs`
- Modify: `MainWindow.xaml.cs` (grep `_codex`, `AddProviderCard`, `ApplyProviderUsage`)
- Modify: `Views/SettingsDialog.xaml(.cs)` (grep `ShowFooterCodexToggle`)
- 참조: `Services/GrokUsageService.cs`, `Services/GrokCredentialStore.cs`, `Models/ProviderUsage.cs`

**Interfaces:**
- Produces: `KimiUsageService{ Start(), RefreshNow(), IsConnected(), event Updated(ProviderUsage) }` → `ProviderUsage{Provider="kimi", Primary(5h %), Weekly(주간 %), PlanLabel}`.

- [ ] **Step 1:** `KimiCredentialStore` — 토큰 경로 폴백 `[~/.kimi-code/credentials/kimi-code.json, ~/.kimi/credentials/kimi-code.json]`. flat 스키마 `{access_token,refresh_token,expires_at,scope,token_type,expires_in}` 읽기. 갱신은 host `https://auth.kimi.com`, clientId `17e5f671-d194-4dfb-9706-5516cb48c098`, `grant_type=refresh_token`. (읽기전용 우선; 갱신 실패해도 stale 토큰으로 조회 시도.)
- [ ] **Step 2:** `KimiUsageService` — `GET https://api.kimi.com/coding/v1/usages` (Bearer). 응답 파싱(번들 `parseManagedUsagePayload` 이식): `usage`(주간 limit/used/reset) + `limits[]`(각 `window.duration`+`timeUnit` MINUTE/HOUR/DAY → "5h"/"7d" 라벨) → % 계산. 3분 폴링, DropGuard 스냅샷(`%APPDATA%\DevezCode\kimi-usage.json`).
- [ ] **Step 3:** MainWindow — 필드 `_kimi`/`_lastKimi`, `_kimi.Updated += ApplyProviderUsage`, `_kimi.Start()`, `AddProviderCard(cards,_lastKimi,"Kimi",App.KimiIconUri)`, `ApplyProviderUsage` case "kimi", 푸터 divider/disconnect/connected 분기.
- [ ] **Step 4:** `App.xaml.cs` 에 `KimiIconUri`.
- [ ] **Step 5:** SettingsDialog 푸터 토글 `ShowFooterKimiToggle` (XAML 체크박스 + load/capture/dirty/save/revert).
- [ ] **Step 6 (검증·사용자):** kimi 로그인 상태에서 푸터에 Kimi 카드(주간·5h %) 표시 + 설정 토글 on/off 동작. (미로그인 시 disconnect 표시.)
- [ ] **Step 7 (커밋):** `git commit -m "feat(kimi): usage footer (coding/v1/usages, plan %)"`

---

### Task 12: 모델 선택 도크

**Files:**
- Modify: `Views/WorkspacePaneView.xaml(.cs)` (grep `CodexModelEffortDock`)

**Interfaces:**
- Consumes: config.toml `[models.*]` alias, 생성 `-m <alias>` (Task 6 배치에 반영).

- [ ] **Step 1:** `KimiModelEffortDock`(effort 없이 모델만) XAML + 표시 로직 — config.toml `[models.*]` 키 목록을 사용가능 모델로 읽기(`TomlParser`). 현재 세션 모델 = 방 설정(`LoadAgentRoomModel("kimi",room)`) ?? config `default_model`.
- [ ] **Step 2:** Task 6 배치 빌드에 `LoadAgentRoomModel` 유효(=config `[models.*]`에 존재)하면 `-m <alias>` 추가.
- [ ] **Step 3:** 도크에서 모델 선택 시 `SaveAgentRoomModel` + (재진입/재시작으로 반영).
- [ ] **Step 4 (검증·사용자):** 도크에 현재 모델 표시. 모델 alias 여러 개일 때 선택→재시작 후 `-m` 반영. (현재 1종이라 표시 위주.)
- [ ] **Step 5 (커밋):** `git commit -m "feat(kimi): model dock (-m alias from [models.*])"`

---

### Task 13: 테마 (tui.toml dark/light)

**Files:**
- Create: `Services/Terminal/KimiCustomThemes.cs`
- Modify: `Services/Terminal/TerminalSessionManager.cs` (Task 6 `TryBuildKimiDirectLaunch` 시작부 + `OnAppThemeChanged` 브로드캐스트)
- 참조: `Services/Terminal/GrokCustomThemes.cs`

**Interfaces:**
- Produces: `KimiCustomThemes.Apply(theme)` — `~/.kimi-code/tui.toml` 의 `theme` 값을 dark/light로 upsert.

- [ ] **Step 1:** `KimiCustomThemes` — GrokCustomThemes.UpsertUiTheme 응용하되 대상 = `tui.toml`, 키 = 최상위 `theme`(섹션 없음). map: dark→"dark", 그 외→"light".
- [ ] **Step 2:** `TryBuildKimiDirectLaunch` 시작부에 `KimiCustomThemes.Apply(App.CurrentTheme);`
- [ ] **Step 3:** `OnAppThemeChanged` 브로드캐스트/App.xaml.cs 시작·테마변경 경로에 kimi Apply 추가. (테마변경이 세션 재시작을 요구하면 GracefulDispose 흐름 정상 동작 전제.)
- [ ] **Step 4 (검증·사용자):** 앱 테마 dark↔light 변경 → kimi tui.toml `theme` 갱신 → 세션 재시작 후 TUI 반영 + 대화 유지.
- [ ] **Step 5 (커밋):** `git commit -m "feat(kimi): theme sync (tui.toml theme dark/light)"`

---

### Task 14: 텍스트 내보내기 (wire.jsonl → .md)

**Files:**
- Modify: `Services/SessionExporter.cs` (grep `FromCodex`, `AgentLabel`)

**Interfaces:**
- Produces: `FromKimi(roomId)` → 마크다운 문자열.

- [ ] **Step 1 (사전 실측):** 실제 대화 세션의 `~/.kimi-code/sessions/**/agents/main/wire.jsonl` 스키마 확인(USER/ASSISTANT 이벤트 타입·텍스트 필드). 스키마 문서화.
- [ ] **Step 2:** `BuildMarkdown` switch에 `"kimi" => FromKimi(roomId)`, `AgentLabel` 에 `"kimi" => "Kimi"`.
- [ ] **Step 3:** `FromKimi` — 방 세션 id로 wire.jsonl 찾아 user/assistant 텍스트 추출 → md. (파일 없거나 빈 스키마면 빈 목록 → 조용히 skip.)
- [ ] **Step 4 (검증·사용자):** 세션 텍스트 내보내기 → .md 생성 확인.
- [ ] **Step 5 (커밋):** `git commit -m "feat(kimi): export session to markdown (wire.jsonl)"`

---

### Task 15: 세션 클리너

**Files:**
- Modify: `Services/SessionCleanerService.cs` (grep `CleanerAgentKind`)
- Modify: `Views/SettingsDialog.xaml(.cs)` (grep `CodexCatBtn`, `SetCleanerActive`)

**Interfaces:**
- Consumes: `~/.kimi-code/sessions/` 스캔.

- [ ] **Step 1:** `CleanerAgentKind` enum에 `Kimi` 추가 + 카운트/스냅샷 레코드 확장.
- [ ] **Step 2:** `EnumerateUnmanagedKimi` + id 추출(`session_<uuid>` 폴더명) — 스캔 루트 `~/.kimi-code/sessions/**/session_*`. 관리중(방 매핑) 세션 제외.
- [ ] **Step 3:** SettingsDialog — XAML pill `KimiCatBtn`(`Tag="kimi"`, `CleanerPill` 스타일) + Tag→enum 매핑 + `SetCleanerAgentVisible`(EnabledAgents) + 아이콘.
- [ ] **Step 4 (검증·사용자):** 클리너 탭에 Kimi 카테고리 표시 + 미관리 세션 카운트/정리 동작.
- [ ] **Step 5 (커밋):** `git commit -m "feat(kimi): session cleaner category"`

---

### Task 16: 한글 IME + 지식베이스 마무리

**Files:**
- Modify: `Resources/Terminal/web/terminal.html` (grep `pinsLogicalCaret`) — **실측 후 조건부**
- Modify: `.knowledge/에이전트추가규칙.md` (인덱스/본문/부록표에 kimi 반영)
- Modify: `AGENTS.md`/`CLAUDE.md` (필요 시 — 내용 무관하면 생략, 단 둘 동기화)

- [ ] **Step 1 (실측·사용자):** kimi 입력란에서 한글 조합 중 **블록커서 깜빡이는가?** 판정(§11-2).
  - 깜빡임(B형) → `pinsLogicalCaret` 에 `agent === 'kimi'` 추가 + chain 우측경계(`cols-4`) 확인.
  - 안 깜빡임(A형)·틀어짐 없음 → 무개입.
- [ ] **Step 2:** §13 IME 매트릭스(16) 전항목 통과 확인.
- [ ] **Step 3:** `.knowledge/에이전트추가규칙.md` 갱신 — 부록 비교표에 kimi 열 추가, 마지막 검증일·본문에 "kimi = 훅(config.toml [[hooks]]) + session_index 폴백, InlineTui, 포크=dir복사, 사용량=/usages" 요약. 인덱스 표에 필요 시 한 줄.
- [ ] **Step 4 (커밋):** `git commit -m "docs(kimi): IME handling + knowledge base update"`

---

## Self-Review (스펙 대비)

- **스펙 §0 검증사실** → Task 3/6(훅·-S), 11(usage/OAuth), 13(theme), 6(InlineTui) 반영. ✅
- **§1 아키텍처**(훅+TOML+앱재진입+2차폴백+원자적) → Task 3,4,6,8 + Global Constraints. ✅
- **§2 이벤트 매핑** → Task 2,3. ✅
- **§4 신규8파일** → Task 2(2),3,4,11(2),13,1(png). ✅ (KimiCredentialStore=Task11)
- **§5 편집파일** → Task 1,5,6,7,8,9,10,12,13,14,15,16 전수. ✅
- **§6 종료/재진입** → Task 8. ✅
- **§7 사용량** → Task 11. ✅
- **§8 점검 시나리오** → 각 태스크 검증 스텝 + Task16 IME 매트릭스. ✅
- **§9 제약** → 검증방식/Global Constraints 명시. ✅
- **Placeholder scan**: Task 14 Step1(wire.jsonl 스키마), Task 16 Step1(IME)은 "실측 필요"로 스펙에서 이미 미확정 표기된 항목 — placeholder 아님(실기 의존). 그 외 TODO 없음. ✅
- **Type 일관성**: `KimiHookService.LoadTrackedSessionId`(Task4)↔Task7 사용, `SaveKimiRoomSession`(Task5)↔Task7/9/10, `ProviderUsage{Provider="kimi"}`(Task11) 일치. ✅

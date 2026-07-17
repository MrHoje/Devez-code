# Kimi Code CLI 통합 설계 스펙

- 날짜: 2026-07-18
- 대상 CLI: **Kimi Code CLI** (`@moonshot-ai/kimi-code`, 실측 v0.27.0, entry `dist/main.mjs`)
- 통합 수준: **Tier 3 풀 통합** + 포크 + 사용량 푸터 + 모델 도크 + (기본 포함) 테마·내보내기·클리너
- 제외: MCP 매니저(추후), 훅 신뢰해시(codex 0.129 전용)
- 근거 문서: `.knowledge/에이전트추가규칙.md` (canonical 체크리스트)

---

## 0. 검증된 런타임 사실 (0.27.0, 번들 정적분석 · 쿼터 소모 0)

| # | 사실 | 출처(dist/main.mjs) |
|---|---|---|
| 1 | 훅은 `spawn(command, {shell:true, windowsHide:true, stdio:"pipe", detached:!win32})` 로 실행 → **Windows=cmd.exe, 콘솔창 안 뜸**, `env={...process.env, ...hook.env}`(부모 env 상속) | `buildHookSpawnOptions`/`runHook$1` ~125846 |
| 2 | stdin = `JSON.stringify({hookEventName, sessionId, cwd, ...eventFields})`; **exit 2=block, exit 0=allow**(+ stdout JSON 구조화 선택) | `triggerInner` ~126145, `resultFromExitCode$1` ~125928 |
| 3 | 이벤트 전량 실재: SessionStart/SessionEnd, UserPromptSubmit, Stop/StopFailure, PreToolUse/PostToolUse/PostToolUseFailure, **PermissionRequest/PermissionResult**, SubagentStart/Stop, Interrupt, Notification, Pre/PostCompact | 55633+, 127988+, 156429+ |
| 4 | config `[[hooks]]` 배열 (`event`,`command`,`matcher`,`timeout`,`cwd`,`env`), `~/.kimi-code/config.toml` | `readHooks$1`/`HookDefSchema$1` ~189964 |
| 5 | `-S <id>` 복원 = **cwd 무관**(전역 `session_index.jsonl` id 조회). 전용 분기는 항상 `-S <id>` 사용. `-c`(continue)는 cwd 종속(공유 cwd 방 충돌) → Tier-1 generic 폴백 전용. `-S`(id 없이)=대화형 피커라 폴백 부적합 | `findSessionEntry`/`readSessionIndex$1` ~188853 |
| 6 | alt-screen(`?1049`) **없음** → 인라인 TUI → `InlineTui=true` (동기화출력 `?2026`만 사용) | 229581+ |
| 7 | 사용량: `GET https://api.kimi.com/coding/v1/usages` → `{usage:{limit,used|remaining,reset_at}, limits:[{detail, window:{duration,timeUnit(MINUTE/HOUR/DAY)}}], boosterWallet}`; 창 라벨 "5h limit"/"7d limit"/"Weekly limit" | `managed-usage.ts` 207705-207850 |
| 8 | OAuth: host `https://auth.kimi.com`, clientId `17e5f671-d194-4dfb-9706-5516cb48c098`, device-code + refresh_token grant; 토큰 flat 스키마 `{access_token,refresh_token,expires_at,scope,token_type,expires_in}`; 위치 = **레거시 `~/.kimi/credentials/kimi-code.json`**(현재), `~/.kimi-code/credentials/` 미생성 | 207700, fs 실측 |
| 9 | 세션 저장: `~/.kimi-code/sessions/wd_<user>_<sha256앞12>/session_<uuid>/{state.json, agents/main/wire.jsonl}`; `state.json`={createdAt,updatedAt,title,isCustomTitle,agents,workDir(,forkedFrom)}; 홈 루트 `session_index.jsonl`={sessionId,sessionDir,workDir} | fs 실측 |
| 10 | 패키지 `@moonshot-ai/kimi-code`, exe `kimi.cmd`; `⚠️ --work-dir 플래그 없음` → 배치에서 cd 필수 | `kimi --help` |
| 11 | `kimi export [sessionId] -o <zip>` = ZIP. .md 일관성 위해 wire.jsonl 파싱 채택(스키마는 구현 중 실측) | `kimi export --help` |
| 12 | 테마 = `tui.toml` 의 `theme = "dark"|"light"` | fs 실측 |
| 13 | 모델 = config `[models.*]` alias, 실행 `-m <alias>`; 현재 `kimi-code/kimi-for-coding`("Kimi-k2.6") 1종 | config.toml 실측 |

### 구현 중/빌드 시 실측 필요 (미확정)
- 한글 IME 커서형(§11-2 A/B형) — 실기만 가능
- wire.jsonl 이벤트 스키마(내보내기용) — 샘플 세션이 비어 있었음
- DevezCode 실행 세션에서 훅 end-to-end 발화(코드상 확실하나 미관측)
- alt-screen 부재로 로딩 오버레이 해제 타이밍(InlineTui=true 검증)

---

## 1. 아키텍처

**codex형 훅 + grok형 TOML 하이브리드.**
- 훅 로직·상태추적: codex 패턴(`CodexHookInstaller`/`CodexHookService`) 이식.
- TOML 쓰기: `[[hooks]]`(array-of-tables)는 기존 `TomlParser`가 못 읽으므로(무시) **수동 문자열 편집**(grok `GrokMcpBackend.Save`/`StripMcpSections` 패턴). 읽기는 `TomlParser` 재사용.
- 재진입: **앱 레벨**(배치 `cmd /c` → exit → ConPTY 종료 → `IsAutoReenterRoom` 재시작 + `IsShuttingDown` 가드). 배치 __reenter 루프 미사용.
- 세션추적: **훅 1차**(SessionStart→session_id) + **`session_index.jsonl` workDir 매칭 2차 폴백**. busy stuck 방지 **120s stale failsafe**.
- 훅 명령 형식: `cmd.exe /d /c call <8.3-safe path> <arg>`(codex `BuildFastStateHookCommand` 재사용). `.ps1`은 `powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File <8.3path>`.
- **전역 훅 무해화**: `DEVEZCODE_ROOM_ID` 없으면 스크립트 즉시 no-op(사용자가 앱 밖에서 kimi 직접 실행 시).
- 상태파일: `%APPDATA%\DevezCode\kimi\{sessions,busy,waiting,lastmsg}\<room>.txt`, **원자적 쓰기(temp→Move)** 필수.

## 2. 훅 이벤트 매핑

| Kimi 이벤트 | 상태 | 스크립트 |
|---|---|---|
| SessionStart | session_id 기록 | kimi-hook.ps1 |
| UserPromptSubmit | busy=ON + lastmsg(prompt=ContentPart[]→텍스트 추출) | kimi-hook.ps1 |
| Stop / StopFailure | busy=OFF(idle) | kimi-hook.ps1 |
| PreToolUse / PostToolUse | busy 유지(working) | kimi-state-hook.cmd(fast) |
| PermissionRequest | waiting=ON(❗) | kimi-state-hook.cmd |
| PermissionResult | waiting=OFF | kimi-state-hook.cmd |
| SessionEnd | (선택) 정리 | kimi-hook.ps1 |

## 3. 세션 복원 흐름

1. 저장된 `KimiRoomSessions[room]` 유효성 검증(sessions 디렉터리 존재) → 있으면 사용.
2. 없으면 `session_index.jsonl` 에서 room workDir 매칭 최신 세션 id 폴백.
3. 배치: `cd /d <workDir>` + `set DEVEZCODE_ROOM_ID` + (`kimi -S <id>` 복원 | `kimi` 신규) + `exit`, `cmd.exe /c "<batch>"` 반환.
4. 공유 cwd 방 충돌은 훅 per-room session_id 로 해결(폴백은 최근값이라 부정확 가능 → 훅 우선).

---

## 4. 신규 파일 (8)

| 파일 | 역할 | 템플릿 |
|---|---|---|
| `Services/KimiHookInstaller.cs` | config.toml `[[hooks]]` 멱등 주입(수동 TOML) + 스크립트 배치 | CodexHookInstaller + GrokMcpBackend.Save |
| `Services/KimiHookService.cs` | 상태파일 감시 → busy/waiting/lastmsg/session 이벤트 | CodexHookService |
| `Resources/Hooks/kimi-hook.ps1` | stdin JSON 파싱 → 상태파일(원자적) | codex-hook.ps1 |
| `Resources/Hooks/kimi-state-hook.cmd` | fast busy/waiting | codex-state-hook.cmd |
| `Services/KimiUsageService.cs` | OAuth 토큰(양쪽 경로 폴백) → `/usages` 조회 → ProviderUsage(주간/5h %) | GrokUsageService |
| `Services/KimiCredentialStore.cs` | 토큰 읽기/갱신(refresh_token) | GrokCredentialStore |
| `Services/Terminal/KimiCustomThemes.cs` | tui.toml `theme` dark/light upsert | GrokCustomThemes.UpsertUiTheme |
| `Resources/Images/ShellPresets/kimi.png` | 아이콘(50×50) | — |

## 5. 편집 파일 ("kimi" 분기 추가)

- `Services/AgentRegistry.cs`: AgentDef(`Id="kimi"`,`DisplayName="Kimi"`,`Provider="Moonshot AI"`,`Command="kimi"`,`ExeNames=[kimi.cmd,kimi.ps1,kimi.exe,kimi]`,`ResumeFlag="-c"`(Tier1 generic 폴백 전용; 전용 분기는 `-S <id>`),`SupportsHooks=false`(전용 분기),`InlineTui=true`,`InstallCommand="npm install -g @moonshot-ai/kimi-code"`,`UpdateCommand="kimi upgrade"`).
- `Models/AgentImageConverter.cs` + csproj Resource(kimi.png).
- `Services/Terminal/TerminalSessionManager.cs`: `agent.Id=="kimi"` 런치분기 + `TryBuildKimiDirectLaunch`(EnsureInstalled + theme + cd/env/-S) + `TrySnapshotRoomSession` + `GracefulExitPlan`("kimi"→`("\x04",2, IsKimiBusyRunning)` = **Ctrl+D×2, busy면 Esc선행**) + `TryForkKimiSession`(세션 디렉터리 복사→새 session_<uuid>→state.json workDir/forkedFrom 갱신→session_index.jsonl append) + `CanSafelyResumeRoom` + `PurgeAppOwnedRoomArtifacts` + 고스트 GC + `KimiLaunchDir()`.
- `Services/SettingsService.cs`: `KimiRoomSessions` + Load/Save/Clear + `RemoveClaudeCodeRoomDir` 제거 + `ShowFooterKimi` 토글 + 클리너 스냅샷 튜플 확장.
- `MainWindow.xaml.cs`: `_kimiHook`/`_kimi`(usage)/`_lastKimi` 필드 + 이벤트 구독(busy→`NotifyIfSessionFinished`, waiting→`NotifyIfSessionWaiting`(❗), session 저장) + Start/Dispose + EnsureInstalled + 푸터 카드/패널/토글 + `HasBusyOrWaitingTrackingFile`.
- `Views/TerminalHostView.cs`: `IsAutoReenterRoom` 에 `"kimi"` 추가(IsShuttingDown 가드 기존).
- `Views/WorkspacePaneView.xaml.cs`: 포크 화이트리스트 + fork 저장 분기(`SaveKimiRoomSession`) + **KimiModelEffortDock**(grok식: config.toml `[models.*]` alias → `-m`, 모델만; effort 없음).
- `Services/SessionExporter.cs`: `FromKimi()`(wire.jsonl 파싱 → .md) + AgentLabel.
- `Services/SessionCleanerService.cs` + `Views/SettingsDialog.xaml(.cs)`: `CleanerAgentKind.Kimi` + XAML pill `KimiCatBtn` + 매핑 switch + `~/.kimi-code/sessions` 스캔(`session_*` id 추출) + 푸터 토글 체크박스 `ShowFooterKimiToggle`.
- `Resources/Terminal/web/terminal.html`: IME **B형 판정 시** `pinsLogicalCaret` 에 `kimi` 추가(실기 후 결정).
- `DevezCode.csproj`: 훅 스크립트 `<Content>` 2개 + 아이콘 `<Resource>`.
- `App.xaml.cs`: `KimiIconUri`(푸터/테마 아이콘) + 필요 시 시작 시 theme apply.
- `AGENTS.md`/`CLAUDE.md` 동기화 불필요(내용 무관), `.knowledge/에이전트추가규칙.md` 인덱스/본문에 kimi 반영.

## 6. 종료/재진입 계약
- `GracefulExitPlan("kimi")` = Ctrl+D(0x04) ×2, busy면 Esc 선행(스트리밍 인터럽트 후 종료). 실패 시 timeout→Job 하드정리.
- 앱 레벨 재진입: 사용자가 TUI 내부에서 종료키 입력 → cmd 종료 → ConPTY Exited → `IsAutoReenterRoom && !IsShuttingDown && AllowAutoRestart` → 새 ConPTY 로 `-S` 재기동.

## 7. 사용량 푸터 (정액제 %)
- `KimiUsageService`: 토큰 경로 `[~/.kimi-code/credentials/kimi-code.json, ~/.kimi/credentials/kimi-code.json]` 순차 시도 → 만료 임박 시 `auth.kimi.com` refresh_token 갱신 → `GET /coding/v1/usages` → `parseManagedUsagePayload` 로직 이식(weekly=summary, limits[]에서 5h/7d 창) → `ProviderUsage{Provider="kimi", Primary(5h %), Weekly(주간 %), PlanLabel}`.
- 표시는 **% only**(공식 CLI도 절대수 없음). 3분 폴링, DropGuard 스냅샷.

## 8. 최종 점검 시나리오 (에이전트추가규칙 §13 준거)
T1 피커/실행 · T2 복원/스냅샷/방삭제격리 · T3 busy·❗·lastmsg·재진입·테마 · 포크 · 내보내기 · 푸터 · 클리너 · **한글 IME 매트릭스(16)**.

## 9. 제약 (중요)
- **본 작업 세션이 DevezCode(PID 1428) 내부에서 실행 중** → 코드 작성·커밋은 세션이 수행하되, **빌드·실행·수동검증은 사용자가 직접**(앱 종료가 세션을 끊음). CLAUDE.md 최우선 규칙(강제종료 금지) 준수.

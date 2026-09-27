using DevezCode.Models;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DevezCode.Services;

/// <summary>앱에서 URL을 열 위치. 인앱 탭은 대상 프로젝트의 새 브라우저 탭으로 연다.</summary>
public enum TerminalUrlOpenTarget
{
    InAppBrowserTab,
    DefaultBrowser,
}

/// <summary>세션 토큰 사용량 비용($) 계산용 모델별 단가 규칙(외부 설정). settings.json 의 UsagePricing 배열에 담긴다.
/// Match 는 모델ID 부분일치(소문자, 예 "opus"·"gpt-5"), 단가는 100만 토큰당 달러. 위에서부터 먼저 맞는 규칙 사용.</summary>
public sealed class UsagePriceRule
{
    public string Match { get; set; } = "";
    public double InPerM { get; set; }
    public double OutPerM { get; set; }
    // 캐시 배수(입력 단가 대비). 생략(null)하면 표준 기본값: write 5m ×1.25, 1h ×2, read ×0.1.
    // 공급자/모델별로 캐시 요율이 다르면 여기서 규칙별로 덮어쓴다(예: codex cached 가 0.1× 아닐 때).
    public double? CacheWrite5m { get; set; }
    public double? CacheWrite1h { get; set; }
    public double? CacheRead { get; set; }
}

/// <summary>
/// 앱 설정 영속(터미널 폰트 크기 + Claude Code 방별 디렉터리/세션 추적).
/// %AppData%\DevezCode\settings.json 에 저장. 터미널 스택(TerminalSessionManager,
/// TerminalHostView)이 요구하는 API만 구현한 경량 버전.
/// </summary>
public static class SettingsService
{
    private sealed class SettingsData
    {
        public int TerminalFontSizePt { get; set; } = 0;
        // 빈 값이면 Windows Terminal 기본 프로필의 글꼴을 사용한다.
        public string TerminalFontFamily { get; set; } = "";
        // 방별 폰트 고정 마이그레이션 완료 여부. 이 수정 이전 세션은 방별 크기 기록이 없어
        // 최초 1회 현재 전역 기본값으로 고정한다(이후 기본값 변경이 기존 세션에 영향 없게).
        public bool TerminalRoomFontMigrated { get; set; } = false;
        // 마크다운 뷰어 본문 너비(px). 0=화면에 맞추기.
        public int MarkdownViewportWidth { get; set; } = 0;
        // 문서(md) 뷰어 테마. "auto"=앱 테마를 따라감, 그 외(minimal/soft/dark/gray/softpink/midnight)=고정.
        public string MarkdownTheme { get; set; } = "auto";
        // 웹브라우저 패널 색 구성. "system"=앱 테마를 따라감, "light"/"dark"=고정.
        public string BrowserTheme { get; set; } = "system";
        // 토큰 사용량 비용 단가(모델별). 비어 있으면 SessionUsageService 내장 기본값 사용.
        // 채우면 내장값보다 우선(덮어쓰기/신규 모델 추가). 갱신 절차: .knowledge/토큰사용량-단가-갱신.md
        public List<UsagePriceRule> UsagePricing { get; set; } = new();
        // 앱 전체 글꼴 크기 단계(devez 이식). 0=작게(기본), 1=크게(+2px). App.SetFontScale 에 전달.
        public int FontScale { get; set; } = 0;
        // 좌·우 패널 접힘 상태 + 펼침 시 복원 폭. 로컬 전용.
        public bool   LeftPanelCollapsed  { get; set; } = false;
        public bool   RightPanelCollapsed { get; set; } = false;
        public double SidebarWidth { get; set; } = 262;
        public double FileExpWidth { get; set; } = 300;
        // 최우측 계정 사용량 사이드바 펼침 상태. 기본 접힘.
        public bool   UsagePanelOpen { get; set; } = false;
        /// <summary>하단 터미널 패널 높이(px). 스플리터 드래그로 변경·저장.</summary>
        public double ShellTerminalHeight { get; set; } = 260;
        // 계정 사용량 오른쪽의 세션 완료 기록 사이드바 펼침 상태. 기본 접힘.
        public bool   SessionHistoryPanelOpen { get; set; } = false;
        // Claude Code를 터미널 TUI 대신 Agent SDK 기반 대화형 화면으로 실행. 기본 false=기존 터미널.
        public bool ClaudeGuiMode { get; set; } = false;
        // Agent SDK GUI의 간결한 Vibe 표시. true면 Shell·Diff·도구 실행 내역을 숨긴다.
        public bool ClaudeVibeMode { get; set; } = true;
        // Agent SDK GUI 슬래시 명령 목록에 설치된 Skill을 표시한다. 기본 false=숨김.
        public bool ClaudeShowSkills { get; set; } = false;
        // Agent SDK GUI 전체 확대 배율(Ctrl+휠/Ctrl+±). 앱 전체에서 공유한다.
        public double ClaudeGuiZoomFactor { get; set; } = 1.0;
        // 세션 완료 기록 "한줄만 보기"/"전체보기" 토글 상태. 기본 한줄만 보기(=false).
        public bool   ShowFullPrompt { get; set; } = false;
        // 세션 완료 기록 사이드바 너비 (드래그로 조절, settings.json 에 영속).
        public double SessionHistoryWidth { get; set; } = 218;
        // 플러그인 컨트롤러 우측 출력 영역 너비 (스플리터 드래그로 조절, settings.json 에 영속).
        public double PluginOutputWidth { get; set; } = 403;
        public Dictionary<string, string> ClaudeCodeRoomDirs { get; set; } = new();
        public Dictionary<string, string> ClaudeCodeRoomSessions { get; set; } = new();
        public List<string> ClaudeCodeRoomLaunched { get; set; } = new();
        // 방별 model/effort 선택 (claude --model / --effort 런치 플래그). 빈 값/미존재 = 미적용(claude 기본). 로컬 전용.
        public Dictionary<string, string> ClaudeCodeRoomModel { get; set; } = new();
        public Dictionary<string, string> ClaudeCodeRoomEffort { get; set; } = new();
        // Agent SDK GUI의 방별 권한 모드. 미지정 시 SDK 기본(default)을 사용한다.
        public Dictionary<string, string> ClaudeCodeRoomPermissionMode { get; set; } = new();
        // 비-Claude 방별 model/effort. 키는 "roomId|agentId"라 에이전트를 바꿔도 이전 선택이 섞이지 않는다.
        public Dictionary<string, string> AgentRoomModel { get; set; } = new();
        public Dictionary<string, string> AgentRoomEffort { get; set; } = new();
        // 방별 터미널 폰트 크기(pt) — 지정 없으면 전역 TerminalFontSizePt 사용. 에이전트 종류 무관.
        public Dictionary<string, string> TerminalRoomFontSizePt { get; set; } = new();
        // 범용 — "roomId|agentId" 키로 첫 실행 여부 추적. 비-Claude 에이전트도 같은 메커니즘으로
        // 첫 실행=plain, 이후=ResumeFlag(--last / -c) 분기. (Claude 는 별도 IsClaudeCodeRoomLaunched 그대로 사용)
        public List<string> AgentRoomsLaunched { get; set; } = new();
        // codex 방별 세션 ID (ClaudeCodeRoomSessions 와 동일 패턴). --session-id 첫 실행 → SessionStart 훅이 실제 ID 기록 → --resume 로 이어가기.
        public Dictionary<string, string> CodexRoomSessions { get; set; } = new();
        // opencode 방별 세션 ID (Devez 패턴 이식). 플러그인이 sessions\<room>.txt 에 기록한 최신 ID 를 영속.
        // 재오픈 시 `opencode --session <id>` 로 같은 대화 정확히 복원(같은 폴더의 여러 방도 분리).
        public Dictionary<string, string> OpenCodeRoomSessions { get; set; } = new();
        // 가재코드(gjc) 방별 세션 ID. gjc 는 사전 발급 플래그가 없어, 방별 격리 --session-dir 의
        // 최신 .jsonl 파일명에서 추출한 ID 를 영속 → 재오픈 시 `gjc -r <id>` 로 같은 대화 복원.
        public Dictionary<string, string> GajaeRoomSessions { get; set; } = new();
        // Grok 방별 세션 ID (훅 sessions\<room>.txt → `grok -r <id>`).
        public Dictionary<string, string> GrokRoomSessions { get; set; } = new();
        // 안티그래비티(agy) 방별 conversation ID. agy 는 사전 발급이 없어 cwd→conversation
        // 매핑(last_conversations.json)·훅에서 추종한 ID 를 영속 → `agy --conversation <id>` 복원.
        public Dictionary<string, string> AntigravityRoomSessions { get; set; } = new();
        // Kimi(kimi-code) 방별 세션 ID (session_&lt;uuid&gt;). SessionStart 훅이 기록한 ID 를 영속 →
        // 재오픈 시 `kimi -S <id>`(전역 session_index.jsonl, cwd 무관) 로 같은 대화 복원.
        public Dictionary<string, string> KimiRoomSessions { get; set; } = new();
        // Devez Vibe(dvz) 방별 스레드 ID. dvz 는 codex app-server 의 thread 를 그대로 쓰므로 값은
        // codex rollout 과 같은 UUID 다(별도 저장소인 이유: 같은 방을 codex/dvz 로 오가도 안 섞이게).
        // dvz 가 sessions\<room>.txt 에 기록한 ID 를 영속 → 재오픈 시 `dvz -r <id>` 로 같은 대화 복원.
        public Dictionary<string, string> DevezVibeRoomSessions { get; set; } = new();
        // 레거시(devezcli 시절) 스레드 ID 저장소. 로드 시 DevezVibeRoomSessions 로 1회 흡수하고 비운다.
        public Dictionary<string, string> DevezCliRoomSessions { get; set; } = new();
        // 세션 포크: 새 방(roomId) → 포크 원본 세션 ID. 새 방 첫 실행에 --fork-session/--fork 로 1회 소비.
        public Dictionary<string, string> RoomForkSources { get; set; } = new();
        // 방별 에이전트 ID (예: "claude", "codex"). 미설정이면 기본값(claude) — 기존 세션 호환.
        public Dictionary<string, string> RoomAgents { get; set; } = new();
        // 사용자가 활성화한 에이전트 ID 목록. 빈 값이면 모든 설치된 에이전트 활성화로 간주.
        public List<string> EnabledAgents { get; set; } = new();
        // 레거시 설정에는 필드가 없으므로 false+빈 목록만 "첫 실행 기본값"으로 해석한다.
        public bool EnabledAgentsConfigured { get; set; }
        // 자동 업데이트 대상 에이전트 ID 목록(옵트인). 미설정이면 '사용 중인 에이전트'를 기본값으로 본다.
        public List<string> AutoUpdateAgentIds { get; set; } = new();
        // 레거시 설정에는 필드가 없으므로 false+빈 목록만 "첫 실행 기본값"으로 해석한다.
        public bool AutoUpdateAgentIdsConfigured { get; set; }
        public List<WakeScheduleEntry> WakeSchedules { get; set; } = new();
        // 마지막으로 활성이던 프로젝트/세션. 정상 종료(CleanShutdown=true) 때만 복원한다.
        public string? LastActiveProjectPath { get; set; }
        public string? LastActiveSessionId { get; set; }
        public bool CleanShutdown { get; set; }
        // 중앙 패널 분할 상태(두 프로젝트 동시 열기). 활성 시 양쪽 패널의 프로젝트/세션을 복원한다.
        public bool SplitActive { get; set; }
        public string? SplitAProjectPath { get; set; }
        public string? SplitASessionId { get; set; }
        public string? SplitBProjectPath { get; set; }
        public string? SplitBSessionId { get; set; }
        // 분할 비율 (PaneB star 값). 1.0 = 50/50 동등 분할.
        public double SplitBStarRatio { get; set; } = 1.0;
        // PaneA/PaneB 좌우 위치 교환 여부
        public bool SplitSwapped { get; set; } = false;
        // 우측 패널 브라우저 — 프로젝트별 마지막 방문 URL(재시작 시 복원).
        // 키 = 프로젝트 절대경로. 프로젝트가 없거나 저장된 적 없으면 HomeUrl 로 폴백.
        // (하위호환 유지용. 신규 코드는 BrowserHistoryByProject 사용 — 단 마이그레이션 소스로 계속 읽힘.)
        public Dictionary<string, string> BrowserLastUrlByProject { get; set; } = new();
        // 우측 패널 브라우저 — 프로젝트별 가상 히스토리(뒤로/앞으로 복원).
        // WebView2 네이티브 히스토리는 외부 주입 불가하므로 앱이 방문 URL 스택+현재 위치를 직접 관리.
        // 키 = 프로젝트 절대경로.
        public Dictionary<string, BrowserHistoryEntry> BrowserHistoryByProject { get; set; } = new();
        // 우측 패널 작업 큐 — 프로젝트 경로별로 저장. 키 = 프로젝트 절대경로, 값 = 큐 항목 목록.
        // 로컬 전용 — 재시작 시 그대로 복원. 프로젝트 경로가 null/empty 면 "전역" 큐 (실제론 잘 안 씀).
        public Dictionary<string, List<TaskQueueEntry>> TaskQueueItemsByProject { get; set; } = new();
        // 작업 큐 입력창 사용자 지정 높이(MinHeight). 0/미설정이면 기본(자연 높이). 로컬 전용.
        public double TaskQueueInputMinHeight { get; set; }
        // 작업 큐 버블/입력 글꼴 크기(Ctrl+휠로 조절, devez BubbleFontSize 정합 10~28). 기본 14.
        public double TaskQueueBubbleFontSize { get; set; } = 14;
        // 프로젝트 선택 시 모든 세션을 미리 켤지 여부. 기본 false = 첫/활성 세션만 실행.
        public bool PreloadAllProjectSessions { get; set; } = false;
        // 표시하지 않은 유휴 세션 자동 종료 시간(분). 0=사용 안 함.
        // 구버전 설정에는 필드가 없어 0으로 역직렬화되므로 기능은 명시적으로 켠 경우에만 동작한다.
        public int IdleSessionShutdownMinutes { get; set; } = 0;
        // 프로그램 실행 시 마지막으로 보던 프로젝트를 자동으로 불러올지 여부. 기본 false = 미선택 상태로 시작.
        public bool AutoLoadLastProject { get; set; } = false;
        // 새 세션을 추가할 때 이름 입력 팝업을 바로 표시할지 여부. 기본 false = 자동 생성 이름 사용.
        public bool PromptForNewSessionName { get; set; } = false;
        // 새 브라우저 탭을 만들 때 이름 입력 팝업을 바로 표시할지 여부. 기본 false = 자동 생성 이름 사용.
        public bool PromptForNewBrowserTabName { get; set; } = false;
        // 세션(에이전트)이 MCP 도구로 내장 브라우저를 조작할 수 있게 할지. 켜면 각 에이전트 설정에
        // devez-browser MCP 서버를 등록한다. 기본 false = 세션은 브라우저에 접근 못 함.
        public bool BrowserMcpEnabled { get; set; } = false;
        // 브라우저 기록이 없는 새 탭의 첫 주소. 잘못된 구버전 값은 LoadBrowserHomeUrl에서 Google로 보정한다.
        public string BrowserHomeUrl { get; set; } = "https://www.google.com";
        // 미니 브라우저 창의 시작 주소. 방문 기록이 없을 때 이 주소를 연다.
        public string MiniBrowserHomeUrl { get; set; } = "https://claude.ai";
        // URL 링크 열기 방식. 기본은 대상 프로젝트의 인앱 브라우저 새 탭.
        public TerminalUrlOpenTarget TerminalUrlOpenTargetMode { get; set; } = TerminalUrlOpenTarget.InAppBrowserTab;
        // 새로 숨긴 세션을 숨김 목록 맨 위에 넣을지 여부. JSON 필드명은 기존 설정 호환을 위해 유지한다.
        // 기본 false = 맨 아래.
        public bool HiddenSessionsOnTop { get; set; } = false;
        // 세션 탭 위의 프로젝트 정보 헤더(MetaBar: 프로젝트명~effort) 숨김 여부. 기본 false = 표시.
        public bool HideProjectInfoHeader { get; set; } = false;
        // 프로그램 실행 시 켜진 에이전트 CLI 를 최신 버전으로 자동 업데이트할지 여부. 기본 true.
        public bool AutoUpdateAgents { get; set; } = true;
        // 시작 시 자동 업데이트를 마지막으로 실행한 날짜("yyyy-MM-dd"). 하루 1회만 동작하도록 게이트.
        public string LastAgentAutoUpdateDate { get; set; } = "";
        // Node.js 설치 안내를 마지막으로 표시한 날짜("yyyy-MM-dd"). 거절해도 같은 날에는 다시 묻지 않는다.
        public string LastNodeInstallPromptDate { get; set; } = "";
        // devez-marketplace/hoje-code 조용한 자동업데이트의 하루 1회 게이트(yyyy-MM-dd). 위 에이전트 게이트와 별개.
        public string LastDevezPluginUpdateDate { get; set; } = "";
        // 최대화 시 작업표시줄까지 덮는 전체화면 동작 여부. 기본 false = 작업영역에만 맞춤.
        public bool UseFullScreen { get; set; } = false;
        // X(닫기) 버튼으로 종료하지 않고 창을 최소화할지 여부. 기본 false = 닫기 시 종료.
        public bool MinimizeOnClose { get; set; } = false;
        // 좌측 프로젝트 목록 열 수(1 또는 2). 2면 좌측 패널 최소너비 2배 + 카드 2열 그리드 + 가로 드래그.
        public int ProjectColumns { get; set; } = 1;
        // 좌측 프로젝트 카드가 접혔을 때 경로 줄 표시 여부. 기본 true.
        public bool ShowCollapsedProjectPath { get; set; } = true;
        // 메인 창 위치/크기 + 최대화 상태(재시작 시 복원). 화면 밖이면 복원 안 함. 로컬 전용.
        public double? WindowLeft   { get; set; }
        public double? WindowTop    { get; set; }
        public double? WindowWidth  { get; set; }
        public double? WindowHeight { get; set; }
        public bool    WindowMaximized { get; set; }
        // 미니 브라우저 창 위치/크기(재개 시 복원). 화면 밖이면 복원 안 함. 로컬 전용.
        public double? MiniBrowserLeft   { get; set; }
        public double? MiniBrowserTop    { get; set; }
        public double? MiniBrowserWidth  { get; set; }
        public double? MiniBrowserHeight { get; set; }
        // 우측 패널 마지막 활성 탭 (0=탐색기, 1=브라우저, 2=DIFF, 3=작업 큐). 기본=3.
        public int FileExpActiveTab { get; set; } = 3;
        // 사이드 패널 뷰 전환 버튼 표시 여부. 기본=모두 표시.
        public bool ShowDirViewBtn     { get; set; } = true;  // 탐색기
        public bool ShowQueueViewBtn   { get; set; } = true;  // 작업 큐
        public bool ShowBrowserViewBtn { get; set; } = true;  // 브라우저
        public bool ShowDiffViewBtn    { get; set; } = true;  // DIFF
        // Diff 패널에서 git 기능(스테이징/되돌리기/상태글자/커밋·푸시·풀 컨트롤) 사용 여부. 기본 false=순수 diff 뷰어.
        public bool DiffGitEnabled     { get; set; } = false;
        // 계정 사용량 사이드바/툴팁에 한도 도달 예상 시간 표시. 기본 켜짐.
        public bool ShowEstimate { get; set; } = false;
        // 계정 사용량을 사용한 양 대신 남은 양(100%-사용률)으로 표시. 기본 꺼짐.
        public bool ShowRemainingUsage { get; set; } = false;
        // 탭 이동 전역 단축키(가상키코드). 기본 한자(0x19) + 좌(0x25)/우(0x27) 방향키.
        public int TabHotkeyModifierVk { get; set; } = 0x19;
        public int TabHotkeyPrevVk     { get; set; } = 0x25;
        public int TabHotkeyNextVk     { get; set; } = 0x27;
        // 세션 스피너가 멈출 때(응답 완료) 토스트 알림 표시 여부. 기본 켜짐.
        public bool NotifySessionDoneEnabled { get; set; } = true;
        // 세션 완료 기록 (재시작 시 복원). 최신순(index 0 = 가장 최근).
        public List<SessionCompletionRecord>? SessionHistoryRecords { get; set; }
        // 알림 토스트 표시 위치: "tl"=좌상단, "tr"=우상단, "bl"=좌하단, "br"=우하단. 기본 우하단.
        public string NotifyPosition { get; set; } = "br";
        // 알림을 표시할 모니터 DeviceName. 빈 값이면 주 모니터. (devez 이식)
        public string NotifyMonitorDevice { get; set; } = "";
        // 알림 자동 닫힘 시간(초). 1~10, 0=영구(자동 닫힘 없음). 기본 6초.
        public int NotifyAutoCloseSeconds { get; set; } = 6;
        // WPF GPU 하드웨어 가속. false 면 SoftwareOnly(원격/CRD 캡처 안정). 기본 true.
        // 시작 시 적용 + 타이틀 로고 세 번 클릭으로 런타임 전환. 원격 세션이면 시작 시 강제 소프트.
        public bool UseGpuAcceleration { get; set; } = true;
    }

    private static readonly object _lock = new();
    private static SettingsData? _current;
    private static readonly JsonSerializerOptions SaveJsonOptions = new() { WriteIndented = true };
    private static readonly CoalescingSaveQueue<string> SaveQueue = new(WriteSnapshot);

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "settings.json");

    private static SettingsData Current
    {
        get
        {
            lock (_lock)
            {
                if (_current != null) return _current;
                // 본 파일 → .bak 순으로 읽되 역직렬화 성공해야 유효. 손상 시 원본 격리.
                var text = AtomicFile.ReadValidated(SettingsPath, IsParseable, out _);
                if (text != null)
                {
                    try { _current = JsonSerializer.Deserialize<SettingsData>(text); }
                    catch { /* 손상 시 새로 시작 */ }
                }
                if (_current != null && MigrateDevezVibeRename(_current)) _ = TrySave();
                return _current ??= new SettingsData();
            }
        }
    }

    /// <summary>Devez CLI → Devez Vibe 개명 1회 마이그레이션. 에이전트 ID 가 settings 전반의 키/값에
    /// 들어가 있어(방별 에이전트·활성 목록·"room|agent" 키) 그대로 두면 기존 방이 에이전트를 잃는다.
    /// 이미 마이그레이션된 설정에서는 아무것도 바꾸지 않으므로 매 로드 호출해도 안전하다.</summary>
    private static bool MigrateDevezVibeRename(SettingsData d)
    {
        const string OldId = "devezcli";
        const string NewId = "devezvibe";
        bool changed = false;

        foreach (var roomId in d.RoomAgents.Where(kv => OldId.Equals(kv.Value, StringComparison.OrdinalIgnoreCase))
                                           .Select(kv => kv.Key).ToList())
        {
            d.RoomAgents[roomId] = NewId;
            changed = true;
        }

        for (int i = 0; i < d.EnabledAgents.Count; i++)
        {
            if (!OldId.Equals(d.EnabledAgents[i], StringComparison.OrdinalIgnoreCase)) continue;
            d.EnabledAgents[i] = d.EnabledAgents.Contains(NewId, StringComparer.OrdinalIgnoreCase) ? "" : NewId;
            changed = true;
        }
        if (changed) d.EnabledAgents.RemoveAll(string.IsNullOrEmpty);

        // "roomId|agentId" 복합 키(첫 실행 여부·모델·effort).
        for (int i = 0; i < d.AgentRoomsLaunched.Count; i++)
        {
            var renamed = RenameAgentSuffix(d.AgentRoomsLaunched[i], OldId, NewId);
            if (renamed == null) continue;
            d.AgentRoomsLaunched[i] = renamed;
            changed = true;
        }
        foreach (var map in new[] { d.AgentRoomModel, d.AgentRoomEffort })
        {
            foreach (var key in map.Keys.ToList())
            {
                var renamed = RenameAgentSuffix(key, OldId, NewId);
                if (renamed == null || map.ContainsKey(renamed)) continue;
                map[renamed] = map[key];
                map.Remove(key);
                changed = true;
            }
        }

        // 스레드 ID: 새 저장소에 없는 방만 옮긴다(이미 새 이름으로 돌린 세션이 우선).
        if (d.DevezCliRoomSessions.Count > 0)
        {
            foreach (var kv in d.DevezCliRoomSessions)
                if (!d.DevezVibeRoomSessions.ContainsKey(kv.Key)) d.DevezVibeRoomSessions[kv.Key] = kv.Value;
            d.DevezCliRoomSessions.Clear();
            changed = true;
        }
        return changed;
    }

    /// <summary>"roomId|devezcli" → "roomId|devezvibe". 해당 없으면 null.</summary>
    private static string? RenameAgentSuffix(string key, string oldId, string newId)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var cut = key.LastIndexOf('|');
        if (cut < 0) return null;
        return key.AsSpan(cut + 1).Equals(oldId, StringComparison.OrdinalIgnoreCase)
            ? key[..(cut + 1)] + newId
            : null;
    }

    private static bool IsParseable(string text)
    {
        try { return JsonSerializer.Deserialize<SettingsData>(text) != null; }
        catch { return false; }
    }

    private static string SerializeCurrent()
        => JsonSerializer.Serialize(_current, SaveJsonOptions);

    private static bool WriteSnapshot(string json)
    {
        try
        {
            AtomicFile.WriteAllText(SettingsPath, json);
            return true;
        }
        catch (Exception ex)
        {
            DiagLog.Write($"Settings save failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool TrySave()
    {
        long version;
        lock (_lock) version = SaveQueue.Enqueue(SerializeCurrent());
        return SaveQueue.Flush(version);
    }

    private static void Save()
    {
        _ = TrySave();
    }

    private static void SaveDeferred()
    {
        try
        {
            lock (_lock) SaveQueue.Enqueue(SerializeCurrent());
        }
        catch { /* 전환 중 비핵심 영속 실패는 호출자 동작을 막지 않는다 */ }
    }

    /// <summary>정상 종료 전에 큐에 남은 최신 설정을 디스크까지 반영한다.</summary>
    public static bool FlushPendingSaves() => SaveQueue.FlushAll();
    // ── 터미널 글꼴 ───────────────────────────────────────────────
    /// <summary>실행 중인 WebView2 터미널에 글꼴 변경을 즉시 전달한다. 빈 값은 Windows Terminal 기본값이다.</summary>
    public static event Action<string>? TerminalFontFamilyChanged;

    /// <summary>숨겨졌던 WebView2 표면을 다시 표시한 뒤 글꼴 렌더링을 갱신한다.</summary>
    public static event Action? TerminalFontRenderRefreshRequested;

    public static string LoadTerminalFontFamily() => Current.TerminalFontFamily?.Trim() ?? "";
    public static void SaveTerminalFontFamily(string? family)
    {
        var next = family?.Trim() ?? "";
        if (string.Equals(Current.TerminalFontFamily, next, StringComparison.Ordinal)) return;
        Current.TerminalFontFamily = next;
        Save();
        TerminalFontFamilyChanged?.Invoke(next);
    }

    /// <summary>현재 글꼴 값을 다시 전달해 열린 터미널과 Claude GUI의 글꼴 렌더링을 갱신한다.</summary>
    public static void RefreshTerminalFontRendering()
        => TerminalFontRenderRefreshRequested?.Invoke();

    // ── 터미널 글꼴 크기 ──────────────────────────────────────────
    public static int LoadTerminalFontSizePt() => Current.TerminalFontSizePt;
    public static void SaveTerminalFontSizePt(int pt) { Current.TerminalFontSizePt = pt; Save(); }

    // ── Claude Code 실행 화면 ─────────────────────────────────────
    public static bool LoadClaudeGuiMode() => Current.ClaudeGuiMode;
    public static void SaveClaudeGuiMode(bool value) { Current.ClaudeGuiMode = value; Save(); }
    public static bool LoadClaudeVibeMode() => Current.ClaudeVibeMode;
    public static void SaveClaudeVibeMode(bool value) { Current.ClaudeVibeMode = value; Save(); }
    public static bool LoadClaudeShowSkills() => Current.ClaudeShowSkills;
    public static void SaveClaudeShowSkills(bool value) { Current.ClaudeShowSkills = value; Save(); }
    public static double LoadClaudeGuiZoomFactor() => Math.Clamp(Current.ClaudeGuiZoomFactor, 0.8, 2.4);
    public static void SaveClaudeGuiZoomFactor(double value)
    {
        Current.ClaudeGuiZoomFactor = Math.Clamp(value, 0.8, 2.4);
        Save();
    }

    // ── 마크다운 뷰어 본문 너비 ────────────────────────────────────
    public static event Action<int>? MarkdownViewportWidthChanged;
    public static int LoadMarkdownViewportWidth() => System.Math.Max(0, Current.MarkdownViewportWidth);
    public static void SaveMarkdownViewportWidth(int width)
    {
        width = System.Math.Max(0, width);
        if (Current.MarkdownViewportWidth == width) return;
        Current.MarkdownViewportWidth = width;
        Save();
        MarkdownViewportWidthChanged?.Invoke(width);
    }

    // ── 문서(md) 뷰어 테마 ("auto"=앱 테마 따라감) ────────────────
    public static event Action? MarkdownThemeChanged;
    public static string LoadMarkdownTheme()
    {
        var t = Current.MarkdownTheme?.Trim();
        return string.IsNullOrEmpty(t) ? "auto" : t;
    }
    public static void SaveMarkdownTheme(string theme)
    {
        theme = string.IsNullOrWhiteSpace(theme) ? "auto" : theme.Trim();
        if (Current.MarkdownTheme == theme) return;
        Current.MarkdownTheme = theme;
        Save();
        MarkdownThemeChanged?.Invoke();
    }

    // ── 웹브라우저 색 구성 ("system"=앱 테마 따라감 / "light" / "dark") ──
    public static event Action? BrowserThemeChanged;
    public static string LoadBrowserTheme()
    {
        var t = Current.BrowserTheme?.Trim().ToLowerInvariant();
        return t is "light" or "dark" ? t : "system";
    }
    public static void SaveBrowserTheme(string theme)
    {
        theme = theme?.Trim().ToLowerInvariant() is "light" or "dark" ? theme!.Trim().ToLowerInvariant() : "system";
        if (LoadBrowserTheme() == theme) return;
        Current.BrowserTheme = theme;
        Save();
        BrowserThemeChanged?.Invoke();
    }

    // ── 앱 전체 글꼴 크기 단계 (devez 이식: 0=작게, 1=크게) ───────
    public static int LoadFontScale() => Current.FontScale;
    public static void SaveFontScale(int v) { Current.FontScale = v; Save(); }

    // ── GPU 하드웨어 가속 (false = SoftwareOnly, 원격/CRD용) ─────
    public static bool LoadUseGpuAcceleration() => Current.UseGpuAcceleration;
    public static void SaveUseGpuAcceleration(bool v) { Current.UseGpuAcceleration = v; Save(); }

    // ── 토큰 사용량 비용 단가(외부 설정). 갱신 절차: .knowledge/토큰사용량-단가-갱신.md ──
    public static List<UsagePriceRule> LoadUsagePricing() => Current.UsagePricing;

    // ── 좌·우 패널 접힘 상태 + 복원 폭 ───────────────────────────
    public static bool   LoadLeftPanelCollapsed()  => Current.LeftPanelCollapsed;
    public static bool   LoadRightPanelCollapsed() => Current.RightPanelCollapsed;
    public static double LoadSidebarWidth() => Current.SidebarWidth;
    public static double LoadFileExpWidth() => Current.FileExpWidth;
    public static double LoadSessionHistoryWidth() => Current.SessionHistoryWidth;

    public static void SaveLeftPanel(bool collapsed, double width)
    {
        Current.LeftPanelCollapsed = collapsed;
        Current.SidebarWidth = width;
        Save();
    }

    public static void SaveRightPanel(bool collapsed, double width)
    {
        Current.RightPanelCollapsed = collapsed;
        Current.FileExpWidth = width;
        Save();
    }

    public static void SaveSessionHistoryWidth(double width)
    {
        Current.SessionHistoryWidth = width;
        Save();
    }

    // ── 플러그인 컨트롤러 우측 출력 영역 너비 ────────────────────
    public static double LoadPluginOutputWidth() => Current.PluginOutputWidth;
    public static void SavePluginOutputWidth(double width)
    {
        Current.PluginOutputWidth = width;
        Save();
    }

    // ── 최우측 계정 사용량 사이드바 펼침 상태 ────────────────────
    public static bool LoadUsagePanelOpen() => Current.UsagePanelOpen;
    public static void SaveUsagePanelOpen(bool open) { Current.UsagePanelOpen = open; Save(); }

    // ── 하단 터미널 패널 높이 ─────────────────────────────────────
    public static double LoadShellTerminalHeight() => Math.Max(120, Current.ShellTerminalHeight);
    public static void SaveShellTerminalHeight(double h) { Current.ShellTerminalHeight = h; Save(); }

    // ── 세션 완료 기록 사이드바 펼침 상태 ───────────────────────
    public static bool LoadSessionHistoryPanelOpen() => Current.SessionHistoryPanelOpen;
    public static void SaveSessionHistoryPanelOpen(bool open) { Current.SessionHistoryPanelOpen = open; Save(); }

    // ── 세션 완료 기록 목록 영속 ────────────────────────────────
    public static List<SessionCompletionRecord>? LoadSessionHistoryRecords()
        => Current.SessionHistoryRecords;

    public static void SaveSessionHistoryRecords(List<SessionCompletionRecord> records, int maxCount)
    {
        while (records.Count > maxCount) records.RemoveAt(records.Count - 1);
        Current.SessionHistoryRecords = records;
        Save();
    }

    // ── 세션 완료 기록 "한줄만 보기"/"전체보기" 토글 상태 ──────────────
    public static bool LoadShowFullPrompt() => Current.ShowFullPrompt;
    public static void SaveShowFullPrompt(bool full) { Current.ShowFullPrompt = full; Save(); }


    // ── 우측 패널 활성 탭 ─────────────────────────────────────────
    public static int LoadFileExpActiveTab() => Current.FileExpActiveTab;
    public static void SaveFileExpActiveTab(int idx) { Current.FileExpActiveTab = idx; Save(); }

    // ── 사이드 패널 뷰 전환 버튼 표시 여부 ────────────────────────
    public static bool LoadShowDirViewBtn()     => Current.ShowDirViewBtn;
    public static bool LoadShowQueueViewBtn()   => Current.ShowQueueViewBtn;
    public static bool LoadShowBrowserViewBtn() => Current.ShowBrowserViewBtn;
    public static bool LoadShowDiffViewBtn()    => Current.ShowDiffViewBtn;
    public static void SaveShowDirViewBtn(bool v)     { Current.ShowDirViewBtn     = v; Save(); }
    public static void SaveShowQueueViewBtn(bool v)   { Current.ShowQueueViewBtn   = v; Save(); }
    public static void SaveShowBrowserViewBtn(bool v) { Current.ShowBrowserViewBtn = v; Save(); }
    public static void SaveShowDiffViewBtn(bool v)    { Current.ShowDiffViewBtn    = v; Save(); }
    public static bool LoadDiffGitEnabled() => Current.DiffGitEnabled;
    public static void SaveDiffGitEnabled(bool v) { Current.DiffGitEnabled = v; Save(); }

    // ── 계정 사용량 한도 도달 예상 표시 ──────────────────────────
    public static bool LoadShowEstimate() => Current.ShowEstimate;
    public static void SaveShowEstimate(bool v) { Current.ShowEstimate = v; Save(); }

    // ── 계정 사용량 남은 수치 표시 ────────────────────────────────
    public static bool LoadShowRemainingUsage() => Current.ShowRemainingUsage;
    public static void SaveShowRemainingUsage(bool v) { Current.ShowRemainingUsage = v; Save(); }

    // ── 탭 이동 전역 단축키 (수정자 + 이전/다음 키, 가상키코드) ──────
    public static (int mod, int prev, int next) LoadTabHotkey()
        => (Current.TabHotkeyModifierVk, Current.TabHotkeyPrevVk, Current.TabHotkeyNextVk);

    public static void SaveTabHotkey(int mod, int prev, int next)
    {
        Current.TabHotkeyModifierVk = mod;
        Current.TabHotkeyPrevVk = prev;
        Current.TabHotkeyNextVk = next;
        Save();
    }

    // ── 세션 종료 알림 (스피너 멈출 때 토스트) ────────────────────
    public static bool LoadNotifySessionDoneEnabled() => Current.NotifySessionDoneEnabled;
    public static void SaveNotifySessionDoneEnabled(bool v) { Current.NotifySessionDoneEnabled = v; Save(); }

    /// <summary>알림 표시 위치("tl"/"tr"/"bl"/"br"). 알 수 없는 값은 "br" 로 폴백.</summary>
    public static string LoadNotifyPosition()
        => Current.NotifyPosition is "tl" or "tr" or "bl" or "br" ? Current.NotifyPosition : "br";
    public static void SaveNotifyPosition(string v) { Current.NotifyPosition = v; Save(); }

    /// <summary>알림 표시 모니터 DeviceName. 빈 값이면 주 모니터.</summary>
    public static string LoadNotifyMonitorDevice() => Current.NotifyMonitorDevice;
    public static void SaveNotifyMonitorDevice(string v) { Current.NotifyMonitorDevice = v ?? ""; Save(); }

    /// <summary>알림 자동 닫힘 시간(초). 0=영구, 그 외 1~10 범위로 클램프.</summary>
    public static int LoadNotifyAutoCloseSeconds()
    {
        var s = Current.NotifyAutoCloseSeconds;
        if (s <= 0) return 0;
        return s > 10 ? 10 : s;
    }
    public static void SaveNotifyAutoCloseSeconds(int v) { Current.NotifyAutoCloseSeconds = v < 0 ? 0 : (v > 10 ? 10 : v); Save(); }

    // ── Claude Code 방 작업 디렉터리 ──────────────────────────────
    // 아래 방별 dict/list 들은 claude 세션 워처(FileSystemWatcher = 비-UI 스레드)와 UI 스레드가
    // 동시에 접근한다. Dictionary/List 는 스레드 안전하지 않고 Save() 가 _current 를 직렬화(읽기)하므로,
    // 접근을 _lock 으로 감싸 Save 직렬화와 상호배제한다(_lock 은 재진입 가능 → 내부 Save() 안전).
    public static string? LoadClaudeCodeRoomDir(string roomId)
    { lock (_lock) return Current.ClaudeCodeRoomDirs.TryGetValue(roomId, out var d) ? d : null; }

    public static void SaveClaudeCodeRoomDir(string roomId, string dir)
    {
        lock (_lock)
        {
            if (Current.ClaudeCodeRoomDirs.TryGetValue(roomId, out var current)
                && string.Equals(current, dir, StringComparison.Ordinal))
                return;
            Current.ClaudeCodeRoomDirs[roomId] = dir;
            Save();
        }
    }

    /// <summary>새 방의 경로와 에이전트를 한 스냅샷으로 저장해 중복 원자 쓰기를 피한다.</summary>
    public static void SaveRoomRegistration(string roomId, string dir, string agentId)
    {
        lock (_lock)
        {
            bool changed = false;
            if (!Current.ClaudeCodeRoomDirs.TryGetValue(roomId, out var currentDir)
                || !string.Equals(currentDir, dir, StringComparison.Ordinal))
            {
                Current.ClaudeCodeRoomDirs[roomId] = dir;
                changed = true;
            }

            var normalizedAgent = AgentRegistry.NormalizeId(agentId);
            if (!Current.RoomAgents.TryGetValue(roomId, out var currentAgent)
                || !string.Equals(currentAgent, normalizedAgent, StringComparison.Ordinal))
            {
                Current.RoomAgents[roomId] = normalizedAgent;
                changed = true;
            }

            if (changed) SaveDeferred();
        }
    }

    /// <summary>여러 방의 작업 디렉터리를 한 번에 갱신한다. 프리로드처럼 같은 경로를 반복 저장하는
    /// 경로에서 settings.json 원자적 쓰기를 방마다 수행하지 않도록 변경분을 한 번만 저장한다.</summary>
    public static void SaveClaudeCodeRoomDirs(IEnumerable<string> roomIds, string dir)
    {
        var ids = roomIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) return;

        lock (_lock)
        {
            bool changed = false;
            foreach (var roomId in ids)
            {
                if (Current.ClaudeCodeRoomDirs.TryGetValue(roomId, out var current)
                    && string.Equals(current, dir, StringComparison.Ordinal))
                    continue;
                Current.ClaudeCodeRoomDirs[roomId] = dir;
                changed = true;
            }
            if (changed) Save();
        }
    }

    public static void RemoveClaudeCodeRoomDir(string roomId)
    {
      lock (_lock)
      {
        bool changed = Current.ClaudeCodeRoomDirs.Remove(roomId);
        changed |= Current.ClaudeCodeRoomSessions.Remove(roomId);
        changed |= Current.ClaudeCodeRoomLaunched.Remove(roomId);
        changed |= Current.RoomAgents.Remove(roomId);
        changed |= Current.ClaudeCodeRoomModel.Remove(roomId);
        changed |= Current.ClaudeCodeRoomEffort.Remove(roomId);
        changed |= Current.ClaudeCodeRoomPermissionMode.Remove(roomId);
        changed |= RemoveWhere(Current.AgentRoomModel, k => k.StartsWith(roomId + "|", StringComparison.Ordinal));
        changed |= RemoveWhere(Current.AgentRoomEffort, k => k.StartsWith(roomId + "|", StringComparison.Ordinal));
        changed |= Current.TerminalRoomFontSizePt.Remove(roomId);
        changed |= Current.CodexRoomSessions.Remove(roomId);
        changed |= Current.OpenCodeRoomSessions.Remove(roomId);
        changed |= Current.GajaeRoomSessions.Remove(roomId);
        changed |= Current.GrokRoomSessions.Remove(roomId);
        changed |= Current.AntigravityRoomSessions.Remove(roomId);
        changed |= Current.KimiRoomSessions.Remove(roomId);
        changed |= Current.DevezVibeRoomSessions.Remove(roomId);
        changed |= Current.RoomForkSources.Remove(roomId);
        changed |= Current.AgentRoomsLaunched.RemoveAll(k => k.StartsWith(roomId + "|", StringComparison.Ordinal)) > 0;
        if (changed) Save();
      }
    }
    /// <summary>클리너 보호 목록용: DevezCode 가 현재 관리 중인 room/session ID 스냅샷.</summary>
    public static (IReadOnlyCollection<string> Claude, IReadOnlyCollection<string> OpenCode, IReadOnlyCollection<string> Gajae, IReadOnlyCollection<string> Codex, IReadOnlyCollection<string> Grok, IReadOnlyCollection<string> Antigravity, IReadOnlyCollection<string> Kimi, IReadOnlyCollection<string> Rooms)
        LoadManagedSessionSnapshot()
    {
        lock (_lock)
        {
            // dvz 세션의 실체는 백엔드마다 다른 저장소다(codex rollout / claude transcript /
            // opencode 내부). ID 접두사로 갈라 각 백엔드 보호 목록에 넣지 않으면, 예컨대
            // claude 백엔드로 이어온 방의 transcript 가 claude 정리에 지워진다.
            var devezVibe = Current.DevezVibeRoomSessions.Values
                .Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            var devezVibeClaude = devezVibe
                .Where(v => v.StartsWith("claude:", StringComparison.Ordinal))
                .Select(DevezVibeStateService.StripBackendPrefix);
            var devezVibeOpenCode = devezVibe
                .Where(v => v.StartsWith("ses_", StringComparison.Ordinal));
            var devezVibeCodex = devezVibe
                .Where(v => !v.StartsWith("claude:", StringComparison.Ordinal)
                    && !v.StartsWith("ses_", StringComparison.Ordinal));

            return (
                Current.ClaudeCodeRoomSessions.Values.Concat(devezVibeClaude)
                    .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Current.OpenCodeRoomSessions.Values.Concat(devezVibeOpenCode)
                    .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Current.GajaeRoomSessions.Values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList(),
                Current.CodexRoomSessions.Values.Concat(devezVibeCodex)
                    .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Current.GrokRoomSessions.Values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList(),
                Current.AntigravityRoomSessions.Values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList(),
                Current.KimiRoomSessions.Values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList(),
                Current.ClaudeCodeRoomDirs.Keys.Where(v => !string.IsNullOrWhiteSpace(v)).ToList()
            );
        }
    }

    // ── 방별 에이전트 ID (미설정 시 기본값 claude) ────────────────
    public static string LoadAgentForRoom(string roomId)
        => Current.RoomAgents.TryGetValue(roomId, out var a)
            ? AgentRegistry.NormalizeId(a) : AgentRegistry.DefaultAgentId;

    public static void SaveAgentForRoom(string roomId, string agentId)
    {
        lock (_lock)
        {
            if (Current.RoomAgents.TryGetValue(roomId, out var current)
                && string.Equals(current, agentId, StringComparison.Ordinal))
                return;
            Current.RoomAgents[roomId] = agentId;
            Save();
        }
    }

    // ── 사용자가 활성화한 에이전트 목록 ────────────────────────────
    /// <summary>빈 값이면 HiddenFromUI·opencode 제외 에이전트를 활성화한 것으로 간주(첫 실행 기본값).</summary>
    public static IReadOnlyList<string> LoadEnabledAgents()
    {
        var current = Current;
        var list = current.EnabledAgents;
        if (list.Count == 0 && !current.EnabledAgentsConfigured)
            return AgentRegistry.All
                .Where(a => a.Id != "opencode" && a.PreviewNote.Length == 0
                            && !AgentRegistry.HiddenFromUI.Contains(a.Id))
                .Select(a => a.Id).ToList();
        return list;
    }

    public static void SaveEnabledAgents(IEnumerable<string> agents)
    {
        Current.EnabledAgents = agents.ToList();
        Current.EnabledAgentsConfigured = true;
        Save();
    }
    // ── wake 일정 ──────────────────────────────────────────────────
    public static IReadOnlyList<WakeScheduleEntry> LoadWakeSchedules()
    {
        lock (_lock)
        {
            Current.WakeSchedules ??= new();
            return Current.WakeSchedules.Where(e => e is not null).Select(e => e.Clone()).ToList();
        }
    }

    public static bool SaveWakeSchedules(IEnumerable<WakeScheduleEntry> schedules)
    {
        if (schedules is null) throw new ArgumentNullException(nameof(schedules));
        lock (_lock)
        {
            Current.WakeSchedules ??= new();
            var previous = Current.WakeSchedules;
            var existingById = previous
                .Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Id))
                .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var replacement = new List<WakeScheduleEntry>();
            foreach (var schedule in schedules)
            {
                var copy = schedule.Clone();
                if (existingById.TryGetValue(copy.Id, out var existing))
                {
                    // 스케줄러가 소유하는 실행 메타데이터는 열린 편집기의 오래된 복사본으로 덮지 않는다.
                    copy.LastOccurrenceKey = existing.LastOccurrenceKey;
                    copy.LastExecutedAt = existing.LastExecutedAt;
                    copy.LastResult = existing.LastResult;
                }
                replacement.Add(copy);
            }

            Current.WakeSchedules = replacement;
            if (TrySave()) return true;
            Current.WakeSchedules = previous;
            return false;
        }
    }

    public static bool TryClaimWakeSchedule(string scheduleId, string occurrenceKey)
    {
        if (string.IsNullOrWhiteSpace(scheduleId) || string.IsNullOrWhiteSpace(occurrenceKey))
            return false;

        lock (_lock)
        {
            Current.WakeSchedules ??= new();
            var entry = Current.WakeSchedules.FirstOrDefault(e =>
                string.Equals(e.Id, scheduleId, StringComparison.OrdinalIgnoreCase));
            if (entry is null || !entry.Enabled ||
                string.Equals(entry.LastOccurrenceKey, occurrenceKey, StringComparison.Ordinal))
                return false;

            var previousKey = entry.LastOccurrenceKey;
            entry.LastOccurrenceKey = occurrenceKey;
            if (TrySave()) return true;
            entry.LastOccurrenceKey = previousKey;
            return false;
        }
    }

    public static bool TrySaveWakeResult(string scheduleId, string occurrenceKey, string executedAt, string result)
    {
        lock (_lock)
        {
            Current.WakeSchedules ??= new();
            var entry = Current.WakeSchedules.FirstOrDefault(e =>
                string.Equals(e.Id, scheduleId, StringComparison.OrdinalIgnoreCase));
            if (entry is null || !string.Equals(entry.LastOccurrenceKey, occurrenceKey, StringComparison.Ordinal))
                return false;

            var previousExecutedAt = entry.LastExecutedAt;
            var previousResult = entry.LastResult;
            entry.LastExecutedAt = executedAt ?? "";
            entry.LastResult = result ?? "";
            if (TrySave()) return true;
            entry.LastExecutedAt = previousExecutedAt;
            entry.LastResult = previousResult;
            return false;
        }
    }

    // ── Claude 세션 ID ────────────────────────────────────────────
    public static string? LoadClaudeCodeRoomSession(string roomId)
    { lock (_lock) return Current.ClaudeCodeRoomSessions.TryGetValue(roomId, out var s) ? s : null; }

    public static void SaveClaudeCodeRoomSession(string roomId, string sessionId)
    {
        lock (_lock) { Current.ClaudeCodeRoomSessions[roomId] = sessionId; Save(); }
    }

    /// <summary>지정 방을 제외한 모든 claude 방의 현재 세션 ID 집합(소문자). 세션 삭제(PurgeRoom)가
    /// cwd 공유 프로젝트에서 살아있는 다른 방의 transcript 를 실수로 지우지 않는지 검사하는 용도.</summary>
    public static HashSet<string> ClaudeSessionIdsExcept(string exceptRoomId)
    {
        lock (_lock)
            return Current.ClaudeCodeRoomSessions
                .Where(kv => !string.Equals(kv.Key, exceptRoomId, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.ToLowerInvariant())
                .ToHashSet();
    }

    /// <summary>방의 추적 세션 ID 를 제거. transcript 없는 빈 세션 ID 고착을 풀 때 호출.</summary>
    public static void RemoveClaudeCodeRoomSession(string roomId)
    {
        lock (_lock) { if (Current.ClaudeCodeRoomSessions.Remove(roomId)) Save(); }
    }

    // ── 세션 포크 소스 ────────────────────────────────────────────
    /// <summary>새 방의 포크 원본 세션 ID 저장(첫 실행에 --fork-session/--fork 로 1회 소비).</summary>
    public static void SaveRoomForkSource(string roomId, string sourceSessionId)
    { lock (_lock) { Current.RoomForkSources[roomId] = sourceSessionId; Save(); } }

    public static string? LoadRoomForkSource(string roomId)
    { lock (_lock) return Current.RoomForkSources.TryGetValue(roomId, out var s) ? s : null; }

    /// <summary>포크 마커 소비(제거). 첫 실행 후 재-포크 방지.</summary>
    public static void RemoveRoomForkSource(string roomId)
    { lock (_lock) { if (Current.RoomForkSources.Remove(roomId)) Save(); } }

    // ── 방별 model/effort (claude --model / --effort) ─────────────
    public static string? LoadClaudeCodeRoomModel(string roomId)
        => Current.ClaudeCodeRoomModel.TryGetValue(roomId, out var v) && !string.IsNullOrEmpty(v) ? v : null;
    public static void SaveClaudeCodeRoomModel(string roomId, string? value)
    { SetOrRemove(Current.ClaudeCodeRoomModel, roomId, value); Save(); }

    public static string? LoadClaudeCodeRoomEffort(string roomId)
        => Current.ClaudeCodeRoomEffort.TryGetValue(roomId, out var v) && IsSupportedClaudeCodeEffort(v) ? v : null;
    public static void SaveClaudeCodeRoomEffort(string roomId, string? value)
    { SetOrRemove(Current.ClaudeCodeRoomEffort, roomId, IsSupportedClaudeCodeEffort(value) ? value : null); Save(); }

    public static bool IsSupportedClaudeCodeEffort(string? value)
        => value is "low" or "medium" or "high" or "xhigh" or "max";

    public static string LoadClaudeCodeRoomPermissionMode(string roomId)
        => Current.ClaudeCodeRoomPermissionMode.TryGetValue(roomId, out var value)
           && ClaudeGlobalSettings.IsSupportedPermissionMode(value)
            ? value
            : ClaudeGlobalSettings.GetDefaultPermissionMode();

    public static void SaveClaudeCodeRoomPermissionMode(string roomId, string? value)
    { SetOrRemove(Current.ClaudeCodeRoomPermissionMode, roomId, ClaudeGlobalSettings.IsSupportedPermissionMode(value) ? value : null); Save(); }

    public static string? LoadAgentRoomModel(string roomId, string agentId)
    {
        lock (_lock)
        {
            var key = roomId + "|" + agentId;
            return Current.AgentRoomModel.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : null;
        }
    }

    public static void SaveAgentRoomModel(string roomId, string agentId, string? value)
    {
        lock (_lock) { SetOrRemove(Current.AgentRoomModel, roomId + "|" + agentId, value); Save(); }
    }

    public static string? LoadAgentRoomEffort(string roomId, string agentId)
    {
        lock (_lock)
        {
            var key = roomId + "|" + agentId;
            return Current.AgentRoomEffort.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : null;
        }
    }

    public static void SaveAgentRoomEffort(string roomId, string agentId, string? value)
    {
        lock (_lock) { SetOrRemove(Current.AgentRoomEffort, roomId + "|" + agentId, value); Save(); }
    }

    public static int? LoadTerminalRoomFontSizePt(string roomId)
        => Current.TerminalRoomFontSizePt.TryGetValue(roomId, out var v) && int.TryParse(v, out var pt) ? pt : null;

    /// <summary>이 수정 이전에 만든 세션들을 현재 전역 기본 크기로 최초 1회 고정한다.
    /// 방별 override 가 이미 있는 방은 건드리지 않는다(사용자가 지정한 값 존중).
    /// 이후 설정에서 기본값을 바꿔도 기존 세션은 이 크기를 유지하고 새 세션만 새 기본값으로 열린다.</summary>
    public static void MigrateExistingRoomFontSizesOnce(IEnumerable<string> roomIds, int defaultPt)
    {
        lock (_lock)
        {
            if (Current.TerminalRoomFontMigrated) return;
            int defPt = defaultPt > 0 ? defaultPt : 12; // 방어적 폴백
            foreach (var id in roomIds)
                if (!string.IsNullOrEmpty(id) && !Current.TerminalRoomFontSizePt.ContainsKey(id))
                    Current.TerminalRoomFontSizePt[id] = defPt.ToString();
            Current.TerminalRoomFontMigrated = true;
            Save();
        }
    }
    public static void SaveTerminalRoomFontSizePt(string roomId, int? pt)
    { SetOrRemove(Current.TerminalRoomFontSizePt, roomId, pt?.ToString()); Save(); }

    /// <summary>값이 비면 키 제거, 아니면 설정. (저장은 호출부에서)</summary>
    private static void SetOrRemove(Dictionary<string, string> map, string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) map.Remove(key);
        else map[key] = value;
    }

    private static bool RemoveWhere(Dictionary<string, string> map, Func<string, bool> predicate)
    {
        var keys = map.Keys.Where(predicate).ToArray();
        foreach (var key in keys) map.Remove(key);
        return keys.Length > 0;
    }

    // ── 마지막 활성 프로젝트/세션 (정상 종료 시에만 복원) ────────
    public static (string? projectPath, string? sessionId) LoadLastActive()
        => (Current.LastActiveProjectPath, Current.LastActiveSessionId);

    public static void SaveLastActive(string? projectPath, string? sessionId)
    {
        lock (_lock)
        {
            if (string.Equals(Current.LastActiveProjectPath, projectPath, StringComparison.Ordinal)
                && string.Equals(Current.LastActiveSessionId, sessionId, StringComparison.Ordinal))
                return;
            Current.LastActiveProjectPath = projectPath;
            Current.LastActiveSessionId = sessionId;
            SaveDeferred();
        }
    }

    // ── 중앙 패널 분할 상태 ──────────────────────────────────────
    public static (bool active, string? aProj, string? aSess, string? bProj, string? bSess, bool swapped) LoadFullSplitState()
        => (Current.SplitActive, Current.SplitAProjectPath, Current.SplitASessionId,
            Current.SplitBProjectPath, Current.SplitBSessionId, Current.SplitSwapped);

    public static void SaveFullSplitState(bool active, string? aProj, string? aSess, string? bProj, string? bSess, bool swapped)
    {
        Current.SplitActive = active;
        Current.SplitAProjectPath = aProj;
        Current.SplitASessionId = aSess;
        Current.SplitBProjectPath = bProj;
        Current.SplitBSessionId = bSess;
        Current.SplitSwapped = swapped;
        Save();
    }

    /// <summary>분할 비율 (PaneB star 값). 기본 1.0 (50/50). 로드 시 10%~90% 범위로 클램프.</summary>
    public static double LoadSplitBStar()
    {
        double star = Current.SplitBStarRatio;
        if (star < 0.112) star = 0.112;
        if (star > 9.0) star = 9.0;
        return star;
    }
    public static void SaveSplitBStar(double star)
    {
        // 최소 10% ~ 최대 90% 비율 보장 (PaneA=1* 기준 PaneB star)
        if (star < 0.112) star = 0.112; // PaneB >= 10%
        if (star > 9.0) star = 9.0;     // PaneA >= 10%
        Current.SplitBStarRatio = star;
        Save();
    }

    // ── 하위 호환: 기존 단일 패널 저장 (레거시, Active 만 추출용) ──
    public static (bool active, string? projectPath, string? sessionId) LoadSplitState()
        => (Current.SplitActive, Current.SplitBProjectPath, Current.SplitBSessionId);

    public static void SaveSplitState(bool active, string? projectPath, string? sessionId)
    {
        Current.SplitActive = active;
        Current.SplitBProjectPath = projectPath;
        Current.SplitBSessionId = sessionId;
        Save();
    }

    /// <summary>직전 실행이 정상 종료됐는지. true 일 때만 마지막 세션을 복원한다(크래시 시 복원 방지).</summary>
    public static bool LoadCleanShutdown() => Current.CleanShutdown;
    public static void SaveCleanShutdown(bool v) { Current.CleanShutdown = v; Save(); }

    // ── 우측 패널 브라우저 히스토리 (프로젝트별) ─────────────────────
    /// <summary>프로젝트당 보관하는 최대 방문 기록 수. 초과 시 오래된 항목부터 버림.</summary>
    private const int BrowserHistoryCap = 100;

    /// <summary>지정 프로젝트의 저장된 가상 히스토리(URL 스택 + 현재 위치).
    /// 없으면 기존 단일 URL(BrowserLastUrlByProject)을 1개짜리 히스토리로 마이그레이션.
    /// 그것도 없으면 null. Index 는 항상 [0, Urls.Count-1] 로 보정해 반환.</summary>
    public static (List<string> Urls, int Index)? LoadBrowserHistory(string? projectPath)
    {
        if (string.IsNullOrEmpty(projectPath)) return null;
        if (Current.BrowserHistoryByProject.TryGetValue(projectPath, out var e)
            && e.Urls is { Count: > 0 })
        {
            int idx = Math.Clamp(e.Index, 0, e.Urls.Count - 1);
            return (new List<string>(e.Urls), idx);
        }
        // 마이그레이션 — 구버전 단일 URL → 1개짜리 히스토리
        if (Current.BrowserLastUrlByProject.TryGetValue(projectPath, out var u)
            && !string.IsNullOrWhiteSpace(u))
            return (new List<string> { u }, 0);
        return null;
    }

    /// <summary>지정 프로젝트의 가상 히스토리 저장. 빈 목록은 무시.
    /// 상한(BrowserHistoryCap) 초과 시 오래된 앞쪽을 잘라내고 Index 를 함께 보정.
    /// 하위호환을 위해 현재 위치 URL 을 BrowserLastUrlByProject 에도 기록.</summary>
    public static void SaveBrowserHistory(string? projectPath, IReadOnlyList<string> urls, int index)
    {
        if (string.IsNullOrEmpty(projectPath)) return;
        if (urls is null || urls.Count == 0) return;

        var list = new List<string>(urls);
        int idx = index;
        if (list.Count > BrowserHistoryCap)
        {
            int drop = list.Count - BrowserHistoryCap;
            list.RemoveRange(0, drop);
            idx -= drop;
        }
        idx = Math.Clamp(idx, 0, list.Count - 1);

        Current.BrowserHistoryByProject[projectPath] = new BrowserHistoryEntry { Urls = list, Index = idx };
        Current.BrowserLastUrlByProject[projectPath] = list[idx]; // 하위호환
        Save();
    }

    /// <summary>프로젝트 삭제 시 해당 프로젝트의 저장된 URL/히스토리 정리.</summary>
    public static void RemoveBrowserLastUrl(string? projectPath)
    {
        if (string.IsNullOrEmpty(projectPath)) return;
        bool changed = Current.BrowserLastUrlByProject.Remove(projectPath);
        changed |= Current.BrowserHistoryByProject.Remove(projectPath);
        if (changed) Save();
    }

    // ── 우측 패널 작업 큐 (버블 항목, 프로젝트별) ────────────────────
    /// <summary>저장된 작업 큐 항목을 (text, sortOrder) 튜플 목록으로 반환. 정렬 순서대로.
    /// projectPath 가 null/empty 면 전역 큐 (별도 "__global__" 키). 저장된 게 없으면 빈 목록.</summary>
    public static IReadOnlyList<(string Text, long SortOrder, bool IsPinned)> LoadTaskQueueItems(string? projectPath)
    {
        var key = string.IsNullOrEmpty(projectPath) ? "__global__" : projectPath;
        if (!Current.TaskQueueItemsByProject.TryGetValue(key, out var list))
            return Array.Empty<(string, long, bool)>();
        return list
            .OrderBy(e => e.SortOrder)
            .Select(e => (e.Text ?? "", e.SortOrder, e.IsPinned))
            .ToList();
    }

    public static void SaveTaskQueueItems(string? projectPath, IEnumerable<(string Text, long SortOrder, bool IsPinned)> items)
    {
        var key = string.IsNullOrEmpty(projectPath) ? "__global__" : projectPath;
        var snapshot = items
            .Select(e => new TaskQueueEntry { Text = e.Text, SortOrder = e.SortOrder, IsPinned = e.IsPinned })
            .ToList();
        // 빈 큐는 키 자체를 제거해 settings.json 크기 축소.
        if (snapshot.Count == 0)
            Current.TaskQueueItemsByProject.Remove(key);
        else
            Current.TaskQueueItemsByProject[key] = snapshot;
        Save();
    }

    // ── 작업 큐 입력창 높이 ───────────────────────────────────────
    public static double LoadTaskQueueInputMinHeight() => Current.TaskQueueInputMinHeight;
    public static void SaveTaskQueueInputMinHeight(double h)
    {
        Current.TaskQueueInputMinHeight = h;
        Save();
    }

    // ── 작업 큐 글꼴 크기 (Ctrl+휠) ───────────────────────────────
    public static double LoadTaskQueueBubbleFontSize()
        => Current.TaskQueueBubbleFontSize <= 0 ? 14 : Current.TaskQueueBubbleFontSize;
    public static void SaveTaskQueueBubbleFontSize(double size)
    {
        Current.TaskQueueBubbleFontSize = size;
        Save();
    }

    // ── 일반 설정 ────────────────────────────────────────────────
    public static bool LoadPreloadAllProjectSessions() => Current.PreloadAllProjectSessions;
    public static void SavePreloadAllProjectSessions(bool v) { Current.PreloadAllProjectSessions = v; Save(); }
    public static int LoadIdleSessionShutdownMinutes()
    {
        var value = Current.IdleSessionShutdownMinutes;
        return value is 30 or 60 or 120 or 240 or 480 ? value : 0;
    }
    public static void SaveIdleSessionShutdownMinutes(int minutes)
    {
        Current.IdleSessionShutdownMinutes = minutes is 30 or 60 or 120 or 240 or 480 ? minutes : 0;
        Save();
    }

    public static bool LoadAutoLoadLastProject() => Current.AutoLoadLastProject;
    public static void SaveAutoLoadLastProject(bool v) { Current.AutoLoadLastProject = v; Save(); }

    public static bool LoadPromptForNewSessionName() => Current.PromptForNewSessionName;
    public static void SavePromptForNewSessionName(bool v) { Current.PromptForNewSessionName = v; Save(); }

    public static bool LoadPromptForNewBrowserTabName() => Current.PromptForNewBrowserTabName;
    public static void SavePromptForNewBrowserTabName(bool v) { Current.PromptForNewBrowserTabName = v; Save(); }

    public static bool LoadBrowserMcpEnabled() => Current.BrowserMcpEnabled;
    public static void SaveBrowserMcpEnabled(bool v) { Current.BrowserMcpEnabled = v; Save(); }

    public static string LoadBrowserHomeUrl()
        => NormalizeBrowserHomeUrl(Current.BrowserHomeUrl);

    public static void SaveBrowserHomeUrl(string? url)
    {
        Current.BrowserHomeUrl = NormalizeBrowserHomeUrl(url);
        Save();
    }

    public static TerminalUrlOpenTarget LoadTerminalUrlOpenTarget()
        => Enum.IsDefined(Current.TerminalUrlOpenTargetMode)
            ? Current.TerminalUrlOpenTargetMode : TerminalUrlOpenTarget.InAppBrowserTab;

    public static void SaveTerminalUrlOpenTarget(TerminalUrlOpenTarget target)
    {
        Current.TerminalUrlOpenTargetMode = Enum.IsDefined(target)
            ? target : TerminalUrlOpenTarget.InAppBrowserTab;
        Save();
    }

    private static string NormalizeBrowserHomeUrl(string? value)
        => NormalizeHomeUrl(value, "https://www.google.com");

    /// <summary>미니 브라우저 창 시작 주소. 비었거나 http(s) 가 아니면 claude.ai 로 보정.</summary>
    public static string LoadMiniBrowserHomeUrl()
        => NormalizeHomeUrl(Current.MiniBrowserHomeUrl, MiniBrowserHomeUrlDefault);

    public static void SaveMiniBrowserHomeUrl(string? url)
    {
        Current.MiniBrowserHomeUrl = NormalizeHomeUrl(url, MiniBrowserHomeUrlDefault);
        Save();
    }

    public const string MiniBrowserHomeUrlDefault = "https://claude.ai";

    /// <summary>스킴 없는 입력은 https 로 보정하고, 그래도 http(s) 절대 URL 이 아니면 fallback 을 쓴다.</summary>
    private static string NormalizeHomeUrl(string? value, string fallback)
    {
        var url = value?.Trim() ?? "";
        if (url.Length > 0 && !url.Contains("://", StringComparison.Ordinal))
            url = "https://" + url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return uri.AbsoluteUri;
        return fallback;
    }

    public static bool LoadHiddenSessionInsertionOnTop() => Current.HiddenSessionsOnTop;
    public static void SaveHiddenSessionInsertionOnTop(bool v) { Current.HiddenSessionsOnTop = v; Save(); }

    public static bool LoadHideProjectInfoHeader() => Current.HideProjectInfoHeader;
    public static void SaveHideProjectInfoHeader(bool v) { Current.HideProjectInfoHeader = v; Save(); }

    public static bool LoadAutoUpdateAgents() => Current.AutoUpdateAgents;
    public static void SaveAutoUpdateAgents(bool v) { Current.AutoUpdateAgents = v; Save(); }

    /// <summary>자동 업데이트 대상 에이전트 ID 목록. 미설정(레거시)이면 사용 중인 에이전트를 기본값으로 —
    /// 쓰지 않는 에이전트는 처음부터 업데이트 대상이 아니다.</summary>
    public static IReadOnlyList<string> LoadAutoUpdateAgentIds()
    {
        var current = Current;
        if (!current.AutoUpdateAgentIdsConfigured && current.AutoUpdateAgentIds.Count == 0)
            return LoadEnabledAgents();
        return current.AutoUpdateAgentIds;
    }

    public static void SaveAutoUpdateAgentIds(IEnumerable<string> agentIds)
    {
        Current.AutoUpdateAgentIds = agentIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Current.AutoUpdateAgentIdsConfigured = true;
        Save();
    }

    /// <summary>해당 에이전트가 자동 업데이트 대상인지(에이전트 사용 여부와 별개 토글).</summary>
    public static bool IsAgentAutoUpdateEnabled(string agentId)
        => LoadAutoUpdateAgentIds().Contains(agentId, StringComparer.OrdinalIgnoreCase);

    public static string LoadLastAgentAutoUpdateDate() => Current.LastAgentAutoUpdateDate;
    public static void SaveLastAgentAutoUpdateDate(string v) { Current.LastAgentAutoUpdateDate = v; Save(); }

    public static string LoadLastNodeInstallPromptDate() => Current.LastNodeInstallPromptDate;
    public static void SaveLastNodeInstallPromptDate(string v) { Current.LastNodeInstallPromptDate = v; Save(); }

    public static string LoadLastDevezPluginUpdateDate() => Current.LastDevezPluginUpdateDate;
    public static void SaveLastDevezPluginUpdateDate(string v) { Current.LastDevezPluginUpdateDate = v; Save(); }

    public static bool LoadUseFullScreen() => Current.UseFullScreen;
    public static void SaveUseFullScreen(bool v) { Current.UseFullScreen = v; Save(); }

    public static bool LoadMinimizeOnClose() => Current.MinimizeOnClose;
    public static void SaveMinimizeOnClose(bool v) { Current.MinimizeOnClose = v; Save(); }

    public static int LoadProjectColumns() => Current.ProjectColumns == 2 ? 2 : 1;
    public static void SaveProjectColumns(int v) { Current.ProjectColumns = v == 2 ? 2 : 1; Save(); }
    public static bool LoadShowCollapsedProjectPath() => Current.ShowCollapsedProjectPath;
    public static void SaveShowCollapsedProjectPath(bool v) { Current.ShowCollapsedProjectPath = v; Save(); }

    // ── 메인 창 위치/크기 (재시작 복원) ──────────────────────────
    public static (double? left, double? top, double? width, double? height, bool maximized) LoadWindowPlacement()
        => (Current.WindowLeft, Current.WindowTop, Current.WindowWidth, Current.WindowHeight, Current.WindowMaximized);

    public static void SaveWindowPlacement(double left, double top, double width, double height, bool maximized)
    {
        Current.WindowLeft = left;
        Current.WindowTop = top;
        Current.WindowWidth = width;
        Current.WindowHeight = height;
        Current.WindowMaximized = maximized;
        Save();
    }

    // ── 미니 브라우저 창 위치/크기 ────────────────────────────────
    public static (double? left, double? top, double? width, double? height) LoadMiniBrowserPlacement()
        => (Current.MiniBrowserLeft, Current.MiniBrowserTop, Current.MiniBrowserWidth, Current.MiniBrowserHeight);

    public static void SaveMiniBrowserPlacement(double left, double top, double width, double height)
    {
        Current.MiniBrowserLeft = left;
        Current.MiniBrowserTop = top;
        Current.MiniBrowserWidth = width;
        Current.MiniBrowserHeight = height;
        Save();
    }

    // ── 방이 한 번이라도 실행됐는지 (resume 판단용) ──────────────
    public static bool IsClaudeCodeRoomLaunched(string roomId)
    { lock (_lock) return Current.ClaudeCodeRoomLaunched.Contains(roomId); }

    public static void MarkClaudeCodeRoomLaunched(string roomId)
    {
        lock (_lock)
        {
            if (!Current.ClaudeCodeRoomLaunched.Contains(roomId))
            {
                Current.ClaudeCodeRoomLaunched.Add(roomId);
                Save();
            }
        }
    }

    /// <summary>비-Claude 에이전트의 방별 "첫 실행 여부". roomId+agentId 조합으로 추적.</summary>
    public static bool IsAgentRoomLaunched(string roomId, string agentId)
        => Current.AgentRoomsLaunched.Contains($"{roomId}|{agentId}");

    public static void MarkAgentRoomLaunched(string roomId, string agentId)
    {
        var key = $"{roomId}|{agentId}";
        if (!Current.AgentRoomsLaunched.Contains(key))
        {
            Current.AgentRoomsLaunched.Add(key);
            Save();
        }
    }

    // ── codex 세션 ID (ClaudeCodeRoomSessions 와 동일 패턴, hooks 로 채워짐) ─────
    public static string? LoadCodexRoomSession(string roomId)
        => Current.CodexRoomSessions.TryGetValue(roomId, out var s) ? s : null;

    public static void SaveCodexRoomSession(string roomId, string sessionId)
    {
        // 불변식: 비정상 값은 무시 — 실제 transcript 존재 여부는 훅/런처 경계에서 검증.
        if (string.IsNullOrWhiteSpace(sessionId) || !Guid.TryParse(sessionId, out _)) return;
        Current.CodexRoomSessions[roomId] = sessionId;
        Save();
    }

    public static void ClearCodexRoomSession(string roomId)
    {
        if (Current.CodexRoomSessions.Remove(roomId)) Save();
    }

    // ── kimi 세션 ID (session_&lt;uuid&gt; 형식, 훅 → settings 영속) ─────
    public static string? LoadKimiRoomSession(string roomId)
        => Current.KimiRoomSessions.TryGetValue(roomId, out var s) ? s : null;

    public static void SaveKimiRoomSession(string roomId, string sessionId)
    {
        // Kimi 세션 id 는 session_<uuid> 형식. 존재 검증은 훅/런처 경계에서.
        if (!KimiHookService.LooksLikeKimiSessionId(sessionId)) return;
        Current.KimiRoomSessions[roomId] = sessionId;
        Save();
    }

    public static void ClearKimiRoomSession(string roomId)
    {
        if (Current.KimiRoomSessions.Remove(roomId)) Save();
    }

    // ── Devez Vibe(dvz) 스레드 ID (dvz 가 sessions\<room>.txt 에 기록 → settings 영속) ─────
    public static string? LoadDevezVibeRoomSession(string roomId)
        => Current.DevezVibeRoomSessions.TryGetValue(roomId, out var s) ? s : null;

    public static void SaveDevezVibeRoomSession(string roomId, string sessionId)
    {
        // codex thread = UUID, claude 세션 = claude:UUID, opencode 세션 = ses_….
        // 형식이 어긋난 값(내부 에이전트 ID 등)은 resume 이 불가능하므로 버린다.
        if (!DevezVibeStateService.LooksLikeSessionId(sessionId)) return;
        Current.DevezVibeRoomSessions[roomId] = sessionId;
        Save();
    }

    /// <summary>지정 방을 제외한 모든 dvz 방의 claude 백엔드 세션 ID 집합(소문자).
    /// 세션 삭제(PurgeRoom)가 살아있는 dvz 방의 claude 대화를 오삭제하지 않게 하는 방어 목록용.</summary>
    public static HashSet<string> DevezVibeClaudeSessionIdsExcept(string exceptRoomId)
    {
        lock (_lock)
            return Current.DevezVibeRoomSessions
                .Where(kv => !string.Equals(kv.Key, exceptRoomId, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v)
                    && v.StartsWith("claude:", StringComparison.Ordinal))
                .Select(v => DevezVibeStateService.StripBackendPrefix(v).ToLowerInvariant())
                .Where(v => Guid.TryParse(v, out _))
                .ToHashSet();
    }

    public static void ClearDevezVibeRoomSession(string roomId)
    {
        if (Current.DevezVibeRoomSessions.Remove(roomId)) Save();
    }

    // ── grok 세션 ID (훅 → settings 영속) ─────
    public static string? LoadGrokRoomSession(string roomId)
        => Current.GrokRoomSessions.TryGetValue(roomId, out var s) ? s : null;

    public static void SaveGrokRoomSession(string roomId, string sessionId)
    {
        if (!Guid.TryParse(sessionId, out var parsed)) return;
        Current.GrokRoomSessions[roomId] = parsed.ToString();
        Save();
    }

    /// <summary>검증 가능한 transcript가 없는 Grok 세션 ID 고착을 해제.</summary>
    public static void RemoveGrokRoomSession(string roomId)
    {
        if (Current.GrokRoomSessions.Remove(roomId)) Save();
    }

    // ── opencode 세션 ID (Devez 패턴 이식, 플러그인이 채움) ─────────────
    public static string? LoadOpenCodeRoomSession(string roomId)
        => Current.OpenCodeRoomSessions.TryGetValue(roomId, out var s) ? s : null;

    public static void SaveOpenCodeRoomSession(string roomId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        Current.OpenCodeRoomSessions[roomId] = sessionId;
        Save();
    }

    // ── 가재코드(gjc) 세션 ID (방별 --session-dir 최신 세션에서 추출) ─────
    public static string? LoadGajaeRoomSession(string roomId)
        => Current.GajaeRoomSessions.TryGetValue(roomId, out var s) ? s : null;

    public static void SaveGajaeRoomSession(string roomId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        Current.GajaeRoomSessions[roomId] = sessionId;
        Save();
    }

    // ── 안티그래비티(agy) conversation ID ────────────────────────
    public static string? LoadAntigravityRoomSession(string roomId)
        => Current.AntigravityRoomSessions.TryGetValue(roomId, out var s) ? s : null;

    public static void SaveAntigravityRoomSession(string roomId, string sessionId)
    {
        if (!Guid.TryParse(sessionId, out var parsed)) return;
        Current.AntigravityRoomSessions[roomId] = parsed.ToString();
        Save();
    }

    /// <summary>방의 추적 conversation ID 를 제거. transcript(.db) 없는 빈/유실 ID 고착을 풀 때 호출.</summary>
    public static void RemoveAntigravityRoomSession(string roomId)
    {
        if (Current.AntigravityRoomSessions.Remove(roomId)) Save();
    }
}

/// <summary>작업 큐에 저장되는 단일 항목. JSON 직렬화용.</summary>
public sealed class TaskQueueEntry
{
    public string Text { get; set; } = "";
    public long SortOrder { get; set; }
    public bool IsPinned { get; set; }
}

/// <summary>우측 패널 브라우저의 프로젝트별 가상 히스토리.
/// Urls = 방문 순서대로의 URL 스택, Index = 현재 위치(뒤로/앞으로 기준점).</summary>
public sealed class BrowserHistoryEntry
{
    public List<string> Urls { get; set; } = new();
    public int Index { get; set; }
}

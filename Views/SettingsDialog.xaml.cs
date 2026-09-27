using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Threading;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>설정창 (devez 이식). 오버레이로 사용: 최상위 Grid에 올린 뒤 <see cref="CloseRequested"/> 로 닫는다.
/// 옵션은 <b>변경 즉시 저장</b>된다(저장/취소 버튼 없음). 컨트롤 변경 이벤트를 루트에서 받아
/// 짧게 디바운스한 뒤 <see cref="ApplySettings"/> 로 확정한다.
/// 테마 변경만 예외로, 세션 재시작이 필요하므로 저장은 즉시 하고 재시작 여부는 창을 닫을 때 묻는다.
/// Claude GUI 사용값은 저장 직후 실행 중인 Claude 세션을 새 화면 방식으로 전환한다.</summary>
public partial class SettingsDialog : UserControl
{
    /// <summary>닫기 요청 시 발생.</summary>
    public event EventHandler? CloseRequested;

    // 열림 시점의 저장값(기준). 저장 시 실제 변경된 항목만 반영하는 데 사용하고, 저장하면 갱신된다.
    private string _originalTheme;
    private int    _originalFontScale;
    private bool   _originalPreloadAllSessions;
    private bool   _originalClaudeGuiMode;
    private int    _originalIdleSessionShutdownMinutes;
    private int    _originalDefaultFontSizePt;
    private string _originalTerminalFontFamily = "";
    private int    _originalMarkdownViewportWidth;
    private bool   _originalAutoLoadLastProject;
    private bool   _originalPromptForNewSessionName;
    private bool   _originalPromptForNewBrowserTabName;
    private bool   _originalBrowserMcp;
    private bool   _selectedBrowserMcp;
    private string _originalBrowserHomeUrl = "";
    private TerminalUrlOpenTarget _originalTerminalUrlOpenTarget;
    private bool   _originalHiddenSessionInsertionOnTop;
    private bool   _originalHideProjectInfoHeader;
    private bool   _originalDiffGitEnabled;
    private bool   _originalAutoUpdateAgents;
    private bool   _originalUseFullScreen;
    private bool   _originalMinimizeOnClose;
    private HashSet<string> _originalEnabledAgents = new(StringComparer.OrdinalIgnoreCase);
    private int _originalRetentionDays = ClaudeGlobalSettings.DefaultCleanupPeriodDays;

    private int    _originalProjectColumns;
    private bool   _originalShowCollapsedProjectPath;
    private string _selectedTheme;
    private int    _selectedFontScale;
    private bool   _selectedPreloadAllSessions;
    private bool   _selectedClaudeGuiMode;
    private int    _selectedIdleSessionShutdownMinutes;
    private int    _selectedDefaultFontSizePt;
    private string _selectedTerminalFontFamily = "";
    private int    _selectedMarkdownViewportWidth;
    private bool   _syncingMarkdownViewportWidth;
    private bool   _selectedAutoLoadLastProject;
    private bool   _selectedPromptForNewSessionName;
    private bool   _selectedPromptForNewBrowserTabName;
    private string _selectedBrowserHomeUrl = "";
    private TerminalUrlOpenTarget _selectedTerminalUrlOpenTarget;
    private bool   _selectedHiddenSessionInsertionOnTop;
    private bool   _selectedHideProjectInfoHeader;
    private bool   _selectedDiffGitEnabled;
    private bool   _selectedAutoUpdateAgents;
    private bool   _selectedUseFullScreen;
    private bool   _selectedMinimizeOnClose;
    private int    _selectedProjectColumns;
    private bool   _selectedShowCollapsedProjectPath;
    // DeepSeek 연결 토글 — 다른 설정과 동일하게 [저장] 시점에만 디스크 반영(끄고 저장 시 키 삭제).
    private bool   _originalDeepSeekEnabled;
    private bool   _selectedDeepSeekEnabled;

    // 사이드패널 뷰 버튼 표시 — [저장] 시점에만 디스크 반영.
    private bool _originalShowDirView, _originalShowQueueView, _originalShowBrowserView, _originalShowDiffView;
    private bool _selectedShowDirView, _selectedShowQueueView, _selectedShowBrowserView, _selectedShowDiffView;

    // 계정 사용량 표시 방식 — [저장] 시점에만 디스크 반영.
    private bool _originalShowEstimate, _originalShowRemainingUsage;
    private bool _selectedShowEstimate, _selectedShowRemainingUsage;

    // 알림 상세(자동닫힘/모니터) — [저장] 시점에만 디스크 반영. 위치는 기존 _notifyPos/_originalNotifyPos 사용.
    private int    _originalNotifyAutoCloseSec, _selectedNotifyAutoCloseSec;
    private string _originalNotifyMonitor = "", _selectedNotifyMonitor = "";
    private string _originalNotifyPos = "br";

    // 탭 이동 단축키(가상키코드). 디스크 저장은 [저장] 버튼에서만 — 다른 설정과 동일.
    private int _originalHkMod, _originalHkPrev, _originalHkNext;
    private int _selectedHkMod, _selectedHkPrev, _selectedHkNext;
    private string? _capturingField; // 캡처 중인 필드 키("mod"/"prev"/"next"), null=비캡처
    private readonly ObservableCollection<AgentItem> _agentItems = new();
    // 현재 활성 좌측 카테고리. 테마 변경 시 활성 버튼의 brush instance가 stale 되므로 재계산에 사용.
    private string _activeCategoryKey = "theme";

    // ── 업데이트 내역(Changelog) 데이터 — devez 정합. 최신 5개만 유지, 새 버전 추가 시 가장 오래된 항목 제거. ──
    private static readonly (string Version, string Date, bool IsLatest, string[] Notes)[] _changelog =
      {
          ("v1.27.12", "2026-09-27", true, new[]
          {
              "Codex 세션 삭제 후 마우스 포인터가 사라질 수 있던 문제를 수정했습니다.",
          }),
          ("v1.27.11", "2026-09-26", false, new[]
          {
              "터미널을 제외한 프로젝트 목록, 완료기록, 설정 등의 스크롤이 부드럽게 움직이도록 개선했습니다.",
              "DevezVibe 세션을 다시 열 때 세션 안에서 바꾼 모델이 원래대로 돌아가던 문제를 수정했습니다.",
          }),
          ("v1.27.10", "2026-09-23", false, new[]
          {
              "Claude GUI에서 세션을 포크하면 컨텍스트 사용량이 원본과 다르게 표시되던 문제를 수정했습니다.",
          }),
          ("v1.27.9", "2026-09-23", false, new[]
          {
              "GPT-6 계열 모델의 예상 비용 단가를 갱신했습니다.",
          }),
          ("v1.27.8", "2026-09-21", false, new[]
          {
              "설정에서 Claude 계정을 다시 연결하면 사용량을 설정 창을 닫기 전에 바로 갱신하도록 수정했습니다.",
          }),
          ("v1.27.7", "2026-09-21", false, new[]
          {
              "프로젝트 카드 우클릭 메뉴에 프로젝트 경로 복사를 추가했습니다.",
              "세션을 포크할 때 부모 세션에서 실제 사용 중인 모델과 추론 수준을 Claude·Codex·Devez Vibe에 이어가도록 수정했습니다.",
              "Claude에서 닫은 아티팩트 패널이 포크한 세션에서 다시 열리던 문제를 수정했습니다.",
          }),
          ("v1.27.6", "2026-09-21", false, new[]
          {
              "Devez Vibe 세션에서 컨텍스트 압축이 끝나면 완료기록에 직전 프롬프트 대신 Context compacted 로 남도록 수정했습니다.",
          }),
          ("v1.27.5", "2026-09-18", false, new[]
          {
              "터미널이 이모지 폭을 틀리게 계산해 이모지가 든 줄을 다시 그릴 때 이후 줄이 한 칸씩 밀리고 오른쪽 끝에 글자가 남던 문제를 수정했습니다.",
          }),
          ("v1.27.4", "2026-09-17", false, new[]
          {
              "세션 우클릭·탭 메뉴에 세션ID 복사를 추가해 현재 세션의 ID를 바로 클립보드에 담을 수 있게 했습니다.",
          }),
          ("v1.27.3", "2026-09-17", false, new[]
          {
              "세션 우클릭·탭 메뉴에 세션 종료를 추가해 대화 기록은 남기고 실행 중인 세션만 내릴 수 있게 했습니다.",
              "유휴 상태에서 불필요하게 돌던 화면 갱신을 제거해 자원 사용을 줄였습니다.",
          }),
          ("v1.27.2", "2026-09-10", false, new[]
          {
              "Codex 선택지 답변을 기다리는 동안 완료로 표시되던 문제를 수정했습니다.",
          }),
          ("v1.27.1", "2026-09-10", false, new[]
          {
              "사소한 오류 수정",
          }),
          ("v1.27.0", "2026-09-10", false, new[]
          {
              "설정에 Claude·Codex 계정을 추가·삭제하고 선택할 수 있는 계정 관리 기능을 추가했습니다.",
          }),
          ("v1.26.26", "2026-09-09", false, new[]
          {
              "내장 터미널에서 Devez Vibe 질문 상자의 모서리가 어긋나 보이던 문제를 수정했습니다.",
          }),
          ("v1.26.25", "2026-09-09", false, new[]
          {
              "GPT-6-Astra의 예상 비용 표시를 추가하고 GPT-5.6 Sol의 최신 단가를 반영했습니다.",
              "모델 변경, 장문 입력, 처리 등급과 요약 작업을 반영하도록 토큰 비용 집계를 개선했습니다.",
              "사용 기록이나 과금 정보가 불완전한 경우 예상 비용의 불확실성을 표시하도록 개선했습니다.",
          }),
          ("v1.26.24", "2026-09-09", false, new[]
          {
              "Devez Vibe 업데이트 후에도 이전 버전이 실행되던 문제를 수정했습니다.",
          }),
          ("v1.26.23", "2026-09-04", false, new[]
          {
              "셀 배경에 단차가 생기던 문제를 수정했습니다.",
          }),
          ("v1.26.22", "2026-09-03", false, new[]
          {
              "한글을 연속 입력할 때 조합 밑줄이 음절마다 깜빡이던 문제를 수정했습니다.",
          }),
          ("v1.26.21", "2026-09-03", false, new[]
          {
              "Devez Vibe에서 한글을 조합할 때 밑줄이 모음 획을 가리던 문제를 수정했습니다.",
          }),
          ("v1.26.20", "2026-09-03", false, new[]
          {
              "특수문자 입력 시 한글 자음이 지워지지 않는 문제를 수정했습니다.",
          }),
          ("v1.26.19", "2026-09-03", false, new[]
          {
              "사소한 오류를 수정했습니다.",
          }),
          ("v1.26.18", "2026-09-03", false, new[]
          {
              "특정 세션에서 CPU를 비정상적으로 점유하던 문제 수정 (배포누락건 처리)",
          }),
          ("v1.26.17", "2026-09-03", false, new[]
          {
              "한글 자음 하나를 조합할 때 빈 입력 힌트의 일부 글자가 남아 보이던 문제를 수정했습니다.",
          }),
          ("v1.26.16", "2026-09-03", false, new[]
          {
              "한글 조합을 잠시 멈춰도 Devez Vibe의 빈 입력 힌트가 다시 나타나지 않도록 보완했습니다.",
          }),
          ("v1.26.15", "2026-09-03", false, new[]
          {
              "한글 조합을 시작하면 Devez Vibe의 빈 입력 힌트가 즉시 사라지도록 개선했습니다.",
              "Codex 경고의 실제 내용과 상세 안내가 정상 표시되도록 수정했습니다.",
              "앱 실행 파일의 임시 추출 위치를 설치 폴더로 고정해 Windows 보안 프로그램의 오탐 가능성을 낮췄습니다.",
          }),
          ("v1.26.14", "2026-08-29", false, new[]
          {
              "모바일 원격 데스크톱에서 한글을 빠르게 입력하면 일부 글자가 누락되던 문제를 수정했습니다.",
          }),
          ("v1.26.13", "2026-08-29", false, new[]
          {
              "다른 프로세스에서 돌아온 뒤 터미널을 눌러도 입력되지 않아 세션 탭을 바꿔야 했던 문제를 추가로 수정했습니다.",
          }),
          ("v1.26.12", "2026-08-29", false, new[]
          {
              "다른 프로세스에서 돌아온 뒤 터미널을 클릭해도 탭을 바꾸기 전까지 입력되지 않던 문제를 수정했습니다.",
          }),
          ("v1.26.11", "2026-08-29", false, new[]
          {
              "작업표시줄에서 다른 앱으로 전환했다 돌아오면 Devez Vibe 컴포저가 외곽선 커서로 멈추고 입력되지 않던 문제를 수정했습니다.",
              "Devez Vibe와 터미널의 특수문자 셀 폭을 일치시켜 일반 출력과 컴포저의 글자 겹침·줄바꿈·세로줄 어긋남을 수정했습니다.",
              "본문 없는 응답 조각이 빈 불릿으로 남던 문제를 수정했습니다.",
          }),
          ("v1.26.10", "2026-08-29", false, new[]
          {
              "한글 입력 중 반복되던 고비용 IME 진단 기록과 화면 상태 조사를 중단해 미세 끊김을 줄였습니다.",
          }),
          ("v1.26.9", "2026-08-28", false, new[]
          {
              "Devez Vibe에서 한글을 빠르게 입력할 때 확정 음절별 재묘화로 입력이 미세하게 끊기던 문제를 해결했습니다.",
          }),
          ("v1.26.8", "2026-08-28", false, new[]
          {
              "Devez Vibe·Codex 세션에서 한글을 빠르게 입력할 때 화면이 잠시 멈춘 뒤 글자가 겹치거나 누락되던 문제를 수정했습니다.",
          }),
          ("v1.26.7", "2026-08-28", false, new[]
          {
              "특정 에이전트의 컴포저에 ✔ 같은 특수문자를 붙여넣으면 여전히 사라지던 문제를 마저 수정했습니다.",
          }),
          ("v1.26.6", "2026-08-28", false, new[]
          {
              "Devez Vibe 컴포저에 이모지·특수문자를 붙여넣으면 사라지던 문제를 수정했습니다.",
          }),
          ("v1.26.5", "2026-08-27", false, new[]
          {
              "미니 브라우저 제어 스킬이 Claude 세션에서만 보이던 문제를 수정해, Codex와 OpenCode 세션에서도 사용할 수 있게 했습니다.",
          }),
          ("v1.26.4", "2026-08-27", false, new[]
          {
              "입력창 내용을 전체 선택한 상태에서 한글을 입력할 때, 기존 내용이 바로 지워지지 않고 조합 글자가 겹쳐 보이던 문제를 수정했습니다.",
          }),
          ("v1.26.3", "2026-08-27", false, new[]
          {
              "한글 입력 중 Ctrl+백스페이스로 단어를 지울 때 마지막 글자가 잠깐 남아 보이던 문제를 수정했습니다.",
              "세션 화면이 여러 조각으로 나뉘어 그려지면서 글자가 깜빡이던 현상을 줄였습니다.",
          }),
          ("v1.26.2", "2026-08-26", false, new[]
          {
              "터미널에서 마우스 휠 한 칸이 화면을 3줄만 스크롤하도록 이동량을 맞췄습니다.",
              "세션 종류에 따라 휠 한 칸이 10줄 넘게 스크롤되던 문제를 수정했습니다.",
              "휠을 천천히 굴릴 때 일부 입력이 무시되던 문제를 수정했습니다.",
          }),
          ("v1.26.1", "2026-08-25", false, new[]
          {
              "세션을 삭제할 때 삭제하지 않은 다른 Devez Vibe 세션의 대화 기록이 함께 지워질 수 있던 문제를 수정했습니다.",
          }),
          ("v1.26.0", "2026-08-25", false, new[]
          {
              "세션이 미니 브라우저 창을 직접 열고 조작할 수 있게 추가했습니다.",
              "설정 > 브라우저의 \"세션이 내장 브라우저 사용\" 옵션을 켜고, 세션에게 미니 브라우저를 다뤄 달라고 말하거나 슬래시 목록의 /devez-mini-browser 로 부를 수 있게 했습니다.",
          }),
          ("v1.25.1", "2026-08-24", false, new[]
          {
              "미니 브라우저에 뒤로·앞으로·새로고침·주소창 툴바를 추가했습니다.",
              "미니 브라우저 전용 주소 설정을 없애고, 인앱 브라우저 기본 주소로 함께 열리도록 바꿨습니다.",
          }),
          ("v1.25.0", "2026-08-24", false, new[]
          {
              "타이틀바 버튼으로 여닫는 미니 웹 브라우저를 추가했습니다.",
              "미니 브라우저 시작 주소를 설정 > 브라우저에서 지정할 수 있습니다.",
          }),
          ("v1.24.14", "2026-08-23", false, new[]
          {
              "codex와 Devez Vibe 터미널 오른쪽에 동작하지 않는 스크롤바가 표시되던 문제를 수정했습니다.",
          }),
          ("v1.24.13", "2026-08-23", false, new[]
          {
              "codex와 Devez Vibe 터미널에서 간헐적으로 글자가 깨져 보이던 문제를 수정했습니다.",
          }),
          ("v1.24.12", "2026-08-22", false, new[]
          {
              "여러 터미널을 동시에 사용할 때 상태줄과 기존 글자가 간헐적으로 사라지는 문제를 수정했습니다.",
          }),
          ("v1.24.11", "2026-08-21", false, new[]
          {
              "Claude Code GUI 모드에서 작업이 끝난 뒤에도 작업 중 표시가 계속 도는 문제를 수정했습니다.",
          }),
          ("v1.24.10", "2026-08-21", false, new[]
          {
              "세션을 전환하거나 새로 만들 때 간헐적으로 화면이 1~2초 멈추던 문제를 수정했습니다.",
          }),
          ("v1.24.9", "2026-08-21", false, new[]
          {
              "세션을 빠르게 전환한 뒤 이전 화면의 일부가 오른쪽에 남아 보이던 문제를 수정했습니다.",
          }),
          ("v1.24.8", "2026-08-20", false, new[]
          {
              "다른 창이나 팝업을 거쳐 터미널로 돌아온 뒤 한글을 입력하면 조합 중인 글자가 화면 왼쪽 위에 나타나던 문제를 수정했습니다.",
              "알림 팝업이 떠오를 때 입력 포커스를 빼앗던 문제를 수정했습니다.",
          }),
          ("v1.24.7", "2026-08-19", false, new[]
          {
              "파일 탐색기와 일부 목록 오른쪽 아래에 흰색 사각형이 보이던 문제를 수정했습니다.",
              "파일 탐색기를 스크롤할 때 하단 흐림 효과가 스크롤바를 덮던 문제를 수정했습니다.",
          }),
          ("v1.24.6", "2026-08-19", false, new[]
          {
              "Codex 구형 아이콘을 제거하고 ChatGPT 로고로 변경했습니다.",
              "라이트 테마에서 하단 셸 터미널의 입력 글자가 보이지 않던 문제를 수정했습니다.",
          }),
          ("v1.24.5", "2026-08-19", false, new[]
          {
              "세션 단축키(Ctrl+Shift+T/W/H/N/Delete)를 터미널 밖에서도 쓸 수 있게 했습니다.",
          }),
          ("v1.24.4", "2026-08-19", false, new[]
          {
              "설정 > 에이전트에서 에이전트별로 자동 업데이트를 켜고 끌 수 있도록 추가했습니다.",
          }),
          ("v1.24.3", "2026-08-18", false, new[]
          {
              "세션이 없는 프로젝트에서도 Ctrl+Shift+T로 새 세션을 열 수 있도록 수정했습니다.",
          }),
          ("v1.24.2", "2026-08-15", false, new[]
          {
              "Devez Vibe 터미널 오른쪽에 스크롤바가 표시되던 문제를 수정했습니다.",
          }),
          ("v1.24.1", "2026-08-15", false, new[]
          {
              "Devez Vibe 터미널에서 한글이 간헐적으로 엉뚱한 글자로 깨져 보이던 문제를 수정했습니다.",
          }),
          ("v1.24.0", "2026-08-14", false, new[]
          {
              "문서(md) 뷰어의 테마를 설정 > 테마/글꼴에서 앱 테마와 별도로 지정하는 기능을 추가했습니다.",
          }),
          ("v1.23.13", "2026-08-11", false, new[]
          {
              "터미널Claude GUI 글꼴변경기능을 추가했습니다.",
          }),
          ("v1.23.12", "2026-08-10", false, new[]
          {
              "Claude GUI 에서 ESC로 프롬프트를 중단하는 기능을 추가했습니다.",
          }),
          ("v1.23.11", "2026-08-10", false, new[]
          {
              "이전 배포에서 누락된 세션 복원 안정화 작업을 다시 반영했습니다.",
          }),
          ("v1.23.10", "2026-08-10", false, new[]
          {
              "Devez Vibe 세션을 다시 열 때 이전 대화가 표시되기 전에 로딩 화면이 사라지는 문제를 수정했습니다.",
          }),
          ("v1.23.9", "2026-08-09", false, new[]
          {
              "터미널영역 컴포저 렌더링 보정",
          }),
          ("v1.23.8", "2026-08-09", false, new[]
          {
              "사이드패널을 표시한 상태에서 dvz 컴포저의 한글 입력이 줄바꿈될 때 글자가 사라지는 문제를 수정했습니다.",
          }),
          ("v1.23.7", "2026-08-09", false, new[]
          {
              "dvz 세션 컴포저에서 한글 입력이 줄바꿈될 때 글자가 깜빡이거나 사라지는 문제를 수정했습니다.",
          }),
          ("v1.23.6", "2026-08-08", false, new[]
          {
              "Devez Vibe 세션의 컴포저와 프롬프트 세로 구분선이 끊겨 보이던 문제를 수정했습니다.",
          }),
          ("v1.23.5", "2026-08-07", false, new[]
          {
              "Claude GUI 입력창에서 슬래시 명령어를 색상으로 강조하고, 이름을 정확히 입력하면 Enter로 바로 전송되도록 했습니다.",
              "Claude GUI에 보낸 메시지 복사 버튼을 추가하고, Ctrl+Enter 줄바꿈과 Tab 포커스 동작을 정리했습니다. Ctrl+Tab 세션 전환 단축키는 제거했습니다.",
              "작업 중 보낸 프롬프트를 전송 대기 중으로 표시하고, 대기 프롬프트가 이어서 실행될 때 작업 표시가 깜빡이던 문제를 수정했습니다.",
              "중단한 요청의 프롬프트를 취소선으로 표시하고, 응답이 끝난 권한·질문 카드는 선택한 답변만 남기고 접도록 개선했습니다.",
              "Claude 백엔드로 실행한 dvz 세션의 토큰 사용량이 표시되지 않던 문제를 해결했습니다.",
          }),
          ("v1.23.4", "2026-08-07", false, new[]
          {
              "Claude GUI 입력창에서 @로 파일을 검색해 넣을 수 있도록 추가했습니다.",
              "다른 세션에 다녀와도 보고 있던 스크롤 위치가 유지되도록 개선했습니다.",
          }),
          ("v1.23.3", "2026-08-07", false, new[]
          {
              "특정 PC에서 설정 화면이 열리지 않던 문제를 해결했습니다.",
              "화면 전환 중 브라우저 응답이 멈춰도 앱이 계속 동작하도록 개선했습니다.",
          }),
          ("v1.23.2", "2026-08-07", false, new[]
          {
              "Devez Vibe의 상호작용과 UX를 개선했습니다.",
          }),
          ("v1.23.1", "2026-08-06", false, new[]
          {
              "Windows 환경의 Claude 실행 경로 호환 문제를 해결했습니다.",
          }),
          ("v1.23.0", "2026-08-06", false, new[]
          {
              "Claude GUI (Beta) 버전이 추가되었습니다.",
          }),
          ("v1.22.8", "2026-08-05", false, new[]
          {
              "Claude 응답 출력 중 Backspace로 입력을 지울 때 글자와 diff 화면이 밀려 보이던 문제를 수정했습니다.",
          }),
          ("v1.22.7", "2026-08-04", false, new[]
          {
              "업데이트 프로세스를 개선해 배포 안정성을 강화했습니다.",
          }),
          ("v1.22.6", "2026-08-04", false, new[]
          {
              "세션 토큰 사용량의 비용 계산에서 GPT-5.6 Terra·Luna의 인하된 최신 단가를 반영했습니다.",
          }),
          ("v1.22.5", "2026-08-03", false, new[]
          {
              "업데이트 노트가 두 줄 이상으로 표시될 때 글머리 기호 아래로 문장이 붙어 어긋나 보이던 문제를 수정했습니다.",
          }),
          ("v1.22.4", "2026-08-03", false, new[]
          {
              "Node.js가 설치되지 않은 경우 앱 시작 시 설치를 안내하고, 동의하면 앱 안에서 바로 설치하도록 추가했습니다.",
              "세션이 준비 중인 동안에는 작업 중 표시가 켜지지 않도록 수정했습니다.",
          }),
          ("v1.22.3", "2026-08-03", false, new[]
          {
              "메모리 점유 및 GC 처리 로직을 개선했습니다.",
          }),
          ("v1.22.2", "2026-08-02", false, new[]
          {
              "모든 지원 에이전트에서 내장 브라우저를 사용할 수 있도록 개선했습니다.",
              "Codex에서 내장 브라우저가 연결되지 않던 문제를 수정했습니다.",
              "내장 브라우저 명령 실행 중 발생하던 오류를 수정했습니다.",
          }),
          ("v1.22.1", "2026-08-02", false, new[]
          {
              "터미널 밖 화면에서 돌아온 뒤 한글 입력 포커스가 안정적으로 복구되도록 수정했습니다.",
              "탭 추가 메뉴가 즉시 닫히던 문제를 수정했습니다.",
          }),
          ("v1.22.0", "2026-08-02", false, new[]
          {
              "세션에서 DevezCode 내장 브라우저를 열어 검색·이동·입력·본문 읽기를 할 수 있도록 추가했습니다.",
              "열린 브라우저 탭을 선택하거나 새 전용 탭을 만들어 사용할 수 있도록 추가했습니다.",
              "설정 > 브라우저에서 세션의 내장 브라우저 사용 여부를 선택할 수 있도록 추가했습니다.",
              "브라우저와 파일 탐색 화면의 동작 안정성과 성능을 개선했습니다.",
          }),
          ("v1.21.7", "2026-08-02", false, new[]
          {
              "파일 검색 중 화면 멈춤과 연속 검색 시 결과 충돌을 줄였습니다.",
              "세션 시작 시 불필요한 설정 저장과 중복 메시지 확인 작업을 줄였습니다.",
              "세션이 MCP 도구로 내장 브라우저를 조작할 수 있는 선택 기능을 추가했습니다.",
              "브라우저 탭 연결·삭제·동시 호출 안정성을 개선했습니다.",
          }),
          ("v1.21.6", "2026-08-01", false, new[]
          {
              "새 세션 생성이나 완료 기록 클릭 직후 한글 조합 글자가 모니터 왼쪽 위에 표시되던 문제를 개선했습니다.",
          }),
          ("v1.21.5", "2026-07-31", false, new[]
          {
              "앱 전체의 트랙패드·휠 스크롤 감도를 통일해 목록과 화면이 부드럽게 움직이도록 정규화했습니다.",
          }),
          ("v1.21.4", "2026-07-31", false, new[]
          {
              "설정 화면이 열려 있는 동안에는 닫기 버튼을 비활성화해 실수로 앱이 종료되지 않도록 했습니다.",
              "업데이트 안내 창에서 노트가 길어도 다운로드 진행률이 가려지지 않고 항상 보이도록 고정했습니다.",
              "Devez Vibe 세션을 불러오는 중에도 스피너가 표시되고, 복원만으로 응답 완료 기록이 잘못 쌓이던 문제를 수정했습니다.",
          }),
          ("v1.21.3", "2026-07-31", false, new[]
          {
              "프로젝트 문서 그룹과 탭을 드래그로 서로 옮길 수 있도록 추가했습니다.",
              "파일 에디터 탭과 마크다운 뷰어, 빈 패널에도 외부 파일을 드래그해 열 수 있도록 추가했습니다.",
          }),
          ("v1.21.2", "2026-07-31", false, new[]
          {
              "프로젝트 문서 메뉴에서 현재 문서를 제외한 파일을 한 번에 닫을 수 있도록 추가했습니다.",
              "Codex 사용량 갱신과 Devez Vibe의 Codex 설치 안내를 개선했습니다.",
          }),
          ("v1.21.1", "2026-07-31", false, new[]
        {
            "그레이, 소프트 핑크 테마에서 Claude Code 상태줄 색상이 앱 테마와 어울리도록 보정했습니다.",
        }),
        ("v1.21.0", "2026-07-31", false, new[]
        {
            "설정 화면을 별도 창이 아닌 앱 내부 화면(도킹) 방식으로 변경했습니다.",
        }),
        ("v1.20.0", "2026-07-30", false, new[]
        {
            "그레이, 소프트 핑크, 미드나이트 블루 테마를 추가했습니다.",
        }),
        ("v1.19.8", "2026-07-30", false, new[]
        {
            "실행 중인 세션이 있는 프로젝트만 보여주는 활성 프로젝트 필터를 추가했습니다.",
            "프로젝트 목록 우클릭 메뉴에서 세션을 분할 화면으로 바로 옮길 수 있도록 추가했습니다.",
            "프로젝트 드래그 재정렬 시 가장자리 자동 스크롤을 추가하고, 스크롤 중 엉뚱한 위치로 놓이던 문제를 수정했습니다.",
            "분할 화면을 닫거나 세션을 옮긴 뒤 화면이 비어 보이던 문제와, 파일 트리 펼침 상태가 풀리던 문제를 수정했습니다.",
        }),
        ("v1.19.7", "2026-07-30", false, new[]
        {
            "Devez Vibe(dvz) 자동 업데이트 중 새 콘솔 창이 떴다가 사라지던 문제를 수정했습니다.",
            "Devez Vibe 업데이트가 완료되기 전에 \"최신 버전\"으로 잘못 표시되던 문제를 수정했습니다.",
        }),
        ("v1.19.6", "2026-07-30", false, new[]
        {
            "새 세션을 만든 직후 간헐적으로 첫 한글이 두 번 입력되던 문제를 수정했습니다.",
            "세션을 다른 세션의 자식으로 편입할 때, 이제 우측 화살표 영역에 놓아야 편입되도록 변경해 실수로 편입되는 일을 줄였습니다.",
        }),
        ("v1.19.5", "2026-07-29", false, new[]
        {
            "Windows 사용자 이름이 한글인 PC에서 자동 업데이트가 적용되지 않던 문제를 해결했습니다.",
            "Markdown 문서에 목차 버튼을 추가하고, 코드 블록에서 코드 복사와 HTML 브라우저 실행을 바로 할 수 있게 했습니다.",
            "세션을 클릭할 때 프로젝트 목록이 위아래로 튀던 문제를 수정했습니다.",
            "업데이트에 관리자 권한이 필요한데 승격을 취소한 경우, 일반 실패와 구분해 안내하도록 수정했습니다.",
            "Devez Vibe(dvz)를 에이전트 자동 업데이트 대상에 추가했습니다.",
        }),
        ("v1.19.4", "2026-07-28", false, new[]
        {
            "프로젝트를 2열로 배치한 상태에서 드래그로 순서를 바꿀 때 엉뚱한 위치로 이동되던 문제를 수정했습니다.",
            "Ctrl+Shift 단축키 안내에 세션 내 검색(Ctrl+Shift+F) 항목을 추가했습니다.",
        }),
        ("v1.19.3", "2026-07-28", false, new[]
        {
            "사이드패널 파일 목록의 우클릭 메뉴를 폴더/파일에 맞게 정리했습니다.",
            "폴더 우클릭 메뉴에 \"새 파일\", \"새 폴더\", \"탐색기에서 열기\"를 추가했습니다.",
            "파일 이름 옆 빈 여백에서도 우클릭 메뉴가 열리도록 개선했습니다.",
        }),
        ("v1.19.2", "2026-07-27", false, new[]
        {
            "Devez Vibe CLI 에이전트를 추가했습니다.",
        }),
        ("v1.19.1", "2026-07-27", false, new[]
        {
            "프로젝트 드래그 시 간헐적으로 앱이 크래시로 종료되던 문제를 수정했습니다.",
        }),
        ("v1.19.0", "2026-07-24", false, new[]
        {
            "웹브라우저 탭을 Windows 기본 브라우저에서 열 수 있는 기능을 추가했습니다.",
            "웹브라우저 탭 우클릭 메뉴에 \"URL 복사\"를 추가했습니다.",
            "프로젝트 카드 우클릭 메뉴에 \"Git 저장소 열기\"를 추가했습니다.",
            "파일을 세션 영역으로 드래그하면 \"파일 열기 / 파일 추가\"를 선택할 수 있도록 추가했습니다.",
            "세션·탭 헤더 우클릭 메뉴에 진행 중인 세션을 외부 터미널로 내보내는 기능을 추가했습니다.",
            "프로젝트 우클릭 메뉴에 마커 기능을 추가했습니다.",
        }),
        ("v1.18.0", "2026-07-23", false, new[]
        {
            "탭 추가(+) 메뉴에 \"파일 열기\"를 추가해 텍스트·이미지·PDF 파일을 앱 탭으로 바로 열 수 있도록 추가했습니다.",
            "2열로 정렬된 상태에서 폴더 밖으로 꺼낸 프로젝트가 해당 폴더와 같은 열 바로 아래에 놓이도록 개선했습니다.",
        }),
        ("v1.17.9", "2026-07-23", false, new[]
        {
            "Codex 세션에서 커서가 좌측 상단(0,0)에 고정되고 한글 입력 박스가 잘못 표시되던 문제를 개선했습니다.",
        }),
        ("v1.17.8", "2026-07-22", false, new[]
        {
            "Claude Code에서 하위 에이전트(Task) 완료 신호가 세션 완료기록에 매번 중복 누적되던 문제를 수정했습니다.",
            "Claude Code 슬래시 명령(/compact 등) 실행 후 세션 완료기록이 남지 않던 문제를 수정했습니다.",
        }),
        ("v1.17.7", "2026-07-22", false, new[]
        {
            "폴더 안에서 프로젝트를 드래그로 정렬할 때 발생하던 오동작을 수정했습니다.",
            "업데이트 배포 직후 이전 버전이 캐시되어 정상적으로 업데이트되지 않던 문제를 수정했습니다.",
        }),
        ("v1.17.6", "2026-07-22", false, new[]
        {
            "2열 보기에서 1열(반폭) 폴더를 드래그로 우측 열에 정렬할 수 있도록 수정했습니다.",
            "프로젝트 카드 안 숨긴 세션이 10개를 넘으면 카드 내부에서 스크롤되도록 개선했습니다.",
        }),
        ("v1.17.5", "2026-07-22", false, new[]
        {
            "Ctrl+Shift 세션 관리 단축키를 개선하고, 터미널 우상단에 단축키 힌트 오버레이를 추가했습니다.",
            "오픈소스 전환 준비에 맞춰 라이선스 고지를 점검·정비했습니다.",
        }),
        ("v1.17.4", "2026-07-21", false, new[]
        {
            "세션 탭 관리 단축키를 추가했습니다. (설정 > 단축키)",
            "기본 폰트 크기가 새 세션에 적용되지 않던 문제를 수정했습니다.",
            "프로젝트를 2열로 사용하는 경우 폴더 너비를 1열·2열로 설정하는 기능을 추가했습니다.",
        }),
        ("v1.17.3", "2026-07-21", false, new[]
        {
            "세션 헤더 영역에 다이얼로그를 통해 파일을 첨부할 수 있는 첨부파일 버튼을 추가했습니다.",
            "파일을 드래그 앤 드롭하여 첨부할 수 있는 기능을 추가했습니다.",
            "여러 버전을 건너뛰어 업데이트할 때 누락된 버전의 업데이트 노트도 함께 확인할 수 있도록 개선했습니다.",
        }),
        ("v1.17.2", "2026-07-20", false, new[]
        {
            "종료 버튼 오입력을 막기 위해 종료 확인 다이얼로그를 추가했습니다.",
            "기본 글꼴 크기를 설정하는 기능을 추가했습니다.",
            "터미널에 출력된 디렉터리 경로를 눌러 바로 열 수 있도록 개선했습니다.",
            "터미널에 출력된 URL을 눌러 바로 열 수 있도록 개선했습니다.",
            "웹 브라우저 탭의 기본 URL을 변경하는 기능을 추가했습니다.",
            "URL을 기본 브라우저 또는 인앱 브라우저 탭 중 어디서 열지 선택하는 옵션을 추가했습니다.",
        }),
        ("v1.17.1", "2026-07-20", false, new[]
        {
            "Codex CLI에서 아래로 스크롤할 때 항상 맨 아래로 이동하던 문제를 수정했습니다.",
        }),
        ("v1.17.0", "2026-07-18", false, new[]
        {
            "Kimi Code CLI 에이전트 연결 기능을 추가했습니다.",
        }),
        ("v1.16.3", "2026-07-16", false, new[]
        {
            "사이드패널에서 파일별 변경 내용(diff)을 더 보기 좋게 확인할 수 있도록 개선했습니다.",
            "커밋·푸시·풀 등 기본 Git 연동 기능을 추가했습니다.",
        }),
        ("v1.16.2", "2026-07-14", false, new[]
        {
            "프로젝트 카드를 우클릭해 세션을 추가하면 추가된 세션이 바로 선택되도록 개선했습니다.",
            "설정 > 일반에 창 닫기 버튼을 최소화로 동작시키는 옵션을 추가했습니다.",
            "상단 콤보 박스를 열고 닫으면 터미널에 자동으로 포커스가 돌아오도록 개선했습니다.",
            "폴더·프로젝트·세션의 드래그 사용성을 개선했습니다.",
            "에이전트 작업 상태를 나타내는 스피너가 켜지고 꺼지는 과정을 더 정확하게 인식하도록 Hook 처리 방식을 개선했습니다.",
            "Claude 상태줄에서 브랜치 이름에 한글이 포함되면 깨지던 인코딩 문제를 해결했습니다.",
        }),
        ("v1.16.1", "2026-07-14", false, new[]
        {
            "터미널 한글 입력 시 조합 글자의 커서 위치가 틀어지고 깜빡이던 문제를 개선했습니다.",
            "opencode 세션에서 하위 에이전트로 인해 방 상태가 잘못 기록되던 문제를 방지했습니다.",
            "Grok·Antigravity 세션 복원 정확도를 개선했습니다.",
        }),
        ("v1.16.0", "2026-07-14", false, new[]
        {
            "Google Antigravity CLI 연결 기능을 추가했습니다.",
            "계정 사용량 표시에 Antigravity 정보를 추가했습니다.",
            "Codex 5시간 한도 정보가 없을 때 주간 한도가 정확히 표시되도록 개선했습니다.",
            "에이전트 자동 업데이트의 안정성과 보안을 개선했습니다.",
        }),
        ("v1.15.0", "2026-07-13", false, new[]
        {
            "정해진 시간에 세션을 자동으로 깨워 메시지를 보내는 예약 실행(깨우기) 기능을 추가했습니다. (우측 하단에 깨우기 버튼 추가)",
            "Grok CLI 연결 기능을 추가했습니다.",
            "계정 사용량 표시에 Grok 정보를 추가했습니다.",
        }),
        ("v1.14.2", "2026-07-12", false, new[]
        {
            "계정 사용량을 남은 수치로 표시하는 기능을 추가했습니다.",
            "프로젝트 영역의 드래그 사용성을 개선했습니다.",
            "프로젝트 카드를 Ctrl 또는 Shift 클릭으로 여러 개 선택할 수 있는 기능을 추가했습니다.",
        }),
        ("v1.14.1", "2026-07-11", false, new[]
        {
            "에이전트 자동 업데이트 기능을 추가했습니다.",
            "계정 사용량 표시의 정확도를 개선했습니다.",
        }),
        ("v1.14.0", "2026-07-11", false, new[]
        {
            "프로젝트 패널에 폴더를 만들어 프로젝트를 정리할 수 있는 기능을 추가했습니다.",
            "세션을 드래그 드롭으로 트리 구조로 묶어 관리할 수 있는 기능을 추가했습니다.",
            "세션 탭에서 웹 브라우저를 열어 볼 수 있는 기능을 추가했습니다.",
            "세션 완료 기록에 마킹(표시)을 남길 수 있는 기능을 추가했습니다.",
            "설정 > 에이전트에서 사용량 연동 연결을 끊을 수 있는 기능을 추가했습니다.",
            "프로젝트 검색 기능을 개선했습니다.",
            "화면 전환 시 프레임 드랍을 줄이기 위해 슬라이드 애니메이션을 제거했습니다.",
            "Codex CLI 훅(Hook) 구조를 개선했습니다.",
        }),
        ("v1.13.2", "2026-07-10", false, new[]
        {
            "조기 한도 초기화(정기 초기화일 이전에 한도가 초기화되는 경우) 시 계정 사용량 패널과 하단 푸터의 Claude 사용량이 이전 최고치로 잘못 고정되던 문제를 수정했습니다.",
        }),
        ("v1.13.1", "2026-07-09", false, new[]
        {
            "사소한 UX 버그를 수정했습니다.",
        }),
        ("v1.13.0", "2026-07-08", false, new[]
        {
            "Codex CLI 연결 기능을 추가했습니다.",
            "파일 편집기에 구문 강조와 줄 번호를 추가했습니다.",
            "터미널 폰트 크기를 선택할 수 있는 콤보박스를 추가했습니다.",
            "하단 사용량 표시에 Fable 정보를 추가했습니다.",
            "세션 클리너가 설정 창 내부로 이동했습니다.",
            "패널에 세션이 하나도 없으면 브랜치·폰트 정보가 숨겨지도록 변경했습니다.",
            "세션을 /exit 또는 Ctrl+C로 종료하면 재실행 전까지 스피너가 표시되도록 변경했습니다.",
            "포크한 세션이 재실행 시 사라지던 문제를 수정했습니다.",
            "분할된 프로젝트 탭이 하나만 남으면 반대편으로 드래그할 수 없던 문제를 수정했습니다.",
            "탭을 이동할 때 숨긴 세션이 함께 나타나 선택되던 문제를 수정했습니다.",
            "사이드 패널이 중앙 세션 영역을 넘어 확장될 때 일부 패널이 화면 밖으로 밀려나던 현상을 수정했습니다.",
        }),
        ("v1.12.0", "2026-07-07", false, new[]
        {
            "세션을 잠가 실수로 삭제되지 않도록 보호하는 기능을 추가했습니다.",
            "프로젝트를 제거할 때 이름을 입력해 한 번 더 확인하고, 잠긴 세션이 있으면 삭제를 차단하도록 변경했습니다.",
            "프로젝트를 전환할 때 일부 세션에서 스크롤 위치가 어긋나던 문제를 수정했습니다.",
            "숨긴 세션을 좌/우 그룹과 분리해 프로젝트 카드 맨 아래에 모아 표시하도록 변경했습니다.",
        }),
        ("v1.11.6", "2026-07-07", false, new[]
        {
            "사이드바·탭 헤더 세션 우클릭 메뉴에 \"숨기기\"(다른 세션 모두 숨기기 포함)를 추가했습니다.",
            "세션 탭 우클릭 메뉴 항목 순서를 정리했습니다.",
        }),
        ("v1.11.5", "2026-07-07", false, new[]
        {
            "마크다운(.md) 파일을 열 때 간헐적으로 로딩 스피너가 멈춰 있던 문제를 수정했습니다.",
            "프로젝트 파일 탭 우클릭 메뉴에 \"파일 경로 복사\"를 추가했습니다.",
            "탭 우클릭 메뉴 항목 순서를 정리했습니다.",
            "창 최대화·복원 버튼을 아이콘으로 교체하고, 전체화면 전환 시에도 아이콘·툴팁이 정확히 표시되도록 수정했습니다.",
        }),
        ("v1.11.4", "2026-07-06", false, new[]
        {
            "DevezCode 테마 설정이 외부 터미널에서 실행 중인 Claude Code에 영향을 주지 않도록 수정했습니다.",
            "프로젝트 이름을 변경해도 세션 완료 기록에 이전 프로젝트명이 표시되던 문제를 수정했습니다.",
            "Claude Code 세션 유지 로직을 강화했습니다.",
        }),
        ("v1.11.3", "2026-07-06", false, new[]
        {
            "심플·소프트 테마에서 하단 상태 표시줄(모델·컨텍스트·사용량 등)의 색상 가독성을 개선했습니다.",
        }),
        ("v1.11.2", "2026-07-06", false, new[]
        {
            "플러그인 관리에서 목록을 새로고침할 때 같은 플러그인이 중복으로 쌓이던 문제를 수정했습니다.",
        }),
        ("v1.11.1", "2026-07-05", false, new[]
        {
            "터미널에서 세션을 /exit·Ctrl+C 로 종료해도 세션이 유지된 채 자동으로 다시 시작되도록 개선했습니다.",
            "사소한 오류를 수정하고 일부 UI를 개선했습니다.",
        }),
        ("v1.11.0", "2026-07-05", false, new[]
        {
            "세션을 포크해 대화를 분기할 수 있도록 추가했습니다.",
            "세션 대화 내역을 파일로 내보낼 수 있도록 추가했습니다.",
            "(프로젝트 카드의 세션 우클릭, 세션 탭 헤더 우클릭 메뉴에 포크·내보내기 추가)",
            "우측 하단에 Claude 플러그인 관리 메뉴를 추가했습니다.",
        }),
        ("v1.10.2", "2026-07-03", false, new[]
        {
            "일부 안내 대화상자가 Windows 기본 창으로 표시되던 것을 앱 디자인에 맞게 통일했습니다.",
            "분할된 패널에서 텍스트 파일을 닫으면 이전 세션이 자동으로 선택되지 않던 문제를 수정했습니다.",
        }),
        ("v1.10.1", "2026-07-03", false, new[]
        {
            "분할된 프로젝트에서 상단 탭 헤더를 드래그해 반대쪽 패널로 옮길 수 있도록 추가했습니다.",
            "창 크기를 조절할 때 터미널이 부드럽게 따라오고 스크롤 위치가 유실되지 않도록 개선했습니다.",
        }),
        ("v1.10.0", "2026-07-03", false, new[]
        {
            "여러 파일과 세션을 좌우로 나눠 볼 수 있는 패널 분할 기능을 추가했습니다.",
            "계정 사용량에 한도 도달 예상 시점을 표시하도록 추가했습니다. (설정 > 계정 사용량에서 켤 수 있습니다.)",
            "파일 뷰어에서 문서 내 텍스트를 검색할 수 있도록 추가했습니다.",
            "파일 뷰어 저장 버튼 옆에 파일 용량을 표시하도록 추가했습니다.",
            "다른 프로젝트를 보는 중에도 외부에서 수정된 마크다운 파일의 변경 사항이 반영되도록 수정했습니다.",
        }),
        ("v1.9.7", "2026-07-02", false, new[]
        {
            "세션 로드 폴백 상황을 진단 로그에 기록하도록 개선했습니다.",
        }),
        ("v1.9.6", "2026-07-02", false, new[]
        {
            "계정 사용량 패널에 Fable 사용량 정보를 표시하도록 추가했습니다.",
            "프로젝트 카드 우클릭 메뉴에 디렉토리 열기를 추가했습니다.",
            "클립보드 처리 방식을 개선해 다른 프로그램에 영향을 주지 않도록 했습니다.",
            "특정 환경에서 Claude 세션에 들어갈 때마다 로딩 스피너가 표시되던 문제를 수정했습니다.",
        }),
        ("v1.9.5", "2026-07-01", false, new[]
        {
            "Claude 세션에서 서브에이전트 작업 지시 후 작업 완료 신호가 잘못 수신되던 문제를 수정했습니다.",
        }),
        ("v1.9.4", "2026-07-01", false, new[]
        {
            "업데이트 누락으로 회귀되던 문제를 수정했습니다.",
        }),
        ("v1.9.3", "2026-07-01", false, new[]
        {
            "일부 환경에서 발생하던 클립보드 충돌 문제를 해결했습니다.",
        }),
        ("v1.9.2", "2026-07-01", false, new[]
        {
            "전반적인 성능개선 작업을 진행했습니다.",
        }),
        ("v1.9.1", "2026-06-30", false, new[]
        {
            "프로젝트 이름을 변경한 뒤 재실행해도 변경한 이름이 유지되도록 수정했습니다.",
            "프로젝트·세션 이름을 변경하면 세션 완료 기록 카드에도 즉시 반영되도록 수정했습니다.",
        }),
        ("v1.9.0", "2026-06-29", false, new[]
        {
            "같은 프로젝트(폴더)를 여러 개 등록할 수 있도록 추가했습니다.",
            "세션 터미널에서 마우스로 드래그해 텍스트를 선택·복사할 수 있도록 추가했습니다.",
            "계정 사용량 패널에 DeepSeek API 잔액을 표시하도록 추가했습니다.",
            "그 외 사소한 오류를 수정했습니다.",
        }),
        ("v1.8.2", "2026-06-29", false, new[]
        {
            "Claude 세션 상태줄(statusLine)이 간헐적으로 불러와지지 않던 문제를 수정했습니다.",
        }),
        ("v1.8.1", "2026-06-28", false, new[]
        {
            "계정 사용량 패널에 OpenAI Codex 초기화권 개수와 유효기간을 표시하도록 추가했습니다.",
            "기타 사소한 오류를 수정했습니다.",
        }),
        ("v1.8.0", "2026-06-28", false, new[]
        {
            "사용하지 않는 세션을 한 번에 정리하는 세션 클리너 기능을 추가했습니다.",
            "OpenCode 데이터베이스 용량을 정리(VACUUM)하는 기능을 추가했습니다.",
            "세션 응답 대기 표시가 잘못 뜨거나 누락되던 문제를 수정했습니다.",
        }),
        ("v1.7.0", "2026-06-27", false, new[]
        {
            "세션 완료 기록 기능을 추가했습니다.",
            "세션 입력 대기 상태 표시 기능을 추가했습니다.",
            "세션 탭 헤더 우클릭 메뉴를 추가했습니다.",
            "마크다운 편집 화면의 스타일을 변경했습니다.",
            "우클릭 붙여넣기가 두 번 입력되던 문제를 수정했습니다.",
            "그 외 사소한 오류를 수정했습니다.",
        }),
        ("v1.6.0", "2026-06-26", false, new[]
        {
            "세션 화면을 좌우로 분할해 두 프로젝트를 동시에 볼 수 있는 분할 기능을 추가했습니다.",
            "응답 완료 알림이 서브에이전트 작업을 본 응답 완료로 잘못 인식하던 문제를 수정했습니다.",
        }),
        ("v1.5.2", "2026-06-25", false, new[]
        {
            "하단 푸터의 계정 사용량 표시가 동작하지 않던 문제를 수정했습니다.",
        }),
        ("v1.5.1", "2026-06-25", false, new[]
        {
            "전체화면 기능을 추가했습니다.",
            "가재코드(Gajae Code) 에이전트를 추가했습니다.",
            "프로젝트에 열어두었던 파일을 재실행 시 그대로 유지하도록 개선했습니다.",
        }),
        ("v1.5.0", "2026-06-25", false, new[]
        {
            "계정 사용량을 우측 전용 사이드바로 분리하고 차트 버튼 또는 F1 키로 열고 닫을 수 있도록 변경했습니다.",
            "마크다운 파일 뷰어를 옵시디언 스타일로 개선했습니다.",
            "좌우 패널 접기 버튼 등 자잘한 UI를 다듬었습니다.",
        }),
        ("v1.4.0", "2026-06-23", false, new[]
        {
            "프로젝트 목록을 1열 또는 2열로 표시하는 옵션을 추가했습니다. (설정 > 프로젝트)",
            "2열에서는 프로젝트 카드를 좌우로 끌어 원하는 열에 배치할 수 있도록 추가했습니다.",
        }),
        ("v1.3.0", "2026-06-23", false, new[]
        {
            "프로젝트 보관함 기능을 추가했습니다.",
            "프로젝트 우클릭 메뉴에 이름 변경 기능을 추가했습니다.",
            "탭 이동 단축키 기능을 추가했습니다.",
            "터미널 드래그 선택과 복사 기능을 개선했습니다.",
            "세션을 불러오는 성능을 개선했습니다.",
            "관리자 권한 바로가기가 실행되지 않던 문제를 수정했습니다.",
            "프로그램 종료 시 간헐적으로 세션 대화 내용이 소실되던 문제를 수정했습니다.",
        }),
        ("v1.2.1", "2026-06-23", false, new[]
        {
            "선택하지 않은 프로젝트의 세션도 우클릭 메뉴와 마우스 호버가 동작하도록 수정했습니다.",
            "선택하지 않은 프로젝트의 세션을 클릭하면 해당 세션이 바로 열리도록 수정했습니다.",
            "세션을 클릭할 때 다른 세션이 함께 로드되던 문제를 수정했습니다.",
        }),
        ("v1.2.0", "2026-06-23", false, new[]
        {
            "마크다운 파일을 에디터 탭에서 열고 구문 강조를 지원하도록 추가했습니다.",
            "파일 탐색기에 검색과 파일 타입 아이콘을 추가했습니다.",
        }),
        ("v1.1.0", "2026-06-22", false, new[]
        {
            "사이드바에 새로운 메뉴를 추가했습니다.",
            "프로젝트에 실행파일 바로가기 추가 기능을 도입했습니다.",
            "전반적인 UI를 개선했습니다.",
            "세션 변경 시 항상 표시되던 스피너 문제를 수정했습니다.",
        }),
    };
    private const int ChangelogPageSize = 5;
    private int _changelogPage = 0;
    private readonly Action<string> _themeChangedHandler;
    private readonly Action<int> _fontScaleChangedHandler;
    private bool _subscribed;
    private bool _terminalFontFamilyReady;

    public SettingsDialog()
    {
        InitializeComponent();
        InitAutoSave();   // 옵션 변경 → 즉시 저장(디바운스)
        CodexLoginIcon.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(App.CodexIconUri));
        CodexCatIcon.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(App.CodexIconUri));
        GrokLoginIcon.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(App.GrokIconUri));
        _originalTheme       = App.CurrentTheme;
        _selectedTheme       = App.CurrentTheme;
        _originalFontScale   = SettingsService.LoadFontScale();
        _selectedFontScale   = _originalFontScale;
        _originalPreloadAllSessions = SettingsService.LoadPreloadAllProjectSessions();
        _selectedPreloadAllSessions = _originalPreloadAllSessions;
        PreloadAllSessionsToggle.IsChecked = _selectedPreloadAllSessions;
        _originalClaudeGuiMode = SettingsService.LoadClaudeGuiMode();
        _selectedClaudeGuiMode = _originalClaudeGuiMode;
        ClaudeGuiModeToggle.IsChecked = _selectedClaudeGuiMode;
        _originalIdleSessionShutdownMinutes = SettingsService.LoadIdleSessionShutdownMinutes();
        _selectedIdleSessionShutdownMinutes = _originalIdleSessionShutdownMinutes;
        SelectComboByTag(IdleSessionShutdownCombo, _selectedIdleSessionShutdownMinutes.ToString());
        // 설정값이 있으면 그 값, 없으면 WT 프로필 기본값(보통 12pt) — 실제 새 세션에 적용되는 값과 일치시킨다.
        _originalDefaultFontSizePt = Services.Terminal.TerminalSessionManager.Instance.DefaultFontSizePt;
        _selectedDefaultFontSizePt = _originalDefaultFontSizePt;
        SelectComboByTag(DefaultFontSizeCombo, _selectedDefaultFontSizePt.ToString());
        _originalTerminalFontFamily = SettingsService.LoadTerminalFontFamily();
        _selectedTerminalFontFamily = _originalTerminalFontFamily;
        PopulateTerminalFontFamilyCombo(_selectedTerminalFontFamily);
        _terminalFontFamilyReady = true;
        _originalMarkdownViewportWidth = SettingsService.LoadMarkdownViewportWidth();
        _selectedMarkdownViewportWidth = _originalMarkdownViewportWidth;
        SetMarkdownViewportWidthEditor(_selectedMarkdownViewportWidth);
        SelectComboByTag(MarkdownThemeCombo, SettingsService.LoadMarkdownTheme());
        SelectComboByTag(BrowserThemeCombo, SettingsService.LoadBrowserTheme());
        _originalAutoLoadLastProject = SettingsService.LoadAutoLoadLastProject();
        _selectedAutoLoadLastProject = _originalAutoLoadLastProject;
        AutoLoadLastProjectToggle.IsChecked = _selectedAutoLoadLastProject;
        _originalPromptForNewSessionName = SettingsService.LoadPromptForNewSessionName();
        _selectedPromptForNewSessionName = _originalPromptForNewSessionName;
        PromptForNewSessionNameToggle.IsChecked = _selectedPromptForNewSessionName;
        _originalPromptForNewBrowserTabName = SettingsService.LoadPromptForNewBrowserTabName();
        _selectedPromptForNewBrowserTabName = _originalPromptForNewBrowserTabName;
        PromptForNewBrowserTabNameToggle.IsChecked = _selectedPromptForNewBrowserTabName;
        _originalBrowserMcp = SettingsService.LoadBrowserMcpEnabled();
        _selectedBrowserMcp = _originalBrowserMcp;
        BrowserMcpToggle.IsChecked = _selectedBrowserMcp;
        _originalBrowserHomeUrl = SettingsService.LoadBrowserHomeUrl();
        _selectedBrowserHomeUrl = _originalBrowserHomeUrl;
        BrowserHomeUrlBox.Text = _selectedBrowserHomeUrl;
        _originalTerminalUrlOpenTarget = SettingsService.LoadTerminalUrlOpenTarget();
        _selectedTerminalUrlOpenTarget = _originalTerminalUrlOpenTarget;
        SelectComboByTag(TerminalUrlOpenTargetCombo, _selectedTerminalUrlOpenTarget.ToString());
        _originalHiddenSessionInsertionOnTop = SettingsService.LoadHiddenSessionInsertionOnTop();
        _selectedHiddenSessionInsertionOnTop = _originalHiddenSessionInsertionOnTop;
        SelectComboByTag(HiddenSessionInsertionCombo, _selectedHiddenSessionInsertionOnTop ? "top" : "bottom");
        _originalHideProjectInfoHeader = SettingsService.LoadHideProjectInfoHeader();
        _selectedHideProjectInfoHeader = _originalHideProjectInfoHeader;
        HideProjectInfoHeaderToggle.IsChecked = _selectedHideProjectInfoHeader;
        _originalDiffGitEnabled = SettingsService.LoadDiffGitEnabled();
        _selectedDiffGitEnabled = _originalDiffGitEnabled;
        DiffGitEnabledToggle.IsChecked = _selectedDiffGitEnabled;
        _originalAutoUpdateAgents = SettingsService.LoadAutoUpdateAgents();
        _selectedAutoUpdateAgents = _originalAutoUpdateAgents;
        AutoUpdateAgentsToggle.IsChecked = _selectedAutoUpdateAgents;
        _originalUseFullScreen = SettingsService.LoadUseFullScreen();
        _selectedUseFullScreen = _originalUseFullScreen;
        UseFullScreenToggle.IsChecked = _selectedUseFullScreen;
        _originalMinimizeOnClose = SettingsService.LoadMinimizeOnClose();
        _selectedMinimizeOnClose = _originalMinimizeOnClose;
        MinimizeOnCloseToggle.IsChecked = _selectedMinimizeOnClose;
        _originalProjectColumns = SettingsService.LoadProjectColumns();
        _selectedProjectColumns = _originalProjectColumns;
        SelectComboByTag(ProjectColumnsCombo, _selectedProjectColumns.ToString());
        _originalShowCollapsedProjectPath = SettingsService.LoadShowCollapsedProjectPath();
        _selectedShowCollapsedProjectPath = _originalShowCollapsedProjectPath;
        ShowCollapsedProjectPathToggle.IsChecked = _selectedShowCollapsedProjectPath;
        (_originalHkMod, _originalHkPrev, _originalHkNext) = SettingsService.LoadTabHotkey();
        _selectedHkMod = _originalHkMod; _selectedHkPrev = _originalHkPrev; _selectedHkNext = _originalHkNext;
        UpdateShortcutVisual();
        BuildAgentList();
        UpdateThemeSelectionVisual();
        UpdateFontSelectionVisual();
        SetActiveCategory("general");

        // 라이브 미리보기: 테마/글꼴 변경 시 좌측 탭 활성 배경·테마 카드 보더·글꼴 카드 보더를
        // 즉시 재계산. 캡처된 brush instance 라 DynamicResource 가 자동 갱신되지 않는 케이스 보정.
        _themeChangedHandler    = _ => RefreshAfterThemeChange();
        _fontScaleChangedHandler = _ => UpdateFontSelectionVisual();
        App.ThemeChanged     += _themeChangedHandler;
        App.FontScaleChanged  += _fontScaleChangedHandler;
        _subscribed = true;
        Unloaded += (_, _) => { Unsubscribe(); GlobalTabHotkey.CancelCapture(); };
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        try { App.ThemeChanged    -= _themeChangedHandler;    } catch { }
        try { App.FontScaleChanged -= _fontScaleChangedHandler; } catch { }
    }

    // ── 카테고리 전환 ─────────────────────────────────────────────
    private void Category_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string key) SetActiveCategory(key);
    }

    /// <summary>좌측 카테고리 활성 표시 + 우측 패널 전환.</summary>
    private void SetActiveCategory(string key)
    {
        _activeCategoryKey = key;
        var active  = (Brush)FindResource("PanelBrush");
        var primary = (Brush)FindResource("PrimaryBrush");
        var text    = (Brush)FindResource("TextBrush");

        CatGeneralBtn.Background   = key == "general"    ? active : Brushes.Transparent;
        CatGeneralBtn.Foreground   = key == "general"    ? primary : text;
        CatProjectBtn.Background   = key == "project"    ? active : Brushes.Transparent;
        CatProjectBtn.Foreground   = key == "project"    ? primary : text;
        CatSessionBtn.Background   = key == "session"    ? active : Brushes.Transparent;
        CatSessionBtn.Foreground   = key == "session"    ? primary : text;
        CatBrowserBtn.Background   = key == "browser"    ? active : Brushes.Transparent;
        CatBrowserBtn.Foreground   = key == "browser"    ? primary : text;
        CatThemeBtn.Background     = key == "theme"      ? active : Brushes.Transparent;
        CatThemeBtn.Foreground     = key == "theme"      ? primary : text;
        CatAgentBtn.Background     = key == "agent"      ? active : Brushes.Transparent;
        CatAgentBtn.Foreground     = key == "agent"      ? primary : text;
        CatAccountBtn.Background   = key == "account"    ? active : Brushes.Transparent;
        CatAccountBtn.Foreground   = key == "account"    ? primary : text;
        CatCleanerBtn.Background   = key == "cleaner"    ? active : Brushes.Transparent;
        CatCleanerBtn.Foreground   = key == "cleaner"    ? primary : text;
        CatSidePanelBtn.Background = key == "sidepanel"  ? active : Brushes.Transparent;
        CatSidePanelBtn.Foreground = key == "sidepanel"  ? primary : text;
        CatUsageBtn.Background     = key == "usage"      ? active : Brushes.Transparent;
        CatUsageBtn.Foreground     = key == "usage"      ? primary : text;
        CatMcpBtn.Background       = key == "mcp"        ? active : Brushes.Transparent;
        CatMcpBtn.Foreground       = key == "mcp"        ? primary : text;
        CatChangelogBtn.Background = key == "changelog"  ? active : Brushes.Transparent;
        CatChangelogBtn.Foreground = key == "changelog"  ? primary : text;
        CatLicensesBtn.Background  = key == "licenses"   ? active : Brushes.Transparent;
        CatLicensesBtn.Foreground  = key == "licenses"   ? primary : text;
        CatShortcutBtn.Background  = key == "shortcut"   ? active : Brushes.Transparent;
        CatShortcutBtn.Foreground  = key == "shortcut"   ? primary : text;
        CatNotifyBtn.Background    = key == "notify"     ? active : Brushes.Transparent;
        CatNotifyBtn.Foreground    = key == "notify"     ? primary : text;
        CatWakeBtn.Background      = key == "wake"       ? active : Brushes.Transparent;
        CatWakeBtn.Foreground      = key == "wake"       ? primary : text;

        GeneralPanel.Visibility    = key == "general"    ? Visibility.Visible : Visibility.Collapsed;
        ProjectPanel.Visibility    = key == "project"    ? Visibility.Visible : Visibility.Collapsed;
        SessionPanel.Visibility    = key == "session"    ? Visibility.Visible : Visibility.Collapsed;
        BrowserPanel.Visibility    = key == "browser"    ? Visibility.Visible : Visibility.Collapsed;
        ThemePanel.Visibility      = key == "theme"      ? Visibility.Visible : Visibility.Collapsed;
        AgentPanel.Visibility      = key == "agent"      ? Visibility.Visible : Visibility.Collapsed;
        AccountPanel.Visibility    = key == "account"    ? Visibility.Visible : Visibility.Collapsed;
        CleanerPanel.Visibility    = key == "cleaner"    ? Visibility.Visible : Visibility.Collapsed;
        SidePanelPanel.Visibility  = key == "sidepanel"  ? Visibility.Visible : Visibility.Collapsed;
        UsagePanel.Visibility      = key == "usage"      ? Visibility.Visible : Visibility.Collapsed;
        McpPanel.Visibility        = key == "mcp"        ? Visibility.Visible : Visibility.Collapsed;
        ChangelogPanel.Visibility  = key == "changelog"  ? Visibility.Visible : Visibility.Collapsed;
        if (key != "changelog") ChangelogPager.Visibility = Visibility.Collapsed;
        LicensesPanel.Visibility   = key == "licenses"   ? Visibility.Visible : Visibility.Collapsed;
        ShortcutPanel.Visibility   = key == "shortcut"   ? Visibility.Visible : Visibility.Collapsed;
        NotifyPanel.Visibility     = key == "notify"     ? Visibility.Visible : Visibility.Collapsed;
        WakePanel.Visibility       = key == "wake"       ? Visibility.Visible : Visibility.Collapsed;

        if (key != "shortcut") CancelShortcutCapture(); // 패널 떠나면 캡처 중단
        if (key == "wake") EnterWake();
        if (key == "sidepanel") LoadSidePanelSettings();
        if (key == "usage") LoadFooterUsageSettings();
        if (key == "account") AccountPanel.RefreshAccounts();
        if (key == "notify") LoadNotifySettings();
        if (key == "changelog") { _changelogPage = 0; RenderChangelogPage(); }
        if (key == "cleaner") EnterCleaner();
    }

    private void OpenVisualStudio2017ImageLibrary_Click(object sender, RoutedEventArgs e)
        => OpenExternalUrl("https://www.microsoft.com/en-us/download/details.aspx?id=35825");

    private void OpenVisualStudio2022ImageLibrary_Click(object sender, RoutedEventArgs e)
        => OpenExternalUrl("https://learn.microsoft.com/en-us/visualstudio/ide/the-visual-studio-image-library");

    private static void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ConfirmDialog.Alert("페이지 열기 실패", ex.Message);
        }
    }

    // ── 세션 클리너 (SessionCleanerWindow 이식 — 설정창 내부 탭) ──────
    private CleanerAgentKind _cleanerCurrent;
    private bool _cleanerBuilt;
    private readonly List<CleanerAgentKind> _cleanerVisible = new();
    private readonly Dictionary<CleanerAgentKind, CleanerScanInfo?> _cleanerCounts = new();
    private readonly HashSet<CleanerAgentKind> _cleanerLoading = new();

    /// <summary>클리너 탭 진입 — 최초 1회만 켜진 에이전트 목록을 구성하고 전체 스캔을 시작한다.</summary>
    private void EnterCleaner()
    {
        if (_cleanerBuilt) return;
        _cleanerBuilt = true;

        var enabled = SettingsService.LoadEnabledAgents().ToHashSet(StringComparer.OrdinalIgnoreCase);
        SetCleanerAgentVisible(CleanerAgentKind.Claude, ClaudeCatBtn, enabled.Contains("claude"));
        SetCleanerAgentVisible(CleanerAgentKind.OpenCode, OpenCodeCatBtn, enabled.Contains("opencode"));
        SetCleanerAgentVisible(CleanerAgentKind.Gajae, GajaeCatBtn, enabled.Contains("gajae"));
        SetCleanerAgentVisible(CleanerAgentKind.Codex, CodexCatBtn, enabled.Contains("codex"));
        SetCleanerAgentVisible(CleanerAgentKind.Grok, GrokCatBtn, enabled.Contains("grok"));
        SetCleanerAgentVisible(CleanerAgentKind.Antigravity, AntigravityCatBtn, enabled.Contains("antigravity"));
        SetCleanerAgentVisible(CleanerAgentKind.Kimi, KimiCatBtn, enabled.Contains("kimi"));

        CleanerEmptyText.Visibility = _cleanerVisible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CleanerBody.Visibility = _cleanerVisible.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_cleanerVisible.Count == 0) return;

        SetCleanerActive(_cleanerVisible[0]);
        foreach (var kind in _cleanerVisible)
            _ = RefreshCleanerAsync(kind);
        ApplyCleanerCount();
    }

    private void SetCleanerAgentVisible(CleanerAgentKind kind, Button button, bool visible)
    {
        button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) { _cleanerVisible.Add(kind); _cleanerCounts[kind] = null; }
    }

    private void CleanerAgent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        SetCleanerActive(tag switch
        {
            "opencode" => CleanerAgentKind.OpenCode,
            "gajae" => CleanerAgentKind.Gajae,
            "codex" => CleanerAgentKind.Codex,
            "grok" => CleanerAgentKind.Grok,
            "antigravity" => CleanerAgentKind.Antigravity,
            "kimi" => CleanerAgentKind.Kimi,
            _ => CleanerAgentKind.Claude,
        });
        ApplyCleanerCount();
    }

    private void SetCleanerActive(CleanerAgentKind kind)
    {
        _cleanerCurrent = kind;
        var active = (Brush)FindResource("PanelBrush");
        var primary = (Brush)FindResource("PrimaryBrush");
        var line = (Brush)FindResource("LineBrush");
        var text = (Brush)FindResource("TextBrush");

        ApplyCleanerPill(ClaudeCatBtn, kind == CleanerAgentKind.Claude, active, primary, line, text);
        ApplyCleanerPill(OpenCodeCatBtn, kind == CleanerAgentKind.OpenCode, active, primary, line, text);
        ApplyCleanerPill(GajaeCatBtn, kind == CleanerAgentKind.Gajae, active, primary, line, text);
        ApplyCleanerPill(CodexCatBtn, kind == CleanerAgentKind.Codex, active, primary, line, text);
        ApplyCleanerPill(GrokCatBtn, kind == CleanerAgentKind.Grok, active, primary, line, text);
        ApplyCleanerPill(AntigravityCatBtn, kind == CleanerAgentKind.Antigravity, active, primary, line, text);
        ApplyCleanerPill(KimiCatBtn, kind == CleanerAgentKind.Kimi, active, primary, line, text);

        VacuumSection.Visibility = kind == CleanerAgentKind.OpenCode ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void ApplyCleanerPill(Button button, bool selected, Brush active, Brush primary, Brush line, Brush text)
    {
        button.Background = selected ? active : Brushes.Transparent;
        button.BorderBrush = selected ? primary : line;
        button.Foreground = selected ? primary : text;
    }

    private async Task RefreshCleanerAsync(CleanerAgentKind kind)
    {
        _cleanerLoading.Add(kind);
        if (kind == _cleanerCurrent) ApplyCleanerCount();
        try
        {
            _cleanerCounts[kind] = await Task.Run(() => SessionCleanerService.GetScanInfo(kind));
        }
        catch
        {
            _cleanerCounts[kind] = null;
        }
        finally
        {
            _cleanerLoading.Remove(kind);
            if (kind == _cleanerCurrent) ApplyCleanerCount();
        }
    }

    private void ApplyCleanerCount()
    {
        if (_cleanerVisible.Count == 0) return;
        if (_cleanerLoading.Contains(_cleanerCurrent))
        {
            AgentCountText.Visibility = Visibility.Collapsed;
            CountSpinner.Visibility = Visibility.Visible;
            CleanerDeleteBtn.IsEnabled = false;
            return;
        }

        if (_cleanerCounts.TryGetValue(_cleanerCurrent, out var info) && info is { } scan)
        {
            CountSpinner.Visibility = Visibility.Collapsed;
            AgentCountText.Visibility = Visibility.Visible;
            AgentCountText.Text = $"{scan.Count}";
            AgentCountText.FontSize = 32;
            AgentCapacityText.Text = $"({FormatCleanerBytes(scan.Bytes)})";
            AgentCapacityText.FontSize = 17;
            CleanerDeleteBtn.IsEnabled = scan.Count > 0;
            return;
        }

        CountSpinner.Visibility = Visibility.Collapsed;
        AgentCountText.Visibility = Visibility.Visible;
        AgentCountText.Text = "-";
        AgentCountText.FontSize = 32;
        AgentCapacityText.Text = "";
        CleanerDeleteBtn.IsEnabled = false;
    }

    private async void CleanerRefresh_Click(object sender, RoutedEventArgs e) => await RefreshCleanerAsync(_cleanerCurrent);

    private async void CleanerVacuum_Click(object sender, RoutedEventArgs e)
    {
        VacuumBtn.IsEnabled = false;
        VacuumBtn.Content = "정리 중...";
        try
        {
            var result = await Task.Run(() => SessionCleanerService.VacuumOpenCodeDb());
            ConfirmDialog.Alert("OpenCode DB 정리", result);
            await RefreshCleanerAsync(CleanerAgentKind.OpenCode);
        }
        finally
        {
            VacuumBtn.IsEnabled = true;
            VacuumBtn.Content = "OpenCode DB 정리";
        }
    }

    private async void CleanerDelete_Click(object sender, RoutedEventArgs e)
    {
        var scan = await EnsureCleanerScanAsync();
        if (scan.Count <= 0)
        {
            await RefreshCleanerAsync(_cleanerCurrent);
            return;
        }

        var name = _cleanerCurrent switch
        {
            CleanerAgentKind.OpenCode => "OpenCode",
            CleanerAgentKind.Gajae => "Gajae Code",
            CleanerAgentKind.Codex => "Codex",
            CleanerAgentKind.Grok => "Grok",
            CleanerAgentKind.Antigravity => "Antigravity",
            CleanerAgentKind.Kimi => "Kimi",
            _ => "Claude",
        };
        var extra = _cleanerCurrent == CleanerAgentKind.OpenCode
            ? "\n\n⚠ 빠른 삭제를 위해 실행 중인 OpenCode가 모두 종료됩니다. 진행 중인 OpenCode 작업이 중단될 수 있습니다."
            : "";
        var ok = ConfirmDialog.Show("관리중이지 않은 세션 삭제",
            $"{name}의 DevezCode에서 관리중이지 않은 세션 {scan.Count}개를 PC에서 완전 삭제합니다.\n" +
            "DevezCode가 현재 관리 중인 세션은 삭제 대상에서 제외됩니다." + extra,
            okLabel: "삭제", danger: true, iconKey: "IconTrash2");
        if (!ok) return;

        AgentCountText.Visibility = Visibility.Collapsed;
        CountSpinner.Visibility = Visibility.Visible;
        CleanerDeleteBtn.IsEnabled = false;
        var result = await Task.Run(() => SessionCleanerService.DeleteUnmanaged(_cleanerCurrent));
        await RefreshCleanerAsync(_cleanerCurrent);

        var message = result.failed == 0
            ? $"{name}의 DevezCode에서 관리중이지 않은 세션 {result.deleted}개를 삭제했습니다."
            : $"{name}의 DevezCode에서 관리중이지 않은 세션 {result.deleted}개를 삭제했습니다.\n삭제 실패 {result.failed}개는 파일 잠금 또는 에이전트 CLI 제한으로 남았습니다.";
        ConfirmDialog.Alert("세션 클리너", message);
    }

    private async Task<CleanerScanInfo> EnsureCleanerScanAsync()
    {
        if (_cleanerCounts.TryGetValue(_cleanerCurrent, out var cached) && cached is { } scan) return scan;
        await RefreshCleanerAsync(_cleanerCurrent);
        return _cleanerCounts.TryGetValue(_cleanerCurrent, out var count) && count is { } v ? v : new CleanerScanInfo(0, 0);
    }

    private static string FormatCleanerBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = System.Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    private void PreloadAllSessionsToggle_Changed(object sender, RoutedEventArgs e)
    {
        _selectedPreloadAllSessions = PreloadAllSessionsToggle.IsChecked == true;
    }

    private void ShowCollapsedProjectPathToggle_Changed(object sender, RoutedEventArgs e)
    {
        _selectedShowCollapsedProjectPath = ShowCollapsedProjectPathToggle.IsChecked == true;
    }

    private void ClaudeGuiModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        _selectedClaudeGuiMode = ClaudeGuiModeToggle.IsChecked == true;
    }

    private void IdleSessionShutdownCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IdleSessionShutdownCombo.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out var minutes))
            _selectedIdleSessionShutdownMinutes = minutes;
    }

    private void DefaultFontSizeCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DefaultFontSizeCombo.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out var pt))
            _selectedDefaultFontSizePt = pt;
    }

    private static readonly string[] RecommendedTerminalFontFamilies =
    {
        "Cascadia Mono", "Cascadia Code", "Consolas", "D2Coding", "JetBrains Mono", "Fira Code", "NanumGothicCoding"
    };

    // WebView 자산에 포함되어 있어 Windows에 설치되지 않은 PC에서도 인앱 터미널과 Claude GUI에서 사용할 수 있다.
    private static readonly string[] BundledFontFamilies = { "Pretendard" };

    private void PopulateTerminalFontFamilyCombo(string selectedFamily)
    {
        var installed = Fonts.SystemFontFamilies
            .Select(font => font.Source)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var options = RecommendedTerminalFontFamilies
            .Where(installed.Contains)
            .Concat(BundledFontFamilies)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!string.IsNullOrWhiteSpace(selectedFamily) && !options.Contains(selectedFamily, StringComparer.OrdinalIgnoreCase))
            options.Add(selectedFamily);

        var windowsTerminalDefault = Services.Terminal.TerminalSessionManager.Instance.Config.FontFamily;
        DefaultFontFamilyCombo.Items.Clear();
        DefaultFontFamilyCombo.Items.Add(new ComboBoxItem
        {
            Content = $"Windows Terminal 기본값 ({windowsTerminalDefault})",
            Tag = "",
        });
        foreach (var family in options)
        {
            var isBundled = BundledFontFamilies.Contains(family, StringComparer.OrdinalIgnoreCase);
            DefaultFontFamilyCombo.Items.Add(new ComboBoxItem
            {
                Content = isBundled ? $"{family} (내장)" : family,
                Tag = family,
            });
        }

        SelectComboByTag(DefaultFontFamilyCombo, selectedFamily);
    }

    private void DefaultFontFamilyCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DefaultFontFamilyCombo.SelectedItem is not ComboBoxItem item) return;
        _selectedTerminalFontFamily = item.Tag?.ToString()?.Trim() ?? "";
        if (!_terminalFontFamilyReady || string.Equals(_selectedTerminalFontFamily, _originalTerminalFontFamily, StringComparison.Ordinal)) return;
        SettingsService.SaveTerminalFontFamily(_selectedTerminalFontFamily);
        _originalTerminalFontFamily = _selectedTerminalFontFamily;
    }

    private void SetMarkdownViewportWidthEditor(int width)
    {
        _syncingMarkdownViewportWidth = true;
        try
        {
            bool custom = width > 0;
            SelectComboByTag(MarkdownViewportWidthCombo, custom ? "custom" : "fit");
            MarkdownViewportWidthInputBox.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            MarkdownViewportWidthUnit.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            MarkdownViewportWidthBox.Text = custom ? width.ToString() : "";
        }
        finally { _syncingMarkdownViewportWidth = false; }
    }

    /// <summary>문서 테마 선택 — 즉시 저장(SaveMarkdownTheme 이 동일값이면 무시하므로 초기 선택 발화는 무해).
    /// 이벤트가 열려 있는 문서 뷰어에 바로 반영된다.</summary>
    private void MarkdownThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((MarkdownThemeCombo.SelectedItem as ComboBoxItem)?.Tag is string tag)
            SettingsService.SaveMarkdownTheme(tag);
    }

    /// <summary>웹브라우저 색 구성 선택 — 즉시 저장하고 열려 있는 브라우저 패널에 반영.</summary>
    private void BrowserThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((BrowserThemeCombo.SelectedItem as ComboBoxItem)?.Tag is string tag)
            SettingsService.SaveBrowserTheme(tag);
    }

    private void MarkdownViewportWidthCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingMarkdownViewportWidth) return;
        bool custom = (MarkdownViewportWidthCombo.SelectedItem as ComboBoxItem)?.Tag as string == "custom";
        MarkdownViewportWidthInputBox.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        MarkdownViewportWidthUnit.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        if (!custom)
        {
            _selectedMarkdownViewportWidth = 0;
            MarkdownViewportWidthBox.Text = "";
        }
        else if (_selectedMarkdownViewportWidth > 0)
        {
            MarkdownViewportWidthBox.Text = _selectedMarkdownViewportWidth.ToString();
        }
        else
        {
            _selectedMarkdownViewportWidth = 800;
            MarkdownViewportWidthBox.Text = "800";
        }
    }

    private void MarkdownViewportWidthBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingMarkdownViewportWidth) return;
        if (string.IsNullOrWhiteSpace(MarkdownViewportWidthBox.Text))
        {
            _selectedMarkdownViewportWidth = 0;
            return;
        }
        if (int.TryParse(MarkdownViewportWidthBox.Text, out var width))
            _selectedMarkdownViewportWidth = System.Math.Max(0, width);
    }

    private void MarkdownViewportWidthBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = e.Text.Any(ch => !char.IsDigit(ch));

    private void AutoLoadLastProjectToggle_Changed(object sender, RoutedEventArgs e)
    {
        _selectedAutoLoadLastProject = AutoLoadLastProjectToggle.IsChecked == true;
    }

    private void PromptForNewSessionNameToggle_Changed(object sender, RoutedEventArgs e)
    {
        _selectedPromptForNewSessionName = PromptForNewSessionNameToggle.IsChecked == true;
    }

    private void PromptForNewBrowserTabNameToggle_Changed(object sender, RoutedEventArgs e)
    {
        _selectedPromptForNewBrowserTabName = PromptForNewBrowserTabNameToggle.IsChecked == true;
    }

    private void BrowserHomeUrlBox_TextChanged(object sender, TextChangedEventArgs e)
        => _selectedBrowserHomeUrl = BrowserHomeUrlBox.Text;

    private void BrowserMcpToggle_Changed(object sender, RoutedEventArgs e)
    {
        _selectedBrowserMcp = BrowserMcpToggle.IsChecked == true;
    }

    private void TerminalUrlOpenTargetCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TerminalUrlOpenTargetCombo.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<TerminalUrlOpenTarget>(item.Tag?.ToString(), out var target))
            _selectedTerminalUrlOpenTarget = target;
    }

    private void HiddenSessionInsertionCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (HiddenSessionInsertionCombo.SelectedItem is ComboBoxItem item)
            _selectedHiddenSessionInsertionOnTop = string.Equals(item.Tag?.ToString(), "top", StringComparison.Ordinal);
    }

    private void HideProjectInfoHeaderToggle_Changed(object sender, RoutedEventArgs e)
    {
        _selectedHideProjectInfoHeader = HideProjectInfoHeaderToggle.IsChecked == true;
    }

    private void DiffGitEnabledToggle_Changed(object sender, RoutedEventArgs e)
    {
        _selectedDiffGitEnabled = DiffGitEnabledToggle.IsChecked == true;
    }

    private void AutoUpdateAgentsToggle_Changed(object sender, RoutedEventArgs e)
    {
        _selectedAutoUpdateAgents = AutoUpdateAgentsToggle.IsChecked == true;
    }

    private bool _instantUpdating;

    /// <summary>'즉시 업데이트' 링크 — 켜진 에이전트를 지금 최신화.
    /// · 실행 중인 세션이 없으면: 그 자리에서 인플레이스 업데이트(모달 ShowDialog, 완료 후 '닫기').
    /// · 세션이 있으면: 인플레이스 설치는 실행 중 바이너리 잠금으로 실패/파손 위험 → 확인 후 앱을 안전 종료·
    ///   재시작하며 업데이트한다(세션은 저장·복원). 시작 경로가 세션 생성 전에 돌아 깨끗이 설치된다.</summary>
    private void InstantUpdateAgents_Click(object sender, MouseButtonEventArgs e)
    {
#if DEBUG
        ConfirmDialog.Show("Debug 실행", "Debug 실행에서는 에이전트 업데이트를 실행하지 않습니다.", okLabel: "확인");
        return;
#endif

        if (_instantUpdating) return;
        _instantUpdating = true;
        try
        {
            if (Services.Terminal.TerminalSessionManager.Instance.HasLiveSessions())
            {
                if (!ConfirmDialog.Show(
                        "재시작하고 업데이트",
                        "실행 중인 세션이 있어 안전하게 업데이트하려면 앱을 재시작해야 합니다.\n" +
                        "세션은 저장 후 자동으로 복원되며, 재시작하면서 에이전트를 최신 버전으로 업데이트합니다.\n\n" +
                        "지금 재시작하고 업데이트할까요?",
                        okLabel: "재시작하고 업데이트"))
                    return;

                // 모달 SettingsWindow 를 먼저 닫는다(CloseRequested → Window.Close(), 동기) — 안 그러면 이 창이
                // 메인 창의 '안전하게 종료합니다' 종료 오버레이를 덮어 메시지가 안 보인다.
                CloseRequested?.Invoke(this, EventArgs.Empty);

                if (!App.RestartForAgentUpdate())
                    ConfirmDialog.Show("재시작 실패",
                        "재시작을 시작하지 못했습니다. 잠시 후 다시 시도하거나 앱을 직접 재시작해 주세요.",
                        okLabel: "확인");
                return; // 성공 시 앱이 곧 종료·재실행됨
            }

            var win = new AgentUpdateWindow
            {
                Owner = Window.GetWindow(this),
                ShowInTaskbar = false,
                AutoCloseOnComplete = false,
            };
            win.ProceedRequested += () => { try { win.Close(); } catch { } };
            win.ShowDialog();
        }
        finally { _instantUpdating = false; }
    }

    private void UseFullScreenToggle_Changed(object sender, RoutedEventArgs e)
    {
        _selectedUseFullScreen = UseFullScreenToggle.IsChecked == true;
    }

    private void MinimizeOnCloseToggle_Changed(object sender, RoutedEventArgs e)
    {
        _selectedMinimizeOnClose = MinimizeOnCloseToggle.IsChecked == true;
    }

    // ── 프로젝트 목록 열 수 (1/2) — 적용은 [저장] 시점에만(라이브 미리보기 없음) ──
    private void ProjectColumnsCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectColumnsCombo.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out var cols))
            _selectedProjectColumns = cols == 2 ? 2 : 1;
    }

    // ── 업데이트 내역 렌더링/페이지네이션 (devez 정합) ──
    private void RenderChangelogPage()
    {
        ChangelogItemsHost.Children.Clear();
        var totalPages = (int)System.Math.Ceiling(_changelog.Length / (double)ChangelogPageSize);
        var items = _changelog.Skip(_changelogPage * ChangelogPageSize).Take(ChangelogPageSize);
        foreach (var (version, date, isLatest, notes) in items)
            ChangelogItemsHost.Children.Add(MakeVersionCard(version, date, isLatest, notes));

        if (totalPages > 1)
        {
            ChangelogPager.Visibility = Visibility.Visible;
            PageIndicator.Text = $"{_changelogPage + 1} / {totalPages}";
            PrevPageBtn.IsEnabled = _changelogPage > 0;
            NextPageBtn.IsEnabled = _changelogPage < totalPages - 1;
        }
        else ChangelogPager.Visibility = Visibility.Collapsed;
    }

    private Border MakeVersionCard(string version, string date, bool isLatest, string[] notes)
    {
        var badgeBg = isLatest ? (Brush)FindResource("PrimarySoftBrush") : (Brush)FindResource("PanelSoftBrush");
        var badgeFg = isLatest ? (Brush)FindResource("PrimaryBrush")     : (Brush)FindResource("TextMutedBrush");

        var badge = new Border
        {
            Background   = badgeBg,
            CornerRadius = new CornerRadius(6),
            Padding      = new Thickness(8, 3, 8, 3),
            Child        = new TextBlock { Text = version, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = badgeFg },
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(badge);
        header.Children.Add(new TextBlock
        {
            Text = date, FontSize = 12, Foreground = (Brush)FindResource("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
        });

        // 항목이 워드랩될 때 둘째 줄이 글머리 기호 아래로 붙지 않도록 항목마다 매달린 들여쓰기로 그린다.
        var noteList = new StackPanel();
        var noteFg = (Brush)FindResource("TextBrush");
        foreach (var note in notes)
            noteList.Children.Add(NoteText.BulletRow("•", note, noteFg, 13, 22));

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(noteList);
        return new Border
        {
            Background = (Brush)FindResource("PanelBrush"),
            CornerRadius = new CornerRadius(10),
            BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(16, 14, 16, 14),
            Child = body,
        };
    }

    private void PrevPageBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_changelogPage > 0) { _changelogPage--; RenderChangelogPage(); }
    }

    private void NextPageBtn_Click(object sender, RoutedEventArgs e)
    {
        var totalPages = (int)System.Math.Ceiling(_changelog.Length / (double)ChangelogPageSize);
        if (_changelogPage < totalPages - 1) { _changelogPage++; RenderChangelogPage(); }
    }

    /// <summary>테마 변경 시 DynamicResource가 아닌 코드 할당 브러시와 테마별 아이콘을 모두 재계산한다.</summary>
    private void RefreshAfterThemeChange()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            SetActiveCategory(_activeCategoryKey);
            UpdateThemeSelectionVisual();
            UpdateFontSelectionVisual();
            UpdateNotifyPositionVisual();
            UpdateShortcutVisual();
            if (_cleanerBuilt && _cleanerVisible.Count > 0)
                SetCleanerActive(_cleanerCurrent);
            foreach (var item in _agentItems)
                item.InstalledBrush = (Brush)FindResource(
                    item.Installed ? "PrimaryBrush" : "TextMutedBrush");
            if (DeepSeekKeyStatus.Visibility == Visibility.Visible)
                DeepSeekKeyStatus.SetResourceReference(
                    TextBlock.ForegroundProperty,
                    "DangerBrush");
            if (ChangelogItemsHost.Children.Count > 0)
                RenderChangelogPage();

            CodexLoginIcon.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(App.CodexIconUri));
            CodexCatIcon.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(App.CodexIconUri));
            OpenCodeLoginIcon.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(App.OpenCodeIconUri));
            OpenCodeCatIcon.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(App.OpenCodeIconUri));
            GrokLoginIcon.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(App.GrokIconUri));
            foreach (var a in _agentItems) a.RefreshAgentIcon();   // 에이전트 목록 아이콘(흑/백) 테마 반영
        }));
    }

    /// <summary>MCP 서버 관리 — 별도 오버레이 창으로 열기. 설정창은 닫지 않는다(독립 편집).</summary>
    private void OpenMcpManager_Click(object sender, RoutedEventArgs e)
    {
        // 옵션은 즉시 저장되므로 원복할 미리보기가 없다. 대기 중인 저장만 확정하고 띄운다.
        FlushAutoSave();
        var dlg = new McpManagerWindow { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
        // 다시 돌아왔을 때 카테고리는 mcp 그대로 유지
        SetActiveCategory("mcp");
    }

    // ── 사이드 패널 뷰 전환 버튼 표시 설정 ──────────────────────────
    private bool _loadingSidePanel;
    private void LoadSidePanelSettings()
    {
        _loadingSidePanel = true;
        _originalShowDirView     = _selectedShowDirView     = SettingsService.LoadShowDirViewBtn();
        _originalShowQueueView   = _selectedShowQueueView   = SettingsService.LoadShowQueueViewBtn();
        _originalShowBrowserView = _selectedShowBrowserView = SettingsService.LoadShowBrowserViewBtn();
        _originalShowDiffView    = _selectedShowDiffView    = SettingsService.LoadShowDiffViewBtn();
        ShowDirViewToggle.IsChecked     = _originalShowDirView;
        ShowQueueViewToggle.IsChecked   = _originalShowQueueView;
        ShowBrowserViewToggle.IsChecked = _originalShowBrowserView;
        ShowDiffViewToggle.IsChecked    = _originalShowDiffView;
        _loadingSidePanel = false;
    }

    private void SidePanelToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSidePanel) return;
        _selectedShowDirView     = ShowDirViewToggle.IsChecked == true;
        _selectedShowQueueView   = ShowQueueViewToggle.IsChecked == true;
        _selectedShowBrowserView = ShowBrowserViewToggle.IsChecked == true;
        _selectedShowDiffView    = ShowDiffViewToggle.IsChecked == true;
    }

    // ── 하단 푸터 계정 사용량(provider) 표시 설정 ─────────────────
    private bool _loadingFooterUsage;
    private void LoadFooterUsageSettings()
    {
        _loadingFooterUsage = true;
        UpdateConnectionBadges();

        // 한도 도달 예상 표시 토글
        _originalShowEstimate = _selectedShowEstimate = SettingsService.LoadShowEstimate();
        ShowEstimateToggle.IsChecked = _originalShowEstimate;
        _originalShowRemainingUsage = _selectedShowRemainingUsage = SettingsService.LoadShowRemainingUsage();
        ShowRemainingUsageToggle.IsChecked = _originalShowRemainingUsage;
        _loadingFooterUsage = false;

        // DeepSeek 연결 토글 상태 복원 — 키가 이미 저장되어 있으면 입력 영역은 숨김.
        // 키 없이 켜 둔 선택(키 입력을 기다리는 중)은 그대로 살린다. 이 복원은 카테고리를 다시 열
        // 때마다 도는데, 토글 ON 자체는 디스크에 남는 값이 아니라 키 유무만으로 되돌리면 방금 켠
        // 토글이 곧바로 꺼지고 입력창도 같이 사라진다.
        bool hasKey = DeepSeekCredentialStore.IsConnected();
        bool on = hasKey || _selectedDeepSeekEnabled;
        _originalDeepSeekEnabled = hasKey;
        _selectedDeepSeekEnabled = on;
        DeepSeekEnabledToggle.IsChecked = on;
        DeepSeekKeyArea.Visibility = on && !hasKey ? Visibility.Visible : Visibility.Collapsed;
        DeepSeekKeyStatus.Visibility = Visibility.Collapsed;
    }

    private void EstimateToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingFooterUsage) return;
        _selectedShowEstimate = ShowEstimateToggle.IsChecked == true;
    }

    private void RemainingUsageToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingFooterUsage) return;
        _selectedShowRemainingUsage = ShowRemainingUsageToggle.IsChecked == true;
    }

    // ── 계정 사용량 로그인/재연결 — OAuth 창을 띄운다(갱신은 설정 닫힐 때 MainWindow 가 RefreshNow). ──
    private void ClaudeLogin_Click(object sender, RoutedEventArgs e)
    {
        // 계정 전환은 설정을 닫기 전에 바로 보여야 하므로 MainWindow 경로로 로그인 + 즉시 갱신.
        if (Application.Current.MainWindow is MainWindow main)
            main.LoginClaude();
        else
            new ClaudeLoginWindow(Window.GetWindow(this)).ShowDialog();
        UpdateConnectionBadges();
    }

    private void CodexLogin_Click(object sender, RoutedEventArgs e)
    {
        new CodexLoginWindow(Window.GetWindow(this)).ShowDialog();
        UpdateConnectionBadges();
    }

    private void OpenCodeLogin_Click(object sender, RoutedEventArgs e)
    {
        new OpenCodeGoLoginWindow(Window.GetWindow(this)).ShowDialog();
        UpdateConnectionBadges();
    }

    private void GrokLogin_Click(object sender, RoutedEventArgs e)
    {
        var win = new GrokLoginWindow(Window.GetWindow(this));
        win.ShowDialog();
        UpdateConnectionBadges();
        if (win.Captured)
            (Application.Current.MainWindow as MainWindow)?.RefreshGrokUsage();
    }

    private void AntigravityLogin_Click(object sender, RoutedEventArgs e)
    {
        ConfirmDialog.Alert("Antigravity 연결",
            "Antigravity(agy) CLI에서 먼저 로그인해 주세요.\n" +
            "DevezCode는 agy가 Windows 자격 증명 관리자에 저장한 토큰만 읽으며, 제3자 OAuth 자격증명은 포함하지 않습니다.");
        UpdateConnectionBadges();
        (Application.Current.MainWindow as MainWindow)?.RefreshAntigravityUsage();
    }

    private void UsageDisconnect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string provider }) return;

        if (Application.Current.MainWindow is MainWindow main)
        {
            main.DisconnectUsageProvider(provider);
        }
        else
        {
            switch (provider)
            {
                case "codex": CodexCredentialStore.Disconnect(); break;
                case "opencode-go": OpenCodeGoCredentialStore.Disconnect(); break;
                case "grok": GrokCredentialStore.Disconnect(); break;
                case "deepseek": DeepSeekCredentialStore.SaveApiKey(null); break;
            }
        }

        if (provider == "deepseek")
        {
            _originalDeepSeekEnabled = false;
            _selectedDeepSeekEnabled = false;
            DeepSeekEnabledToggle.IsChecked = false;
            DeepSeekKeyArea.Visibility = Visibility.Collapsed;
            DeepSeekKeyStatus.Visibility = Visibility.Collapsed;
        }

        UpdateConnectionBadges();
    }

    // ── DeepSeek API 키 토글/저장 ─────────────────────────────────

    private void DeepSeekToggle_Changed(object sender, RoutedEventArgs e)
    {
        // 디스크 반영은 [저장] 시점에만 — 여기선 선택값과 입력 영역 표시만 토글.
        _selectedDeepSeekEnabled = DeepSeekEnabledToggle.IsChecked == true;
        DeepSeekKeyStatus.Visibility = Visibility.Collapsed;
        // ON + 키 없음 → 키 입력 영역 노출, 그 외(ON+연결됨 / OFF)는 숨김
        DeepSeekKeyArea.Visibility =
            _selectedDeepSeekEnabled && !DeepSeekCredentialStore.IsConnected()
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DeepSeekKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        => DeepSeekKeyHint.Visibility = string.IsNullOrEmpty(DeepSeekKeyBox.Password)
            ? Visibility.Visible : Visibility.Collapsed;

    private void DeepSeekSaveKey_Click(object sender, RoutedEventArgs e)
    {
        var key = DeepSeekKeyBox.Password?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            DeepSeekKeyStatus.Text = "API 키를 입력하세요.";
            DeepSeekKeyStatus.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            DeepSeekKeyStatus.Visibility = Visibility.Visible;
            return;
        }

        DeepSeekCredentialStore.SaveApiKey(key);
        _originalDeepSeekEnabled = true;   // 즉시 연결 — [저장] 기준값도 갱신
        _selectedDeepSeekEnabled = true;
        DeepSeekConnectedBadge.Visibility = Visibility.Visible;
        DeepSeekKeyBox.Clear();
        // 저장 직후 입력 영역 숨김 — 토글만 켜진 상태 유지
        DeepSeekKeyArea.Visibility = Visibility.Collapsed;
        DeepSeekKeyStatus.Visibility = Visibility.Collapsed;
        UpdateConnectionBadges();

        (Application.Current.MainWindow as MainWindow)?.RefreshDeepSeekUsage();
    }

    /// <summary>provider 별 "연결됨" 배지를 현재 토큰/자격증명 상태로 갱신.</summary>
    private void UpdateConnectionBadges()
    {
        bool codexConnected = CodexUsageService.IsConnected();
        bool goConnected = OpenCodeGoCredentialStore.IsConnected();
        bool deepSeekConnected = DeepSeekCredentialStore.IsConnected();
        bool grokConnected = GrokUsageService.IsConnected();
        bool antigravityConnected = AntigravityUsageService.IsConnected();

        ClaudeConnectedBadge.Visibility = UsageApiService.IsConnected() ? Visibility.Visible : Visibility.Collapsed;
        AntigravityConnectedBadge.Visibility = antigravityConnected ? Visibility.Visible : Visibility.Collapsed;
        AntigravityDisconnectButton.Visibility = antigravityConnected ? Visibility.Visible : Visibility.Collapsed;
        CodexConnectedBadge.Visibility = codexConnected ? Visibility.Visible : Visibility.Collapsed;
        GoConnectedBadge.Visibility = goConnected ? Visibility.Visible : Visibility.Collapsed;
        DeepSeekConnectedBadge.Visibility = deepSeekConnected ? Visibility.Visible : Visibility.Collapsed;
        GrokConnectedBadge.Visibility = grokConnected ? Visibility.Visible : Visibility.Collapsed;
        CodexDisconnectButton.Visibility = codexConnected ? Visibility.Visible : Visibility.Collapsed;
        GoDisconnectButton.Visibility = goConnected ? Visibility.Visible : Visibility.Collapsed;
        DeepSeekDisconnectButton.Visibility = deepSeekConnected ? Visibility.Visible : Visibility.Collapsed;
        GrokDisconnectButton.Visibility = grokConnected ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 알림 설정 (모두 [저장] 버튼에서만 디스크 반영 — 테스트 버튼은 저장된 값으로 동작) ──
    private bool _loadingNotify;
    private string _notifyPos = "br";
    private bool _selectedNotifyEnabled;
    private bool _originalNotifyEnabled;
    private void LoadNotifySettings()
    {
        _loadingNotify = true;
        _originalNotifyEnabled = SettingsService.LoadNotifySessionDoneEnabled();
        _selectedNotifyEnabled = _originalNotifyEnabled;
        NotifyEnabledToggle.IsChecked = _originalNotifyEnabled;
        _notifyPos = _originalNotifyPos = SettingsService.LoadNotifyPosition();
        UpdateNotifyPositionVisual();
        _originalNotifyAutoCloseSec = _selectedNotifyAutoCloseSec = SettingsService.LoadNotifyAutoCloseSeconds();
        SelectComboByTag(NotifyAutoCloseCombo, _originalNotifyAutoCloseSec.ToString());
        InitNotifyMonitorCombo();
        UpdateNotifyDetailVisibility();
        _loadingNotify = false;
    }

    /// <summary>세션 종료 알림이 꺼져 있으면 상세 설정(자동닫힘/위치/모니터)과 테스트 버튼을 모두 숨긴다.</summary>
    private void UpdateNotifyDetailVisibility()
    {
        var on = NotifyEnabledToggle.IsChecked == true;
        NotifyDetailPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        TestNotifyBtn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>설치된 모니터 목록을 콤보에 채우고 저장된 선택값(없으면 주 모니터)을 선택.</summary>
    private void InitNotifyMonitorCombo()
    {
        var monitors = MonitorHelper.GetAllMonitors();
        _originalNotifyMonitor = _selectedNotifyMonitor = SettingsService.LoadNotifyMonitorDevice();
        NotifyMonitorCombo.Items.Clear();
        int selectIndex = 0;
        for (int i = 0; i < monitors.Count; i++)
        {
            NotifyMonitorCombo.Items.Add(new ComboBoxItem { Content = monitors[i].DisplayName, Tag = monitors[i].DeviceName });
            if (monitors[i].DeviceName == _originalNotifyMonitor) selectIndex = i;
        }
        if (NotifyMonitorCombo.Items.Count > 0) NotifyMonitorCombo.SelectedIndex = selectIndex;
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (var obj in combo.Items)
            if (obj is ComboBoxItem it && (string?)it.Tag == tag) { combo.SelectedItem = it; return; }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private void NotifyEnabledToggle_Changed(object sender, RoutedEventArgs e)
    {
        UpdateNotifyDetailVisibility();
        if (_loadingNotify) return;
        _selectedNotifyEnabled = NotifyEnabledToggle.IsChecked == true; // 저장은 [저장] 버튼에서
    }

    private void NotifyAutoCloseCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingNotify) return;
        if (NotifyAutoCloseCombo.SelectedItem is ComboBoxItem it && int.TryParse((string)it.Tag, out var sec))
            _selectedNotifyAutoCloseSec = sec;
    }

    private void NotifyMonitorCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingNotify) return;
        if (NotifyMonitorCombo.SelectedItem is ComboBoxItem it)
            _selectedNotifyMonitor = (string)(it.Tag ?? "");
    }

    private void NotifyPositionCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b || b.Tag is not string pos) return;
        _notifyPos = pos;
        UpdateNotifyPositionVisual();
        ScheduleAutoSave();
    }

    private void UpdateNotifyPositionVisual()
    {
        var primary = (Brush)FindResource("PrimaryBrush");
        var line    = (Brush)FindResource("LineBrush");
        foreach (var (card, dot, pos) in new (Border, Ellipse, string)[]
        {
            (PosCard_tl, PosDot_tl, "tl"),
            (PosCard_tr, PosDot_tr, "tr"),
            (PosCard_bl, PosDot_bl, "bl"),
            (PosCard_br, PosDot_br, "br"),
        })
        {
            var selected = _notifyPos == pos;
            card.BorderBrush = selected ? primary : line;
            dot.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void TestNotify_Click(object sender, RoutedEventArgs e)
        => App.ShowNotification("테스트 알림", "세션이 끝나면 이렇게 알려드립니다.");

    // ── 탭 이동 단축키 수식키 리바인드 (방향키는 ← / → 고정) ──────
    /// <summary>수식키 칸 클릭 → 전역 훅 캡처 시작. 다음 키다운 1회를 수식키로 지정.</summary>
    private void KeyField_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b || b.Tag is not string field) return;
        if (_capturingField == field) { CancelShortcutCapture(); return; } // 같은 칸 재클릭 = 취소
        _capturingField = field;
        UpdateShortcutVisual();
        GlobalTabHotkey.BeginCapture(vk =>
        {
            if (vk != 0x1B) { _selectedHkMod = vk; ScheduleAutoSave(); } // Esc = 취소, 그 외 = 지정
            _capturingField = null;
            UpdateShortcutVisual();
        });
    }

    private void CancelShortcutCapture()
    {
        if (_capturingField == null) return;
        _capturingField = null;
        GlobalTabHotkey.CancelCapture();
        UpdateShortcutVisual();
    }

    /// <summary>수식키 칸 텍스트/보더를 현재 선택값(또는 캡처 중 표시)으로 갱신.</summary>
    private void UpdateShortcutVisual()
    {
        bool capturing = _capturingField == "mod";
        ModKeyText.Text = capturing ? "키 입력…" : GlobalTabHotkey.KeyName(_selectedHkMod);
        ModKeyField.BorderBrush = capturing
            ? (Brush)FindResource("PrimaryBrush")
            : (Brush)FindResource("LineBrush");
    }


    // ── 깨우기 (WakeSchedulerWindow 이식 — 설정창 내부 탭) ─────────
    private static readonly DayOfWeek[] _wakeEveryDay = Enum.GetValues<DayOfWeek>();
    private readonly ObservableCollection<WakeItem> _wakeItems = new();
    private readonly List<WakeScheduleEntry> _wakeHiddenEntries = new();   // UI 대상이 아닌 예약(꺼진 에이전트)은 그대로 보존
    private AgentDef? _untrustedWakeAgent;
    private bool _wakeBuilt;
    private bool _wakeTimeFormatting;
    private bool _wakeTrustCheckInProgress;
    private string _originalWakeSignature = "";

    /// <summary>깨우기 탭 진입 — 최초 1회 목록을 구성하고, 켜진 항목의 신뢰 설정을 확인한다.</summary>
    private void EnterWake()
    {
        if (!_wakeBuilt) BuildWakeList();
        RefreshWakeTrustState();
        _ = EnsureRequiredWakeTrustAsync();
    }

    private void BuildWakeList()
    {
        _wakeBuilt = true;
        var agents = AgentRegistry.GetEnabledAndInstalled()
            .Where(a => string.Equals(a.Id, "claude", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(a.Id, "codex", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var providerIds = agents.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var schedules = SettingsService.LoadWakeSchedules().Select(e => e.Clone()).ToList();

        _wakeHiddenEntries.Clear();
        _wakeHiddenEntries.AddRange(schedules.Where(e => !providerIds.Contains(e.Provider)));

        _wakeItems.Clear();
        foreach (var agent in agents)
        {
            var entry = schedules.FirstOrDefault(e =>
                            string.Equals(e.Provider, agent.Id, StringComparison.OrdinalIgnoreCase))
                        ?? new WakeScheduleEntry { Provider = agent.Id, Enabled = false };
            entry.Weekdays = _wakeEveryDay.ToList();   // 요일 선택 없이 매일 고정
            _wakeItems.Add(new WakeItem(agent, entry));
        }

        WakeList.ItemsSource = _wakeItems;
        WakeEmptyHint.Visibility = _wakeItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _originalWakeSignature = WakeSignature();
    }

    private string WakeSignature()
        => string.Join("|", _wakeItems.Select(i => $"{i.Id}:{i.Enabled}:{i.Time}"));

    /// <summary>깨우기 예약 저장 — 시간이 유효할 때만 디스크에 쓰고 스케줄러에 알린다.
    /// (유효하지 않은 값은 콤보 LostFocus 에서 보정된 뒤 다음 저장에 반영된다.)</summary>
    private void SaveWakeSchedulesIfChanged()
    {
        if (!_wakeBuilt) return;
        var signature = WakeSignature();
        if (signature == _originalWakeSignature) return;
        if (_wakeItems.Any(i => !TryParseWakeTime(i.Time).valid)) return;

        if (!SettingsService.SaveWakeSchedules(_wakeHiddenEntries.Concat(_wakeItems.Select(i => i.Entry))))
            return;
        _originalWakeSignature = signature;
        (Application.Current.MainWindow as MainWindow)?.NotifyWakeSchedulesChanged();
    }

    private void WakeEnabledToggle_Click(object sender, RoutedEventArgs e)
    {
        RefreshWakeTrustState();
        _ = EnsureRequiredWakeTrustAsync();
    }

    /// <summary>켜진 예약 중 설치 경로 신뢰가 아직 안 된 에이전트를 찾아 안내 카드를 갱신한다.</summary>
    private void RefreshWakeTrustState()
    {
        _untrustedWakeAgent = _wakeItems
            .FirstOrDefault(i => i.Enabled && !WakeTrustService.IsTrusted(i.Id))?.Agent;
        WakeTrustPanel.Visibility = _untrustedWakeAgent == null ? Visibility.Collapsed : Visibility.Visible;
        if (_untrustedWakeAgent != null)
            WakeTrustMessage.Text = $"{_untrustedWakeAgent.DisplayName}: 프로젝트 경로의 신뢰 설정을 자동으로 확인하고 있습니다.\n" +
                                    WakeTrustService.InstallDirectory;
    }

    private async Task EnsureRequiredWakeTrustAsync()
    {
        if (_wakeTrustCheckInProgress) return;
        if (Application.Current.MainWindow is not MainWindow main) return;
        _wakeTrustCheckInProgress = true;
        try
        {
            while (IsLoaded && WakePanel.Visibility == Visibility.Visible)
            {
                RefreshWakeTrustState();
                var agent = _untrustedWakeAgent;
                if (agent == null) return;

                WakeTrustMessage.Text = $"{agent.DisplayName}: 프로젝트 경로의 신뢰 설정을 자동으로 처리하고 있습니다.\n" +
                                        WakeTrustService.InstallDirectory;
                if (!await main.EnsureWakeTrustAsync(agent.Id))
                {
                    RefreshWakeTrustState();
                    WakeTrustMessage.Text = $"{agent.DisplayName} 신뢰 설정을 자동으로 완료하지 못했습니다.";
                    return;
                }
            }
        }
        finally
        {
            _wakeTrustCheckInProgress = false;
        }
    }

    // 시간 칸: 숫자만 입력받아 HH:mm 으로 자동 정리하고, 포커스를 잃을 때 24시간 형식으로 확정한다.
    private void WakeTimeCombo_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        if (combo.Template.FindName("PART_EditableTextBox", combo) is not TextBox textBox) return;
        textBox.PreviewTextInput -= WakeTimeTextBox_PreviewTextInput;
        textBox.PreviewTextInput += WakeTimeTextBox_PreviewTextInput;
        textBox.TextChanged -= WakeTimeTextBox_AutoFormat;
        textBox.TextChanged += WakeTimeTextBox_AutoFormat;
        DataObject.RemovePastingHandler(textBox, WakeTimeTextBox_Pasting);
        DataObject.AddPastingHandler(textBox, WakeTimeTextBox_Pasting);
    }

    private static void WakeTimeTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = !e.Text.All(char.IsDigit);

    private void WakeTimeTextBox_AutoFormat(object sender, TextChangedEventArgs e)
    {
        if (_wakeTimeFormatting || sender is not TextBox textBox) return;
        var digits = new string(textBox.Text.Where(char.IsDigit).ToArray());
        if (digits.Length > 4) digits = digits[..4];
        var formatted = digits.Length <= 2 ? digits : $"{digits[..2]}:{digits[2..]}";
        if (formatted == textBox.Text) return;
        _wakeTimeFormatting = true;
        textBox.Text = formatted;
        textBox.CaretIndex = formatted.Length;
        _wakeTimeFormatting = false;
    }

    private static void WakeTimeTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(typeof(string)))
        {
            e.CancelCommand();
            return;
        }

        var digits = new string(((e.DataObject.GetData(typeof(string)) as string) ?? "")
            .Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
        {
            e.CancelCommand();
            return;
        }

        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, digits);
        e.DataObject = data;
    }

    private void WakeTimeCombo_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        var text = combo.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        var (time, valid) = TryParseWakeTime(text);
        combo.Text = valid ? $"{time.Hours:D2}:{time.Minutes:D2}" : "23:59";
    }

    private static (TimeSpan time, bool valid) TryParseWakeTime(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (TimeSpan.TryParse(text, out var time) && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1))
            return (new TimeSpan(time.Hours, time.Minutes, 0), true);

        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (digits.Length is 1 or 2 && int.TryParse(digits, out var parsedHour) && parsedHour < 24)
            return (new TimeSpan(parsedHour, 0, 0), true);
        if (digits.Length is 3 or 4)
        {
            var hour = int.Parse(digits[..^2]);
            var minute = int.Parse(digits[^2..]);
            if (hour < 24 && minute < 60) return (new TimeSpan(hour, minute, 0), true);
        }

        return (TimeSpan.Zero, false);
    }

    // ── 카드형 선택 (라우팅 이벤트가 아니므로 저장을 직접 예약한다) ──
    private void ThemeCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is string key)
        {
            _selectedTheme = key;
            (Application.Current as App)?.SetTheme(key, persist: false); // 화면 즉시 반영(저장은 ApplySettings)
            UpdateThemeSelectionVisual();
            ScheduleAutoSave();
        }
    }

    private void FontSizeCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is string tag && int.TryParse(tag, out var scale))
        {
            _selectedFontScale = scale;
            (Application.Current as App)?.SetFontScale(scale); // 화면 즉시 반영
            UpdateFontSelectionVisual();
            ScheduleAutoSave();
        }
    }

    // ── 즉시 저장(디바운스) ───────────────────────────────────────
    // 컨트롤의 변경 이벤트는 버블링되므로 루트에서 한 번만 받아 처리한다(자식 핸들러가 먼저
    // 실행돼 _selected* 가 이미 갱신된 상태). 카드형 선택(테마/글꼴/알림 위치)과 단축키 캡처는
    // 라우팅 이벤트가 아니어서 각 핸들러에서 ScheduleAutoSave() 를 직접 호출한다.
    private System.Windows.Threading.DispatcherTimer? _autoSaveTimer;
    private bool _autoSaveReady;      // 초기 로딩 중 발생하는 변경 이벤트는 무시
    private bool _applyingSettings;   // ApplySettings 가 컨트롤 값을 되쓸 때의 재진입 방지

    private void InitAutoSave()
    {
        AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent,
                   new RoutedEventHandler(AutoSave_Changed), true);
        AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent,
                   new RoutedEventHandler(AutoSave_Changed), true);
        AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,
                   new SelectionChangedEventHandler(AutoSave_Changed), true);
        AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                   new TextChangedEventHandler(AutoSave_Changed), true);
        _autoSaveTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = System.TimeSpan.FromMilliseconds(350),
        };
        _autoSaveTimer.Tick += (_, _) => { _autoSaveTimer!.Stop(); ApplySettings(); };
        // 컨트롤 초기값 주입(ctor·비동기 로딩)이 끝난 뒤부터 저장을 받는다.
        Loaded += (_, _) => Dispatcher.BeginInvoke(new System.Action(() => _autoSaveReady = true),
                                                  System.Windows.Threading.DispatcherPriority.Background);
    }

    private void AutoSave_Changed(object sender, RoutedEventArgs e) => ScheduleAutoSave();

    /// <summary>변경을 잠시 모아 한 번에 저장한다(연속 입력 시 디스크 쓰기 폭주 방지).</summary>
    private void ScheduleAutoSave()
    {
        if (!_autoSaveReady || _applyingSettings || _autoSaveTimer == null) return;
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    /// <summary>대기 중인 저장을 즉시 확정한다(창을 닫기 전 등).</summary>
    private void FlushAutoSave()
    {
        if (_autoSaveTimer is { IsEnabled: true })
        {
            _autoSaveTimer.Stop();
            ApplySettings();
        }
    }

    // ── 닫기 ──────────────────────────────────────────────────────


    /// <summary>헤더 X — devez 처럼 미저장 변경이 있으면 저장 여부를 묻는다.</summary>
    private void CancelBtn_Click(object sender, RoutedEventArgs e) => TryCloseWithConfirm();

    /// <summary>ESC / "앱으로 돌아가기" / 외부에서 호출하는 닫기.
    /// 옵션은 이미 즉시 저장돼 있으므로 저장 여부는 묻지 않고, 테마를 바꿨을 때만
    /// 세션 재시작 여부를 여기서 묻는다(재시작하지 않아도 저장은 유지된다).</summary>
    public void TryCloseWithConfirm()
    {
        if (AccountPanel.IsBusy) { SetActiveCategory("account"); return; }
        FlushAutoSave();   // 디바운스 대기 중인 변경 확정

        if (_themeReloadPending)
        {
            _themeReloadPending = false;
            var restart = ConfirmDialog.Show(
                "테마 변경 적용",
                "테마 변경을 적용하려면 열려 있는 세션을 다시 시작해야 합니다.\n" +
                "응답 생성 중인 세션은 중단될 수 있으며, 필요한 경우 요청을 다시 보내야 합니다.",
                okLabel: "다시 시작하고 변경",
                cancelLabel: "테마 되돌리기",
                iconKey: "IconPalette",
                wideLayout: true); // 세션 재시작 안내 — 긴 본문이라 넓게 유지
            if (restart) (Application.Current.MainWindow as MainWindow)?.ReloadAllSessionsForTheme();
            else         RevertTheme();
        }
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>재시작을 거절한 경우 — 테마를 변경 전 값으로 되돌려 저장한다(세션과 앱 색을 일치시킨다).</summary>
    private void RevertTheme()
    {
        if (_themeBeforeChange == null || _themeBeforeChange == _selectedTheme) return;
        _selectedTheme = _themeBeforeChange;
        _originalTheme = _themeBeforeChange;
        (Application.Current as App)?.SetTheme(_themeBeforeChange);   // persist
        UpdateThemeSelectionVisual();
        _themeBeforeChange = null;
    }

    /// <summary>테마를 처음 바꾼 시점의 이전 테마(되돌리기 기준).</summary>
    private string? _themeBeforeChange;

    /// <summary>앱 종료 등 외부 사유로 화면이 사라질 때 — 대기 중인 저장만 확정한다.
    /// (테마 재시작 안내는 곧 종료되므로 띄우지 않는다.)</summary>
    public void FlushPendingSave()
    {
        FlushAutoSave();
        _themeReloadPending = false;
    }

    /// <summary>테마가 바뀐 뒤 아직 세션에 반영(재시작)되지 않았음.</summary>
    private bool _themeReloadPending;


    /// <summary>현재 UI 값을 디스크에 저장·확정하고 기준값을 갱신한다.</summary>
    private void ApplySettings()
    {
        if (_applyingSettings) return;
        _applyingSettings = true;
        try { ApplySettingsCore(); }
        finally { _applyingSettings = false; }
    }

    private void ApplySettingsCore()
    {
        // 테마는 저장은 즉시 하되 세션 재시작이 필요하므로, 재시작 여부는 창을 닫을 때 묻는다.
        // 되돌리기 기준은 "처음 바꾼 시점의 이전 테마"를 유지한다(여러 번 바꿔도 원본으로 복귀).
        // 테마가 실제로 바뀐 경우에만 적용한다. 무조건 부르면 설정에서 아무 값이나 건드릴 때마다
        // ThemeChanged 가 나가 현재 카테고리가 통째로 재로드되고(RefreshAfterThemeChange), 에이전트
        // 테마 파일 재작성·세션 색 재전송까지 매번 따라붙는다.
        if (_selectedTheme != _originalTheme)
        {
            _themeBeforeChange ??= _originalTheme;
            _themeReloadPending = true;
            (Application.Current as App)?.SetTheme(_selectedTheme); // persist
        }
        SettingsService.SaveFontScale(_selectedFontScale);
        SettingsService.SavePreloadAllProjectSessions(_selectedPreloadAllSessions);
        if (_selectedClaudeGuiMode != _originalClaudeGuiMode)
        {
            SettingsService.SaveClaudeGuiMode(_selectedClaudeGuiMode);
            (Application.Current.MainWindow as MainWindow)?.ReloadClaudeSessionsForGuiMode();
        }
        if (_selectedIdleSessionShutdownMinutes != _originalIdleSessionShutdownMinutes)
        {
            SettingsService.SaveIdleSessionShutdownMinutes(_selectedIdleSessionShutdownMinutes);
            (Application.Current.MainWindow as MainWindow)?.ApplyIdleSessionShutdownSettings();
        }
        if (_selectedDefaultFontSizePt != _originalDefaultFontSizePt)
            SettingsService.SaveTerminalFontSizePt(_selectedDefaultFontSizePt);
        if (!string.Equals(_selectedTerminalFontFamily, _originalTerminalFontFamily, StringComparison.Ordinal))
            SettingsService.SaveTerminalFontFamily(_selectedTerminalFontFamily);
        if (_selectedMarkdownViewportWidth != _originalMarkdownViewportWidth)
            SettingsService.SaveMarkdownViewportWidth(_selectedMarkdownViewportWidth);
        SettingsService.SaveAutoLoadLastProject(_selectedAutoLoadLastProject);
        SettingsService.SavePromptForNewSessionName(_selectedPromptForNewSessionName);
        SettingsService.SavePromptForNewBrowserTabName(_selectedPromptForNewBrowserTabName);
        if (_selectedBrowserMcp != _originalBrowserMcp)
        {
            SettingsService.SaveBrowserMcpEnabled(_selectedBrowserMcp);
            _originalBrowserMcp = _selectedBrowserMcp;
            // 각 에이전트 설정 파일에 devez-browser MCP 서버를 등록/제거. 이미 떠 있는 세션은
            // 재시작해야 반영된다(에이전트가 시작 시 mcpServers 를 읽음).
            BrowserMcpInstaller.Sync();
        }
        SettingsService.SaveBrowserHomeUrl(_selectedBrowserHomeUrl);
        SettingsService.SaveTerminalUrlOpenTarget(_selectedTerminalUrlOpenTarget);
        if (_selectedHiddenSessionInsertionOnTop != _originalHiddenSessionInsertionOnTop)
            SettingsService.SaveHiddenSessionInsertionOnTop(_selectedHiddenSessionInsertionOnTop);
        if (_selectedHideProjectInfoHeader != _originalHideProjectInfoHeader)
        {
            SettingsService.SaveHideProjectInfoHeader(_selectedHideProjectInfoHeader);
            (Application.Current.MainWindow as MainWindow)?.ApplyProjectInfoHeaderVisibility();
        }
        if (_selectedDiffGitEnabled != _originalDiffGitEnabled)
        {
            SettingsService.SaveDiffGitEnabled(_selectedDiffGitEnabled);
            DevezCode.Models.GitUiState.Instance.DiffGitEnabled = _selectedDiffGitEnabled;   // 열린 Diff 패널 즉시 반영
        }
        if (_selectedAutoUpdateAgents != _originalAutoUpdateAgents)
            SettingsService.SaveAutoUpdateAgents(_selectedAutoUpdateAgents);
        if (_selectedUseFullScreen != _originalUseFullScreen)
        {
            SettingsService.SaveUseFullScreen(_selectedUseFullScreen);
            (Application.Current.MainWindow as MainWindow)?.ApplyFullScreen(_selectedUseFullScreen);
        }
        if (_selectedMinimizeOnClose != _originalMinimizeOnClose)
            SettingsService.SaveMinimizeOnClose(_selectedMinimizeOnClose);
        if (_selectedProjectColumns != _originalProjectColumns)
        {
            SettingsService.SaveProjectColumns(_selectedProjectColumns);
            (Application.Current.MainWindow as MainWindow)?.ApplyProjectColumns(_selectedProjectColumns);
        }
        if (_selectedShowCollapsedProjectPath != _originalShowCollapsedProjectPath)
        {
            SettingsService.SaveShowCollapsedProjectPath(_selectedShowCollapsedProjectPath);
            if (Application.Current.MainWindow is MainWindow mw)
                mw.Sidebar.ShowCollapsedProjectPath = _selectedShowCollapsedProjectPath;
        }
        UpdateAgentEnabledInSettings();
        SaveWakeSchedulesIfChanged();

        // DeepSeek: 토글 OFF로 저장 → 저장된 키 삭제. (ON은 키 입력 영역의 [저장]에서 이미 반영됨)
        if (!_selectedDeepSeekEnabled && _originalDeepSeekEnabled)
        {
            DeepSeekCredentialStore.SaveApiKey(null);
            DeepSeekConnectedBadge.Visibility = Visibility.Collapsed;
            DeepSeekDisconnectButton.Visibility = Visibility.Collapsed;
            DeepSeekKeyArea.Visibility = Visibility.Collapsed;
            (Application.Current.MainWindow as MainWindow)?.ApplyFooterUsageVisibility();
            (Application.Current.MainWindow as MainWindow)?.RefreshDeepSeekUsage();
        }
        _originalDeepSeekEnabled = _selectedDeepSeekEnabled;

        if (_selectedNotifyEnabled != _originalNotifyEnabled)
            SettingsService.SaveNotifySessionDoneEnabled(_selectedNotifyEnabled);
        _originalNotifyEnabled = _selectedNotifyEnabled;

        if (_selectedNotifyAutoCloseSec != _originalNotifyAutoCloseSec)
            SettingsService.SaveNotifyAutoCloseSeconds(_selectedNotifyAutoCloseSec);
        _originalNotifyAutoCloseSec = _selectedNotifyAutoCloseSec;
        if (_selectedNotifyMonitor != _originalNotifyMonitor)
            SettingsService.SaveNotifyMonitorDevice(_selectedNotifyMonitor);
        _originalNotifyMonitor = _selectedNotifyMonitor;
        if (_notifyPos != _originalNotifyPos)
            SettingsService.SaveNotifyPosition(_notifyPos);
        _originalNotifyPos = _notifyPos;

        if (_selectedShowDirView != _originalShowDirView || _selectedShowQueueView != _originalShowQueueView
            || _selectedShowBrowserView != _originalShowBrowserView || _selectedShowDiffView != _originalShowDiffView)
        {
            SettingsService.SaveShowDirViewBtn(_selectedShowDirView);
            SettingsService.SaveShowQueueViewBtn(_selectedShowQueueView);
            SettingsService.SaveShowBrowserViewBtn(_selectedShowBrowserView);
            SettingsService.SaveShowDiffViewBtn(_selectedShowDiffView);
            (Application.Current.MainWindow as MainWindow)?.ApplySidePanelButtonVisibility();
        }
        _originalShowDirView = _selectedShowDirView;
        _originalShowQueueView = _selectedShowQueueView;
        _originalShowBrowserView = _selectedShowBrowserView;
        _originalShowDiffView = _selectedShowDiffView;

        if (_selectedShowEstimate != _originalShowEstimate)
        {
            SettingsService.SaveShowEstimate(_selectedShowEstimate);
            (Application.Current.MainWindow as MainWindow)?.RefreshUsagePanelIfVisible();
        }
        _originalShowEstimate = _selectedShowEstimate;

        if (_selectedShowRemainingUsage != _originalShowRemainingUsage)
        {
            SettingsService.SaveShowRemainingUsage(_selectedShowRemainingUsage);
            (Application.Current.MainWindow as MainWindow)?.ApplyFooterUsageVisibility();
        }
        _originalShowRemainingUsage = _selectedShowRemainingUsage;

        if (_selectedHkMod != _originalHkMod || _selectedHkPrev != _originalHkPrev || _selectedHkNext != _originalHkNext)
        {
            SettingsService.SaveTabHotkey(_selectedHkMod, _selectedHkPrev, _selectedHkNext);
            GlobalTabHotkey.Configure(_selectedHkMod, _selectedHkPrev, _selectedHkNext); // 런타임 즉시 적용
        }

        _originalTheme       = _selectedTheme;
        _originalFontScale   = _selectedFontScale;
        _originalPreloadAllSessions = _selectedPreloadAllSessions;
        _originalClaudeGuiMode = _selectedClaudeGuiMode;
        _originalIdleSessionShutdownMinutes = _selectedIdleSessionShutdownMinutes;
        _originalDefaultFontSizePt = _selectedDefaultFontSizePt;
        _originalTerminalFontFamily = _selectedTerminalFontFamily;
        _originalMarkdownViewportWidth = _selectedMarkdownViewportWidth;
        SetMarkdownViewportWidthEditor(_selectedMarkdownViewportWidth);
        _originalAutoLoadLastProject = _selectedAutoLoadLastProject;
        _originalPromptForNewSessionName = _selectedPromptForNewSessionName;
        _originalPromptForNewBrowserTabName = _selectedPromptForNewBrowserTabName;
        _originalBrowserHomeUrl = SettingsService.LoadBrowserHomeUrl();
        _selectedBrowserHomeUrl = _originalBrowserHomeUrl;
        BrowserHomeUrlBox.Text = _selectedBrowserHomeUrl;
        _originalTerminalUrlOpenTarget = _selectedTerminalUrlOpenTarget;
        _originalHiddenSessionInsertionOnTop = _selectedHiddenSessionInsertionOnTop;
        _originalHideProjectInfoHeader = _selectedHideProjectInfoHeader;
        _originalDiffGitEnabled = _selectedDiffGitEnabled;
        _originalAutoUpdateAgents = _selectedAutoUpdateAgents;
        _originalUseFullScreen = _selectedUseFullScreen;
        _originalMinimizeOnClose = _selectedMinimizeOnClose;
        _originalProjectColumns = _selectedProjectColumns;
        _originalShowCollapsedProjectPath = _selectedShowCollapsedProjectPath;
        _originalHkMod = _selectedHkMod; _originalHkPrev = _selectedHkPrev; _originalHkNext = _selectedHkNext;
        _originalEnabledAgents = new HashSet<string>(
            _agentItems.Where(a => a.Enabled).Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        _originalRetentionDays = _agentItems.FirstOrDefault(a => a.IsClaudeCode)?.RetentionDays
            ?? ClaudeGlobalSettings.DefaultCleanupPeriodDays;
    }


    private void UpdateThemeSelectionVisual()
    {
        var primary = (Brush)FindResource("PrimaryBrush");
        var line    = (Brush)FindResource("LineBrush");

        foreach (var (card, dot, key) in new (Border, Ellipse, string)[]
        {
            (ThemeCard_Minimal, ThemeRadioDot_Minimal, "minimal"),
            (ThemeCard_Soft,    ThemeRadioDot_Soft,    "soft"),
            (ThemeCard_Dark,    ThemeRadioDot_Dark,    "dark"),
            (ThemeCard_Gray, ThemeRadioDot_Gray, "gray"),
            (ThemeCard_SoftPink, ThemeRadioDot_SoftPink, "softpink"),
            (ThemeCard_Midnight, ThemeRadioDot_Midnight, "midnight"),
        })
        {
            var selected = _selectedTheme == key;
            card.BorderBrush = selected ? primary : line;
            dot.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateFontSelectionVisual()
    {
        var primary = (Brush)FindResource("PrimaryBrush");
        var line    = (Brush)FindResource("LineBrush");

        foreach (var (card, dot, scale) in new (Border, Ellipse, int)[]
        {
            (FontCard_Small, FontRadioDot_Small, 0),
            (FontCard_Large, FontRadioDot_Large, 1),
        })
        {
            var selected = _selectedFontScale == scale;
            card.BorderBrush = selected ? primary : line;
            dot.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ── 에이전트 패널 ──────────────────────────────────────────────
    private void BuildAgentList()
    {
        // 경로 재스캔 (Settings 가 늦게 열릴 수 있으므로 매번 새로).
        AgentRegistry.InvalidateCache();
        var muted = (Brush)FindResource("TextMutedBrush");

        _agentItems.Clear();
        var enabledSet = new HashSet<string>(SettingsService.LoadEnabledAgents(), StringComparer.OrdinalIgnoreCase);
        var autoUpdateSet = new HashSet<string>(
            SettingsService.LoadAutoUpdateAgentIds(), StringComparer.OrdinalIgnoreCase);
        var codex = AgentRegistry.Find("codex");
        var codexInstalled = codex != null && AgentRegistry.IsInstalled(codex);
        foreach (var agent in AgentRegistry.All)
        {
            // UI 노출 제외 (codex 등) — 세션 생성 피커와 동일한 정책 유지
            if (AgentRegistry.HiddenFromUI.Contains(agent.Id)) continue;
            bool installed = AgentRegistry.IsInstalled(agent);
            var path = installed ? AgentRegistry.ResolvePath(agent) : null;
            _agentItems.Add(new AgentItem
            {
                Id = agent.Id,
                DisplayName = agent.DisplayName,
                InstallCommand = agent.InstallCommand,
                Installed = installed,
                InstalledLabel = installed ? "설치됨" : "미설치",
                InstalledBrush = installed
                    ? (Brush)FindResource("PrimaryBrush")
                    : muted,
                VersionText = installed ? "확인 중…" : "—",
                LastUpdatedText = path != null
                    ? File.GetLastWriteTime(path).ToString("yyyy-MM-dd")
                    : "—",
                // PreviewNote/PreviewLocked 는 Enabled 보다 먼저 — 잠금 판정이 Enabled setter 안에서 일어난다.
                PreviewNote = agent.PreviewNote,
                PreviewLocked = agent.PreviewNote.Length > 0 && !enabledSet.Contains(agent.Id),
                RequirementNote = agent.Id == "devezvibe" && !codexInstalled
                    ? "Codex가 설치되어 있지 않습니다. Devez Vibe를 사용하려면 Codex를 설치하세요."
                    : "",
                Enabled = installed && enabledSet.Contains(agent.Id),
                SupportsAutoUpdate = !string.IsNullOrWhiteSpace(agent.UpdateCommand),
                // Enabled 뒤에 대입해야 한다 — Enabled setter 가 자동 업데이트를 같은 값으로 맞추므로
                // 저장된 값이 그 뒤에 와야 사용 여부와 어긋난 조합(한쪽만 켬)이 그대로 살아난다.
                AutoUpdate = installed && autoUpdateSet.Contains(agent.Id),
                IsClaudeCode = agent.Id == "claude",
                RetentionDays = agent.Id == "claude"
                    ? ClaudeGlobalSettings.GetCleanupPeriodDays()
                    : ClaudeGlobalSettings.DefaultCleanupPeriodDays,
            });
        }
        _originalEnabledAgents = new HashSet<string>(
            _agentItems.Where(a => a.Enabled).Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        _originalRetentionDays = _agentItems.FirstOrDefault(a => a.IsClaudeCode)?.RetentionDays
            ?? ClaudeGlobalSettings.DefaultCleanupPeriodDays;
        AgentList.ItemsSource = _agentItems;
        _ = RefreshAgentVersionsAsync(_agentItems.Where(a => a.Installed).ToList());
    }

    private static async Task RefreshAgentVersionsAsync(IEnumerable<AgentItem> items)
    {
        await Task.WhenAll(items.Select(async item =>
        {
            var agent = AgentRegistry.Find(item.Id);
            if (agent == null) return;
            var version = await GetAgentVersionAsync(agent);
            item.VersionText = string.IsNullOrWhiteSpace(version) ? "확인 실패" : version;
        }));
    }

    private static async Task<string> GetAgentVersionAsync(AgentDef agent)
    {
        try
        {
            var executablePath = AgentRegistry.ResolvePath(agent);
            if (string.IsNullOrWhiteSpace(executablePath)) return "";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            var escapedPath = executablePath.Replace("'", "''");
            var versionArgument = agent.Id.Equals("grok", StringComparison.OrdinalIgnoreCase)
                ? "version"
                : "--version";
            psi.ArgumentList.Add($"& '{escapedPath}' {versionArgument}");

            using var process = new Process { StartInfo = psi };
            var output = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
            if (!process.Start()) return "";
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return "";
            }

            lock (output)
            {
                var match = Regex.Match(
                    output.ToString(),
                    @"(?<![\d.])\d+\.\d+\.\d+(?![\d.])",
                    RegexOptions.CultureInvariant);
                return match.Success ? "v" + match.Value : "";
            }
        }
        catch { return ""; }
    }

    private void UpdateAgentEnabledInSettings()
    {
        var enabled = _agentItems.Where(a => a.Enabled).Select(a => a.Id).ToList();
        SettingsService.SaveEnabledAgents(enabled);

        // 자동 업데이트는 대상 목록(옵트인)으로 저장한다. 목록에 없는 에이전트(UI 미노출 등)의 기존 설정은
        // 건드리지 않고 그대로 보존한다.
        var listed = _agentItems.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        SettingsService.SaveAutoUpdateAgentIds(
            SettingsService.LoadAutoUpdateAgentIds().Where(id => !listed.Contains(id))
                .Concat(_agentItems.Where(a => a.AutoUpdate).Select(a => a.Id)));

        AgentRegistry.InvalidateCache();

        // Claude Code 세션 유지기간(cleanupPeriodDays)은 설정 UI 에서 노출하지 않고,
        // App 시작 시 항상 '영구 보관'으로 강제한다(App.OnStartup). 여기서 덮어쓰지 않는다.
    }

    /// <summary>토글 변경 시 저장 (UI 토글은 즉시 반영되지만, 디스크 저장은 [저장] 버튼에서만 — 다른 설정과 동일).</summary>
    private void AgentItem_EnabledChanged(object? sender, System.Windows.RoutedPropertyChangedEventArgs<bool> e)
    {
        // [저장] 버튼을 눌러야 디스크에 기록되므로 여기선 _selectedEnabledAgents 만 갱신하면 됨.
        // (BuildAgentList 가 기준값을 잡았고, ApplySettings 가 enabled 목록을 디스크에 쓴다.)
    }

    /// <summary>에이전트 설치 명령을 클립보드에 복사.</summary>
    private void CopyInstallCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string cmd } || string.IsNullOrWhiteSpace(cmd)) return;
        try { Clipboard.SetText(cmd); }
        catch { /* 클립보드 잠김 등 무시 */ }
    }

    /// <summary>PATH 재스캔 후 해당 에이전트 설치 상태 배지·토글 활성 갱신.</summary>
    private async void RefreshAgentInstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } || string.IsNullOrWhiteSpace(id)) return;
        var item = _agentItems.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        var agent = AgentRegistry.Find(id);
        if (item == null || agent == null) return;

        AgentRegistry.InvalidateCache();
        bool installed = AgentRegistry.IsInstalled(agent);
        var muted = (Brush)FindResource("TextMutedBrush");
        var primary = (Brush)FindResource("PrimaryBrush");

        item.Installed = installed;
        item.InstalledLabel = installed ? "설치됨" : "미설치";
        item.InstalledBrush = installed ? primary : muted;
        var path = installed ? AgentRegistry.ResolvePath(agent) : null;
        item.LastUpdatedText = path != null
            ? File.GetLastWriteTime(path).ToString("yyyy-MM-dd")
            : "—";
        item.VersionText = installed ? "확인 중…" : "—";
        if (installed)
        {
            var version = await GetAgentVersionAsync(agent);
            item.VersionText = string.IsNullOrWhiteSpace(version) ? "확인 실패" : version;
        }
        // 미설치면 토글 강제 off (IsEnabled 가 false 이므로 켤 수 없음)
        if (!installed) item.Enabled = false;
        UpdateDevezVibeCodexRequirement();
    }

    private void UpdateDevezVibeCodexRequirement()
    {
        var devezVibe = _agentItems.FirstOrDefault(a => a.Id.Equals("devezvibe", StringComparison.OrdinalIgnoreCase));
        var codex = AgentRegistry.Find("codex");
        if (devezVibe == null || codex == null) return;

        devezVibe.RequirementNote = AgentRegistry.IsInstalled(codex)
            ? ""
            : "Codex가 설치되어 있지 않습니다. Devez Vibe를 사용하려면 Codex를 설치하세요.";
    }
}

/// <summary>설정 → 에이전트 패널의 한 줄 (이름·설치 상태·활성화 토글).</summary>
public sealed class AgentItem : INotifyPropertyChanged
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>설치 명령(복사 대상). 예: irm https://claude.ai/install.ps1 | iex.
    /// 배포 경로가 없는 에이전트(dvz)는 비어 있고, 그 줄은 통째로 숨긴다.</summary>
    public string InstallCommand { get; set; } = "";

    /// <summary>설치 명령 행 표시 여부. 빈 명령이면 "설치 명령:" 라벨만 남는 빈 줄이 되므로 접는다.</summary>
    public Visibility InstallCommandVisibility
        => string.IsNullOrWhiteSpace(InstallCommand) ? Visibility.Collapsed : Visibility.Visible;

    private bool _installed;
    public bool Installed
    {
        get => _installed;
        set
        {
            if (_installed == value) return;
            _installed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoUpdateTogglable));
        }
    }

    private string _installedLabel = "";
    public string InstalledLabel
    {
        get => _installedLabel;
        set { if (_installedLabel != value) { _installedLabel = value; OnPropertyChanged(); } }
    }

    private Brush _installedBrush = Brushes.Gray;
    public Brush InstalledBrush
    {
        get => _installedBrush;
        set { if (!ReferenceEquals(_installedBrush, value)) { _installedBrush = value; OnPropertyChanged(); } }
    }

    /// <summary>공개 전 에이전트에 붙는 안내 문구(빈 값이면 행을 숨긴다).</summary>
    public string PreviewNote { get; set; } = "";
    public Visibility PreviewNoteVisibility
        => string.IsNullOrEmpty(PreviewNote) ? Visibility.Collapsed : Visibility.Visible;

    private string _requirementNote = "";
    public string RequirementNote
    {
        get => _requirementNote;
        set
        {
            if (_requirementNote == value) return;
            _requirementNote = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RequirementNoteVisibility));
        }
    }
    public Visibility RequirementNoteVisibility
        => string.IsNullOrEmpty(RequirementNote) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>준비 중 에이전트의 켜기 잠금. 이미 켜진 채로 목록에 들어온 항목은 잠그지 않는다 —
    /// 한 번 연 사람이 껐다 켤 때마다 다시 열 번을 누르게 하지는 않는다.
    /// <see cref="BuildAgentList"/> 에서 <see cref="Enabled"/> 보다 먼저 대입해야 한다.</summary>
    public bool PreviewLocked { get; set; }

    /// <summary>잠금이 열리는 연속 클릭 수.</summary>
    private const int PreviewUnlockTaps = 10;
    private int _previewTaps;

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (value && PreviewLocked)
            {
                if (++_previewTaps < PreviewUnlockTaps)
                {
                    // 바인딩이 값을 쓰는 도중이라 지금 보내는 알림은 삼켜진다 — 다음 디스패치에 되돌린다.
                    Application.Current?.Dispatcher.BeginInvoke(
                        new Action(() => OnPropertyChanged(nameof(Enabled))));
                    return;
                }
                PreviewLocked = false;  // 열렸다 — 이후로는 평범한 토글
            }
            if (_enabled == value) return;
            _enabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoUpdateTogglable));
            // 사용 토글을 따라간다 — 켜면 자동 업데이트도 켜지고, 끄면 함께 꺼진다(끈 에이전트는 대상이 아니므로).
            // 켠 뒤 자동 업데이트만 따로 끄는 것은 그대로 가능하다.
            AutoUpdate = _enabled;
        }
    }

    /// <summary>갱신 명령이 있는 에이전트만 자동 업데이트 토글을 노출한다(antigravity 처럼 자체 갱신하는 CLI 는 제외).</summary>
    public bool SupportsAutoUpdate { get; set; }
    public Visibility AutoUpdateVisibility
        => SupportsAutoUpdate ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>자동 업데이트 대상 여부. 기본은 꺼짐이며, 사용 토글을 켤 때 함께 켜진다.</summary>
    private bool _autoUpdate;
    public bool AutoUpdate
    {
        get => _autoUpdate;
        set { if (_autoUpdate != value) { _autoUpdate = value; OnPropertyChanged(); } }
    }

    /// <summary>설치돼 있으면 사용 여부와 무관하게 조작 가능 — 쓰지 않는 에이전트도 최신으로만 유지할 수 있다.</summary>
    public bool AutoUpdateTogglable => Installed;

    private string _versionText = "—";
    public string VersionText
    {
        get => _versionText;
        set { if (_versionText != value) { _versionText = value; OnPropertyChanged(); } }
    }

    private string _lastUpdatedText = "—";
    public string LastUpdatedText
    {
        get => _lastUpdatedText;
        set { if (_lastUpdatedText != value) { _lastUpdatedText = value; OnPropertyChanged(); } }
    }

    /// <summary>Claude Code 항목에만 세션 유지기간 설정 노출.</summary>
    public bool IsClaudeCode { get; set; }

    /// <summary>유지기간 프리셋. ComboBox 바인딩용(DisplayMemberPath=Label, SelectedValuePath=Days).</summary>
    public RetentionOption[] RetentionOptions { get; } =
    {
        new(7,   "7일"),
        new(14,  "14일"),
        new(30,  "30일"),
        new(60,  "60일"),
        new(90,  "90일"),
        new(180, "180일"),
        new(365, "365일"),
        new(ClaudeGlobalSettings.PermanentDays, "영구 보관"),
    };

    private int _retentionDays = ClaudeGlobalSettings.DefaultCleanupPeriodDays;
    public int RetentionDays { get => _retentionDays; set { if (_retentionDays != value) { _retentionDays = value; OnPropertyChanged(); } } }

    /// <summary>테마 미리보기 전환 시 아이콘 재평가(흑/백 png 는 AgentImageConverter 가 현재 테마로 고르므로
    /// Id 바인딩을 다시 통보해야 그림이 바뀐다).</summary>
    public void RefreshAgentIcon() => OnPropertyChanged(nameof(Id));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>깨우기 예약 한 줄 (에이전트 1개 = 예약 1개). 실제 값은 <see cref="WakeScheduleEntry"/> 에 그대로 쓴다.</summary>
public sealed class WakeItem : INotifyPropertyChanged
{
    private static readonly string[] _timeOptions = BuildTimeOptions();

    public WakeItem(AgentDef agent, WakeScheduleEntry entry)
    {
        Agent = agent;
        Entry = entry;
    }

    public AgentDef Agent { get; }
    public WakeScheduleEntry Entry { get; }

    public string Id => Agent.Id;
    public string DisplayName => Agent.DisplayName;
    public IReadOnlyList<string> TimeOptions => _timeOptions;
    public string LastExecutionDisplay => Entry.LastExecutionDisplay;

    public string Time
    {
        get => Entry.Time;
        set
        {
            var next = value ?? "";
            if (Entry.Time == next) return;
            Entry.Time = next;
            OnPropertyChanged();
        }
    }

    public bool Enabled
    {
        get => Entry.Enabled;
        set
        {
            if (Entry.Enabled == value) return;
            Entry.Enabled = value;
            OnPropertyChanged();
        }
    }

    private static string[] BuildTimeOptions()
    {
        var times = new List<string>();
        for (int hour = 0; hour < 24; hour++)
            for (int minute = 0; minute < 60; minute += 30)
                times.Add($"{hour:D2}:{minute:D2}");
        return times.ToArray();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>세션 유지기간 프리셋 한 항목. ToString=Label (콤보 SelectionBox 가 DisplayMemberPath 대신 ToString 사용).</summary>
public sealed record RetentionOption(int Days, string Label)
{
    public override string ToString() => Label;
}

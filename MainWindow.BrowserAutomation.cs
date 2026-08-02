using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DevezCode.Models;
using DevezCode.Services;
using DevezCode.Views;

namespace DevezCode;

/// <summary>세션(에이전트) 전용 자동화 브라우저 탭의 생성·주차 담당.
/// 세션이 MCP 브라우저 도구를 호출할 때만 탭이 생기고, 화면에는 자동 전환하지 않는다(백그라운드 유지).</summary>
public partial class MainWindow
{

    /// <summary>roomId 세션에 연결된 브라우저 탭을 반환한다.
    /// 일반 브라우저 탭이 남아 있으면 임의로 고르지 않고, 에이전트가 사용자에게 선택을 물어보게 한다.</summary>
    public async Task<BrowserHostView> EnsureAutomationBrowserAsync(string roomId)
    {
        RequireSession(roomId);
        var assigned = FindAutomationBrowserTab(roomId);
        if (assigned != null) return await PrepareAutomationBrowserAsync(assigned);

        var choices = GetUnassignedBrowserTabs();
        if (choices.Count > 0)
            throw new InvalidOperationException(BuildSelectionRequiredMessage(choices));

        return await CreateAutomationBrowserAsync(roomId);
    }

    /// <summary>사용자가 직접 만든 미연결 브라우저 탭 목록. 자동화 탭·보관 프로젝트는 제외한다.</summary>
    public string ListAutomationBrowserTabs(string roomId)
    {
        RequireSession(roomId);
        var choices = GetUnassignedBrowserTabs();
        return choices.Count == 0
            ? "열린 일반 브라우저 탭이 없습니다. 새 전용 탭을 만들 수 있습니다."
            : FormatBrowserChoices(choices);
    }

    /// <summary>사용자가 명시적으로 고른 일반 브라우저 탭을 현재 세션의 자동화 탭으로 연결한다.</summary>
    public async Task<BrowserHostView> UseAutomationBrowserTabAsync(string roomId, string tabId)
    {
        RequireSession(roomId);
        if (string.IsNullOrWhiteSpace(tabId)) throw new ArgumentException("tabId가 필요합니다.");

        var tab = _projects.SelectMany(p => p.Tabs.OfType<BrowserTabItem>())
            .FirstOrDefault(t => string.Equals(t.Id, tabId, StringComparison.Ordinal));
        if (tab == null) throw new InvalidOperationException("선택한 브라우저 탭을 찾을 수 없습니다. browser_tabs로 목록을 다시 확인하세요.");
        var assigned = FindAutomationBrowserTab(roomId);
        if (assigned != null && !ReferenceEquals(assigned, tab))
            throw new InvalidOperationException("이 세션에는 이미 연결된 브라우저 탭이 있습니다. 해당 탭을 계속 사용하세요.");
        if (tab.AutomationRoomId is { Length: > 0 } owner && owner != roomId)
            throw new InvalidOperationException("선택한 브라우저 탭은 다른 세션이 사용 중입니다. 다른 탭을 선택하거나 새 전용 탭을 만드세요.");

        if (tab.AutomationRoomId != roomId)
        {
            tab.AutomationRoomId = roomId;
            WorkspaceStore.Save(_projects);
            DiagLog.Write($"UseAutomationBrowserTab bind tab={tab.Id} room={roomId}");
        }
        return await PrepareAutomationBrowserAsync(tab);
    }

    /// <summary>사용자가 새 탭 생성을 선택했을 때만 세션 전용 브라우저 탭을 만든다.</summary>
    public async Task<BrowserHostView> CreateAutomationBrowserAsync(string roomId)
    {
        var (proj, session) = RequireSession(roomId);
        var assigned = FindAutomationBrowserTab(roomId);
        if (assigned != null) return await PrepareAutomationBrowserAsync(assigned);

        var tab = new BrowserTabItem { Name = session.Name + " 브라우저", AutomationRoomId = roomId };
        // 세션 서브트리 인접성(부모/자식 세션 순서)을 깨지 않도록 항상 맨 끝에 붙인다.
        proj.Tabs.Add(tab);
        proj.IsExpanded = true;
        PlaceNewAutomationBrowserOppositeSession(proj, session, tab);
        WorkspaceStore.Save(_projects);
        DiagLog.Write($"CreateAutomationBrowser tab={tab.Id} room={roomId} project={proj.Name}");
        return await PrepareAutomationBrowserAsync(tab);
    }

    /// <summary>새 세션 전용 브라우저는 대화와 나란히 보이도록 반대쪽 패널에 배치한다.
    /// 단일 패널이면 우측 분할을 만들고, 이미 분할돼 있으면 현재 세션의 반대 패널을 사용한다.</summary>
    private void PlaceNewAutomationBrowserOppositeSession(ProjectItem project, SessionItem session, BrowserTabItem tab)
    {
        var source = _panes.FirstOrDefault(p => ReferenceEquals(p.ActiveSession, session))
            ?? _panes.FirstOrDefault(p => p.ShowsTab(session))
            ?? _focusedPane;

        if (!_splitActive)
        {
            // 단일 화면의 대화를 좌측에 유지하고, 새 브라우저만 우측에 격리한다.
            source.CoverForTransition();
            if (ReferenceEquals(source.ActiveProject, project)) source.HideTabInPane(tab);
            EnableSplit(animate: false, persist: false);
            PaneB.OpenBrowserTab(tab);
            PaneB.IsolateTab(tab);
            project.SplitEnabled = true;
            _focusedPane = PaneB;
            source.RevealAfterTransition(kick: true);
        }
        else
        {
            var target = ReferenceEquals(source, LeftPane) ? RightPane : LeftPane;
            if (ReferenceEquals(source.ActiveProject, project)) source.HideTabInPane(tab);

            bool sameProject = ReferenceEquals(target.ActiveProject, project);
            if (sameProject) target.UnhideTabInPane(tab);
            target.OpenBrowserTab(tab);
            if (!sameProject) target.IsolateTab(tab);
            _focusedPane = target;
        }

        SyncShellToFocusedPane();
        UpdatePaneFocusVisual();
        PersistSplitState();
    }

    private async Task<BrowserHostView> PrepareAutomationBrowserAsync(BrowserTabItem tab)
    {
        tab.Browser.StateKey = tab.PersistenceKey;
        if (!tab.IsActive) ParkAutomationBrowser(tab.Browser);
        await tab.Browser.EnsureStartedAsync();
        return tab.Browser;
    }

    private (ProjectItem Project, SessionItem Session) RequireSession(string roomId)
    {
        if (!IsLoaded)
            throw new InvalidOperationException("앱이 아직 초기화 중입니다. 잠시 후 다시 시도하세요.");
        return FindSessionByRoomId(roomId)
            ?? throw new InvalidOperationException($"세션을 찾을 수 없습니다(roomId={roomId}).");
    }

    private List<(ProjectItem Project, BrowserTabItem Tab)> GetUnassignedBrowserTabs()
        => _projects.SelectMany(project => project.Tabs.OfType<BrowserTabItem>()
                .Where(tab => string.IsNullOrEmpty(tab.AutomationRoomId))
                .Select(tab => (project, tab)))
            .ToList();

    private BrowserTabItem? FindAutomationBrowserTab(string roomId)
        => _projects.Concat(_archivedProjects)
            .SelectMany(project => project.Tabs.OfType<BrowserTabItem>())
            .FirstOrDefault(tab => string.Equals(tab.AutomationRoomId, roomId, StringComparison.Ordinal));

    /// <summary>제거되는 세션에 연결된 브라우저를 일반 탭으로 되돌린다.
    /// 사용자가 다른 프로젝트의 탭을 선택했을 수도 있으므로 전체 프로젝트를 확인한다.</summary>
    private void UnbindAutomationBrowsers(IEnumerable<string> roomIds)
    {
        var removed = roomIds.ToHashSet(StringComparer.Ordinal);
        if (removed.Count == 0) return;
        foreach (var browser in _projects.Concat(_archivedProjects)
                     .SelectMany(project => project.Tabs.OfType<BrowserTabItem>()))
            if (browser.AutomationRoomId != null && removed.Contains(browser.AutomationRoomId))
                browser.AutomationRoomId = null;
    }

    private static string BuildSelectionRequiredMessage(IReadOnlyList<(ProjectItem Project, BrowserTabItem Tab)> choices)
        => "브라우저 탭 선택이 필요합니다. 아래 열린 탭을 사용할지 새 전용 탭을 만들지 사용자에게 물어보세요. " +
           "사용자가 기존 탭을 고르면 browser_use_tab에 해당 tabId를, 새 탭을 고르면 browser_new_tab을 호출하세요.\n\n" +
           FormatBrowserChoices(choices);

    private static string FormatBrowserChoices(IReadOnlyList<(ProjectItem Project, BrowserTabItem Tab)> choices)
        => string.Join("\n", choices.Select((choice, index) =>
        {
            var url = choice.Tab.Browser.GetCurrentUrl(choice.Tab.PersistenceKey) ?? "방문 기록 없음";
            return $"{index + 1}. {choice.Project.Name} / {choice.Tab.Name}\n   tabId: {choice.Tab.Id}\n   주소: {url}";
        }));

    /// <summary>브라우저 뷰를 화면 밖 주차장으로 옮긴다(WebView2 는 살아 있고 정상 크기로 렌더).</summary>
    public void ParkAutomationBrowser(BrowserHostView browser)
    {
        if (AutomationBrowserPark == null) return;
        if (ReferenceEquals(browser.Parent, AutomationBrowserPark)) return;
        DetachFromParent(browser);
        AutomationBrowserPark.Children.Add(browser);
        browser.ResumeContent();   // 파킹 중에도 실제 WebView2 가 떠 있어야 스크립트/캡처가 동작
    }

    /// <summary>주차장에서 제거(탭 닫힘 시).</summary>
    public void UnparkAutomationBrowser(BrowserHostView browser)
    {
        if (AutomationBrowserPark != null && ReferenceEquals(browser.Parent, AutomationBrowserPark))
            AutomationBrowserPark.Children.Remove(browser);
    }

    /// <summary>ContentControl / Panel 어느 쪽에 붙어 있어도 부모에서 떼어낸다(같은 창 안 재부모화).</summary>
    public static void DetachFromParent(BrowserHostView browser)
    {
        switch (browser.Parent)
        {
            case ContentControl host when ReferenceEquals(host.Content, browser):
                host.Content = null;
                break;
            case Panel panel:
                panel.Children.Remove(browser);
                break;
        }
    }

    private (ProjectItem Project, SessionItem Session)? FindSessionByRoomId(string roomId)
    {
        foreach (var proj in _projects.Concat(_archivedProjects))
            foreach (var s in proj.Tabs.OfType<SessionItem>())
                if (s.Id == roomId) return (proj, s);
        return null;
    }
}

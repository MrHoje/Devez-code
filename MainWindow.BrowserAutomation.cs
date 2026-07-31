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
    public static MainWindow? Current => Application.Current?.MainWindow as MainWindow;

    /// <summary>roomId 세션의 전용 브라우저 탭을 찾거나 만들고, WebView2 초기화까지 기다려 반환한다.
    /// 탭은 활성화하지 않는다 — 주차장(AutomationBrowserPark)에서 화면 밖으로 렌더된다.</summary>
    public async Task<BrowserHostView> EnsureAutomationBrowserAsync(string roomId)
    {
        if (!IsLoaded)
            throw new InvalidOperationException("앱이 아직 초기화 중입니다. 잠시 후 다시 시도하세요.");

        var found = FindSessionByRoomId(roomId)
            ?? throw new InvalidOperationException($"세션을 찾을 수 없습니다(roomId={roomId}).");
        var (proj, session) = found;

        var tab = proj.Tabs.OfType<BrowserTabItem>().FirstOrDefault(t => t.AutomationRoomId == roomId);
        if (tab == null)
        {
            tab = new BrowserTabItem { Name = session.Name + " 브라우저", AutomationRoomId = roomId };
            // 세션 서브트리 인접성(부모/자식 세션 순서)을 깨지 않도록 항상 맨 끝에 붙인다.
            proj.Tabs.Add(tab);
            proj.IsExpanded = true;
            WorkspaceStore.Save(_projects);
            DiagLog.Write($"EnsureAutomationBrowser create tab id={tab.Id} room={roomId} project={proj.Name}");
        }

        tab.Browser.StateKey = tab.PersistenceKey;
        if (!tab.IsActive) ParkAutomationBrowser(tab.Browser);
        await tab.Browser.EnsureStartedAsync();
        return tab.Browser;
    }

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

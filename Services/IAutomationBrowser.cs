namespace DevezCode.Services;

/// <summary>세션(에이전트)이 MCP 브라우저 도구로 제어할 수 있는 대상.
/// 프로젝트 탭 브라우저(BrowserHostView)와 미니 브라우저 창이 모두 구현한다.</summary>
public interface IAutomationBrowser
{
    Task<string> AutomationNavigateAsync(string urlOrQuery, int timeoutMs = 30000);
    Task<string> AutomationCurrentUrlAsync();
    Task<string> AutomationTitleAsync();
    Task<string> AutomationReadTextAsync(int maxChars = 20000);
    Task<string> AutomationLinksAsync(int max = 50);
    Task<string> AutomationClickAsync(string selectorOrText, int timeoutMs = 3000);
    Task<string> AutomationWaitForSelectorAsync(string selector, int timeoutMs = 10000);
    Task<string> AutomationPressKeyAsync(string key, bool ctrl = false, bool shift = false, bool alt = false);
    Task<string> AutomationFillAsync(string selector, string value, bool submit);
    Task<string> AutomationWaitForTextAsync(string text, int timeoutMs = 15000);
    Task<string> AutomationEvalAsync(string script);
    Task<byte[]> AutomationCaptureAsync();
    Task<string> AutomationBackAsync();
    Task<string> AutomationReloadAsync();
}

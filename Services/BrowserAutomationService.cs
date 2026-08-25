using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Windows;
using DevezCode.Views;

namespace DevezCode.Services;

/// <summary>세션(에이전트)이 보낸 브라우저 명령을 실제 내장 브라우저 탭에 적용한다.
/// 방(roomId)당 전용 탭 1개 + 방당 명령 직렬화(동시 호출이 서로 섞이지 않게).</summary>
public static class BrowserAutomationService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RoomLocks = new();

    /// <summary>명령 1건 처리. 성공하면 문자열 결과, 실패하면 예외(브리지가 에이전트에 사유 전달).</summary>
    public static async Task<string> ExecuteAsync(string roomId, string command, JsonObject args)
    {
        if (string.IsNullOrWhiteSpace(roomId))
            throw new InvalidOperationException(
                "세션을 식별할 수 없습니다. DevezCode 세션 안에서 실행해야 합니다(DEVEZCODE_ROOM_ID 없음).");

        var gate = RoomLocks.GetOrAdd(roomId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            // Application.MainWindow도 UI 스레드 소유 객체다. 백그라운드 파이프 스레드에서
            // MainWindow.Current를 먼저 읽으면 실제 명령을 디스패치하기 전에 접근 예외가 난다.
            var window = await OnUiAsync(() => Task.FromResult(
                MainWindow.Current
                ?? throw new InvalidOperationException("DevezCode 메인 창을 찾을 수 없습니다.")));

            switch (command)
            {
                case "tabs":
                    return await OnUiAsync(() => Task.FromResult(window.ListAutomationBrowserTabs(roomId)));
                case "use_tab":
                {
                    var tabId = RequireString(args, "tabId");
                    await OnUiAsync(() => window.UseAutomationBrowserTabAsync(roomId, tabId));
                    return "선택한 브라우저 탭을 이 세션에 연결했습니다. 이전 작업을 계속하려면 browser_open 등을 다시 호출하세요.";
                }
                case "new_tab":
                    await OnUiAsync(() => window.CreateAutomationBrowserAsync(roomId));
                    return "새 전용 브라우저 탭을 만들었습니다. 이전 작업을 계속하려면 browser_open 등을 다시 호출하세요.";
                case "use_mini":
                    await OnUiAsync(() => window.UseMiniBrowserAsync(roomId));
                    return "미니 브라우저 창을 이 세션에 연결했습니다. 이후 브라우저 도구는 모두 미니 창에 적용됩니다.";
            }

            var browser = await OnUiAsync(() => window.EnsureAutomationBrowserAsync(roomId));

            return await OnUiAsync(() => RunAsync(browser, command, args));
        }
        finally { gate.Release(); }
    }

    private static Task<T> OnUiAsync<T>(Func<Task<T>> work)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("앱이 실행 중이 아닙니다.");
        return dispatcher.CheckAccess() ? work() : dispatcher.InvokeAsync(work).Task.Unwrap();
    }

    private static string RequireString(JsonObject args, string key)
        => args[key]?.GetValue<string>()
           ?? throw new ArgumentException($"'{key}' 인자가 필요합니다.");

    private static Task<string> RunAsync(IAutomationBrowser b, string command, JsonObject args)
    {
        string Str(string key, string? fallback = null)
            => args[key]?.GetValue<string>() ?? fallback ?? throw new ArgumentException($"'{key}' 인자가 필요합니다.");
        int Num(string key, int fallback)
            => args[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : fallback;
        bool Flag(string key)
            => args[key] is JsonValue v && v.TryGetValue<bool>(out var f) && f;

        return command switch
        {
            // 검색어를 그대로 넘기면 BrowserHostView 가 URL/검색어를 구분해 처리한다(주소창과 동일 규칙).
            "open" => b.AutomationNavigateAsync(Str("url"), Num("timeoutMs", 30000)),
            "current" => CurrentAsync(b),
            "read" => b.AutomationReadTextAsync(Num("maxChars", 20000)),
            "links" => b.AutomationLinksAsync(Num("max", 50)),
            "click" => b.AutomationClickAsync(Str("target"), Num("timeoutMs", 3000)),
            "fill" => b.AutomationFillAsync(Str("selector"), Str("value", ""), Flag("submit")),
            "press" => b.AutomationPressKeyAsync(Str("key"), Flag("ctrl"), Flag("shift"), Flag("alt")),
            "wait" => b.AutomationWaitForTextAsync(Str("text"), Num("timeoutMs", 15000)),
            "wait_selector" => b.AutomationWaitForSelectorAsync(Str("selector"), Num("timeoutMs", 10000)),
            "eval" => b.AutomationEvalAsync(Str("script")),
            "back" => b.AutomationBackAsync(),
            "reload" => b.AutomationReloadAsync(),
            "screenshot" => ScreenshotAsync(b),
            _ => throw new ArgumentException($"알 수 없는 명령: {command}"),
        };
    }

    private static async Task<string> CurrentAsync(IAutomationBrowser b)
        => $"{await b.AutomationTitleAsync()}\n{await b.AutomationCurrentUrlAsync()}";

    private static async Task<string> ScreenshotAsync(IAutomationBrowser b)
        => Convert.ToBase64String(await b.AutomationCaptureAsync());
}

using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;

namespace DevezCode.Services;

/// <summary>WebView2 하나를 대상으로 세션 자동화 명령을 실행하는 공용 엔진.
/// <para>탭 브라우저와 미니 브라우저가 같은 구현을 공유하도록 CoreWebView2 만 있으면 되는 동작을 모아 둔다.
/// 히스토리·주소창처럼 호스트마다 다른 동작(뒤로 가기 등)은 각 호스트가 직접 구현한다.</para>
/// <para>화면에 띄우지 않은(파킹된) 대상에서도 동작해야 하므로 매번 코어 초기화를 기다린다.
/// 실패는 예외로 올려 브리지가 에이전트에게 사유를 그대로 전달한다.</para></summary>
public sealed class BrowserAutomationEngine
{
    private readonly Func<Task<CoreWebView2?>> _coreProvider;

    public BrowserAutomationEngine(Func<Task<CoreWebView2?>> coreProvider) => _coreProvider = coreProvider;

    public async Task<CoreWebView2> RequireCoreAsync()
        => await _coreProvider() ?? throw new InvalidOperationException(
            "WebView2 를 시작할 수 없습니다(런타임 미설치 가능).");

    /// <summary>입력이 URL이면 그대로 이동, 아니면 구글 검색(주소창과 동일 규칙).</summary>
    public static string ToNavigationTarget(string input)
    {
        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return input;

        bool looksLikeDomain = !input.Contains(' ')
            && input.Contains('.')
            && Uri.TryCreate("https://" + input, UriKind.Absolute, out var u)
            && u.Host.Contains('.');
        if (looksLikeDomain) return "https://" + input;

        return "https://www.google.com/search?q=" + Uri.EscapeDataString(input);
    }

    /// <summary>탐색을 실행하고 <b>그 탐색</b>의 완료만 기다린다.
    /// <para>NavigationId 로 매칭하지 않으면 초기화 중 시작된 홈 URL 탐색의 완료 이벤트가 대기를 먼저
    /// 깨워, 로드가 끝나기 전에 about:blank 를 돌려주게 된다(실측 버그).</para></summary>
    public static async Task NavigateAndWaitAsync(CoreWebView2 core, Action navigate, int timeoutMs)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navId = null;
        void OnStarting(object? _, CoreWebView2NavigationStartingEventArgs e) => navId ??= e.NavigationId;
        void OnCompleted(object? _, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (navId != null && e.NavigationId == navId) tcs.TrySetResult(e.IsSuccess);
        }

        core.NavigationStarting += OnStarting;
        core.NavigationCompleted += OnCompleted;
        try
        {
            navigate();
            await WaitOrTimeoutAsync(tcs.Task, timeoutMs, "페이지 로드");
        }
        finally
        {
            core.NavigationStarting -= OnStarting;
            core.NavigationCompleted -= OnCompleted;
        }
    }

    /// <summary>URL(또는 검색어)로 이동하고 탐색 완료까지 대기. 반환값=최종 URL.
    /// <para><paramref name="navigate"/> 로 호스트별 탐색 방식(가상 히스토리 기록 등)을 넘길 수 있다.</para></summary>
    public async Task<string> NavigateAsync(string urlOrQuery, int timeoutMs = 30000,
        Action<CoreWebView2, string>? navigate = null)
    {
        var core = await RequireCoreAsync();
        var target = ToNavigationTarget(urlOrQuery.Trim());
        await NavigateAndWaitAsync(core, () =>
        {
            if (navigate != null) navigate(core, target);
            else core.Navigate(target);
        }, timeoutMs);
        return core.Source;
    }

    public async Task<string> CurrentUrlAsync() => (await RequireCoreAsync()).Source;

    public async Task<string> TitleAsync() => (await RequireCoreAsync()).DocumentTitle;

    /// <summary>본문 텍스트 추출. maxChars 초과분은 잘라낸다.</summary>
    public async Task<string> ReadTextAsync(int maxChars = 20000)
    {
        var text = await EvalAsync("""
            (() => {
              const t = (document.body ? document.body.innerText : '') || '';
              return t.replace(/\n{3,}/g, '\n\n').trim();
            })()
            """);
        return maxChars > 0 && text.Length > maxChars ? text[..maxChars] + "\n…(잘림)" : text;
    }

    /// <summary>페이지의 링크 목록을 "텍스트 | URL" 줄로 반환(중복/빈 텍스트 제외).</summary>
    public async Task<string> LinksAsync(int max = 50)
    {
        var js = $$"""
            (() => {
              const seen = new Set(); const out = [];
              for (const a of document.querySelectorAll('a[href]')) {
                const href = a.href; const text = (a.innerText || a.textContent || '').trim().replace(/\s+/g, ' ');
                if (!href || !text || href.startsWith('javascript:')) continue;
                if (seen.has(href)) continue;
                seen.add(href); out.push(text + ' | ' + href);
                if (out.length >= {{Math.Max(1, max)}}) break;
              }
              return out.join('\n');
            })()
            """;
        return await EvalAsync(js);
    }

    /// <summary>CSS 선택자 또는 화면에 보이는 텍스트로 클릭. 대상이 아직 없으면 timeoutMs 까지 폴링한다.
    /// <para>React 같은 프레임워크는 입력 반영 뒤 다음 렌더에서야 전송 버튼을 그리므로, 즉시 조회하면
    /// 대상을 못 찾는다(실측). 그래서 클릭은 '한 번 찾고 실패'가 아니라 짧게 기다린다.</para></summary>
    public async Task<string> ClickAsync(string selectorOrText, int timeoutMs = 3000)
    {
        var arg = JsonSerializer.Serialize(selectorOrText);
        var js = $$"""
            (() => {
              const q = {{arg}};
              let el = null;
              try { el = document.querySelector(q); } catch (_) {}
              if (!el) {
                const cands = document.querySelectorAll('a,button,[role=button],input[type=submit],input[type=button],summary');
                const norm = s => (s || '').trim().replace(/\s+/g, ' ');
                el = [...cands].find(c => norm(c.innerText || c.value) === norm(q))
                  || [...cands].find(c => norm(c.innerText || c.value).includes(norm(q)));
              }
              if (!el) return 'NOTFOUND';
              el.scrollIntoView({ block: 'center' });
              el.click();
              return 'OK';
            })()
            """;
        var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        while (true)
        {
            if (await EvalAsync(js) != "NOTFOUND") return "clicked";
            if (Environment.TickCount64 >= deadline)
                throw new InvalidOperationException(
                    $"클릭 대상을 찾지 못했습니다: {selectorOrText} " +
                    "(아직 렌더 전이면 browser_wait_selector 로 먼저 기다리세요)");
            await Task.Delay(200);
        }
    }

    /// <summary>CSS 선택자가 나타날 때까지 대기. 입력 후 버튼이 생기길 기다리는 용도.</summary>
    public async Task<string> WaitForSelectorAsync(string selector, int timeoutMs = 10000)
    {
        var js = $"document.querySelector({JsonSerializer.Serialize(selector)}) ? '1' : '0'";
        var deadline = Environment.TickCount64 + Math.Max(500, timeoutMs);
        while (Environment.TickCount64 < deadline)
        {
            if (await EvalAsync(js) == "1") return "found";
            await Task.Delay(200);
        }
        throw new TimeoutException($"'{selector}' 가 {timeoutMs}ms 안에 나타나지 않았습니다.");
    }

    /// <summary>키 입력을 CDP(Input.dispatchKeyEvent)로 보낸다.
    /// <para>JS 로 만든 KeyboardEvent 는 untrusted 라 프레임워크/에디터가 무시하는 경우가 있다.
    /// CDP 는 브라우저 입력 파이프라인을 그대로 타므로 React 채팅창 Enter 전송 등에서 안정적이다.</para></summary>
    public async Task<string> PressKeyAsync(string key, bool ctrl = false, bool shift = false, bool alt = false)
    {
        var core = await RequireCoreAsync();
        var (code, vk, text) = ResolveKey(key);
        int modifiers = (alt ? 1 : 0) | (ctrl ? 2 : 0) | (shift ? 8 : 0);

        var payload = new JsonObject
        {
            ["key"] = key,
            ["code"] = code,
            ["windowsVirtualKeyCode"] = vk,
            ["nativeVirtualKeyCode"] = vk,
            ["modifiers"] = modifiers,
        };
        // text 가 있는 키(문자·Enter)는 keyDown 에 실어야 실제 입력으로 처리된다.
        if (text != null && modifiers is 0 or 8) payload["text"] = text;

        async Task Dispatch(string type)
        {
            var p = (JsonObject)payload.DeepClone();
            p["type"] = type;
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", p.ToJsonString());
        }

        await Dispatch("keyDown");
        await Dispatch("keyUp");
        return "pressed " + key;
    }

    /// <summary>키 이름 → (code, Windows 가상키코드, 입력 텍스트). 모르는 키는 문자 1글자로 취급.</summary>
    private static (string Code, int Vk, string? Text) ResolveKey(string key) => key switch
    {
        "Enter" => ("Enter", 13, "\r"),
        "Tab" => ("Tab", 9, "\t"),
        "Escape" => ("Escape", 27, null),
        "Backspace" => ("Backspace", 8, null),
        "Delete" => ("Delete", 46, null),
        "ArrowUp" => ("ArrowUp", 38, null),
        "ArrowDown" => ("ArrowDown", 40, null),
        "ArrowLeft" => ("ArrowLeft", 37, null),
        "ArrowRight" => ("ArrowRight", 39, null),
        "Home" => ("Home", 36, null),
        "End" => ("End", 35, null),
        "PageUp" => ("PageUp", 33, null),
        "PageDown" => ("PageDown", 34, null),
        " " => ("Space", 32, " "),
        _ when key.Length == 1 => (
            char.IsLetter(key[0]) ? "Key" + char.ToUpperInvariant(key[0])
            : char.IsDigit(key[0]) ? "Digit" + key
            : "",
            char.ToUpperInvariant(key[0]),
            key),
        _ => throw new ArgumentException($"지원하지 않는 키: {key}"),
    };

    /// <summary>입력 요소에 값을 넣는다.
    /// <para>값 주입은 CDP <c>Input.insertText</c>(신뢰된 입력)로 한다 — React 처럼 value 를 제어하는
    /// 프레임워크는 JS 로 <c>el.value = ...</c> 만 하면 다음 렌더에서 되돌리므로 그대로는 안 먹힌다.
    /// insertText 가 통하지 않는 요소(구형 위젯 등)만 JS 대입으로 폴백한다.</para>
    /// <para>submit=true 는 폼 submit 대신 Enter 키(CDP)를 보낸다. 채팅 입력창처럼 폼이 없는 UI 가 많다.</para></summary>
    public async Task<string> FillAsync(string selector, string value, bool submit)
    {
        var core = await RequireCoreAsync();
        var sel = JsonSerializer.Serialize(selector);

        // 포커스 + 기존 내용 전체 선택 → insertText 가 덮어쓰도록.
        var focused = await EvalAsync($$"""
            (() => {
              const el = document.querySelector({{sel}});
              if (!el) return 'NOTFOUND';
              el.scrollIntoView({ block: 'center' });
              el.focus();
              if (el.select) el.select();
              else if (el.isContentEditable) document.getSelection().selectAllChildren(el);
              return 'OK';
            })()
            """);
        if (focused == "NOTFOUND") throw new InvalidOperationException($"입력 대상을 찾지 못했습니다: {selector}");

        await core.CallDevToolsProtocolMethodAsync("Input.insertText",
            new JsonObject { ["text"] = value }.ToJsonString());

        // insertText 가 반영 안 된 경우(비표준 위젯)만 JS 대입 + 이벤트 발생으로 폴백.
        var current = await EvalAsync($$"""
            (() => {
              const el = document.querySelector({{sel}});
              if (!el) return '';
              return ('value' in el ? el.value : el.textContent) || '';
            })()
            """);
        if (current != value)
        {
            await EvalAsync($$"""
                (() => {
                  const el = document.querySelector({{sel}});
                  if (!el) return 'NOTFOUND';
                  const v = {{JsonSerializer.Serialize(value)}};
                  if ('value' in el) el.value = v; else el.textContent = v;
                  el.dispatchEvent(new Event('input', { bubbles: true }));
                  el.dispatchEvent(new Event('change', { bubbles: true }));
                  return 'OK';
                })()
                """);
        }

        if (submit) await PressKeyAsync("Enter");
        return "filled";
    }

    /// <summary>지정 텍스트가 본문에 나타날 때까지 폴링 대기(SPA/지연 로딩 대응).</summary>
    public async Task<string> WaitForTextAsync(string text, int timeoutMs = 15000)
    {
        var js = $"(document.body ? document.body.innerText : '').includes({JsonSerializer.Serialize(text)}) ? '1' : '0'";
        var deadline = Environment.TickCount64 + Math.Max(500, timeoutMs);
        while (Environment.TickCount64 < deadline)
        {
            if (await EvalAsync(js) == "1") return "found";
            await Task.Delay(300);
        }
        throw new TimeoutException($"'{text}' 가 {timeoutMs}ms 안에 나타나지 않았습니다.");
    }

    /// <summary>임의 JS 실행. 결과는 문자열로 정규화(객체는 JSON 문자열).</summary>
    public async Task<string> EvalAsync(string script)
    {
        var core = await RequireCoreAsync();
        var raw = await core.ExecuteScriptAsync(script);
        if (string.IsNullOrEmpty(raw) || raw == "null") return "";
        try
        {
            var node = JsonNode.Parse(raw);
            return node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node?.ToJsonString() ?? "";
        }
        catch { return raw; }
    }

    /// <summary>현재 화면 PNG 캡처(base64 로 브리지가 전달).</summary>
    public async Task<byte[]> CaptureAsync()
    {
        var core = await RequireCoreAsync();
        using var ms = new MemoryStream();
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
        return ms.ToArray();
    }

    /// <summary>WebView2 자체 히스토리로 뒤로 가기(가상 히스토리를 쓰지 않는 호스트용).</summary>
    public async Task<string> GoBackAsync()
    {
        var core = await RequireCoreAsync();
        if (!core.CanGoBack) throw new InvalidOperationException("뒤로 갈 기록이 없습니다.");
        await NavigateAndWaitAsync(core, core.GoBack, 30000);
        return core.Source;
    }

    public async Task<string> ReloadAsync()
    {
        var core = await RequireCoreAsync();
        await NavigateAndWaitAsync(core, core.Reload, 30000);
        return core.Source;
    }

    public static async Task WaitOrTimeoutAsync(Task task, int timeoutMs, string what)
    {
        var done = await Task.WhenAny(task, Task.Delay(Math.Max(1000, timeoutMs)));
        if (done != task) throw new TimeoutException($"{what} 가 {timeoutMs}ms 안에 끝나지 않았습니다.");
        await task;
    }
}

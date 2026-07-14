# VS Code형 Git SCM + Monaco diff Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 우측에 VS Code형 Git SCM 패널(staged/unstaged·commit·pull·push·fetch·discard)을 만들고, 파일 클릭 시 중앙에 Monaco DiffEditor diff 탭을 연다.

**Architecture:** 기존 `GitService`(git CLI 래퍼)를 확장하고, 우측 FileExplorer의 'DIFF' 슬롯을 새 `GitScmView`로 교체한다. diff는 전용 탭 타입을 만들지 않고 **기존 `FileTabItem` + `IFileTabEditor` 구현체 `MonacoDiffHostView`**(WebView2, `MarkdownFileEditorView` 패턴)로 렌더한다. 테마는 `App.ThemeChanged`로 Monaco에 재적용.

**Tech Stack:** C#/.NET WPF, WebView2(Microsoft.Web.WebView2, 이미 사용 중), Monaco Editor(오프라인 번들), git CLI.

## Global Constraints

- **빌드하지 않는다(사용자 지시).** 검증은 `scripts/ui-lint.ps1` 정적 스캔 + 코드 리뷰까지. 앱 빌드·실행 확인은 사용자가 직접.
- 모든 색상은 `AppStyles.xaml`의 `DynamicResource` 테마 브러시로만. 하드코딩 `#RRGGBB` 금지. 예외: diff 상태색 `#3FB950`(add)·`#F85149`(del)·`#D29922`(mod).
- 공통 스타일 재사용: 입력=`TaskQueueView` 카드 패턴, 버튼=`PrimaryButton`/`SecondaryButton`/`IconButton`, 확인=`ConfirmDialog.Show(title,msg,okLabel)`→bool·`ConfirmDialog.Alert(title,msg)`, 토스트=`new NotificationPopup(title,content).Show()`, 폰트=`PretendardFont`·`Fs11`~`Fs13`.
- 서버/DB 없이 로컬 git CLI만. `GitService.RunAsync`는 `UseShellExecute=false`라 인자 이스케이프 불필요.
- WebView2 호스팅은 `Views/MarkdownWysiwygHost.cs`의 공유 `CoreWebView2Environment` + `SetVirtualHostNameToFolderMapping` 패턴을 따른다.
- 각 태스크 끝에 커밋. push 금지(사용자가 직접).

## 병렬 실행 순서 (ulw)

- **그룹 1 (병렬):** Task 1(GitService/모델), Task 2(Monaco 웹 자산+csproj), Task 3(MonacoHost)
- **그룹 2 (병렬, 그룹1 후):** Task 4(MonacoDiffHostView ← 1,3), Task 5(GitScmView ← 1)
- **직렬:** Task 6(탭/모델/스토어 ← 없음, 단독 파일) — 그룹1과도 병렬 가능
- **직렬:** Task 7(배선 ← 4,5,6) → Task 8(ui-lint 실행 ← 5,7)

## File Structure

- `Services/GitService.cs` (수정) — status/show/stage/unstage/discard/commit/pull/push/fetch/branchstate/identity.
- `Models/GitModels.cs` (수정) — `GitChange`에 `IsStaged`, 신규 `GitStatus`·`BranchState`.
- `Resources/Monaco/web/{diff.html,bridge.js,vs/**}` (신규) — 오프라인 Monaco.
- `DevezCode.csproj` (수정) — `Resources\Monaco\web\**` Content 포함.
- `Views/MonacoHost.cs` (신규) — WebView2 Monaco 호스트(`MarkdownWysiwygHost` 복제).
- `Views/MonacoDiffHostView.xaml(.cs)` (신규) — `IFileTabEditor` diff 에디터.
- `Views/GitScmView.xaml(.cs)` (신규) — SCM 패널.
- `Models/WorkspaceModels.cs` (수정) — `FileTabItem.IsDiff` + Title.
- `Views/WorkspacePaneView.xaml.cs` (수정) — `CreateDiffTab`/`OpenDiffTab`, `RefreshBranchIfRepo`.
- `Services/WorkspaceStore.cs` (수정) — diff 탭 영속화 제외.
- `Views/FileExplorerView.xaml(.cs)` (수정) — DIFF 슬롯 `GitDiffView`→`GitScmView`, 이벤트 포워딩.
- `MainWindow.xaml.cs` (수정) — SCM 파일 클릭→포커스 패널 diff 탭, 브랜치 버블 갱신.
- `Views/GitDiffView.xaml(.cs)` (삭제) — 네이티브 side-by-side 뷰어(대체됨).
- `scripts/ui-lint.ps1` (신규) — UI 정적 스캔.

---

### Task 1: GitService + 모델 확장

**Files:**
- Modify: `Services/GitService.cs`
- Modify: `Models/GitModels.cs`

**Interfaces (Produces):**
- `Models`: `GitChange { string Status; string Path; bool IsUntracked; bool IsStaged; string StatusColor }`, `GitStatus { List<GitChange> Staged; List<GitChange> Unstaged }`, `BranchState { string? Branch; bool HasUpstream; int Ahead; int Behind }`.
- `GitService`: `StatusAsync(repo)→GitStatus`, `ShowFileAsync(repo,rev,path)→string`, `StageAsync/UnstageAsync/DiscardAsync(repo,path[,untracked])→GitResult`, `CommitAsync(repo,msg)→GitResult`, `PullAsync/PushAsync/FetchAsync(repo)→GitResult`, `HasIdentityAsync(repo)→bool`, `BranchStateAsync(repo)→BranchState`.

- [ ] **Step 1: 모델 추가** — `Models/GitModels.cs`

기존 `GitChange`에 `IsStaged` 추가(기존 `IsUntracked` 옆):
```csharp
    /// <summary>스테이징 영역(Staged Changes) 소속이면 true, 작업트리(Changes)면 false.</summary>
    public bool IsStaged { get; init; }
```
파일 끝(마지막 `}` 앞)에 추가:
```csharp
/// <summary>git status 분류 결과 — 스테이징/작업트리 두 목록.</summary>
public sealed class GitStatus
{
    public List<GitChange> Staged { get; init; } = new();
    public List<GitChange> Unstaged { get; init; } = new();
    public bool IsEmpty => Staged.Count == 0 && Unstaged.Count == 0;
}

/// <summary>현재 브랜치·업스트림·ahead/behind.</summary>
public sealed class BranchState
{
    public string? Branch { get; init; }
    public bool HasUpstream { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
}
```

- [ ] **Step 2: GitService 메서드 추가** — `Services/GitService.cs`

`IsRepoAsync` 뒤, 클래스 닫기 `}` 앞에 삽입:
```csharp
    /// <summary>porcelain=v2 로 staged(X)·unstaged(Y) 를 분리 수집.</summary>
    public static async Task<GitStatus> StatusAsync(string repoDir)
    {
        var res = new GitStatus();
        var r = await RunAsync(repoDir, "status", "--porcelain=v1", "-u");
        if (!r.Ok) return res;
        foreach (var raw in r.Output.Split('\n'))
        {
            if (raw.Length < 4) continue;
            var x = raw[0]; var y = raw[1];
            var rest = raw[3..].Trim();
            if (rest.Length == 0) continue;
            var arrow = rest.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) rest = rest[(arrow + 4)..];

            if (x == '?' && y == '?')
            {
                res.Unstaged.Add(new GitChange { Status = "?", Path = rest, IsUntracked = true, IsStaged = false });
                continue;
            }
            if (x != ' ' && x != '?')
                res.Staged.Add(new GitChange { Status = MapCode(x), Path = rest, IsStaged = true });
            if (y != ' ' && y != '?')
                res.Unstaged.Add(new GitChange { Status = MapCode(y), Path = rest, IsStaged = false });
        }
        return res;

        static string MapCode(char c) => c switch
        { 'A' => "A", 'D' => "D", 'M' => "M", 'R' => "R", 'C' => "R", _ => "M" };
    }

    /// <summary>git show &lt;rev&gt;:&lt;path&gt; — rev 예: "HEAD", ":"(인덱스). 실패/부재 시 빈 문자열.</summary>
    public static async Task<string> ShowFileAsync(string repoDir, string rev, string path)
    {
        var r = await RunAsync(repoDir, "show", $"{rev}:{path}");
        return r.Ok ? r.Output : "";
    }

    public static Task<GitResult> StageAsync(string repoDir, string path)
        => RunAsync(repoDir, "add", "--", path);

    public static async Task<GitResult> UnstageAsync(string repoDir, string path)
    {
        var r = await RunAsync(repoDir, "restore", "--staged", "--", path);
        if (r.Ok) return r;
        return await RunAsync(repoDir, "reset", "-q", "--", path); // 신규 파일 등 폴백
    }

    /// <summary>변경 취소. 추적 파일은 checkout, untracked 는 파일 삭제.</summary>
    public static async Task<GitResult> DiscardAsync(string repoDir, string path, bool untracked)
    {
        if (untracked)
        {
            try
            {
                var full = System.IO.Path.Combine(repoDir, path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (System.IO.File.Exists(full)) System.IO.File.Delete(full);
                return new GitResult(true, "", "");
            }
            catch (Exception ex) { return new GitResult(false, "", ex.Message); }
        }
        return await RunAsync(repoDir, "checkout", "--", path);
    }

    public static Task<GitResult> CommitAsync(string repoDir, string message)
        => RunAsync(repoDir, "commit", "-m", message);

    public static Task<GitResult> PullAsync(string repoDir) => RunAsync(repoDir, "pull");
    public static Task<GitResult> FetchAsync(string repoDir) => RunAsync(repoDir, "fetch");

    public static async Task<GitResult> PushAsync(string repoDir)
    {
        var br = await RunAsync(repoDir, "rev-parse", "--abbrev-ref", "HEAD");
        if (!br.Ok) return br;
        var branch = br.Output.Trim();
        if (string.IsNullOrEmpty(branch) || branch == "HEAD")
            return new GitResult(false, "", "현재 브랜치를 확인할 수 없습니다(detached HEAD).");
        var up = await RunAsync(repoDir, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
        return up.Ok ? await RunAsync(repoDir, "push")
                     : await RunAsync(repoDir, "push", "-u", "origin", branch);
    }

    public static async Task<bool> HasIdentityAsync(string repoDir)
    {
        var n = await RunAsync(repoDir, "config", "user.name");
        var e = await RunAsync(repoDir, "config", "user.email");
        return n.Ok && !string.IsNullOrWhiteSpace(n.Output) && e.Ok && !string.IsNullOrWhiteSpace(e.Output);
    }

    public static async Task<BranchState> BranchStateAsync(string repoDir)
    {
        var br = await RunAsync(repoDir, "rev-parse", "--abbrev-ref", "HEAD");
        var branch = br.Ok ? br.Output.Trim() : null;
        if (string.IsNullOrEmpty(branch) || branch == "HEAD") branch = null;
        if (branch == null) return new BranchState();
        var up = await RunAsync(repoDir, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
        if (!up.Ok) return new BranchState { Branch = branch, HasUpstream = false };
        var counts = await RunAsync(repoDir, "rev-list", "--left-right", "--count", "@{u}...HEAD");
        int behind = 0, ahead = 0;
        var parts = counts.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (counts.Ok && parts.Length == 2) { int.TryParse(parts[0], out behind); int.TryParse(parts[1], out ahead); }
        return new BranchState { Branch = branch, HasUpstream = true, Ahead = ahead, Behind = behind };
    }
```

- [ ] **Step 3: 커밋**
```bash
git add Services/GitService.cs Models/GitModels.cs
git commit -m "feat(git): extend GitService with status/stage/commit/pull/push/fetch"
```

---

### Task 2: Monaco 오프라인 웹 자산 + csproj

**Files:**
- Create: `Resources/Monaco/web/diff.html`, `Resources/Monaco/web/bridge.js`, `Resources/Monaco/web/vs/**`(Monaco 배포본)
- Modify: `DevezCode.csproj`

- [ ] **Step 1: Monaco 배포본 배치 (⚠️ 네트워크 필요 · 1회)**

`monaco-editor`의 `min/vs` 폴더를 `Resources/Monaco/web/vs/`에 복사한다. 방법 중 하나:
```bash
# 임시 폴더에서
npm pack monaco-editor@0.52.2
tar -xzf monaco-editor-0.52.2.tgz
# package/min/vs → 프로젝트로 복사
cp -r package/min/vs C:/Source/DevezCode/Resources/Monaco/web/vs
```
결과 구조: `Resources/Monaco/web/vs/loader.js`, `Resources/Monaco/web/vs/editor/editor.main.js` 등이 존재해야 함. (오프라인 로드 전제 — CDN 사용 금지.)
> ulw 에이전트가 네트워크/npm 불가하면 이 스텝은 사용자가 수행하도록 남기고, `vs/` 부재 시 diff.html 이 안내 문구를 표시(아래 bridge.js 참고)한다.

- [ ] **Step 2: diff.html 작성** — `Resources/Monaco/web/diff.html`
```html
<!doctype html>
<html><head><meta charset="utf-8">
<style>
  html,body,#c{height:100%;margin:0;padding:0;overflow:hidden}
  body{background:#1e1e1e}           /* 초기 플래시 방지: 네이티브가 로드 전 배경 재지정 */
  #err{color:#ccc;font:13px/1.5 'Segoe UI',sans-serif;padding:16px;display:none}
</style></head>
<body>
  <div id="c"></div>
  <div id="err">Monaco 자산이 없습니다. Resources/Monaco/web/vs 를 확인하세요.</div>
  <script src="bridge.js"></script>
</body></html>
```

- [ ] **Step 3: bridge.js 작성** — `Resources/Monaco/web/bridge.js`
```javascript
(function () {
  let editor = null, pending = null, ready = false;
  function post(o){ if(window.chrome&&window.chrome.webview) window.chrome.webview.postMessage(JSON.stringify(o)); }

  function boot() {
    if (typeof require === 'undefined') { document.getElementById('err').style.display='block'; return; }
    require.config({ paths: { vs: 'vs' } });
    require(['vs/editor/editor.main'], function () {
      editor = monaco.editor.createDiffEditor(document.getElementById('c'), {
        readOnly: true, automaticLayout: true, renderSideBySide: true,
        minimap: { enabled: true }, scrollBeyondLastLine: false
      });
      ready = true;
      post({ type: 'pageReady' });
      if (pending) { apply(pending); pending = null; }
    });
  }

  function apply(m) {
    if (!ready) { pending = m; return; }
    if (m.type === 'setDiff') {
      const lang = m.language || 'plaintext';
      editor.setModel({
        original: monaco.editor.createModel(m.originalText || '', lang),
        modified: monaco.editor.createModel(m.modifiedText || '', lang),
      });
    } else if (m.type === 'setTheme') {
      monaco.editor.defineTheme('devez', { base: m.base || 'vs-dark', inherit: true,
        rules: m.rules || [], colors: m.colors || {} });
      monaco.editor.setTheme('devez');
    }
  }

  window.chrome && window.chrome.webview &&
    window.chrome.webview.addEventListener('message', e => {
      try { apply(JSON.parse(e.data)); } catch (_) {}
    });

  // vs/loader.js 동적 로드 후 boot
  const s = document.createElement('script');
  s.src = 'vs/loader.js';
  s.onload = boot;
  s.onerror = function(){ document.getElementById('err').style.display='block'; };
  document.head.appendChild(s);
})();
```

- [ ] **Step 4: csproj 에 Content 추가** — `DevezCode.csproj`

`Resources\Markdown\web\**` Content 항목(48-50행) 바로 뒤에 삽입:
```xml
    <Content Include="Resources\Monaco\web\**">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
```

- [ ] **Step 5: 커밋**
```bash
git add Resources/Monaco/web DevezCode.csproj
git commit -m "feat(monaco): vendor offline Monaco assets + diff bridge"
```

---

### Task 3: MonacoHost (WebView2 호스트)

**Files:**
- Create: `Views/MonacoHost.cs`

**Interfaces (Produces):** `MonacoHost : ContentControl, IDisposable` — `Task EnsureReadyAsync()`, `void SetDiff(string original, string modified, string language)`, `void ApplyTheme()`, `event Action? PageReady`, `Task<BitmapSource?> CaptureSnapshotAsync()`.

- [ ] **Step 1: MarkdownWysiwygHost 복제 후 개조** — `Views/MonacoHost.cs`

`Views/MarkdownWysiwygHost.cs`를 복사해 `MonacoHost`로 만들고 아래만 바꾼다(나머지 WebView2 초기화·공유 `SharedEnvironment`·타임아웃 폴백·Dispose 로직은 그대로 유지):
- 클래스명 `MarkdownWysiwygHost` → `MonacoHost`.
- `VirtualHost` 상수 → `"monaco.devezcode.local"`.
- `webRoot` → `Path.Combine(AppContext.BaseDirectory, "Resources", "Monaco", "web")`.
- Navigate 대상 → `$"https://{VirtualHost}/diff.html?v={ver}"`, 캐시버스터 파일 → `bridge.js`.
- 메시지 프로토콜: 마크다운용 `markdownChanged/baseline/...` 제거. `OnWebMessageReceived` 에서 `type=="pageReady"` 만 처리 → `PageReady?.Invoke()` 발생 + pending diff/theme flush.
- 네이티브→JS 래퍼: `SetMarkdown/ApplyTheme(md)` 제거하고 아래 추가:
```csharp
    public event Action? PageReady;
    private (string o, string m, string lang)? _pendingDiff;

    public void SetDiff(string original, string modified, string language)
    {
        if (!_pageReady) { _pendingDiff = (original, modified, language); return; }
        PostJson(new { type = "setDiff", originalText = original, modifiedText = modified, language });
    }

    public void ApplyTheme()
    {
        var t = MonacoThemePayload.Current(); // Task 4 에서 구현하는 static 헬퍼
        PostJson(new { type = "setTheme", t.@base, t.rules, t.colors });
    }
```
- `pageReady` 수신 시: `_pageReady = true;` → `ApplyTheme();` 먼저, 그다음 `if (_pendingDiff is {} d) { SetDiff(d.o,d.m,d.lang); _pendingDiff=null; }` → `PageReady?.Invoke();`
- `PostJson` 은 원본의 `_webView.CoreWebView2.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(obj))` 그대로.
- `CaptureSnapshotAsync`(airspace 스냅샷)와 `CurrentBgColor`(초기 배경) 는 원본 유지.

> `MonacoThemePayload` 는 Task 4 에서 정의하지만 Task 3 컴파일을 위해 이 파일 상단에 `using` 없이 같은 네임스페이스(`DevezCode.Views`)로 참조 가능하다. 두 태스크가 같은 네임스페이스에 각자 파일을 두므로 순서 무관하게 컴파일된다.

- [ ] **Step 2: 커밋**
```bash
git add Views/MonacoHost.cs
git commit -m "feat(monaco): add MonacoHost WebView2 wrapper"
```

---

### Task 4: MonacoDiffHostView (IFileTabEditor) + 테마 페이로드

**Files:**
- Create: `Views/MonacoDiffHostView.xaml`, `Views/MonacoDiffHostView.xaml.cs`
- Create: `Views/MonacoThemePayload.cs`

**Interfaces:**
- Consumes: `MonacoHost`(Task 3), `GitService.ShowFileAsync`(Task 1), `App.ThemeChanged`(App.xaml.cs:29).
- Produces: `MonacoDiffHostView : UserControl, IFileTabEditor, IDisposable`(생성자 `(string repo, string relPath, bool staged)`), `MonacoThemePayload.Current()→(string @base, object[] rules, object colors)`.

- [ ] **Step 1: 테마 페이로드 헬퍼** — `Views/MonacoThemePayload.cs`
```csharp
using System.Windows;
using System.Windows.Media;

namespace DevezCode.Views;

/// <summary>현재 앱 테마 브러시에서 Monaco 테마 색을 추출한다.</summary>
public static class MonacoThemePayload
{
    private static string Hex(string key)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush b)
        {
            var c = b.Color;
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }
        return "#1E1E1E";
    }

    /// <summary>다크 계열인지(배경 밝기로 판정) — Monaco base 선택용.</summary>
    private static bool IsDark()
    {
        if (Application.Current?.TryFindResource("BgBrush") is SolidColorBrush b)
            return (0.299 * b.Color.R + 0.587 * b.Color.G + 0.114 * b.Color.B) < 128;
        return true;
    }

    public static (string @base, object[] rules, object colors) Current()
    {
        var colors = new System.Collections.Generic.Dictionary<string, string>
        {
            ["editor.background"] = Hex("BgBrush"),
            ["editor.foreground"] = Hex("TextBrush"),
            ["editorLineNumber.foreground"] = Hex("TextMutedBrush"),
            ["editorGutter.background"] = Hex("BgBrush"),
            ["diffEditor.insertedTextBackground"] = "#3FB95033",
            ["diffEditor.removedTextBackground"] = "#F8514933",
            ["diffEditor.insertedLineBackground"] = "#3FB9501A",
            ["diffEditor.removedLineBackground"] = "#F851491A",
        };
        return (IsDark() ? "vs-dark" : "vs", System.Array.Empty<object>(), colors);
    }
}
```

- [ ] **Step 2: XAML** — `Views/MonacoDiffHostView.xaml`
```xml
<UserControl x:Class="DevezCode.Views.MonacoDiffHostView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Background="{DynamicResource BgBrush}">
    <Grid x:Name="Root"/>
</UserControl>
```

- [ ] **Step 3: code-behind** — `Views/MonacoDiffHostView.xaml.cs`
```csharp
using System.IO;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>Monaco DiffEditor 를 호스팅하는 파일 탭 에디터(읽기 전용 diff).</summary>
public partial class MonacoDiffHostView : UserControl, IFileTabEditor, IDisposable
{
    public event EventHandler? CloseRequested;
    public event EventHandler? DirtyChanged;   // diff 는 dirty 없음(미사용)
    public event EventHandler? Interacted;

    private readonly string _repo;
    private readonly string _relPath;   // repo 기준 상대경로(/)
    private readonly bool _staged;
    private readonly MonacoHost _host = new();

    public MonacoDiffHostView(string repo, string relPath, bool staged)
    {
        InitializeComponent();
        _repo = repo; _relPath = relPath; _staged = staged;
        Root.Children.Add(_host);
        _host.PageReady += OnReady;
        App.ThemeChanged += OnThemeChanged;
        Loaded += async (_, _) => await _host.EnsureReadyAsync();
        Unloaded += (_, _) => App.ThemeChanged -= OnThemeChanged;
        _host.PreviewMouseDown += (_, _) => Interacted?.Invoke(this, EventArgs.Empty);
    }

    private void OnThemeChanged(string _) => _host.ApplyTheme();

    private async void OnReady()
    {
        var (orig, mod) = await LoadTextsAsync();
        _host.SetDiff(orig, mod, LanguageOf(_relPath));
    }

    private async Task<(string, string)> LoadTextsAsync()
    {
        // Unstaged: 인덱스 vs 작업트리 / Staged: HEAD vs 인덱스 / Untracked: 빈 vs 파일
        var abs = Path.Combine(_repo, _relPath.Replace('/', Path.DirectorySeparatorChar));
        if (_staged)
        {
            var head = await GitService.ShowFileAsync(_repo, "HEAD", _relPath);
            var index = await GitService.ShowFileAsync(_repo, ":", _relPath);
            return (head, index);
        }
        else
        {
            var index = await GitService.ShowFileAsync(_repo, ":", _relPath); // 추적 파일이면 내용, untracked 면 ""
            string work = "";
            try { if (File.Exists(abs)) work = await File.ReadAllTextAsync(abs); } catch { }
            return (index, work);
        }
    }

    private static string LanguageOf(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".cs" => "csharp", ".js" => "javascript", ".ts" => "typescript",
            ".json" => "json", ".xaml" or ".xml" => "xml", ".html" => "html",
            ".css" => "css", ".md" => "markdown", ".py" => "python",
            ".ps1" => "powershell", ".sh" => "shell", ".yml" or ".yaml" => "yaml",
            _ => "plaintext",
        };
    }

    // ── IFileTabEditor ──
    public string? FilePath => Path.Combine(_repo, _relPath.Replace('/', Path.DirectorySeparatorChar));
    public bool IsDirty => false;
    public bool LoadFile(string path) => true;   // diff 는 생성자에서 소스 확보
    public bool Save() => true;                   // no-op
    public void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);
    public new bool Focus() => _host.Focus();
    public Task<BitmapSource?> CaptureSnapshotAsync() => _host.CaptureSnapshotAsync();

    public void Dispose() => _host.Dispose();
}
```

- [ ] **Step 4: 커밋**
```bash
git add Views/MonacoDiffHostView.xaml Views/MonacoDiffHostView.xaml.cs Views/MonacoThemePayload.cs
git commit -m "feat(monaco): add MonacoDiffHostView file-tab editor"
```

---

### Task 5: GitScmView (SCM 패널)

**Files:**
- Create: `Views/GitScmView.xaml`, `Views/GitScmView.xaml.cs`

**Interfaces:**
- Consumes: `GitService`(Task 1), `ConfirmDialog`, `NotificationPopup`.
- Produces: `GitScmView : UserControl` — `void SetRepo(string? path)`, `Task RefreshAsync()`, `event Action<string,string,bool>? DiffFileActivated`(repo, relPath, staged), `event Action<string>? GitStateChanged`(repo).

- [ ] **Step 1: XAML** — `Views/GitScmView.xaml`

두 섹션(Staged/Changes) + 커밋박스 + 툴바. 색은 전부 `DynamicResource`, 버튼은 공통 스타일. (아래는 완전한 파일.)
```xml
<UserControl x:Class="DevezCode.Views.GitScmView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:models="clr-namespace:DevezCode.Models"
             Background="{DynamicResource PanelBrush}"
             FontFamily="{StaticResource PretendardFont}">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <ScrollViewer Grid.Row="0" VerticalScrollBarVisibility="Auto" Padding="4">
            <StackPanel>
                <TextBlock Text="Staged Changes" Margin="6,6,0,2"
                           FontSize="{DynamicResource Fs11}" FontWeight="SemiBold"
                           Foreground="{DynamicResource TextMutedBrush}"/>
                <ItemsControl x:Name="StagedHost">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate DataType="{x:Type models:GitChange}">
                            <Border x:Name="Row" CornerRadius="6" Background="Transparent"
                                    Padding="8,5" Margin="0,1" MouseLeftButtonUp="Row_Click">
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="Auto"/>
                                        <ColumnDefinition Width="*"/>
                                        <ColumnDefinition Width="Auto"/>
                                    </Grid.ColumnDefinitions>
                                    <TextBlock Grid.Column="0" Text="{Binding Status}" Width="16"
                                               TextAlignment="Center" FontWeight="Bold"
                                               Foreground="{Binding StatusColor}"/>
                                    <TextBlock Grid.Column="1" Text="{Binding Path}" Margin="8,0,0,0"
                                               TextTrimming="CharacterEllipsis" ToolTip="{Binding Path}"
                                               Foreground="{DynamicResource TextBrush}"/>
                                    <Button Grid.Column="2" Content="−" Tag="{Binding}"
                                            Click="Unstage_Click" Style="{StaticResource IconButton}"
                                            Width="22" Height="22" ToolTip="unstage"/>
                                </Grid>
                            </Border>
                            <DataTemplate.Triggers>
                                <Trigger SourceName="Row" Property="IsMouseOver" Value="True">
                                    <Setter TargetName="Row" Property="Background" Value="{DynamicResource PanelSoftBrush}"/>
                                </Trigger>
                            </DataTemplate.Triggers>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>

                <TextBlock Text="Changes" Margin="6,10,0,2"
                           FontSize="{DynamicResource Fs11}" FontWeight="SemiBold"
                           Foreground="{DynamicResource TextMutedBrush}"/>
                <ItemsControl x:Name="UnstagedHost">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate DataType="{x:Type models:GitChange}">
                            <Border x:Name="Row2" CornerRadius="6" Background="Transparent"
                                    Padding="8,5" Margin="0,1" MouseLeftButtonUp="Row_Click">
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="Auto"/>
                                        <ColumnDefinition Width="*"/>
                                        <ColumnDefinition Width="Auto"/>
                                        <ColumnDefinition Width="Auto"/>
                                    </Grid.ColumnDefinitions>
                                    <TextBlock Grid.Column="0" Text="{Binding Status}" Width="16"
                                               TextAlignment="Center" FontWeight="Bold"
                                               Foreground="{Binding StatusColor}"/>
                                    <TextBlock Grid.Column="1" Text="{Binding Path}" Margin="8,0,0,0"
                                               TextTrimming="CharacterEllipsis" ToolTip="{Binding Path}"
                                               Foreground="{DynamicResource TextBrush}"/>
                                    <Button Grid.Column="2" Content="↩" Tag="{Binding}"
                                            Click="Discard_Click" Style="{StaticResource IconButton}"
                                            Width="22" Height="22" ToolTip="변경 취소"/>
                                    <Button Grid.Column="3" Content="+" Tag="{Binding}"
                                            Click="Stage_Click" Style="{StaticResource IconButton}"
                                            Width="22" Height="22" ToolTip="stage"/>
                                </Grid>
                            </Border>
                            <DataTemplate.Triggers>
                                <Trigger SourceName="Row2" Property="IsMouseOver" Value="True">
                                    <Setter TargetName="Row2" Property="Background" Value="{DynamicResource PanelSoftBrush}"/>
                                </Trigger>
                            </DataTemplate.Triggers>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>

                <TextBlock x:Name="EmptyText" Text="변경된 파일이 없습니다." Visibility="Collapsed"
                           Margin="10" TextAlignment="Center"
                           Foreground="{DynamicResource TextMutedBrush}"/>
            </StackPanel>
        </ScrollViewer>

        <Border Grid.Row="1" Background="{DynamicResource PanelBrush}"
                BorderBrush="{DynamicResource LineBrush}" BorderThickness="0,1,0,0" Padding="8">
            <StackPanel>
                <Border Background="{DynamicResource BgBrush}"
                        BorderBrush="{DynamicResource LineBrush}" BorderThickness="1"
                        CornerRadius="10" Padding="10,6">
                    <Grid>
                        <TextBox x:Name="MsgBox" BorderThickness="0" Background="Transparent"
                                 Foreground="{DynamicResource TextBrush}" CaretBrush="{DynamicResource TextBrush}"
                                 FontFamily="{StaticResource PretendardFont}" FontSize="{DynamicResource Fs12}"
                                 MaxHeight="80" TextWrapping="Wrap" AcceptsReturn="True"
                                 VerticalScrollBarVisibility="Auto" TextChanged="MsgBox_TextChanged"/>
                        <TextBlock x:Name="MsgPlaceholder" Text="커밋 메시지" IsHitTestVisible="False"
                                   VerticalAlignment="Center" Margin="1,0,0,0"
                                   Foreground="{DynamicResource TextMutedBrush}"
                                   FontFamily="{StaticResource PretendardFont}" FontSize="{DynamicResource Fs12}">
                            <TextBlock.Style>
                                <Style TargetType="TextBlock" BasedOn="{StaticResource {x:Type TextBlock}}">
                                    <Setter Property="Visibility" Value="Collapsed"/>
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding Text, ElementName=MsgBox}" Value="">
                                            <Setter Property="Visibility" Value="Visible"/>
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </TextBlock.Style>
                        </TextBlock>
                    </Grid>
                </Border>
                <Grid Margin="0,6,0,0">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="Auto"/>
                        <ColumnDefinition Width="Auto"/>
                        <ColumnDefinition Width="Auto"/>
                    </Grid.ColumnDefinitions>
                    <Button x:Name="CommitBtn" Grid.Column="0" Content="커밋" Click="Commit_Click"
                            Style="{StaticResource PrimaryButton}" Height="30" IsEnabled="False"
                            FontSize="{DynamicResource Fs12}"/>
                    <Button x:Name="PullBtn" Grid.Column="1" Content="pull" Click="Pull_Click" Margin="6,0,0,0"
                            Style="{StaticResource SecondaryButton}" Height="30" Padding="12,0" FontSize="{DynamicResource Fs12}"/>
                    <Button x:Name="PushBtn" Grid.Column="2" Content="push" Click="Push_Click" Margin="6,0,0,0"
                            Style="{StaticResource SecondaryButton}" Height="30" Padding="12,0" FontSize="{DynamicResource Fs12}"/>
                    <Button x:Name="FetchBtn" Grid.Column="3" Content="fetch" Click="Fetch_Click" Margin="6,0,0,0"
                            Style="{StaticResource SecondaryButton}" Height="30" Padding="12,0" FontSize="{DynamicResource Fs12}"/>
                </Grid>
            </StackPanel>
        </Border>
    </Grid>
</UserControl>
```

- [ ] **Step 2: code-behind** — `Views/GitScmView.xaml.cs`
```csharp
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DevezCode.Models;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>우측 Git SCM 패널 — staged/unstaged 목록 + 커밋박스 + pull/push/fetch.</summary>
public partial class GitScmView : UserControl
{
    /// <summary>파일 행 클릭 → 중앙에 diff 탭 열기 요청.(repo, relPath, staged)</summary>
    public event Action<string, string, bool>? DiffFileActivated;
    /// <summary>커밋/스테이지/pull/push 등 git 상태 변경(repo). 브랜치 버블 갱신용.</summary>
    public event Action<string>? GitStateChanged;

    private string? _repo;
    private bool _busy;
    private BranchState _branch = new();
    private readonly ObservableCollection<GitChange> _staged = new();
    private readonly ObservableCollection<GitChange> _unstaged = new();

    public GitScmView()
    {
        InitializeComponent();
        StagedHost.ItemsSource = _staged;
        UnstagedHost.ItemsSource = _unstaged;
    }

    public void SetRepo(string? path) { if (_repo == path) return; _repo = path; }

    public async Task RefreshAsync()
    {
        if (string.IsNullOrEmpty(_repo) || !Directory.Exists(_repo) || !await GitService.IsRepoAsync(_repo))
        {
            _staged.Clear(); _unstaged.Clear();
            EmptyText.Visibility = Visibility.Visible;
            UpdateButtons(); return;
        }
        var st = await GitService.StatusAsync(_repo);
        _branch = await GitService.BranchStateAsync(_repo);
        _staged.Clear(); foreach (var c in st.Staged) _staged.Add(c);
        _unstaged.Clear(); foreach (var c in st.Unstaged) _unstaged.Add(c);
        EmptyText.Visibility = st.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        CommitBtn.IsEnabled = !_busy && _staged.Count > 0 && !string.IsNullOrWhiteSpace(MsgBox.Text);
        PushBtn.Content = _branch.HasUpstream && _branch.Ahead > 0 ? $"push ↑{_branch.Ahead}" : "push";
        PushBtn.IsEnabled = !_busy;
        PullBtn.IsEnabled = FetchBtn.IsEnabled = !_busy && _branch.Branch != null;
    }

    private void MsgBox_TextChanged(object s, TextChangedEventArgs e) => UpdateButtons();

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (_repo == null || sender is not FrameworkElement { DataContext: GitChange c }) return;
        DiffFileActivated?.Invoke(_repo, c.Path, c.IsStaged);
    }

    private async void Stage_Click(object s, RoutedEventArgs e) => await Do(c => GitService.StageAsync(_repo!, c.Path), s);
    private async void Unstage_Click(object s, RoutedEventArgs e) => await Do(c => GitService.UnstageAsync(_repo!, c.Path), s);

    private async void Discard_Click(object s, RoutedEventArgs e)
    {
        if (_repo == null || (s as FrameworkElement)?.Tag is not GitChange c) return;
        if (!ConfirmDialog.Show("변경 취소", $"'{c.Path}' 의 변경을 취소할까요? 되돌릴 수 없습니다.", "취소", danger: true)) return;
        await Do(_ => GitService.DiscardAsync(_repo!, c.Path, c.IsUntracked), s, alreadyResolved: c);
    }

    private async Task Do(Func<GitChange, Task<GitService.GitResult>> op, object sender, GitChange? alreadyResolved = null)
    {
        var c = alreadyResolved ?? (sender as FrameworkElement)?.Tag as GitChange;
        if (_repo == null || c == null) return;
        _busy = true; UpdateButtons();
        var r = await op(c);
        _busy = false;
        if (!r.Ok) ConfirmDialog.Alert("실패", string.IsNullOrWhiteSpace(r.Error) ? r.Output : r.Error);
        await RefreshAsync();
        GitStateChanged?.Invoke(_repo);
    }

    private async void Commit_Click(object s, RoutedEventArgs e)
    {
        if (_repo == null) return;
        var msg = MsgBox.Text.Trim();
        if (msg.Length == 0 || _staged.Count == 0) return;
        if (!await GitService.HasIdentityAsync(_repo))
        { ConfirmDialog.Alert("커밋 불가", "git 사용자 정보가 없습니다.\ngit config user.name / user.email 설정 후 다시 시도하세요."); return; }
        _busy = true; UpdateButtons();
        var r = await GitService.CommitAsync(_repo, msg);
        _busy = false;
        if (!r.Ok) { ConfirmDialog.Alert("커밋 실패", string.IsNullOrWhiteSpace(r.Error) ? r.Output : r.Error); UpdateButtons(); return; }
        MsgBox.Clear();
        await RefreshAsync();
        GitStateChanged?.Invoke(_repo);
    }

    private async void Push_Click(object s, RoutedEventArgs e)
    {
        if (_repo == null) return;
        var msg = _branch.HasUpstream ? $"커밋 {_branch.Ahead}개를 원격에 푸시할까요?" : "이 브랜치를 origin 에 처음 푸시할까요?";
        if (!ConfirmDialog.Show("푸시", msg, "푸시")) return;
        await RunRemote(() => GitService.PushAsync(_repo!), "푸시");
    }
    private async void Pull_Click(object s, RoutedEventArgs e) => await RunRemote(() => GitService.PullAsync(_repo!), "pull");
    private async void Fetch_Click(object s, RoutedEventArgs e) => await RunRemote(() => GitService.FetchAsync(_repo!), "fetch");

    private async Task RunRemote(Func<Task<GitService.GitResult>> op, string label)
    {
        if (_repo == null) return;
        _busy = true; UpdateButtons();
        var r = await op();
        _busy = false;
        if (!r.Ok) ConfirmDialog.Alert($"{label} 실패", string.IsNullOrWhiteSpace(r.Error) ? r.Output : r.Error);
        else new NotificationPopup($"{label} 완료", null).Show();
        await RefreshAsync();
        GitStateChanged?.Invoke(_repo);
    }
}
```

- [ ] **Step 3: 커밋**
```bash
git add Views/GitScmView.xaml Views/GitScmView.xaml.cs
git commit -m "feat(git): add GitScmView SCM panel"
```

---

### Task 6: FileTabItem.IsDiff + 영속화 제외

**Files:**
- Modify: `Models/WorkspaceModels.cs`
- Modify: `Services/WorkspaceStore.cs`

**Interfaces (Produces):** `FileTabItem.IsDiff`(bool init), Title 에 "(변경)" 접미.

- [ ] **Step 1: FileTabItem 수정** — `Models/WorkspaceModels.cs:251-270`

`Title` 프로퍼티(:254)와 새 필드 추가:
```csharp
    public override string Title => string.IsNullOrEmpty(FilePath)
        ? "파일"
        : Path.GetFileName(FilePath) + (IsDiff ? " (변경)" : "");

    /// <summary>diff 뷰용 파일 탭인지. true 면 제목에 표시하고 workspace.json 영속화에서 제외(전환형).</summary>
    public bool IsDiff { get; init; }
```
(기존 `public override string Title => ... Path.GetFileName(FilePath);` 한 줄을 위 3줄로 교체.)

- [ ] **Step 2: WorkspaceStore 에서 diff 탭 제외** — `Services/WorkspaceStore.cs:232`, `:237`

`OpenFiles`(:232) 를 diff 제외로:
```csharp
        OpenFiles = p.Tabs.OfType<FileTabItem>().Where(f => !f.IsDiff).Select(f => f.FilePath).ToList(),
```
`TabOrder` switch(:234-239)의 `FileTabItem f => "F:" + f.FilePath,` 를 diff 제외로:
```csharp
            FileTabItem f => f.IsDiff ? "" : "F:" + f.FilePath,
```
(`_ => ""` 와 `Where(r => r.Length > 0)` 가 이미 빈 문자열을 걸러냄.)

- [ ] **Step 3: 커밋**
```bash
git add Models/WorkspaceModels.cs Services/WorkspaceStore.cs
git commit -m "feat(git): mark diff file tabs transient (IsDiff)"
```

---

### Task 7: 배선 — SCM 파일 클릭 → 중앙 diff 탭 + 브랜치 버블

**Files:**
- Modify: `Views/WorkspacePaneView.xaml.cs`
- Modify: `Views/FileExplorerView.xaml`, `Views/FileExplorerView.xaml.cs`
- Modify: `MainWindow.xaml.cs`
- Delete: `Views/GitDiffView.xaml`, `Views/GitDiffView.xaml.cs`

**Interfaces:**
- Consumes: `GitScmView`(Task 5), `MonacoDiffHostView`(Task 4), `FileTabItem.IsDiff`(Task 6), 기존 `CreateFileTab`/`ActivateFileTab`/`OpenFileTab`(WorkspacePaneView), 기존 `RefreshBranchIfRepo` 없으면 추가.
- Produces: `WorkspacePaneView.OpenDiffTab(ProjectItem, string repo, string relPath, bool staged)`, `WorkspacePaneView.RefreshBranchIfRepo(string repoDir)`, `FileExplorerView.DiffFileActivated`/`GitStateChanged` 이벤트.

- [ ] **Step 1: WorkspacePaneView — 진단탭 생성/열기** — `Views/WorkspacePaneView.xaml.cs`

`CreateFileTab`(:3397) 아래에 diff 전용 생성기 + 공개 열기 메서드 추가:
```csharp
    /// <summary>diff 파일 탭 생성(같은 repo/경로/staged 조합이 이미 있으면 재사용). Editor 는 Monaco diff.</summary>
    private FileTabItem? CreateDiffTab(ProjectItem proj, string repo, string relPath, bool staged)
    {
        var abs = System.IO.Path.Combine(repo, relPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        var existing = proj.Tabs.OfType<FileTabItem>()
            .FirstOrDefault(t => t.IsDiff && string.Equals(t.FilePath, abs, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;

        var tab = new FileTabItem { FilePath = abs, IsDiff = true, Editor = new MonacoDiffHostView(repo, relPath, staged) };
        tab.Editor.CloseRequested += (_, _) => { if (FileTabCloseRequested != null) FileTabCloseRequested(tab); else RemoveFileTab(tab); };
        proj.Tabs.Add(tab);
        return tab;
    }

    /// <summary>SCM 패널의 파일 클릭 → 이 패널에 diff 탭을 열고 활성화.</summary>
    public void OpenDiffTab(ProjectItem proj, string repo, string relPath, bool staged)
    {
        if (!ReferenceEquals(_activeProject, proj)) SetActiveProject(proj);
        var tab = CreateDiffTab(proj, repo, relPath, staged);
        if (tab != null) ActivateFileTab(tab);
    }
```

- [ ] **Step 2: WorkspacePaneView — RefreshBranchIfRepo (없으면 추가)** — `UpdateProjectBranchBubble`(:610) 근처

```csharp
    /// <summary>지정 repo 가 이 패널의 활성 프로젝트와 같으면 브랜치 버블을 다시 읽는다.</summary>
    public void RefreshBranchIfRepo(string repoDir)
    {
        if (_activeProject == null || string.IsNullOrEmpty(repoDir)) return;
        var a = _activeProject.Path?.TrimEnd('\\', '/');
        var b = repoDir.TrimEnd('\\', '/');
        if (!string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            UpdateProjectBranchBubble(_activeProject);
    }
```

- [ ] **Step 3: FileExplorer — GitScmView 로 교체** — `Views/FileExplorerView.xaml:341`

```xml
        <!-- DIFF/Git 뷰 (Git 모드일 때만 표시) -->
        <v:GitScmView x:Name="ScmView" Grid.Row="3" Visibility="Collapsed"/>
```
(기존 `<v:GitDiffView x:Name="DiffView" .../>` 한 줄 교체.)

- [ ] **Step 4: FileExplorer code-behind** — `Views/FileExplorerView.xaml.cs`

기존 `DiffView.RefreshAsync()`(:211)·`DiffView.SetRepo(...)`(:286,:297,:300) 참조를 `ScmView` 로 바꾸고, 이벤트 포워딩 추가. 클래스에:
```csharp
    /// <summary>SCM 파일 클릭 → 중앙 diff 탭 요청.(repo, relPath, staged) MainWindow 가 구독.</summary>
    public event Action<string, string, bool>? DiffFileActivated;
    /// <summary>git 상태 변경(repo). MainWindow 가 구독해 브랜치 버블 갱신.</summary>
    public event Action<string>? GitStateChanged;
```
생성자 `InitializeComponent();` 다음:
```csharp
        ScmView.DiffFileActivated += (repo, rel, staged) => DiffFileActivated?.Invoke(repo, rel, staged);
        ScmView.GitStateChanged += repo => GitStateChanged?.Invoke(repo);
```
그리고 `DiffView` → `ScmView` 치환(:211 `_ = ScmView.RefreshAsync();`, :286 `ScmView.SetRepo(null);`, :297 `ScmView.SetRepo(path);`, :300 `_ = ScmView.RefreshAsync();`).

- [ ] **Step 5: MainWindow — 구독 배선** — `MainWindow.xaml.cs`

`SetupPane(PaneA); SetupPane(PaneB);`(:170-171) 다음에:
```csharp
        FileExplorer.DiffFileActivated += (repo, rel, staged) =>
        {
            var proj = _focusedPane.ActiveProjectOrNull();   // 없으면 아래 대체 참조 사용
            if (proj != null) _focusedPane.OpenDiffTab(proj, repo, rel, staged);
        };
        FileExplorer.GitStateChanged += repo =>
        {
            foreach (var pane in _panes) pane.RefreshBranchIfRepo(repo);
        };
```
> `_focusedPane` 의 활성 프로젝트 접근자가 없으면, `WorkspacePaneView` 에 `public ProjectItem? ActiveProjectOrNull() => _activeProject;` 를 추가한다(간단 getter). FileExplorer 가 이미 특정 repo 를 보고 있으므로 `repo` 경로로 프로젝트를 찾는 방식도 가능하나, 포커스 패널의 활성 프로젝트에 여는 것이 VS Code 동작과 일치.

- [ ] **Step 6: 죽은 코드 제거** — `Views/GitDiffView.xaml`, `Views/GitDiffView.xaml.cs` 삭제
```bash
git rm Views/GitDiffView.xaml Views/GitDiffView.xaml.cs
```
> 삭제 전 `GitDiffView` 참조가 더 없는지 확인: `grep -rn GitDiffView` → FileExplorer 치환 후 0 이어야 함.

- [ ] **Step 7: 커밋**
```bash
git add Views/WorkspacePaneView.xaml.cs Views/FileExplorerView.xaml Views/FileExplorerView.xaml.cs MainWindow.xaml.cs
git commit -m "feat(git): wire SCM panel to Monaco diff tab + branch refresh"
```

---

### Task 8: UI 점검 스크립트 + 실행

**Files:**
- Create: `scripts/ui-lint.ps1`

- [ ] **Step 1: 스크립트 작성** — `scripts/ui-lint.ps1`
```powershell
# UI 점검: 하드코딩 색·인라인 Style 검출. 대상 파일을 인자로 전달.
# 사용: pwsh scripts/ui-lint.ps1 Views/GitScmView.xaml Views/MonacoDiffHostView.xaml ...
param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Files)

$allow = @('#3FB950','#F85149','#D29922')   # diff 상태색 예외
$violations = @()

foreach ($f in $Files) {
    if (-not (Test-Path $f)) { continue }
    $n = 0
    foreach ($line in Get-Content $f) {
        $n++
        foreach ($m in [regex]::Matches($line, '#[0-9A-Fa-f]{6,8}')) {
            $hex = $m.Value.Substring(0,7).ToUpper()
            if ($allow -notcontains $hex) {
                $violations += "{0}:{1}  하드코딩 색 {2}" -f $f,$n,$m.Value
            }
        }
        if ($f -like '*.xaml' -and $line -match '<Style ' -and $line -notmatch 'BasedOn') {
            $violations += "{0}:{1}  인라인 <Style> (BasedOn 없음)" -f $f,$n
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host "UI 점검 위반 $($violations.Count)건:" -ForegroundColor Red
    $violations | ForEach-Object { Write-Host "  $_" }
    exit 1
}
Write-Host "UI 점검 통과: 위반 0" -ForegroundColor Green
```

- [ ] **Step 2: 신규/수정 UI 파일에 실행**
```powershell
pwsh scripts/ui-lint.ps1 `
  Views/GitScmView.xaml Views/MonacoDiffHostView.xaml Views/FileExplorerView.xaml
```
Expected: `UI 점검 통과: 위반 0`. 위반이 나오면 해당 색/스타일을 `DynamicResource`·공통 스타일로 교체 후 재실행.

- [ ] **Step 3: 커밋**
```bash
git add scripts/ui-lint.ps1
git commit -m "chore: add ui-lint static scan for theme/style compliance"
```

---

## UI 점검 체크리스트 (완료 선언 전 필수)

- [ ] `pwsh scripts/ui-lint.ps1 <신규·수정 XAML>` → 위반 0
- [ ] 새 네이티브 UI(GitScmView)의 모든 색 = `DynamicResource`, 버튼 = `PrimaryButton`/`SecondaryButton`/`IconButton`, 입력 = TaskQueue 카드 패턴
- [ ] (사용자 빌드 후) 테마 전환 시 GitScmView + Monaco diff 색 동시 반영
- [ ] (사용자 빌드 후) Monaco 초기 로드 흰 플래시 없음

## Self-Review 결과

- **스펙 커버리지:** SCM 패널 두 섹션·stage/unstage/discard(Task5) / 커밋·pull·push·fetch(Task1+5) / Monaco diff 탭(Task2,3,4) / FileTabItem 재사용·전환형(Task6) / SCM→탭·브랜치 배선(Task7) / 테마 매핑(Task4 MonacoThemePayload + App.ThemeChanged) / UI 점검 로직(Task8) — 모두 태스크 존재.
- **의도적 차이:** 에러는 기존 관례대로 `ConfirmDialog.Alert`, 원격 성공은 `NotificationPopup`.
- **플레이스홀더:** 코드 스텝 모두 실제 코드. `- [ ]` 는 체크리스트(플레이스홀더 아님).
- **타입 일관성:** `DiffFileActivated`(`Action<string,string,bool>`), `GitStateChanged`(`Action<string>`), `OpenDiffTab(ProjectItem,string,string,bool)`, `MonacoDiffHostView(string,string,bool)`, `MonacoThemePayload.Current()→(string,object[],object)`, `GitService.PushStateAsync` 미사용(대신 `BranchStateAsync`) — Task 간 이름 일치 확인.
- **미확정(구현 중 확인):** ① `MainWindow._focusedPane` 활성 프로젝트 접근자(`ActiveProjectOrNull()` 없으면 추가). ② `MarkdownWysiwygHost` 의 `_pageReady` 상태 필드명(복제 시 실제 확인). ③ Monaco 배포본(`vs/`)은 네트워크 필요 — 부재 시 diff.html 이 안내 표시하도록 이미 처리, 실제 파일은 사용자/네트워크 가능 환경에서 배치.

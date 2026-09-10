using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Security.AccessControl;
using DevezCode.Services;
using DevezCode.Views;

internal static class Program
{
    private static int _checks;
    private static string _root = "";

    [STAThread]
    private static int Main(string[] args)
    {
        _root = Path.Combine(Path.GetTempPath(), "devez-account-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        try
        {
            StoreTests();
            AutomaticCurrentAccountTests();
            ReloadTests();
            if (args.Contains("--cli")) CliFormatTests();
            UiTests();
            Console.WriteLine($"통과: {_checks}개 검사. 화면: {_root}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        _checks++;
    }
    private static void Throws(Action action, string message)
    {
        try { action(); } catch { _checks++; return; }
        throw new Exception(message);
    }

    private static string Jwt(string sub, string email, string account)
    {
        var payload = new JsonObject { ["sub"] = sub, ["email"] = email,
            ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = account } };
        return "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
    }
    private static CliAccount Claude(string id) => CliAccountStore.FromLogin("claude", new JsonObject
    {
        ["access_token"] = "test-claude-" + id, ["refresh_token"] = "test-refresh-" + id,
        ["scope"] = "user:profile user:inference", ["expires_in"] = 3600,
        ["account"] = new JsonObject { ["uuid"] = id, ["email_address"] = id + "@example.invalid" },
        ["organization"] = new JsonObject { ["uuid"] = "org-" + id }
    }.ToJsonString(), "작업용 " + id);
    private static CliAccount Codex(string id) => CliAccountStore.FromLogin("codex", new JsonObject
    {
        ["access_token"] = Jwt(id, id + "@example.invalid", "account"), ["id_token"] = Jwt(id, id + "@example.invalid", "account"),
        ["refresh_token"] = "test-refresh-" + id, ["expires_in"] = 3600
    }.ToJsonString(), "개발용 " + id);

    private static void StoreTests()
    {
        var home = Path.Combine(_root, "한글 사용자");
        var claudeHome = Path.Combine(home, ".claude");
        var codexHome = Path.Combine(home, ".codex");
        var storePath = Path.Combine(home, "accounts.dat");
        var store = new CliAccountStore(storePath, claudeHome, codexHome);
        Directory.CreateDirectory(claudeHome);
        Directory.CreateDirectory(codexHome);
        var settings = Path.Combine(claudeHome, "settings.json");
        File.WriteAllText(settings, "{\"theme\":\"dark\",\"hooks\":{}}");
        var transcript = Path.Combine(claudeHome, "projects", "project", "session.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        File.WriteAllText(transcript, "{\"message\":\"이전 대화\"}\n");
        File.WriteAllText(claudeHome + ".json", "{\"projects\":{\"x\":{\"hasTrustDialogAccepted\":true}},\"theme\":\"dark\"}");
        var config = Path.Combine(codexHome, "config.toml");
        File.WriteAllText(config, "model = \"model\"\ncli_auth_credentials_store = \"keyring\"\n[mcp_servers.local]\ncommand = \"server\"\n");
        var a = Claude("a"); var b = Claude("b"); var x = Codex("x"); var y = Codex("y");
        store.Add(a); store.Add(b); store.Add(x); store.Add(y);
        Check(store.Read().Accounts.Count == 4, "계정 추가 보존");
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(storePath)).Contains("test-refresh"), "암호화 누락");
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(storePath)).Contains("example.invalid"), "개인 정보 암호화 누락");
        Throws(() => store.Add(Claude("a")), "동일 계정 중복 등록 허용");
        Check(store.Read().Accounts.Count == 4, "중복 실패가 기존 목록을 변경");
        store.Activate("claude", a.Id);
        Check(store.ReadCurrent("claude")!.Identity == "a", "Claude 전환 실패");
        Check(File.ReadAllText(settings).Contains("hooks"), "공통 설정 유실");
        Check(File.ReadAllText(transcript).Contains("이전 대화"), "대화 유실");
        Check(JsonNode.Parse(File.ReadAllText(claudeHome + ".json"))!["projects"]!["x"]!["hasTrustDialogAccepted"]!.GetValue<bool>(), "프로젝트 신뢰 유실");
        var refreshed = JsonNode.Parse(File.ReadAllText(store.ClaudeCredentialsPath))!;
        refreshed["claudeAiOauth"]!["refreshToken"] = "rotated-refresh-a";
        File.WriteAllText(store.ClaudeCredentialsPath, refreshed.ToJsonString());
        store.Activate("claude", b.Id);
        store.Activate("claude", a.Id);
        Check(File.ReadAllText(store.ClaudeCredentialsPath).Contains("rotated-refresh-a"), "갱신된 토큰을 이전 토큰으로 되돌림");
        Throws(() => store.Delete(a.Id), "활성 계정 삭제 허용");
        store.Delete(b.Id);
        Check(store.Read().Accounts.Count == 3, "비활성 계정 삭제 실패");
        store.Activate("codex", x.Id);
        Check(store.ReadCurrent("codex")!.Identity == "x:account", "Codex 전환 실패");
        Check(File.ReadAllText(config).Contains("cli_auth_credentials_store = \"file\""), "키링 우선순위 미해결");
        Check(File.ReadAllText(config).Contains("[mcp_servers.local]\ncommand = \"server\""), "MCP 설정 유실");
        store.Activate("codex", y.Id);
        Check(store.ReadCurrent("claude")!.Identity == "a", "다른 공급자 인증 변경");
        Check(store.Read().Active["claude"] == a.Id, "다른 공급자 선택 변경");
        var authBefore = File.ReadAllBytes(store.CodexCredentialsPath);
        var activeBefore = store.Read().Active["codex"];
        using (var locked = new FileStream(config, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            Throws(() => store.Activate("codex", x.Id), "잠긴 설정 파일 전환 실패 미감지");
        Check(authBefore.SequenceEqual(File.ReadAllBytes(store.CodexCredentialsPath)), "실패 후 인증 복구 누락");
        Check(store.Read().Active["codex"] == activeBefore, "실패 후 선택값 복구 누락");
        var claudeStateBefore = File.ReadAllBytes(claudeHome + ".json");
        var c = Claude("c"); store.Add(c);
        using (var locked = new FileStream(store.ClaudeCredentialsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            Throws(() => store.Activate("claude", c.Id), "잠긴 인증 파일 전환 실패 미감지");
        Check(File.ReadAllBytes(claudeHome + ".json").SequenceEqual(claudeStateBefore), "인증 쓰기 실패 후 계정 메타데이터 복구 누락");
        store.Delete(c.Id);
        var bad = Claude("bad"); bad.Credentials = "{";
        Throws(() => store.Add(bad), "잘못된 JSON 저장 허용");
        Check(store.Read().Accounts.Count == 3, "실패로 계정 유실");
        var restored = new CliAccountStore(storePath, claudeHome, codexHome);
        Check(restored.Read().Active["codex"] == y.Id, "재시작 후 선택 유지 실패");
        Check(restored.ReadCurrent("codex")!.Identity == "y:account", "재시작 후 대화 계정 유지 실패");
        var authFile = new FileInfo(store.CodexCredentialsPath);
        var permissions = authFile.GetAccessControl();
        permissions.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
        authFile.SetAccessControl(permissions);
        store.Activate("codex", x.Id);
        Check(new FileInfo(store.CodexCredentialsPath).GetAccessControl().AreAccessRulesProtected, "인증 교체가 기존 파일 접근 권한을 변경");
        var scoped = "[profiles.work]\ncli_auth_credentials_store = \"keyring\"\n";
        Check(CliAccountStore.UseFileCredentials(scoped).StartsWith("cli_auth_credentials_store = \"file\""), "루트 옵션 생성 실패");
        Check(CliAccountStore.UseFileCredentials(CliAccountStore.UseFileCredentials(scoped)) == CliAccountStore.UseFileCredentials(scoped), "반복 적용 비멱등");
        Check(!CliAccountStore.UseFileCredentials(scoped).Contains("keyring"), "프로필 인증 우선순위 미해결");
        var multiline = "instructions = \"\"\"\n[pretend]\ncli_auth_credentials_store = 'keyring'\n\"\"\"\n[projects.test]\ntrust_level = 'trusted'\n";
        Check(CliAccountStore.UseFileCredentials(multiline) == "cli_auth_credentials_store = \"file\"\n" + multiline, "다중 행 지침 손상");
        var quoted = "\uFEFF'cli_auth_credentials_store' = 'auto' # comment\nmodel = 'x'\n";
        Check(CliAccountStore.UseFileCredentials(quoted).Count(c => c == '=') == 2, "따옴표 옵션 중복 생성");
        File.WriteAllBytes(storePath, [1, 2, 3]);
        Throws(() => store.Read(), "손상된 계정 파일을 빈 목록으로 오인");
        Check(File.ReadAllBytes(storePath).SequenceEqual(new byte[] { 1, 2, 3 }), "손상 파일 덮어쓰기");
    }

    private static void AutomaticCurrentAccountTests()
    {
        var home = Path.Combine(_root, "automatic-current");
        var claudeHome = Path.Combine(home, ".claude");
        var codexHome = Path.Combine(home, ".codex");
        Directory.CreateDirectory(claudeHome);
        Directory.CreateDirectory(codexHome);
        var claude = Claude("2bbf8b1a-bb80-44f7-879f-e5aa7d2e24b6");
        var codex = Codex("current-codex");
        File.WriteAllText(Path.Combine(claudeHome, ".credentials.json"), claude.Credentials);
        File.WriteAllText(claudeHome + ".json", new JsonObject { ["oauthAccount"] = JsonNode.Parse(claude.ClaudeAccount!) }.ToJsonString());
        File.WriteAllText(Path.Combine(codexHome, "auth.json"), codex.Credentials);
        var path = Path.Combine(home, "accounts.dat");
        var store = new CliAccountStore(path, claudeHome, codexHome);
        var first = store.RefreshCurrentAccounts(out var errors);
        Check(errors.Count == 0 && first.Accounts.Count == 2, "첫 화면의 현재 계정 자동 인식 실패");
        Check(first.Active.Count == 2, "첫 화면의 현재 계정 선택 누락");
        var before = File.ReadAllBytes(path);
        var second = store.RefreshCurrentAccounts(out _);
        Check(second.Accounts.Count == 2 && second.Active["claude"] == first.Active["claude"], "반복 인식 시 중복 등록 또는 ID 변경");
        Check(before.SequenceEqual(File.ReadAllBytes(path)), "변경 없는 새로고침에서 보관 파일 재작성");

        var registered = new CliAccountStore(Path.Combine(home, "registered.dat"), claudeHome, codexHome);
        registered.Add(claude); registered.Add(codex);
        var imported = registered.RefreshCurrentAccounts(out _);
        Check(imported.Accounts.Count == 2 && imported.Active["claude"] == claude.Id && imported.Active["codex"] == codex.Id,
            "이미 등록된 현재 계정을 재사용하지 않음");
        Check(imported.Accounts.Single(a => a.Id == claude.Id).Name == claude.Name, "자동 인식이 사용자 계정 이름을 변경");
        var renamed = Claude(claude.Identity); renamed.Name = "다른 이름";
        Throws(() => registered.Add(renamed), "이름만 바꾼 중복 계정 등록 허용");
        var uppercase = Claude(claude.Identity.ToUpperInvariant());
        Throws(() => registered.Add(uppercase), "UUID 대소문자 차이로 중복 등록 허용");
        Parallel.For(0, 12, _ => registered.CaptureCurrent("claude"));
        Check(registered.Read().Accounts.Count == 2, "동시 가져오기에서 중복 등록");
        File.WriteAllText(registered.ClaudeCredentialsPath, uppercase.Credentials);
        File.WriteAllText(claudeHome + ".json", new JsonObject { ["oauthAccount"] = JsonNode.Parse(uppercase.ClaudeAccount!) }.ToJsonString());
        var recaptured = registered.RefreshCurrentAccounts(out _);
        Check(recaptured.Accounts.Count == 2 && recaptured.Active["claude"] == claude.Id, "UUID 표기 변경으로 다른 계정 생성");
        registered.Activate("claude", claude.Id);
        Check(registered.Read().Active["claude"] == claude.Id, "UUID 표기 변경 후 계정 전환 실패");
        File.WriteAllText(registered.ClaudeCredentialsPath, "{");
        var partial = registered.RefreshCurrentAccounts(out errors);
        Check(errors.SequenceEqual(new[] { "claude" }) && partial.Accounts.Count == 2 && partial.Active["codex"] == codex.Id,
            "한 공급자의 인증 오류가 다른 공급자 목록을 지움");
        var workspaceStore = new CliAccountStore(Path.Combine(home, "workspaces.dat"), claudeHome, codexHome);
        workspaceStore.Add(codex);
        var otherWorkspace = CliAccountStore.FromLogin("codex", new JsonObject
        {
            ["access_token"] = Jwt("current-codex", "current-codex@example.invalid", "another-workspace"),
            ["id_token"] = Jwt("current-codex", "current-codex@example.invalid", "another-workspace"),
            ["refresh_token"] = "test-other-workspace", ["expires_in"] = 3600
        }.ToJsonString(), "다른 워크스페이스");
        workspaceStore.Add(otherWorkspace);
        Check(workspaceStore.Read().Accounts.Count == 2, "이메일이 같은 서로 다른 Codex 워크스페이스를 합침");
    }

    private static void CliFormatTests()
    {
        var home = Path.Combine(_root, "real-cli");
        var claudeHome = Path.Combine(home, "claude");
        var codexHome = Path.Combine(home, "codex");
        var store = new CliAccountStore(Path.Combine(home, "accounts.dat"), claudeHome, codexHome, Path.Combine(claudeHome, ".claude.json"));
        var a = Claude("cli-a"); var b = Claude("cli-b"); var x = Codex("cli-x");
        store.Add(a); store.Add(b); store.Add(x);
        store.Activate("claude", a.Id);
        var first = RunCli("claude auth status --json", home, claudeHome, codexHome);
        Check(first.code == 0, "설치된 Claude CLI가 인증 파일 형식을 거부: " + first.error);
        var status = JsonNode.Parse(first.output)!;
        Check(status["loggedIn"]!.GetValue<bool>(), "설치된 Claude CLI가 보관 인증을 인식하지 못함");
        Check(status["email"]?.GetValue<string>() == "cli-a@example.invalid", "설치된 Claude CLI 계정 정보 경로 불일치");
        store.Activate("claude", b.Id);
        var second = RunCli("claude auth status --json", home, claudeHome, codexHome);
        Check(JsonNode.Parse(second.output)!["email"]?.GetValue<string>() == "cli-b@example.invalid", "설치된 Claude CLI 계정 교체 불일치");
        store.Activate("codex", x.Id);
        var codex = RunCli("codex login status", home, claudeHome, codexHome);
        Check(codex.code == 0 && (codex.output + codex.error).Contains("ChatGPT", StringComparison.OrdinalIgnoreCase),
            "설치된 Codex CLI가 인증 파일 형식을 거부: " + codex.error);
        Console.WriteLine("설치된 CLI의 로컬 인증 형식 확인 통과. 가상 토큰이므로 서버 인증 검증은 아님.");
    }

    private static (int code, string output, string error) RunCli(string command, string home, string claudeHome, string codexHome)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = home };
        start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add(command);
        start.Environment["CLAUDE_CONFIG_DIR"] = claudeHome;
        start.Environment["CODEX_HOME"] = codexHome;
        foreach (var key in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN", "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_AUTH_JSON" }) start.Environment.Remove(key);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(20000)) throw new Exception("로컬 CLI 인증 형식 검사 시간 초과");
        return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }

    private static void AwaitOnDispatcher(Func<Task> action)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var task = action();
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                var dispatcher = Dispatcher.CurrentDispatcher;
                task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
                Dispatcher.PushFrame(frame);
            }
            task.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void ReloadTests()
    {
        var home = Path.Combine(_root, "reload");
        var store = new CliAccountStore(Path.Combine(home, "accounts.dat"), Path.Combine(home, ".claude"), Path.Combine(home, ".codex"));
        var a = Codex("before"); var b = Codex("after");
        store.Add(a); store.Add(b); store.Activate("codex", a.Id);
        DevezCode.MainWindow.TestStore = store;
        DevezCode.MainWindow.Events.Clear();
        DevezCode.TerminalSessionManager.Instance = new();
        DevezCode.ClaudeSdkSessionManager.Instance = new();
        var main = new DevezCode.MainWindow();
        var cli = new DevezCode.SessionItem { Id = "cli", Name = "기존 대화", AgentId = "codex", IsAlive = true };
        var sdk = new DevezCode.SessionItem { Id = "sdk", Name = "GUI 대화", AgentId = "claude", IsAlive = true };
        var dormant = new DevezCode.SessionItem { Id = "idle", Name = "휴면 대화", AgentId = "codex" };
        var starting = new DevezCode.SessionItem { Id = "sdk-starting", Name = "시작 중인 대화", AgentId = "claude" };
        main._projects.Add(new() { Tabs = [cli, sdk, dormant, starting] });
        DevezCode.TerminalSessionManager.Instance.Alive.Add(cli.Id);
        DevezCode.ClaudeSdkSessionManager.Instance.Alive.Add(sdk.Id);
        DevezCode.MainWindow.OnEvent = evt =>
        {
            if (evt == "suspend-sdk")
            {
                DevezCode.ClaudeSdkSessionManager.Instance.Alive.Add(starting.Id);
                Throws(() => main.SwitchCliAccountAsync("codex", a.Id).GetAwaiter().GetResult(), "동시 계정 전환 허용");
            }
            if (evt.StartsWith("stop")) Check(store.ReadCurrent("codex")!.Identity == "before:account", "종료 전에 인증 변경");
            if (evt.StartsWith("start")) Check(store.ReadCurrent("codex")!.Identity == "after:account", "재실행이 이전 계정 사용");
        };
        AwaitOnDispatcher(() => main.SwitchCliAccountAsync("codex", b.Id));
        Check(DevezCode.TerminalSessionManager.Instance.Alive.Contains(cli.Id), "CLI 세션 재실행 누락");
        Check(DevezCode.ClaudeSdkSessionManager.Instance.IsStarted(sdk.Id), "GUI 세션 재실행 누락");
        Check(!DevezCode.MainWindow.Events.Contains("stop-sdk:" + starting.Id) && !DevezCode.MainWindow.Events.Contains("stop-sdk:" + sdk.Id), "Codex 전환이 Claude GUI를 종료");
        Check(!DevezCode.TerminalSessionManager.Instance.Alive.Contains(dormant.Id), "휴면 세션 강제 기동");
        Check(main.IsGateAvailable && !main.IsSwitching, "성공 후 전환 잠금 미해제");
        Check(DevezCode.MainWindow.Events.Last() == "drain", "보류된 사용자 입력 미복원");
        DevezCode.MainWindow.OnEvent = null;
        cli.IsExternal = true;
        var count = DevezCode.MainWindow.Events.Count;
        Throws(() => AwaitOnDispatcher(() => main.SwitchCliAccountAsync("codex", a.Id)), "외부 세션 전환 허용");
        Check(DevezCode.MainWindow.Events.Count == count, "외부 세션 검사 전에 프로세스 변경");
        cli.IsExternal = false;
        Throws(() => AwaitOnDispatcher(() => main.SwitchCliAccountAsync("codex", "removed")), "없는 계정 전환 허용");
        Check(main.IsGateAvailable && !main.IsSwitching, "없는 계정 요청 후 잠금");
        DevezCode.TerminalSessionManager.Instance.FailStop = true;
        Throws(() => AwaitOnDispatcher(() => main.SwitchCliAccountAsync("codex", a.Id)), "종료 오류 무시");
        Check(store.ReadCurrent("codex")!.Identity == "after:account", "종료 실패 후 인증 변경");
        Check(DevezCode.TerminalSessionManager.Instance.Alive.Contains(cli.Id), "부분 종료 실패 후 CLI 복원 누락");
        Check(main.IsGateAvailable && !main.IsSwitching, "종료 실패 후 잠금 미해제");
        DevezCode.TerminalSessionManager.Instance.FailStop = false;
        main._panes[0].FailComplete = true;
        Throws(() => AwaitOnDispatcher(() => main.SwitchCliAccountAsync("codex", a.Id)), "화면 복원 오류 무시");
        Check(main.IsGateAvailable && !main.IsSwitching, "화면 복원 실패 후 잠금 미해제");
        main._panes[0].FailComplete = false;
        ProviderScopeTests(main, store, cli, sdk, starting);
        main.Close();
    }

    private static void ProviderScopeTests(DevezCode.MainWindow main, CliAccountStore store,
        DevezCode.SessionItem codexCli, DevezCode.SessionItem sdk, DevezCode.SessionItem starting)
    {
        var routePath = Path.Combine(_root, "session-routes.json");
        DevezCode.TerminalSessionManager.TestRoutePath = routePath;
        File.WriteAllText(routePath, """
            {"vibe-claude":{"active":"Claude","codex_id":"old-codex","claude_id":"native-claude"},
             "vibe-codex":{"active":"Codex","claude_id":"old-claude","codex_id":"native-codex"},
             "vibe-open":{"active":"OpenCode","open_code_id":"ses_test"}}
            """);
        Check(DevezCode.TerminalSessionManager.ResolveDevezVibeProvider("vibe-claude") == "claude", "Devez Vibe 활성 Claude 식별 실패");
        Check(DevezCode.TerminalSessionManager.ResolveDevezVibeProvider("claude:old-claude") == "codex", "이전 ID 접두사로 공급자 오판");
        Check(DevezCode.TerminalSessionManager.ResolveDevezVibeProvider("old-codex") == "claude", "공급자 변경 후 역조회 실패");
        var vc = new DevezCode.SessionItem { Id = "vibe-claude", Name = "Vibe Claude", AgentId = "devezvibe" };
        var vx = new DevezCode.SessionItem { Id = "vibe-codex", Name = "Vibe Codex", AgentId = "devezvibe" };
        var vo = new DevezCode.SessionItem { Id = "vibe-open", Name = "Vibe OpenCode", AgentId = "devezvibe" };
        var fresh = new DevezCode.SessionItem { Id = "vibe-new", Name = "세션 3", AgentId = "devezvibe" };
        DevezCode.SettingsService.SavedVibeIds[fresh.Id] = null;
        DevezCode.DevezVibeStateService.TrackedIds[vc.Id] = "stale-tracked-id";
        main._projects[0].Tabs.AddRange([vc, vx, vo, fresh]);
        DevezCode.TerminalSessionManager.Instance.Alive.UnionWith([vc.Id, vx.Id, vo.Id, fresh.Id]);
        var ca = Claude("scope-before"); var cb = Claude("scope-after");
        store.Add(ca); store.Add(cb); store.Activate("claude", ca.Id);
        DevezCode.MainWindow.Events.Clear();
        codexCli.IsExternal = true; // 다른 공급자의 외부 세션은 변경을 막지 않는다.
        DevezCode.ClaudeSdkSessionManager.Instance.Alive.Remove(starting.Id);
        DevezCode.MainWindow.OnEvent = evt =>
        {
            if (evt == "suspend-sdk") DevezCode.ClaudeSdkSessionManager.Instance.Alive.Add(starting.Id);
        };
        AwaitOnDispatcher(() => main.SwitchCliAccountAsync("claude", cb.Id));
        DevezCode.MainWindow.OnEvent = null;
        var events = DevezCode.MainWindow.Events;
        Check(events.Contains("start:" + vc.Id), "Claude 계정 전환 시 Vibe Claude 누락");
        Check(events.Contains("start:" + fresh.Id), "대화 ID가 없는 새 세션이 계정 전환을 막음");
        Check(events.Contains("stop-sdk:" + sdk.Id) && events.Contains("start-sdk:" + starting.Id), "Claude GUI 재시작 누락");
        Check(!events.Contains("start:" + vx.Id) && !events.Contains("start:" + vo.Id) && !events.Contains("start:" + codexCli.Id), "다른 공급자 세션을 재시작");
        Check(DevezCode.TerminalSessionManager.Instance.Alive.Contains(vx.Id) && DevezCode.TerminalSessionManager.Instance.Alive.Contains(vo.Id), "다른 공급자의 프로세스를 종료");
        codexCli.IsExternal = false;
        events.Clear();
        AwaitOnDispatcher(() => main.SwitchCliAccountAsync("codex", store.Read().Accounts.First(a => a.Provider == "codex").Id));
        Check(events.Contains("start:" + vx.Id) && !events.Contains("start:" + vc.Id), "Codex 계정 전환의 Vibe 대상 오류");
        events.Clear();
        DevezCode.DevezVibeStateService.LastMessages[fresh.Id] = "이미 보낸 첫 메시지";
        Throws(() => AwaitOnDispatcher(() => main.SwitchCliAccountAsync("claude", ca.Id)), "첫 프롬프트 전송 후 추적 ID가 없는 방을 빈 세션으로 오판");
        Check(events.Count == 0, "대화 흔적이 있는 미확인 세션을 종료");
        DevezCode.DevezVibeStateService.LastMessages.Remove(fresh.Id);
        fresh.IsBusy = true;
        Throws(() => AwaitOnDispatcher(() => main.SwitchCliAccountAsync("claude", ca.Id)), "작업 중인 미확인 세션을 새 세션으로 오판");
        fresh.IsBusy = false;
        DevezCode.DevezVibeStateService.BusyRooms.Add(fresh.Id);
        Throws(() => AwaitOnDispatcher(() => main.SwitchCliAccountAsync("claude", ca.Id)), "상태 파일의 작업 중 표시를 무시");
        DevezCode.DevezVibeStateService.BusyRooms.Remove(fresh.Id);
        var previousAuth = File.ReadAllBytes(store.ClaudeCredentialsPath);
        File.WriteAllText(routePath, "{");
        events.Clear();
        Throws(() => AwaitOnDispatcher(() => main.SwitchCliAccountAsync("claude", ca.Id)), "공급자 미확인 시 전환 강행");
        Check(events.Count == 0 && previousAuth.SequenceEqual(File.ReadAllBytes(store.ClaudeCredentialsPath)), "공급자 확인 실패가 세션 또는 인증을 변경");
        File.WriteAllText(routePath, """{"a":{"active":"Claude","codex_id":"shared"},"b":{"active":"Codex","codex_id":"shared"}}""");
        Check(DevezCode.TerminalSessionManager.ResolveDevezVibeProvider("shared") == null, "중복 역조회에서 공급자를 임의 선택");
        Check(DevezCode.TerminalSessionManager.ResolveDevezVibeProvider("untracked") == null, "없는 라우트의 공급자를 추정");
    }

    private static void UiTests()
    {
        var callback = typeof(UsageLoginWindowBase).GetMethod("IsOAuthCallback", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        bool Callback(string address) => (bool)callback.Invoke(null, [address, "http://localhost:1455/auth/callback"])!;
        Check(Callback("http://localhost:1455/auth/callback?code=test&state=test"), "정상 콜백 거절");
        Check(!Callback("http://localhost:1455/auth/callback-other?code=test"), "유사 경로 콜백 허용");
        Check(!Callback("http://localhost:1456/auth/callback?code=test"), "다른 포트 콜백 허용");
        Check(!Callback("http://localhost.evil.invalid:1455/auth/callback"), "다른 호스트 콜백 허용");
        var app = new DevezCode.App();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DevezCode.Accounts.Tests;component/Styles/AppStyles.xaml", UriKind.Relative) });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DevezCode.Accounts.Tests;component/Resources/Icons.xaml", UriKind.Relative) });
        var data = new CliAccountStore.AccountData();
        var a = Claude("ui-a"); a.Name = "주 계정 · 한글과 긴 이름 표시 확인";
        var b = Claude("ui-b"); b.Name = new string('긴', 70);
        b.Email = new string('a', 64) + "@" + new string('b', 60) + ".example.invalid";
        data.Accounts.AddRange([a, b, Codex("ui-x")]);
        data.Active["claude"] = a.Id;
        var row = new AccountSettingsView.ProviderRow("claude", data);
        Check(row.Options.Single(o => o.Id == row.ActiveId).Name == a.Email, "활성 계정이 이메일 대신 별칭으로 표시됨");
        Check(a.DisplayName == a.Email && b.DisplayName == b.Email, "기존 별칭이 계정 표시값에 포함됨");
        Check(new CliAccount { Name = "이전 별칭" }.DisplayName == "이메일 확인 필요", "이메일 누락 시 별칭을 다시 표시");
        Check(!row.Accounts.First().CanDelete && row.Accounts.Last().CanDelete, "삭제 가능 상태 오류");
        var empty = new AccountSettingsView.ProviderRow("codex", new());
        Check(empty.Options.Count == 1 && empty.Options[0].Id == empty.ActiveId, "빈 목록 표시 실패");
        Check(!empty.CanSwitch, "빈 계정 목록에서 전환 허용");
        var view = new AccountSettingsView();
        ((ItemsControl)view.FindName("Providers")).ItemsSource = new[] { row, new AccountSettingsView.ProviderRow("codex", data) };
        view.FontFamily = (FontFamily)app.FindResource("PretendardFont");
        var scroll = new ScrollViewer { Content = view, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var host = new Border { Child = scroll, Padding = new Thickness(28), Background = (Brush)app.FindResource("BgBrush"), Width = 720, Height = 960 };
        var window = new Window { Content = host, Width = 720, Height = 960, Left = -32000, Top = -32000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        window.Show();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        host.Measure(new Size(720, 960)); host.Arrange(new Rect(0, 0, 720, 960)); host.UpdateLayout();
        var combos = Descendants<ComboBox>(view).ToList();
        Check(combos.Count == 2, "공급자별 콤보박스 누락");
        Check(combos[0].SelectedValue as string == a.Id, "실제 콤보박스 선택값 바인딩 실패");
        Check(combos[0].SelectionBoxItem.ToString() == a.DisplayName, "닫힌 콤보박스 값 표시 실패");
        Check(Descendants<TextBlock>(combos[0]).Any(t => t.Text == a.DisplayName), "닫힌 콤보박스 실제 텍스트 누락");
        Check(!Descendants<TextBlock>(view).Any(t => t.Text.Contains("주 계정")), "계정 목록에 이전 별칭이 노출됨");
        Check(combos.All(c => c.ActualWidth > 200 && c.ActualHeight == 40), "콤보박스 크기 오류");
        SaveImage(host, "accounts-minimal.png");
        var palettes = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ThemePalette.json")))!;
        foreach (var palette in palettes)
        {
            foreach (var entry in palette.Value) app.Resources[entry.Key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(entry.Value));
            host.Background = (Brush)app.FindResource("BgBrush");
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            host.UpdateLayout(); SaveImage(host, "accounts-" + palette.Key + ".png");
            Check(Descendants<TextBlock>(view).First(t => t.Text == "계정").Foreground is SolidColorBrush brush
                && brush.Color == (Color)ColorConverter.ConvertFromString(palette.Value["TextBrush"]), palette.Key + " 테마 변경 시 글자색 미반영");
            Check(combos[0].SelectedValue as string == a.Id, palette.Key + " 테마 변경 시 계정 선택 유실");
        }
        combos[0].IsDropDownOpen = true;
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var popup = (System.Windows.Controls.Primitives.Popup)combos[0].Template.FindName("PART_Popup", combos[0]);
        var dropdown = (FrameworkElement)popup.Child;
        Check(Descendants<TextBlock>(dropdown).Any(t => t.Text == b.DisplayName), "열린 목록 계정 이름 누락");
        Check(dropdown.ActualWidth <= combos[0].ActualWidth + 1, "긴 계정 이름이 드롭다운 너비를 확장");
        SaveImage(dropdown, "accounts-dropdown.png");
        combos[0].IsDropDownOpen = false;
        var peer = new System.Windows.Automation.Peers.ListBoxItemAutomationPeer(combos[0].Items[1],
            new System.Windows.Automation.Peers.ComboBoxAutomationPeer(combos[0]));
        ((System.Windows.Automation.Provider.ISelectionItemProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.SelectionItem)).Select();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Check(combos[0].SelectedValue as string == b.Id, "접근성 선택 동작으로 계정 변경 불가");
        Check(Descendants<TextBlock>(combos[0]).Any(t => t.Text == b.DisplayName), "선택 후 닫힌 값 표시 실패");
        Check(combos.All(c => c.IsTabStop && c.Focusable), "키보드 탐색 대상에서 콤보박스 누락");
        Check(Descendants<Button>(view).All(b => b.IsTabStop && b.FocusVisualStyle != null), "계정 액션의 키보드 접근성 누락");
        host.Width = 460; window.Width = 460;
        foreach (var size in new[] { 11, 12, 13, 14 }) app.Resources["Fs" + size] = size * 1.5;
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        host.UpdateLayout(); SaveImage(host, "accounts-narrow-large-text.png");
        Check(combos.All(c => c.ActualWidth > 150 && c.ActualWidth < 400), "좁은 화면 콤보박스 너비 오류");
        Check(scroll.ScrollableWidth == 0, "좁은 화면 가로 스크롤 발생");
        window.Close();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void SaveImage(FrameworkElement view, string name)
    {
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(_root, name)); encoder.Save(file);
    }
}

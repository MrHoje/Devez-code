using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DevezCode.Services;

public sealed class CliAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Provider { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Identity { get; set; } = "";
    public string Credentials { get; set; } = "";
    public string? ClaudeAccount { get; set; }
    public string DisplayName => string.IsNullOrWhiteSpace(Email) ? "이메일 확인 필요" : Email.Trim();
    public override string ToString() => DisplayName;
}

/// <summary>공통 CLI 홈은 유지하고 인증만 교체한다. 보관 파일은 현재 Windows 사용자로 암호화한다.</summary>
public sealed class CliAccountStore
{
    public static CliAccountStore Instance { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "accounts.dat"),
        ConfigHome("CLAUDE_CONFIG_DIR", ".claude"), ConfigHome("CODEX_HOME", ".codex"),
        Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } claudeConfig
            ? claudeConfig : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json"));

    private static string ConfigHome(string variable, string fallback)
        => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback);

    private readonly string _path;
    private readonly string _claudeHome;
    private readonly string _codexHome;
    private readonly object _sync = new();
    public string ClaudeCredentialsPath => Path.Combine(_claudeHome, ".credentials.json");
    public string CodexCredentialsPath => Path.Combine(_codexHome, "auth.json");
    private string ClaudeStatePath { get; }

    public CliAccountStore(string path, string claudeHome, string codexHome, string? claudeStatePath = null)
    {
        (_path, _claudeHome, _codexHome) = (path, claudeHome, codexHome);
        ClaudeStatePath = claudeStatePath ?? claudeHome.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".json";
    }

    public sealed class AccountData
    {
        public List<CliAccount> Accounts { get; set; } = [];
        public Dictionary<string, string> Active { get; set; } = new();
    }

    public AccountData Read()
    {
        lock (_sync)
        {
            if (!File.Exists(_path)) return new();
            var clear = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<AccountData>(clear) ?? throw new InvalidDataException("계정 목록을 읽을 수 없습니다."); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
    }

    private void Save(AccountData data)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(data);
        try { WriteAtomic(_path, ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public bool HasActive(string provider)
    {
        try { return Read().Active.ContainsKey(provider); }
        catch { return File.Exists(_path); } // 보관 파일 손상 시 다른 계정으로 자동 폴백하지 않는다.
    }

    public string Add(CliAccount account)
    {
        Validate(account);
        lock (_sync)
        {
            var data = Read();
            var existing = data.Accounts.FirstOrDefault(a => SameAccount(a, account));
            if (existing != null)
                throw new InvalidOperationException("이미 등록된 계정입니다. 다른 계정으로 로그인하세요.");
            data.Accounts.Add(account);
            Save(data);
            return account.Id;
        }
    }

    private static bool SameAccount(CliAccount left, CliAccount right)
        => left.Provider == right.Provider && (left.Provider == "claude"
            && Guid.TryParse(left.Identity, out var leftId) && Guid.TryParse(right.Identity, out var rightId)
                ? leftId == rightId : string.Equals(left.Identity, right.Identity, StringComparison.Ordinal));

    public AccountData RefreshCurrentAccounts(out List<string> failedProviders)
    {
        lock (_sync)
        {
            Read(); // 보관 파일 오류와 특정 CLI의 읽기 오류를 구분한다.
            failedProviders = [];
            foreach (var provider in new[] { "claude", "codex" })
            {
                try { CaptureCurrent(provider); }
                catch { failedProviders.Add(provider); }
            }
            return Read();
        }
    }

    /// <summary>외부 /login과 CLI 토큰 갱신도 현재 파일에서 다시 읽는다. 알 수 없는 계정으로 덮어쓰지 않는다.</summary>
    public void CaptureCurrent(string provider)
    {
        lock (_sync)
        {
            var current = ReadCurrent(provider);
            if (current == null) return;
            var data = Read();
            var saved = data.Accounts.FirstOrDefault(a => SameAccount(a, current));
            if (saved == null)
            {
                saved = current;
                data.Accounts.Add(saved);
            }
            else
            {
                if (saved.Credentials == current.Credentials && saved.ClaudeAccount == current.ClaudeAccount
                    && saved.Email == current.Email && data.Active.GetValueOrDefault(provider) == saved.Id) return;
                saved.Credentials = current.Credentials;
                saved.ClaudeAccount = current.ClaudeAccount;
                saved.Email = current.Email;
            }
            data.Active[provider] = saved.Id;
            Save(data);
        }
    }

    public CliAccount? ReadCurrent(string provider)
    {
        var path = CredentialPath(provider);
        if (!File.Exists(path)) return null;
        var json = ReadObject(path);
        if (provider == "claude")
        {
            if (json["claudeAiOauth"]?["accessToken"] == null) return null;
            var metadata = File.Exists(ClaudeStatePath) ? ReadObject(ClaudeStatePath)["oauthAccount"]?.ToJsonString() : null;
            var account = JsonNode.Parse(metadata ?? "{}")!;
            // opaque Claude 토큰은 사용자 식별자를 갖지 않는다. 계정 메타데이터가 없으면 임의로 묶지 않는다.
            var identity = account["accountUuid"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(identity)) return null;
            var result = new CliAccount { Provider = provider, Identity = identity,
                Name = "현재 Claude 계정", Email = account["emailAddress"]?.GetValue<string>() ?? "",
                Credentials = json.ToJsonString(), ClaudeAccount = metadata };
            Validate(result);
            return result;
        }
        if (json["tokens"]?["access_token"] == null) return null;
        return FromCodexCredentials(json);
    }

    public void Delete(string id)
    {
        lock (_sync)
        {
            var data = Read();
            var registered = data.Accounts.SingleOrDefault(a => a.Id == id);
            if (registered != null)
            {
                CaptureCurrent(registered.Provider);
                data = Read();
            }
            if (data.Active.Values.Contains(id))
                throw new InvalidOperationException("사용 중인 계정은 삭제할 수 없습니다. 다른 계정으로 변경한 뒤 삭제하세요.");
            if (data.Accounts.RemoveAll(a => a.Id == id) == 0) return;
            Save(data);
        }
    }

    /// <summary>호출자가 세션을 종료한 뒤 호출한다. 실패 시 인증·선택값을 원래 상태로 복원한다.</summary>
    public void ValidateActivation(string provider, string id)
    {
        if (!Path.IsPathFullyQualified(provider == "claude" ? _claudeHome : _codexHome))
            throw new InvalidOperationException("CLI 설정 폴더를 절대 경로로 지정한 뒤 다시 실행하세요.");
        var variables = provider == "claude"
            ? new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN" }
            : new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_AUTH_JSON" };
        if (variables.Any(v => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(v))))
            throw new InvalidOperationException("별도 인증 환경 변수가 설정되어 있어 구독 계정으로 전환할 수 없습니다. 인증 환경 변수를 해제하고 앱을 다시 실행하세요.");
        var target = Read().Accounts.SingleOrDefault(a => a.Id == id && a.Provider == provider)
            ?? throw new InvalidOperationException("등록된 계정을 찾을 수 없습니다.");
        Validate(target);
    }

    public void Activate(string provider, string id)
    {
        lock (_sync)
        {
            ValidateActivation(provider, id);
            CaptureCurrent(provider);
            var data = Read();
            var target = data.Accounts.Single(a => a.Id == id && a.Provider == provider);
            Validate(target);
            var authPath = CredentialPath(provider);
            var configPath = provider == "claude" ? ClaudeStatePath : Path.Combine(_codexHome, "config.toml");
            var oldAuth = File.Exists(authPath) ? File.ReadAllBytes(authPath) : null;
            var oldConfig = File.Exists(configPath) ? File.ReadAllBytes(configPath) : null;
            bool configWritten = false, authWritten = false;
            try
            {
                if (provider == "claude")
                {
                    var state = File.Exists(configPath) ? ReadObject(configPath) : new JsonObject();
                    state["oauthAccount"] = JsonNode.Parse(target.ClaudeAccount!);
                    WriteAtomic(configPath, Encoding.UTF8.GetBytes(state.ToJsonString()));
                }
                else
                {
                    var config = oldConfig == null ? "" : Encoding.UTF8.GetString(oldConfig);
                    WriteAtomic(configPath, Encoding.UTF8.GetBytes(UseFileCredentials(config)));
                }
                configWritten = true;
                WriteAtomic(authPath, Encoding.UTF8.GetBytes(target.Credentials));
                authWritten = true;
                if (ReadCurrent(provider) is not { } activated || !SameAccount(activated, target))
                    throw new InvalidDataException("선택한 계정과 저장된 인증 정보가 일치하지 않습니다.");
                data.Active[provider] = id;
                Save(data);
            }
            catch
            {
                bool restored = true;
                // 쓰기에 실패한 파일은 원본 그대로다. 잠긴 원본의 불필요한 복원 때문에 다른 파일의 복원이 누락되지 않게 한다.
                if (authWritten) { try { Restore(authPath, oldAuth); } catch { restored = false; } }
                if (configWritten) { try { Restore(configPath, oldConfig); } catch { restored = false; } }
                if (!restored) throw new AccountRestoreException();
                throw;
            }
        }
    }

    public static CliAccount FromLogin(string provider, string response, string name = "계정")
    {
        var token = JsonNode.Parse(response)!.AsObject();
        if (provider == "codex")
        {
            var access = token["access_token"]?.GetValue<string>();
            var claims = Claims(access);
            var auth = claims["https://api.openai.com/auth"];
            var credentials = new JsonObject
            {
                ["auth_mode"] = "chatgpt", ["OPENAI_API_KEY"] = null,
                ["tokens"] = new JsonObject
                {
                    ["access_token"] = access, ["refresh_token"] = token["refresh_token"]?.DeepClone(),
                    ["id_token"] = token["id_token"]?.DeepClone(),
                    ["account_id"] = auth?["chatgpt_account_id"]?.DeepClone()
                },
                ["last_refresh"] = DateTimeOffset.UtcNow.ToString("O")
            };
            var result = FromCodexCredentials(credentials);
            result.Name = name;
            return result;
        }
        if (provider != "claude") throw new ArgumentException("지원하지 않는 공급자입니다.");
        var account = token["account"];
        var uuid = account?["uuid"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(uuid)) throw new InvalidDataException("로그인 응답에 Claude 계정 정보가 없습니다.");
        var metadata = new JsonObject
        {
            ["accountUuid"] = uuid, ["emailAddress"] = account?["email_address"]?.DeepClone(),
            ["organizationUuid"] = token["organization"]?["uuid"]?.DeepClone()
        };
        var oauth = new JsonObject
        {
            ["accessToken"] = token["access_token"]?.DeepClone(),
            ["refreshToken"] = token["refresh_token"]?.DeepClone(),
            ["expiresAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (token["expires_in"]?.GetValue<long>() ?? 3600) * 1000,
            ["scopes"] = new JsonArray((token["scope"]?.GetValue<string>() ?? "user:profile user:inference").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray())
        };
        var claude = new CliAccount { Provider = provider, Name = name, Identity = uuid, Email = account?["email_address"]?.GetValue<string>() ?? "",
            ClaudeAccount = metadata.ToJsonString(), Credentials = new JsonObject { ["claudeAiOauth"] = oauth }.ToJsonString() };
        Validate(claude);
        return claude;
    }

    private static CliAccount FromCodexCredentials(JsonObject json)
    {
        var tokens = json["tokens"]!;
        var claims = Claims(tokens["id_token"]?.GetValue<string>());
        var access = Claims(tokens["access_token"]?.GetValue<string>());
        var subject = claims["sub"]?.GetValue<string>() ?? access["sub"]?.GetValue<string>();
        var accountId = tokens["account_id"]?.GetValue<string>() ?? access["https://api.openai.com/auth"]?["chatgpt_account_id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(accountId))
            throw new InvalidDataException("Codex 계정 식별 정보가 없습니다. 다시 로그인하세요.");
        var result = new CliAccount { Provider = "codex", Identity = subject + ":" + accountId,
            Name = "현재 Codex 계정", Email = claims["email"]?.GetValue<string>() ?? "", Credentials = json.ToJsonString() };
        Validate(result);
        return result;
    }

    private static JsonObject Claims(string? jwt)
    {
        if (string.IsNullOrEmpty(jwt)) return new();
        var parts = jwt.Split('.');
        if (parts.Length != 3) return new();
        var value = parts[1].Replace('-', '+').Replace('_', '/');
        return JsonNode.Parse(Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '=')))!.AsObject();
    }

    private static void Validate(CliAccount account)
    {
        if (account.Provider != "claude" && account.Provider != "codex") throw new ArgumentException("지원하지 않는 공급자입니다.");
        if (string.IsNullOrWhiteSpace(account.Name) || account.Name.Length > 80 || string.IsNullOrWhiteSpace(account.Identity))
            throw new InvalidDataException("계정 이름과 식별 정보를 확인하세요.");
        var root = JsonNode.Parse(account.Credentials)!;
        var token = account.Provider == "claude" ? root["claudeAiOauth"]?["accessToken"] : root["tokens"]?["access_token"];
        var refresh = account.Provider == "claude" ? root["claudeAiOauth"]?["refreshToken"] : root["tokens"]?["refresh_token"];
        if (string.IsNullOrWhiteSpace(token?.GetValue<string>()) || string.IsNullOrWhiteSpace(refresh?.GetValue<string>()))
            throw new InvalidDataException("갱신 가능한 로그인 정보가 없습니다. 다시 로그인하세요.");
        if (account.Provider == "claude" && string.IsNullOrWhiteSpace(account.ClaudeAccount))
            throw new InvalidDataException("Claude 계정 정보가 없습니다.");
    }

    private string CredentialPath(string provider) => provider switch
    {
        "claude" => ClaudeCredentialsPath, "codex" => CodexCredentialsPath,
        _ => throw new ArgumentException("지원하지 않는 공급자입니다.")
    };

    // 문자열·주석 안의 TOML처럼 보이는 텍스트는 건드리지 않는다.
    internal static string UseFileCredentials(string config)
    {
        config = config.TrimStart('\uFEFF');
        var masked = Regex.Replace(config,
            "\"\"\"(?:\\\\[\\s\\S]|(?!\"\"\")[\\s\\S])*\"\"\"|'''[\\s\\S]*?'''|\"(?:\\\\.|[^\"\\\\])*\"|'[^'\\r\\n]*'|#[^\\r\\n]*",
            m => m.Value.StartsWith("\"\"\"") || m.Value.StartsWith("'''") || m.Value.StartsWith('#')
                ? new string(m.Value.Select(c => c is '\r' or '\n' ? c : ' ').ToArray()) : m.Value,
            RegexOptions.None, TimeSpan.FromSeconds(1));
        var firstTable = Regex.Match(masked, @"(?m)^[ \t]*\[");
        var rootEnd = firstTable.Success ? firstTable.Index : masked.Length;
        var edits = Regex.Matches(masked,
            "(?m)^[ \\t]*(?:cli_auth_credentials_store|\"cli_auth_credentials_store\"|'cli_auth_credentials_store')[ \\t]*=[^\\r\\n]*").Cast<Match>()
            .Where(m => m.Index < rootEnd || IsProfileOption(masked[..m.Index])).ToArray();
        foreach (var edit in edits.Reverse())
            config = config.Remove(edit.Index, edit.Length).Insert(edit.Index, "cli_auth_credentials_store = \"file\"");
        if (!edits.Any(m => m.Index < rootEnd)) config = "cli_auth_credentials_store = \"file\"\n" + config;
        return config;
    }

    private static bool IsProfileOption(string prefix)
    {
        var tables = Regex.Matches(prefix, @"(?m)^[ \t]*\[([^\r\n]+)\][ \t]*$");
        return tables.Count > 0 && Regex.IsMatch(tables[^1].Groups[1].Value, "^profiles\\.(?:[A-Za-z0-9_-]+|\"[^\"]+\"|'[^']+')$");
    }

    private static JsonObject ReadObject(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonNode.Parse(stream)!.AsObject();
    }

    private static void Restore(string path, byte[]? bytes)
    {
        if (bytes != null) WriteAtomic(path, bytes);
        else if (File.Exists(path)) File.Delete(path);
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(flushToDisk: true); }
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(temp, path, null); // 기존 인증 파일의 접근 권한을 유지한다.
                    else File.Move(temp, path);
                    break;
                }
                catch (IOException ex) when (attempt < 3 && File.Exists(temp)
                    && (ex.HResult & 0xffff) is 32 or 33 or 1175)
                {
                    System.Threading.Thread.Sleep(25 * (attempt + 1));
                }
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed class AccountRestoreException : IOException
{
    public AccountRestoreException() : base("계정 전환에 실패했고 일부 인증 정보를 복구하지 못했습니다. 세션 자동 재시작을 중단했습니다. 파일 잠금을 해제한 뒤 사용할 계정을 다시 선택하세요.") { }
}

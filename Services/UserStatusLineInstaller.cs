using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace DevezCode.Services;

/// <summary>claude code 의 커스텀 statusline(~/.claude/statusline.js) 을 사용자 머신에 보장.
/// 1) Resources\StatusLine\statusline.js 을 ~/.claude\statusline.js 로 복사(없을 때만 — 사용자 수정 보존).
/// 2) ~/.claude\settings.json 의 statusLine 키가 우리 스크립트를 가리키는지 확인,
///    아니면(또는 부재) 다른 설정은 보존한 채 statusLine 만 머지.
/// 다른 PC 에서 DevezCode 첫 실행 시 statusline.js + settings.json statusLine 이 자동 설치되어
/// branch/model/effort/ctx/5h/week/token 표시가 즉시 동작한다.</summary>
public static class UserStatusLineInstaller
{
    private static string ClaudeDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    private static string StatusLineJsPath => Path.Combine(ClaudeDir, "statusline.js");
    private static string StatusLineProxyPath => Path.Combine(ClaudeDir, "devezcode-statusline-proxy.ps1");
    private static string SettingsJsonPath => Path.Combine(ClaudeDir, "settings.json");
    // 로컬 빌드(bin\DevezCode.exe)에서만 쓰는 일회성 화면 확인 마커. 설치본 경로에는 존재하지 않는다.
    private static string PreviewMarkerPath => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "node-install-preview.once"));

    private static string BundledScriptPath => Path.Combine(
        AppContext.BaseDirectory, "Resources", "StatusLine", "statusline.js");

    /// <summary>우리 관리 스크립트 식별 마커. 포함하면 동기화 대상(사용자 수제 스크립트는 미포함).</summary>
    private const string ManagedMarker = "DEVEZCODE-STATUSLINE";

    /// <summary>statusLine 실행에 필요한 node.exe 를 시스템에서 찾을 수 있는지.
    /// false 면 앱 시작 시 하루 한 번 설치 여부를 안내한다.</summary>
    public static bool HasNode() => ResolveNodePath() != null;

    private static string? _cachedNode;
    private static bool _nodeResolved;
    /// <summary>node.exe 절대경로(캐시). 못 찾으면 null. 매 호출 where.exe 재실행 방지.</summary>
    public static string? ResolveNodePath()
    {
        if (_nodeResolved) return _cachedNode;
        _cachedNode = FindNodePath();
        _nodeResolved = true;
        return _cachedNode;
    }

    /// <summary>statusline.js 가 설치돼 있고 settings.json 의 statusLine 이 그 스크립트를 가리키는지.</summary>
    public static bool IsInstalled()
    {
        try
        {
            if (!File.Exists(StatusLineJsPath)) return false;
            if (!File.Exists(SettingsJsonPath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsJsonPath));
            if (!doc.RootElement.TryGetProperty("statusLine", out var sl)) return false;
            if (sl.ValueKind != JsonValueKind.Object) return false;
            if (!sl.TryGetProperty("command", out var cmd)) return false;
            if (cmd.ValueKind != JsonValueKind.String) return false;
            var cmdStr = cmd.GetString() ?? "";
            return cmdStr.Contains("statusline.js", StringComparison.OrdinalIgnoreCase) &&
                   cmdStr.Contains("devezcode-statusline-proxy.ps1", StringComparison.OrdinalIgnoreCase) &&
                   cmdStr.Contains("-WindowStyle Hidden", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>statusline 스크립트와 settings.json 의 statusLine 등록을 보장.
    /// 스크립트는 항상 번들과 동기화(우리 관리 파일이라 색감/로직 갱신 누락 방지), settings 는 없을 때만 머지.</summary>
    public static void EnsureInstalled()
    {
        try { InstallScript(); } catch { /* best effort */ }
        try { EnsureProxyInstalled(); } catch { /* best effort */ }
        if (!IsInstalled()) { try { InstallSettingsEntry(); } catch { /* best effort */ } }
    }

    /// <summary>Node.js가 없고 오늘 아직 설치 안내를 표시하지 않았는지.</summary>
    public static bool ShouldOfferNodeInstallToday()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return SettingsService.LoadLastNodeInstallPromptDate() != today && !HasNode();
    }

    /// <summary>설치/거절/닫기와 무관하게 오늘은 다시 묻지 않도록 기록한다.</summary>
    public static void MarkNodeInstallPromptedToday()
        => SettingsService.SaveLastNodeInstallPromptDate(
            DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary>개발 확인용 일회성 마커를 소비한다. 존재하면 Node.js 설치 여부와 무관하게 안내창을 한 번 띄운다.</summary>
    public static bool ConsumeNodeInstallPreview()
    {
        try
        {
            if (!File.Exists(PreviewMarkerPath)) return false;
            File.Delete(PreviewMarkerPath);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Node.js LTS를 설치하고 성공 즉시 Claude Code 상태줄 설정을 등록한다.</summary>
    public static async Task InstallNodeAndStatusLineAsync()
    {
        if (HasNode())
        {
            EnsureInstalled();
            return;
        }

        using var process = Process.Start(new ProcessStartInfo("winget.exe",
            "install --id OpenJS.NodeJS.LTS --exact --silent --disable-interactivity --accept-package-agreements --accept-source-agreements")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Node.js 설치 프로세스를 시작하지 못했습니다.");

        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Node.js 설치가 실패했습니다. (종료 코드: {process.ExitCode})");

        _cachedNode = null;
        _nodeResolved = false;
        EnsureInstalled();
        if (!IsInstalled())
            throw new InvalidOperationException("Claude Code 상태줄 설정을 적용하지 못했습니다.");
    }

    /// <summary>
    /// 콘솔용 node.exe 를 CreateNoWindow 로 실행하고 stdin/stdout 을 그대로 중계하는 hidden PowerShell
    /// proxy command. Claude 전역/방별 statusLine 이 별도 콘솔 창을 만들지 않게 공용 사용한다.
    /// </summary>
    public static string? TryBuildHiddenNodeCommand(string scriptPath, string? scriptArg = null)
    {
        var node = ResolveNodePath();
        if (node is null) return null;
        try { EnsureProxyInstalled(); } catch { return null; }
        if (!File.Exists(StatusLineProxyPath)) return null;

        var command =
            $"powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden " +
            $"-File \"{StatusLineProxyPath}\" -NodePath \"{node}\" -ScriptPath \"{scriptPath}\"";
        if (!string.IsNullOrEmpty(scriptArg)) command += $" -ScriptArg \"{scriptArg}\"";
        return command;
    }

    private static void EnsureProxyInstalled()
    {
        Directory.CreateDirectory(ClaudeDir);
        // Windows PowerShell 5.1 기본 콘솔/프로세스 인코딩은 CP949(한국어).
        // node 는 UTF-8 로 쓰므로 StandardOutputEncoding 미지정 시 한글 브랜치명 등이 깨진다.
        // stdin/stdout 모두 UTF-8 바이트로 중계한다(문자열 재인코딩 금지).
        const string script = """
            param(
              [Parameter(Mandatory=$true)][string]$NodePath,
              [Parameter(Mandatory=$true)][string]$ScriptPath,
              [string]$ScriptArg = ''
            )
            $ErrorActionPreference = 'SilentlyContinue'
            try {
              $utf8 = New-Object System.Text.UTF8Encoding $false
              $reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8)
              $raw = $reader.ReadToEnd()
              $reader.Dispose()

              $psi = New-Object System.Diagnostics.ProcessStartInfo
              $psi.FileName = $NodePath
              $escapedScript = $ScriptPath.Replace('"', '\"')
              $psi.Arguments = '"' + $escapedScript + '"'
              if ($ScriptArg) { $psi.Arguments += ' "' + $ScriptArg.Replace('"', '\"') + '"' }
              $psi.UseShellExecute = $false
              $psi.CreateNoWindow = $true
              $psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
              $psi.RedirectStandardInput = $true
              $psi.RedirectStandardOutput = $true
              $psi.RedirectStandardError = $true
              $psi.StandardOutputEncoding = $utf8
              $psi.StandardErrorEncoding  = $utf8

              $p = [System.Diagnostics.Process]::Start($psi)
              if (-not $p) { exit 0 }
              $outTask = $p.StandardOutput.ReadToEndAsync()
              $null = $p.StandardError.ReadToEndAsync()
              $inBytes = $utf8.GetBytes($raw)
              $p.StandardInput.BaseStream.Write($inBytes, 0, $inBytes.Length)
              $p.StandardInput.Close()
              $p.WaitForExit()
              $out = $outTask.GetAwaiter().GetResult()
              $outBytes = $utf8.GetBytes($out)
              [Console]::OpenStandardOutput().Write($outBytes, 0, $outBytes.Length)
              exit $p.ExitCode
            } catch { exit 0 }
            """;
        if (ScriptFile.Ps1NeedsWrite(StatusLineProxyPath, script))
            ScriptFile.WritePs1(StatusLineProxyPath, script);
    }

    /// <summary>번들 statusline.js 를 ~/.claude\statusline.js 로 동기화.
    /// 미설치거나, 우리 관리 스크립트(마커/구버전 시그니처)인 기존 파일이 번들과 다르면 덮어쓴다.
    /// 마커 없는 사용자 수제 스크립트는 보존.</summary>
    private static void InstallScript()
    {
        if (!File.Exists(BundledScriptPath)) return;
        Directory.CreateDirectory(ClaudeDir);

        var bundled = File.ReadAllText(BundledScriptPath);
        if (File.Exists(StatusLineJsPath))
        {
            var existing = File.ReadAllText(StatusLineJsPath);
            bool ours = existing.Contains(ManagedMarker) || IsLegacyManaged(existing);
            if (!ours || existing == bundled) return;
        }
        File.WriteAllText(StatusLineJsPath, bundled, new UTF8Encoding(false));
    }

    /// <summary>마커가 없던 구버전 관리 스크립트 식별 — 우리 고유 시그니처로 추정.</summary>
    private static bool IsLegacyManaged(string content)
        => content.Contains("claude-token-counter-") && content.Contains("38;2;229;231;235");

    /// <summary>~/.claude\settings.json 에 statusLine 키를 머지(나머지 설정은 보존).
    /// 스크립트/command 가 우리 statusline.js 를 가리키도록 강제. node.exe 경로는 시스템에서 자동 탐지.</summary>
    private static void InstallSettingsEntry()
    {
        if (!File.Exists(StatusLineJsPath)) return;

        JsonNode? root;
        try
        {
            root = File.Exists(SettingsJsonPath)
                ? JsonNode.Parse(File.ReadAllText(SettingsJsonPath))
                : JsonNode.Parse("{}");
        }
        catch { root = JsonNode.Parse("{}"); }
        if (root is not JsonObject rootObj) rootObj = JsonNode.Parse("{}") as JsonObject ?? new JsonObject();

        var command = TryBuildHiddenNodeCommand(StatusLineJsPath);
        if (command is null) return; // node 가 없으면 statusline 도 의미 없음 — 조용히 스킵

        // refreshInterval 은 의도적으로 넣지 않음 — event-driven 으로 충분,
        // 명시 주기는 idle 중 CPU 낭비.
        rootObj["statusLine"] = new JsonObject
        {
            ["type"] = "command",
            ["command"] = command,
        };

        Directory.CreateDirectory(ClaudeDir);
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(SettingsJsonPath, rootObj.ToJsonString(opts), new UTF8Encoding(false));
    }

    /// <summary>where.exe 로 node.exe 절대경로 탐색, 실패 시 일반 설치 경로 폴백.</summary>
    private static string? FindNodePath()
    {
        try
        {
            var psi = new ProcessStartInfo("where.exe", "node")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            var first = p?.StandardOutput.ReadToEnd().Split('\n').FirstOrDefault()?.Trim();
            p?.WaitForExit(2000);
            if (!string.IsNullOrEmpty(first) && File.Exists(first)) return first;
        }
        catch { }
        foreach (var c in new[] {
            @"C:\Program Files\nodejs\node.exe",
            @"C:\Program Files (x86)\nodejs\node.exe",
        })
            if (File.Exists(c)) return c;
        return null;
    }
}

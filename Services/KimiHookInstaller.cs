using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace DevezCode.Services;

/// <summary>Kimi Code CLI 훅(kimi-hook.ps1 / kimi-state-hook.cmd) 을 사용자 머신에 설치/유지.
/// 1) 스크립트 본문을 %LOCALAPPDATA%\DevezCode\kimi\ 에 항상 최신본으로 기록 (앱 시작 시).
/// 2) ~/.kimi-code/config.toml 의 [[hooks]] 에 우리 훅을 멱등 주입 (다른 훅·설정은 보존).
///
/// Kimi 훅 runner 는 spawn(command, {shell:true, windowsHide:true}) → Windows 에서 cmd.exe 로 실행하고
/// stdin 으로 이벤트 JSON(hook_event_name/session_id/cwd/prompt, snake_case)을 넘긴다. 콘솔창은 안 뜬다.
/// TOML [[hooks]] 는 array-of-tables 라 기존 TomlParser 로 못 읽으므로(무시) 수동 문자열 편집으로 관리한다.</summary>
public static class KimiHookInstaller
{
    public static string ScriptInstallPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevezCode", "kimi", "hook.ps1");

    public static string FastStateScriptInstallPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevezCode", "kimi", "state-hook.cmd");

    /// <summary>Kimi 홈 — $KIMI_CODE_HOME 우선, 기본 ~/.kimi-code.</summary>
    public static string KimiHome
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kimi-code")
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        }
    }

    public static string ConfigTomlPath => Path.Combine(KimiHome, "config.toml");

    private const string ManagedMarker = "# devezcode-managed";

    // (이벤트, 상태인자). 상태인자 null = ps1(SessionStart/UserPromptSubmit/Stop 등),
    // 값 있으면 경량 cmd 훅(툴/권한). PermissionRequest→❗대기.
    private static readonly (string Event, string? StateArg)[] HookSpecs =
    {
        ("SessionStart",     null),
        ("UserPromptSubmit", null),
        ("Stop",             null),
        ("StopFailure",      null),
        ("PreToolUse",       "working"),
        ("PostToolUse",      "working"),
        // SubagentStart only — keep busy=running while a child agent begins.
        // SubagentStop must NOT write idle (main Stop remains the sole idle authority),
        // and must NOT re-arm after a real Stop (would cause stuck-ON).
        ("SubagentStart",    "working"),
        ("PermissionRequest","waiting-on"),
        ("PermissionResult", "waiting-off"),
    };

    /// <summary>스크립트를 설치 경로에 기록 (내용 동일하면 noop). 앱 시작·매 런치에서 호출.</summary>
    public static void EnsureScriptInstalled()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScriptInstallPath)!);
            var content = ReadEmbeddedScript("kimi-hook.ps1", ScriptInstallPath);
            if (ScriptFile.Ps1NeedsWrite(ScriptInstallPath, content))
                AtomicFile.WriteAllText(ScriptInstallPath, content, ScriptFile.Ps1);
            // state-hook.cmd 는 kimi 콘솔을 상속해 실행되므로 chcp 프롤로그를 넣지 않는다.
            // 내용은 ASCII 로만 유지해 인코딩 의존을 없앤다.
            var fast = ReadEmbeddedScript("kimi-state-hook.cmd", FastStateScriptInstallPath);
            if (!File.Exists(FastStateScriptInstallPath) || File.ReadAllText(FastStateScriptInstallPath) != fast)
                AtomicFile.WriteAllText(FastStateScriptInstallPath, fast);
        }
        catch { /* 권한 부족 등 — 무시 */ }
    }

    private static string ReadEmbeddedScript(string fileName, string installPath)
    {
        var asm = Assembly.GetExecutingAssembly();
        var match = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            using var s = asm.GetManifestResourceStream(match)!;
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }
        var src = Path.Combine(AppContext.BaseDirectory, "Resources", "Hooks", fileName);
        if (File.Exists(src)) return File.ReadAllText(src);
        // 설치본 폴백 — .ps1 은 BOM 있는 UTF-8 로 설치되므로 같은 인코딩으로 읽는다(.cmd 는 ASCII).
        return File.ReadAllText(installPath,
            fileName.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ? ScriptFile.Ps1 : ScriptFile.Cmd);
    }

    /// <summary>스크립트 설치 + config.toml [[hooks]] 주입. 멱등. 예외는 삼켜 다음 시작에 재시도.</summary>
    public static void EnsureInstalled()
    {
        EnsureScriptInstalled();
        try { InstallConfigHooks(); } catch { }
    }

    /// <summary>~/.kimi-code/config.toml 에 우리 [[hooks]] 블록을 멱등 주입.
    /// config 가 없거나 비어 있으면(kimi 가 아직 기본 설정을 생성 안 함) 건드리지 않는다 —
    /// provider/model 기본값 없는 config 를 우리가 만들면 kimi 가 깨진다. kimi 가 생성한 뒤 다음 호출에서 주입.</summary>
    public static bool InstallConfigHooks()
    {
        return AtomicFile.TryUpdateAllText(ConfigTomlPath, original =>
        {
            if (string.IsNullOrWhiteSpace(original)) return null; // 생성/클로버 금지
            var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var lines = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

            var stripped = StripOurBlocks(lines);
            while (stripped.Count > 0 && stripped[^1].Trim().Length == 0) stripped.RemoveAt(stripped.Count - 1);

            var sb = new StringBuilder();
            if (stripped.Count > 0)
            {
                sb.Append(string.Join(newline, stripped));
                sb.Append(newline).Append(newline);
            }
            sb.Append(BuildManagedBlocks(newline));
            return sb.ToString();
        });
    }

    /// <summary>기존 우리 [[hooks]] 블록 제거 — 마커 유무와 무관하게 command 가 우리 스크립트 경로를
    /// 가리키면 제거(중복 누적 방지). 사용자·타 플러그인 훅은 보존.</summary>
    private static List<string> StripOurBlocks(List<string> lines)
    {
        var outLines = new List<string>();
        int i = 0;
        while (i < lines.Count)
        {
            if (lines[i].TrimStart().StartsWith("[[hooks]]", StringComparison.OrdinalIgnoreCase))
            {
                int start = i, j = i + 1;
                var block = new StringBuilder(lines[i]);
                while (j < lines.Count && !lines[j].TrimStart().StartsWith("[", StringComparison.Ordinal))
                {
                    block.Append('\n').Append(lines[j]);
                    j++;
                }
                bool markerAbove = outLines.Count > 0 &&
                    outLines[^1].Trim().Equals(ManagedMarker, StringComparison.OrdinalIgnoreCase);
                if (IsOurBlock(block.ToString()) || markerAbove)
                {
                    if (markerAbove) outLines.RemoveAt(outLines.Count - 1);
                    i = j;
                    continue;
                }
                for (int k = start; k < j; k++) outLines.Add(lines[k]);
                i = j;
                continue;
            }
            outLines.Add(lines[i]);
            i++;
        }
        return outLines;
    }

    private static bool IsOurBlock(string block)
    {
        var norm = block.Replace('/', '\\');
        return Contains(norm, ScriptInstallPath)
            || Contains(norm, FastStateScriptInstallPath)
            || Contains(norm, ToCmdArgSafePath(ScriptInstallPath))
            || Contains(norm, ToCmdArgSafePath(FastStateScriptInstallPath));

        static bool Contains(string hay, string needle) =>
            hay.IndexOf(needle.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string BuildManagedBlocks(string newline)
    {
        var sb = new StringBuilder();
        foreach (var (ev, arg) in HookSpecs)
        {
            sb.Append(ManagedMarker).Append(newline);
            sb.Append("[[hooks]]").Append(newline);
            sb.Append("event = ").Append(TomlString(ev)).Append(newline);
            sb.Append("command = ").Append(TomlString(BuildCommand(arg))).Append(newline);
            sb.Append("timeout = 15").Append(newline);
            sb.Append(newline);
        }
        return sb.ToString();
    }

    /// <summary>TOML 문자열 리터럴. 작은따옴표·개행이 없으면 literal('...') 로 백슬래시 escape 회피(경로 안전).
    /// 그 외에는 basic("...") 로 백슬래시·따옴표 escape.</summary>
    private static string TomlString(string s)
    {
        if (!s.Contains('\'') && !s.Contains('\n') && !s.Contains('\r'))
            return "'" + s + "'";
        var esc = s.Replace("\\", "\\\\", StringComparison.Ordinal)
                   .Replace("\"", "\\\"", StringComparison.Ordinal);
        return "\"" + esc + "\"";
    }

    private static string BuildCommand(string? stateArg)
        => stateArg == null ? BuildPs1Command() : BuildFastStateHookCommand(stateArg);

    private static string BuildPs1Command()
    {
        // Kimi 훅은 cmd.exe(shell:true)로 실행되므로 8.3 무공백 경로를 따옴표 없이 넘겨 중첩 따옴표 파싱을 피한다.
        var path = ToCmdArgSafePath(ScriptInstallPath);
        if (!path.Contains(' '))
            return $"powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File {path}";
        var escaped = ScriptInstallPath.Replace("'", "''", StringComparison.Ordinal);
        var script = $"& '{escaped}'";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return $"powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}";
    }

    private static string BuildFastStateHookCommand(string state)
    {
        var path = ToCmdArgSafePath(FastStateScriptInstallPath);
        if (!path.Contains(' ')) return $"cmd.exe /d /c call {path} {state}";
        var escaped = FastStateScriptInstallPath.Replace("'", "''", StringComparison.Ordinal);
        var script = $"& '{escaped}' {state}";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return $"powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}";
    }

    private static string ToCmdArgSafePath(string path)
    {
        if (!path.Contains(' ')) return path;
        try
        {
            var buffer = new StringBuilder(260);
            if (GetShortPathName(path, buffer, buffer.Capacity) > 0 && buffer.Length > 0)
                return buffer.ToString();
        }
        catch { }
        return path;
    }

    [System.Runtime.InteropServices.DllImport(
        "kernel32", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern int GetShortPathName(string longPath, StringBuilder shortPath, int bufferSize);

    /// <summary>훅 사용 가능 여부 — 스크립트 설치 + config.toml 에 우리 관리 블록 존재.</summary>
    public static bool HookAssetsHealthy()
    {
        if (!File.Exists(ScriptInstallPath) || !File.Exists(FastStateScriptInstallPath)) return false;
        try
        {
            if (!File.Exists(ConfigTomlPath)) return false;
            var text = File.ReadAllText(ConfigTomlPath);
            var markers = Regex.Matches(text, Regex.Escape(ManagedMarker)).Count;
            return markers >= HookSpecs.Length;
        }
        catch { return false; }
    }
}

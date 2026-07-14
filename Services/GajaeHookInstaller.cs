using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace DevezCode.Services;

/// <summary>GJC의 명시적 <c>--hook</c>으로 로드할 방 상태 추적 훅을 설치한다.
/// 훅 옵션이 없는 구버전은 capability probe에서 제외되어 기존 JSONL 폴링만 사용한다.</summary>
public static class GajaeHookInstaller
{
    private static readonly Lazy<bool> ExplicitHookSupported = new(DetectExplicitHookSupport);

    public static string ScriptInstallPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DevezCode", "gajae", "hook", "devezcode-room-tracker.js");

    public static string StateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "gajae", "hook-state");

    /// <summary>설치와 지원 여부 확인에 성공하면 배치에 넣을 명시적 훅 인자를 반환한다.
    /// 실패/구버전이면 빈 문자열 — GJC 실행 인자를 바꾸지 않아 종전 폴링 경로가 그대로 동작한다.</summary>
    public static string BuildExplicitHookArgument()
    {
        try
        {
            EnsureInstalled();
            return File.Exists(ScriptInstallPath) && ExplicitHookSupported.Value
                ? $" --hook \"{ScriptInstallPath}\""
                : "";
        }
        catch { return ""; }
    }

    public static void EnsureInstalled()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ScriptInstallPath)!);
        Directory.CreateDirectory(StateDir);
        var content = ReadBundledScript();
        if (!File.Exists(ScriptInstallPath) || File.ReadAllText(ScriptInstallPath) != content)
            AtomicFile.WriteAllText(ScriptInstallPath, content);
    }

    private static string ReadBundledScript()
    {
        var asm = Assembly.GetExecutingAssembly();
        var match = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("gajae-room-tracker.js", StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            using var stream = asm.GetManifestResourceStream(match)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "Resources", "Hooks", "gajae-room-tracker.js");
        if (File.Exists(bundled)) return File.ReadAllText(bundled);
        return File.ReadAllText(ScriptInstallPath);
    }

    private static bool DetectExplicitHookSupport()
    {
        try
        {
            // 버전 번호 임계값 대신 실제 CLI capability를 확인한다. 포크/배포판도 --hook이 있으면
            // 사용하고, 구버전은 정확히 종전 인자로 실행한다. 한 프로세스에서 최초 1회만 수행(~0.2s).
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/d /s /c \"gjc --help\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var process = Process.Start(psi);
            if (process == null) return false;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(1500))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            if (!Task.WaitAll(new Task[] { stdout, stderr }, 500)) return false;
            return (stdout.Result + stderr.Result).Contains("--hook", StringComparison.Ordinal);
        }
        catch { return false; }
    }
}

using System.IO;

namespace DevezCode.Services.Terminal;

/// <summary>claude CLI 설치 여부 감지 (PATH 탐색). 결과는 프로세스 수명 동안 캐시.</summary>
public static class ClaudeCliDetector
{
    private static bool? _installed;
    private static string? _resolvedPath;
    private static bool _resolveAttempted;

    // npm 전역(claude.cmd/.ps1), native installer(claude.exe), WSL/git-bash(claude) 등 변형 모두 커버
    private static readonly string[] Names =
        { "claude.cmd", "claude.exe", "claude.bat", "claude.ps1", "claude" };

    // stream-json 직접 spawn(인앱 AI 채팅)에는 셸 경유 없는 .exe 가 필요 — .exe 를 우선한다.
    private static readonly string[] ExeFirstNames =
        { "claude.exe", "claude.cmd", "claude.bat", "claude" };

    /// <summary>claude 실행 파일의 전체 경로. 못 찾으면 null. (.exe 우선 — Process 직접 spawn 용)</summary>
    public static string? ResolvePath()
    {
        if (_resolveAttempted) return _resolvedPath;
        _resolveAttempted = true;
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var name in ExeFirstNames)
            {
                foreach (var dir in path.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    try
                    {
                        var full = Path.Combine(dir.Trim(), name);
                        if (File.Exists(full)) { _resolvedPath = full; return _resolvedPath; }
                    }
                    catch (Exception) { /* 잘못된 경로 무시 */ }
                }
            }
        }
        catch (Exception) { _resolvedPath = null; }
        return _resolvedPath;
    }

    /// <summary>claude 실행 파일이 PATH 어딘가에 있으면 true.</summary>
    public static bool IsInstalled()
    {
        if (_installed is bool cached) return cached;

        bool found = false;
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                foreach (var name in Names)
                {
                    try { if (File.Exists(Path.Combine(dir.Trim(), name))) { found = true; break; } }
                    catch (Exception) { /* 잘못된 경로 무시 */ }
                }
                if (found) break;
            }
        }
        catch (Exception) { found = false; }

        _installed = found;
        return found;
    }
}

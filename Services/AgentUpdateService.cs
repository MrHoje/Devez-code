using System.Diagnostics;
using System.IO;
using System.Text;

namespace DevezCode.Services;

/// <summary>앱 시작 시, 설정에서 켜져 있고 실제 설치된 에이전트 CLI 를 최신 버전으로 자동 업데이트한다.
/// 각 에이전트의 자체 업데이터/패키지 매니저 명령(<see cref="AgentDef.UpdateCommand"/>)을 PowerShell 로
/// 백그라운드 실행 — 시작을 블로킹하지 않는다(fire-and-forget). 업데이트 명령은 이미 최신이면 no-op 이라
/// 매 실행 호출해도 안전. 실제로 버전이 바뀐 에이전트만 토스트로 알린다.
///
/// 주의: 실행 중인 exe 는 Windows 에서 덮어쓸 수 없으므로, 세션이 이미 해당 CLI 를 띄운 뒤라면
/// 업데이트가 실패할 수 있다(best-effort — 실패는 로그만 남기고 다음 실행 때 재시도).</summary>
public static class AgentUpdateService
{
    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "agent-update.log");

    /// <summary>켜진+설치된 에이전트를 병렬로 최신화. 전체 실패는 무시(best-effort).</summary>
    public static async Task UpdateEnabledAgentsAsync()
    {
        try
        {
            var agents = AgentRegistry.GetEnabledAndInstalled()
                .Where(a => !string.IsNullOrWhiteSpace(a.UpdateCommand))
                .ToList();
            if (agents.Count == 0) return;

            Log($"=== 자동 업데이트 시작 ({agents.Count}개): {string.Join(", ", agents.Select(a => a.Id))} ===");
            await Task.WhenAll(agents.Select(UpdateOneAsync));
        }
        catch (Exception ex) { Log($"UpdateEnabledAgentsAsync ERROR: {ex.Message}"); }
    }

    private static async Task UpdateOneAsync(AgentDef agent)
    {
        try
        {
            var before = await GetVersionAsync(agent);
            await RunShellAsync(agent.UpdateCommand, TimeSpan.FromMinutes(3));
            var after = await GetVersionAsync(agent);

            Log($"{agent.Id}: '{before}' -> '{after}'");

            // 실제 버전 문자열이 달라졌을 때만(=진짜 업데이트됨) 사용자에게 알림. no-op 은 조용히.
            if (!string.IsNullOrEmpty(after) && !string.IsNullOrEmpty(before) &&
                !string.Equals(before, after, StringComparison.Ordinal))
            {
                DevezCode.App.ShowNotification($"{agent.DisplayName} 업데이트됨", $"{before} → {after}");
            }
        }
        catch (Exception ex) { Log($"{agent.Id}: ERROR {ex.Message}"); }
    }

    /// <summary><c>&lt;command&gt; --version</c> 을 PowerShell 로 실행해 첫 줄(버전 문자열)을 얻는다.
    /// 실패/타임아웃 시 빈 문자열.</summary>
    private static async Task<string> GetVersionAsync(AgentDef agent)
    {
        // & 호출 연산자로 PATH 상의 shim(.cmd/.ps1/.exe)을 그대로 해석.
        var (_, output) = await RunShellCaptureAsync($"& {agent.Command} --version", TimeSpan.FromSeconds(30));
        var firstLine = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        return firstLine.Trim();
    }

    private static Task RunShellAsync(string command, TimeSpan timeout)
        => RunShellCaptureAsync(command, timeout);

    /// <summary>PowerShell 로 명령을 실행하고 (종료코드, stdout+stderr) 를 반환. 창 숨김, 타임아웃 시 강제 종료.</summary>
    private static async Task<(int code, string output)> RunShellCaptureAsync(string command, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);

        using var proc = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };

        if (!proc.Start()) return (-1, "");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = new System.Threading.CancellationTokenSource(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            lock (sb) return (-1, sb.ToString());
        }
        lock (sb) return (proc.ExitCode, sb.ToString());
    }

    private static void Log(string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, line, new UTF8Encoding(false));
        }
        catch { /* 로깅 실패는 무시 */ }
    }
}

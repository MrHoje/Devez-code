using System.Diagnostics;
using System.IO;
using System.Text;

namespace DevezCode.Services;

/// <summary>앱 시작 시(또는 설정의 '즉시 업데이트'), 켜져 있고 실제 설치된 에이전트 CLI 를 최신 버전으로
/// 업데이트한다. 각 에이전트의 자체 업데이터/패키지 매니저 명령(<see cref="AgentDef.UpdateCommand"/>)을
/// PowerShell 로 실행. 업데이트 명령은 이미 최신이면 no-op 이라 매 실행 호출해도 안전.
///
/// 견고성(중단 안전):
///  - 설치 프로세스는 부모(앱)와 <b>파이프로 연결하지 않고</b>, 셸 안에서 출력을 파일로 리다이렉트한다.
///    따라서 사용자가 '건너뛰고 시작'을 누르거나 앱을 통째로 닫아도, 설치 프로세스는 끊기지 않고
///    <b>독립적으로 끝까지 완료</b>된다(npm/bun 반쪽 설치 방지).
///  - 타임아웃 시에도 프로세스를 <b>죽이지 않는다</b>(설치 중 강제 종료가 손상을 유발하므로). 기다림만 멈춘다.
///
/// 남는 경미한 리스크: 세션이 이미 해당 exe 를 띄운 뒤면 Windows 파일 잠금으로 덮어쓰기가 실패할 수 있으나,
/// npm/bun/claude 는 temp→rename/staging 방식이라 구버전이 그대로 유지되고 다음 실행 때 재시도된다(안전 실패).</summary>
public static class AgentUpdateService
{
    private static string DevezDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode");

    /// <summary>앱 측 요약 로그(에이전트별 before→after, 상태).</summary>
    private static string LogPath => Path.Combine(DevezDir, "agent-update.log");

    /// <summary>설치 프로세스의 원시 출력 로그(에이전트별). 병렬 append 충돌을 막기 위해 파일을 분리한다.</summary>
    private static string DetachedLogPath(AgentDef agent) => Path.Combine(DevezDir, $"agent-update-{agent.Id}.log");

    /// <summary>켜진+설치된 에이전트를 병렬로 최신화. 전체 실패는 무시(best-effort).</summary>
    /// <param name="report">진행 상황 콜백(모달 로그용). null 이면 조용히 실행하고 실제 업데이트 시 토스트로 알림.</param>
    public static async Task UpdateEnabledAgentsAsync(Action<string>? report = null)
    {
        try
        {
            var agents = AgentRegistry.GetEnabledAndInstalled()
                .Where(a => !string.IsNullOrWhiteSpace(a.UpdateCommand))
                .ToList();
            if (agents.Count == 0)
            {
                report?.Invoke("업데이트할 에이전트가 없습니다.");
                return;
            }

            Log($"=== 자동 업데이트 시작 ({agents.Count}개): {string.Join(", ", agents.Select(a => a.Id))} ===");
            report?.Invoke($"대상 {agents.Count}개: {string.Join(", ", agents.Select(a => a.DisplayName))}");
            await Task.WhenAll(agents.Select(a => UpdateOneAsync(a, report)));
            report?.Invoke("완료되었습니다.");
        }
        catch (Exception ex)
        {
            Log($"UpdateEnabledAgentsAsync ERROR: {ex.Message}");
            report?.Invoke($"오류: {ex.Message}");
        }
    }

    private static async Task UpdateOneAsync(AgentDef agent, Action<string>? report)
    {
        try
        {
            report?.Invoke($"{agent.DisplayName} 확인 중…");
            var before = await GetVersionAsync(agent);

            // 설치는 파이프에 의존하지 않는 분리 실행 — 앱을 닫아도 끊기지 않고 독립적으로 완료된다.
            var exited = await RunDetachedUpdateAsync(agent, TimeSpan.FromMinutes(5));
            if (!exited)
            {
                // 아직 진행 중(타임아웃) — 죽이지 않고 백그라운드로 계속 완료되게 둔다.
                Log($"{agent.Id}: 아직 진행 중(분리 실행) — 백그라운드로 계속됨");
                report?.Invoke($"{agent.DisplayName}: 백그라운드에서 계속 진행 중…");
                return;
            }

            var after = await GetVersionAsync(agent);
            Log($"{agent.Id}: '{before}' -> '{after}'");

            bool changed = !string.IsNullOrEmpty(after) && !string.IsNullOrEmpty(before) &&
                           !string.Equals(before, after, StringComparison.Ordinal);

            if (changed)
            {
                report?.Invoke($"{agent.DisplayName}: {before} → {after} (업데이트됨)");
                // 모달 로그가 없을 때(백그라운드)만 토스트 — 모달이 있으면 중복 방지.
                if (report == null)
                    DevezCode.App.ShowNotification($"{agent.DisplayName} 업데이트됨", $"{before} → {after}");
            }
            else
            {
                var v = string.IsNullOrEmpty(after) ? "" : $" ({after})";
                report?.Invoke($"{agent.DisplayName}: 최신{v}");
            }
        }
        catch (Exception ex)
        {
            Log($"{agent.Id}: ERROR {ex.Message}");
            report?.Invoke($"{agent.DisplayName}: 실패 ({ex.Message})");
        }
    }

    /// <summary>업데이트(설치) 명령을 분리 실행한다. 출력은 셸 안에서 파일(<see cref="DetachedLogPath"/>)로
    /// 리다이렉트하므로 부모 앱과 파이프로 연결되지 않는다 → 앱 종료/건너뛰기에도 설치가 끊기지 않는다.
    /// 반환값: 타임아웃 안에 종료되면 true, 아직 진행 중이면 false(프로세스는 죽이지 않음).</summary>
    private static async Task<bool> RunDetachedUpdateAsync(AgentDef agent, TimeSpan timeout)
    {
        var logPath = DetachedLogPath(agent);
        try { Directory.CreateDirectory(DevezDir); } catch { }

        // PowerShell 리터럴 문자열 안의 작은따옴표는 '' 로 이스케이프.
        var escapedLog = logPath.Replace("'", "''");
        // 모든 스트림(*)을 파일로 append. 부모에 파이프 리다이렉트를 걸지 않는 것이 이 보강의 핵심.
        var command = $"& {{ {agent.UpdateCommand} }} *>> '{escapedLog}'";

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            // RedirectStandardOutput/Error 를 설정하지 않는다(파이프 미연결) → 앱 종료 시 자식이 안 죽음.
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);

        var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start()) return false;

            using var cts = new System.Threading.CancellationTokenSource(timeout);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                // 타임아웃 — Kill 하지 않는다(설치 중 강제 종료 = 손상 위험). 백그라운드로 계속 진행.
                return false;
            }
        }
        finally
        {
            // Dispose 는 핸들만 닫고 프로세스는 죽이지 않는다(분리 실행 유지).
            try { proc.Dispose(); } catch { }
        }
    }

    /// <summary><c>&lt;command&gt; --version</c> 을 PowerShell 로 실행해 첫 줄(버전 문자열)을 얻는다.
    /// 버전 조회는 짧고 설치가 아니므로 파이프 캡처 + 타임아웃 시 종료해도 안전하다. 실패 시 빈 문자열.</summary>
    private static async Task<string> GetVersionAsync(AgentDef agent)
    {
        // & 호출 연산자로 PATH 상의 shim(.cmd/.ps1/.exe)을 그대로 해석.
        var output = await RunShellCaptureAsync($"& {agent.Command} --version", TimeSpan.FromSeconds(30));
        var firstLine = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        return firstLine.Trim();
    }

    /// <summary>짧은 조회용 — PowerShell 실행 후 stdout+stderr 를 캡처해 반환. 타임아웃 시 종료.</summary>
    private static async Task<string> RunShellCaptureAsync(string command, TimeSpan timeout)
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

        if (!proc.Start()) return "";
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
            lock (sb) return sb.ToString();
        }
        lock (sb) return sb.ToString();
    }

    private static void Log(string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            Directory.CreateDirectory(DevezDir);
            File.AppendAllText(LogPath, line, new UTF8Encoding(false));
        }
        catch { /* 로깅 실패는 무시 */ }
    }
}

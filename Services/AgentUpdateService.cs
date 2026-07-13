using System.Diagnostics;
using System.IO;
using System.Text;

namespace DevezCode.Services;

/// <summary>에이전트 1개의 업데이트 판정 상태.
///  - Updated   : 실행 전/후 --version 문자열이 다름 → 실제 교체 확정(가장 신뢰할 수 있는 근거).
///  - UpToDate  : 종료코드 0 + 버전 동일(no-op).
///  - Failed    : 설치 프로세스 종료코드 != 0 또는 예외.
///  - InProgress: 타임아웃으로 대기만 끝냄 — 설치는 백그라운드로 계속(완료 시점에 별도 토스트).</summary>
public enum AgentUpdateStatus { Updated, UpToDate, Failed, InProgress }

/// <summary>에이전트 1개의 업데이트 결과. 시작 시퀀스의 완료 토스트/설정 모달 로그 공용.</summary>
public sealed record AgentUpdateResult(
    string Id, string DisplayName, string Before, string After,
    AgentUpdateStatus Status, int ExitCode);

/// <summary>앱 시작 시(또는 설정의 '즉시 업데이트'), 켜져 있고 실제 설치된 에이전트 CLI 를 최신 버전으로
/// 업데이트한다. 각 에이전트의 자체 업데이터/패키지 매니저 명령(<see cref="AgentDef.UpdateCommand"/>)을
/// PowerShell 로 실행. 업데이트 명령은 이미 최신이면 no-op 이라 매 실행 호출해도 안전.
///
/// 결과 판정: 실행 전/후 --version 비교(Updated) + 설치 프로세스 종료코드(Failed) 조합.
/// 호출자는 반환된 <see cref="AgentUpdateResult"/> 목록으로 성공/실패 팝업을 띄운다.
///
/// 견고성(중단 안전):
///  - 설치 프로세스는 부모(앱)와 <b>파이프로 연결하지 않고</b>, 셸 안에서 출력을 파일로 리다이렉트한다.
///    따라서 사용자가 '건너뛰고 시작'을 누르거나 앱을 통째로 닫아도, 설치 프로세스는 끊기지 않고
///    <b>독립적으로 끝까지 완료</b>된다(npm/bun 반쪽 설치 방지).
///  - 타임아웃 시에도 프로세스를 <b>죽이지 않는다</b>(설치 중 강제 종료가 손상을 유발하므로). 기다림만 멈추고
///    완주를 백그라운드로 감시해 끝나면 최종 성공/실패를 토스트로 알린다.
///
/// 깨짐 복구(B/C): npm/bun 전역 설치는 다단계(패키지 추출→shim 링크→optional 네이티브)라, 파일 잠금·백신·
/// optional 누락으로 <b>중간에 끊기면 구버전 유지가 아니라 잡탕(temp-rename 잔재 + 네이티브 누락) 상태로 남을 수
/// 있다</b>(자체 업데이터 claude/grok/opencode 는 원자적 교체라 대체로 안전). 이를 방어하기 위해:
///  - B: 설치 직후 <c>before 정상 → after 실행불가</c> 퇴행을 감지하면 같은 명령을 1회 재실행해 회복
///       (<see cref="TryRepairAsync"/>). npm/bun 이 누락분을 다시 채워 사용 가능 상태로 돌아온다.
///  - C: 그래도 실패(Failed)면 호출부(App)가 데일리 게이트를 비워 다음 실행에서 재시도 → 조용히 하루 방치 없음.</summary>
public static class AgentUpdateService
{
    private static string DevezDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode");

    /// <summary>앱 측 요약 로그(에이전트별 before→after, 종료코드, 상태).</summary>
    private static string LogPath => Path.Combine(DevezDir, "agent-update.log");

    /// <summary>설치 프로세스의 원시 출력 로그(에이전트별). 병렬 append 충돌을 막기 위해 파일을 분리한다.</summary>
    private static string DetachedLogPath(string agentId) => Path.Combine(DevezDir, $"agent-update-{agentId}.log");

    /// <summary>실패 토스트 클릭 시 원시 설치 로그를 기본 편집기로 연다.</summary>
    public static void OpenDetachedLog(string agentId)
    {
        try
        {
            var path = DetachedLogPath(agentId);
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { /* 뷰어 실행 실패는 무시 */ }
    }

    /// <summary>켜진+설치된 에이전트를 병렬로 최신화하고 에이전트별 결과를 반환. 전체 실패는 무시(best-effort).</summary>
    /// <param name="report">진행 상황 콜백(모달 로그용). null 이어도 결과는 반환된다.</param>
    public static async Task<IReadOnlyList<AgentUpdateResult>> UpdateEnabledAgentsAsync(Action<string>? report = null)
    {
        var results = new List<AgentUpdateResult>();
        try
        {
            var agents = AgentRegistry.GetEnabledAndInstalled()
                .Where(a => !string.IsNullOrWhiteSpace(a.UpdateCommand))
                .ToList();
            if (agents.Count == 0)
            {
                report?.Invoke("업데이트할 에이전트가 없습니다.");
                return results;
            }

            Log($"=== 자동 업데이트 시작 ({agents.Count}개): {string.Join(", ", agents.Select(a => a.Id))} ===");
            report?.Invoke($"대상 {agents.Count}개: {string.Join(", ", agents.Select(a => a.DisplayName))}");
            results.AddRange(await Task.WhenAll(agents.Select(a => UpdateOneAsync(a, report))));
            report?.Invoke("완료되었습니다.");
        }
        catch (Exception ex)
        {
            Log($"UpdateEnabledAgentsAsync ERROR: {ex.Message}");
            report?.Invoke($"오류: {ex.Message}");
        }
        return results;
    }

    private static async Task<AgentUpdateResult> UpdateOneAsync(AgentDef agent, Action<string>? report)
    {
        var before = "";
        try
        {
            report?.Invoke($"{agent.DisplayName} 확인 중…");
            before = await GetVersionAsync(agent);

            // 설치는 파이프에 의존하지 않는 분리 실행 — 앱을 닫아도 끊기지 않고 독립적으로 완료된다.
            var (exited, exitCode, running) = await RunDetachedUpdateAsync(agent, TimeSpan.FromMinutes(5));
            if (!exited)
            {
                // 아직 진행 중(타임아웃) — 죽이지 않고 완주를 감시, 끝나면 최종 결과를 토스트로 알린다.
                Log($"{agent.Id}: 아직 진행 중(분리 실행) — 백그라운드로 계속됨");
                report?.Invoke($"{agent.DisplayName}: 백그라운드에서 계속 진행 중…");
                if (running != null) _ = WatchDetachedCompletionAsync(agent, running, before);
                return new(agent.Id, agent.DisplayName, before, "", AgentUpdateStatus.InProgress, 0);
            }

            var after = await GetVersionAsync(agent);
            Log($"{agent.Id}: '{before}' -> '{after}' (exit {exitCode})");

            // B(퇴행 자동복구): before 는 정상인데 after 가 비었으면(--version 실패) 이번 설치가 동작하던
            // 에이전트를 망가뜨린 것(예: npm shim temp-rename 실패 + optional 네이티브 누락). 같은 명령을
            // 1회 재실행해 회복을 시도한다(수동 재설치로 즉시 복구되는 것과 동일 원리). 깨졌을 때만 비용 발생.
            if (!string.IsNullOrEmpty(before) && string.IsNullOrEmpty(after))
                return await TryRepairAsync(agent, before, report);

            // 버전 변화가 최우선 근거 — 경고(예: temp 정리 EPERM)로 종료코드가 더러워져도 교체됐으면 성공.
            bool changed = !string.IsNullOrEmpty(after) && !string.IsNullOrEmpty(before) &&
                           !string.Equals(before, after, StringComparison.Ordinal);
            if (changed)
            {
                report?.Invoke($"{agent.DisplayName}: {before} → {after} (업데이트됨)");
                return new(agent.Id, agent.DisplayName, before, after, AgentUpdateStatus.Updated, exitCode);
            }
            if (exitCode != 0)
            {
                report?.Invoke($"{agent.DisplayName}: 실패 (종료 코드 {exitCode}) — 로그: agent-update-{agent.Id}.log");
                return new(agent.Id, agent.DisplayName, before, after, AgentUpdateStatus.Failed, exitCode);
            }
            var v = string.IsNullOrEmpty(after) ? "" : $" ({after})";
            report?.Invoke($"{agent.DisplayName}: 최신{v}");
            return new(agent.Id, agent.DisplayName, before, after, AgentUpdateStatus.UpToDate, exitCode);
        }
        catch (Exception ex)
        {
            Log($"{agent.Id}: ERROR {ex.Message}");
            report?.Invoke($"{agent.DisplayName}: 실패 ({ex.Message})");
            return new(agent.Id, agent.DisplayName, before, "", AgentUpdateStatus.Failed, -1);
        }
    }

    /// <summary>B: 설치 직후 퇴행(before 정상 → after 실행불가) 감지 시 같은 <see cref="AgentDef.UpdateCommand"/> 를
    /// <b>1회</b> 재실행해 회복을 시도한다. npm/bun 은 누락된 shim/네이티브 바이너리를 다시 채우므로, 원인(락·백신·
    /// optional 누락)과 무관하게 사용 가능 상태로 되돌아온다(우리가 수동 재설치로 즉시 복구된 것과 동일).
    ///  - 회복 성공: 버전 나오면 Updated(교체됨)/UpToDate(같은 버전으로 복구).
    ///  - 회복 실패(여전히 깨짐): Failed 로 반환 → 호출부(App)가 데일리 게이트를 비워 <b>다음 실행에서 재시도</b>.
    ///  - 복구가 타임아웃(느림): 죽이지 않고 완주 감시만 걸고 InProgress 반환.</summary>
    private static async Task<AgentUpdateResult> TryRepairAsync(AgentDef agent, string before, Action<string>? report)
    {
        report?.Invoke($"{agent.DisplayName}: 설치 후 실행 불가 — 자동 복구 시도 중…");
        Log($"{agent.Id}: 퇴행 감지(before='{before}', after=''), '{agent.UpdateCommand}' 1회 재실행");

        var (exited, exitCode, running) = await RunDetachedUpdateAsync(agent, TimeSpan.FromMinutes(5));
        if (!exited)
        {
            Log($"{agent.Id}: 복구 진행 중(분리 실행) — 백그라운드로 계속됨");
            report?.Invoke($"{agent.DisplayName}: 복구를 백그라운드에서 계속 진행 중…");
            if (running != null) _ = WatchDetachedCompletionAsync(agent, running, before);
            return new(agent.Id, agent.DisplayName, before, "", AgentUpdateStatus.InProgress, 0);
        }

        var after = await GetVersionAsync(agent);
        Log($"{agent.Id}: 복구 결과 '{before}' -> '{after}' (exit {exitCode})");

        if (!string.IsNullOrEmpty(after))
        {
            report?.Invoke($"{agent.DisplayName}: 자동 복구됨 ({after})");
            bool changed = !string.Equals(before, after, StringComparison.Ordinal);
            return new(agent.Id, agent.DisplayName, before, after,
                       changed ? AgentUpdateStatus.Updated : AgentUpdateStatus.UpToDate, exitCode);
        }

        report?.Invoke($"{agent.DisplayName}: 자동 복구 실패 — 다음 실행에서 재시도 (로그: agent-update-{agent.Id}.log)");
        return new(agent.Id, agent.DisplayName, before, "", AgentUpdateStatus.Failed, exitCode == 0 ? -1 : exitCode);
    }

    /// <summary>타임아웃으로 넘긴 분리 설치의 완주를 감시한다(앱이 살아있는 동안).
    /// 끝나면 버전 재확인으로 성공/실패를 판정해 결과 모달(중앙)로 알린다. '최신'(no-op)은 무음.</summary>
    private static async Task WatchDetachedCompletionAsync(AgentDef agent, Process proc, string before)
    {
        try
        {
            await proc.WaitForExitAsync();
            int exitCode = proc.ExitCode;
            var after = await GetVersionAsync(agent);
            Log($"{agent.Id}: 지연 완료 '{before}' -> '{after}' (exit {exitCode})");

            bool changed = !string.IsNullOrEmpty(after) && !string.IsNullOrEmpty(before) &&
                           !string.Equals(before, after, StringComparison.Ordinal);
            // 퇴행: before 는 정상인데 after 가 비면(--version 실패) 지연 설치가 에이전트를 깨뜨린 것.
            bool regressed = !string.IsNullOrEmpty(before) && string.IsNullOrEmpty(after);
            if (changed)
                DevezCode.App.ShowAgentUpdateResults(new[]
                {
                    new AgentUpdateResult(agent.Id, agent.DisplayName, before, after, AgentUpdateStatus.Updated, exitCode),
                });
            else if (regressed || exitCode != 0)
            {
                // C: 우리 업데이트가 깨뜨린 퇴행(before 정상 → after 실행불가)일 때만 게이트를 비워 다음 실행에서 재시도
                //    (백그라운드 완주 경로에서도 동일 보장). after 정상이면 non-zero 여도, 처음부터 깨졌어도 재시도 안 함.
                if (regressed)
                    SettingsService.SaveLastAgentAutoUpdateDate("");
                DevezCode.App.ShowAgentUpdateResults(new[]
                {
                    new AgentUpdateResult(agent.Id, agent.DisplayName, before, after, AgentUpdateStatus.Failed, exitCode),
                });
            }
        }
        catch { /* 감시 실패는 무시 — 요약 로그로 추적 가능 */ }
        finally { try { proc.Dispose(); } catch { } }
    }

    /// <summary>업데이트(설치) 명령을 분리 실행한다. 출력은 셸 안에서 파일(<see cref="DetachedLogPath"/>)로
    /// 리다이렉트하므로 부모 앱과 파이프로 연결되지 않는다 → 앱 종료/건너뛰기에도 설치가 끊기지 않는다.
    /// 반환: 타임아웃 안에 종료되면 (true, 종료코드, null), 진행 중이면 (false, 0, 프로세스—호출자가 감시/해제).</summary>
    private static async Task<(bool exited, int exitCode, Process? running)> RunDetachedUpdateAsync(
        AgentDef agent, TimeSpan timeout)
    {
        var logPath = DetachedLogPath(agent.Id);
        try { Directory.CreateDirectory(DevezDir); } catch { }

        // PowerShell 리터럴 문자열 안의 작은따옴표는 '' 로 이스케이프.
        var escapedLog = logPath.Replace("'", "''");
        // 모든 스트림(*)을 파일로 append. 부모에 파이프 리다이렉트를 걸지 않는 것이 이 보강의 핵심.
        // 종료코드 명시 전파: powershell -Command 는 네이티브 exit code 를 그대로 반환한다는 보장이 없어
        // $LASTEXITCODE 를 직접 exit 한다. $ok 선캡처 필수 — if 조건 평가가 $? 를 덮어쓰기 때문.
        // 명령 자체를 못 찾은 경우($LASTEXITCODE null + $? false)도 exit 1 로 실패 판정된다.
        var command = $"& {{ {agent.UpdateCommand} }} *>> '{escapedLog}'"
                    + "; $ok = $?; if ($null -ne $LASTEXITCODE) { exit $LASTEXITCODE } elseif ($ok) { exit 0 } else { exit 1 }";

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
            if (!proc.Start())
            {
                try { proc.Dispose(); } catch { }
                return (true, -1, null);
            }
        }
        catch
        {
            try { proc.Dispose(); } catch { }
            return (true, -1, null);
        }

        using var cts = new System.Threading.CancellationTokenSource(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
            int code = proc.ExitCode;
            try { proc.Dispose(); } catch { }
            return (true, code, null);
        }
        catch (OperationCanceledException)
        {
            // 타임아웃 — Kill 하지 않는다(설치 중 강제 종료 = 손상 위험). 호출자가 완주를 감시한다.
            return (false, 0, proc);
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

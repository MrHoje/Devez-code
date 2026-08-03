using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DevezCode.Services;

public enum ExternalSessionState
{
    Stopped,
    Starting,
    Running,
}

/// <summary>
/// DevezCode 방을 같은 세션 ID로 Windows Terminal에 넘긴다.
/// 외부 프록시가 배타적으로 연 lock 파일은 프로세스가 비정상 종료돼도 OS가 즉시 해제한다.
/// 실제 에이전트는 독립 프록시의 ConPTY에서 실행되며, 원본 VT 출력은 파일로도 릴레이된다.
/// </summary>
public static class ExternalSessionService
{
    private static readonly string RootDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "external-sessions");

    private static string SafeName(string roomId)
    {
        var chars = roomId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return chars.Length == 0 ? "room" : new string(chars);
    }

    private static string LockPath(string roomId) => Path.Combine(RootDir, SafeName(roomId) + ".lock");
    private static string TicketPath(string roomId) => Path.Combine(RootDir, SafeName(roomId) + ".ticket");
    private static string ReturnPath(string roomId) => Path.Combine(RootDir, SafeName(roomId) + ".return");
    private static string ScriptPath(string roomId) => Path.Combine(RootDir, SafeName(roomId) + ".ps1");
    private static string RunnerPath(string roomId) => Path.Combine(RootDir, SafeName(roomId) + ".runner.ps1");
    private static string SpecPath(string roomId) => Path.Combine(RootDir, SafeName(roomId) + ".proxy.json");
    private static string OutputPath(string roomId) => Path.Combine(RootDir, SafeName(roomId) + ".output.bin");
    private static string SizePath(string roomId) => Path.Combine(RootDir, SafeName(roomId) + ".size");
    private static string SnapshotPath(string roomId) => Path.Combine(RootDir, SafeName(roomId) + ".png");

    public static bool IsWindowsTerminalAvailable() => ResolveWindowsTerminalPath() != null;

    /// <summary>
    /// 내부 세션을 종료하기 전에 외부 재개에 필요한 모든 조건을 확인한다.
    /// null이면 시작 가능, 그 외에는 사용자에게 표시할 오류다.
    /// </summary>
    public static string? GetLaunchError(string roomId, string agentId)
    {
        var agent = AgentRegistry.Find(agentId) ?? AgentRegistry.GetDefault();
        if (AgentRegistry.ResolvePath(agent) == null)
            return $"{agent.DisplayName} 실행 파일을 찾을 수 없습니다.";
        // WT 없어도 일반 콘솔(powershell)로 폴백하므로 WT 유무는 시작 조건이 아니다.

        var sessionId = LoadSessionId(roomId, agent.Id);
        if (string.IsNullOrWhiteSpace(sessionId))
            return "이어갈 세션 ID를 찾을 수 없습니다.";
        if (BuildResumeArgs(roomId, agent.Id, sessionId) == null)
            return $"{agent.DisplayName}은(는) 외부 세션 이어가기를 지원하지 않습니다.";
        return null;
    }

    public static string PrepareLaunch(string roomId)
    {
        Directory.CreateDirectory(RootDir);
        if (GetState(roomId) != ExternalSessionState.Stopped)
            throw new InvalidOperationException("이미 외부에서 실행 중이거나 시작 중인 세션입니다.");

        CleanupStoppedFiles(roomId);
        var token = Guid.NewGuid().ToString("N");
        File.WriteAllText(TicketPath(roomId), token, Encoding.ASCII);
        return token;
    }

    public static void SaveSnapshot(string roomId, byte[]? png)
    {
        try
        {
            if (png is not { Length: > 0 })
            {
                TryDelete(SnapshotPath(roomId));
                return;
            }
            Directory.CreateDirectory(RootDir);
            File.WriteAllBytes(SnapshotPath(roomId), png);
        }
        catch { }
    }

    public static byte[]? LoadSnapshot(string roomId)
    {
        try
        {
            var path = SnapshotPath(roomId);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 각 TerminalHostView가 독립 offset으로 VT 로그를 읽는다. 앱 재시작이나 패널 이동 시
    /// offset=0에서 전체 로그를 재생하면 동일한 xterm 화면을 복원할 수 있다.
    /// </summary>
    public static byte[]? ReadOutput(string roomId, ref long offset, int maxBytes = 256 * 1024)
    {
        try
        {
            var path = OutputPath(roomId);
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);

            if (offset < 0 || offset > stream.Length) offset = 0;
            if (offset == stream.Length) return null;

            stream.Position = offset;
            int requested = (int)Math.Min(
                Math.Max(1, maxBytes),
                Math.Min(int.MaxValue, stream.Length - offset));
            var bytes = new byte[requested];
            int total = 0;
            while (total < bytes.Length)
            {
                int read = stream.Read(bytes, total, bytes.Length - total);
                if (read <= 0) break;
                total += read;
            }

            if (total == 0) return null;
            offset += total;
            if (total == bytes.Length) return bytes;
            Array.Resize(ref bytes, total);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static bool TryReadSize(string roomId, out int cols, out int rows)
    {
        cols = 0;
        rows = 0;
        try
        {
            using var stream = new FileStream(
                SizePath(roomId), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 128);
            var parts = reader.ReadToEnd().Trim().Split(',');
            if (parts.Length != 2
                || !int.TryParse(parts[0], out cols)
                || !int.TryParse(parts[1], out rows))
                return false;

            cols = Math.Clamp(cols, 20, 500);
            rows = Math.Clamp(rows, 5, 200);
            return true;
        }
        catch
        {
            cols = 0;
            rows = 0;
            return false;
        }
    }

    public static long GetOutputLength(string roomId)
    {
        try
        {
            using var stream = new FileStream(
                OutputPath(roomId), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return stream.Length;
        }
        catch { return 0; }
    }

    public static ExternalSessionState GetState(string roomId)
    {
        var lockPath = LockPath(roomId);
        if (File.Exists(lockPath))
        {
            try
            {
                using var stream = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return ExternalSessionState.Running;
            }
            catch (UnauthorizedAccessException)
            {
                return ExternalSessionState.Running;
            }
        }

        var ticketPath = TicketPath(roomId);
        try
        {
            if (File.Exists(ticketPath)
                && DateTime.UtcNow - File.GetLastWriteTimeUtc(ticketPath) < TimeSpan.FromSeconds(30))
                return ExternalSessionState.Starting;
        }
        catch { }

        // 출력 파일은 호출자가 마지막 바이트를 xterm에 보낸 뒤 명시적으로 정리한다.
        return ExternalSessionState.Stopped;
    }

    /// <summary>아직 시작 전이면 ticket을 취소한다. 이미 lock을 잡았다면 true를 반환한다.</summary>
    public static bool CancelLaunch(string roomId)
    {
        TryDelete(TicketPath(roomId));
        if (GetState(roomId) == ExternalSessionState.Running)
            return true;
        CleanupStoppedFiles(roomId);
        return false;
    }

    /// <summary>종료 상태를 UI에 반영하고 마지막 출력을 소비한 뒤 호출한다.</summary>
    public static void CleanupStoppedSession(string roomId)
    {
        if (GetState(roomId) == ExternalSessionState.Stopped)
            CleanupStoppedFiles(roomId);
    }

    public static async Task<(bool Success, string? Error)> LaunchAsync(
        string roomId,
        string sessionName,
        string agentId,
        string workingDir,
        string token,
        int preferredCols,
        int preferredRows,
        string theme)
    {
        var agent = AgentRegistry.Find(agentId) ?? AgentRegistry.GetDefault();
        var executable = AgentRegistry.ResolvePath(agent);
        if (string.IsNullOrWhiteSpace(executable))
            return (false, $"{agent.DisplayName} 실행 파일을 찾을 수 없습니다.");
        // Windows Terminal 있으면 탭으로, 없으면 일반 콘솔(powershell) 창으로 폴백한다.
        var windowsTerminal = ResolveWindowsTerminalPath();

        var sessionId = LoadSessionId(roomId, agent.Id);
        if (string.IsNullOrWhiteSpace(sessionId))
            return (false, "이어갈 세션 ID를 찾을 수 없습니다.");

        var args = BuildResumeArgs(roomId, agent.Id, sessionId);
        if (args == null)
            return (false, $"{agent.DisplayName}은(는) 외부 세션 이어가기를 지원하지 않습니다.");

        var proxyExecutable = ResolveProxyExecutable();
        if (proxyExecutable == null)
            return (false, "DevezCode 외부 세션 프록시 실행 파일을 찾을 수 없습니다.");

        try
        {
            Directory.CreateDirectory(RootDir);
            var runnerPath = RunnerPath(roomId);
            var specPath = SpecPath(roomId);
            var outputPath = OutputPath(roomId);
            var sizePath = SizePath(roomId);

            File.WriteAllText(
                runnerPath,
                BuildRunnerScript(roomId, agent.Id, workingDir, executable, args, theme),
                ScriptFile.Ps1);

            var spec = new ExternalSessionProxySpec
            {
                Token = token,
                RoomId = roomId,
                WorkingDirectory = Path.GetFullPath(workingDir),
                LockPath = Path.GetFullPath(LockPath(roomId)),
                TicketPath = Path.GetFullPath(TicketPath(roomId)),
                ReturnPath = Path.GetFullPath(ReturnPath(roomId)),
                RunnerScriptPath = Path.GetFullPath(runnerPath),
                OutputPath = Path.GetFullPath(outputPath),
                SizePath = Path.GetFullPath(sizePath),
                Theme = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light",
                PreferredCols = Math.Clamp(preferredCols, 20, 500),
                PreferredRows = Math.Clamp(preferredRows, 5, 200),
            };
            File.WriteAllText(
                specPath,
                JsonSerializer.Serialize(spec),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // 프록시가 열기 전에도 이전 실행의 내용이 잠깐 재생되지 않게 비워 둔다.
            using (new FileStream(
                       outputPath, FileMode.Create, FileAccess.Write,
                       FileShare.ReadWrite | FileShare.Delete))
            {
            }
            TryDelete(sizePath);

            File.WriteAllText(
                ScriptPath(roomId),
                BuildWrapperScript(
                    TicketPath(roomId), token, roomId,
                    agent.Id, workingDir, proxyExecutable, specPath),
                ScriptFile.Ps1);

            ProcessStartInfo start;
            if (windowsTerminal != null)
            {
                start = new ProcessStartInfo
                {
                    FileName = windowsTerminal,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                start.ArgumentList.Add("-w");
                // 외부 세션은 전용 창으로 격리한다. 복귀 후 종료된 탭이 사용 중인 기존 터미널에
                // 남지 않고, 다른 사용자의 탭도 건드리지 않는다.
                start.ArgumentList.Add("-1");
                start.ArgumentList.Add("new-tab");
                // CLI(claude/codex 등)가 OSC title escape로 탭 제목을 덮어쓰지 못하게 고정한다.
                start.ArgumentList.Add("--suppressApplicationTitle");
                start.ArgumentList.Add("--title");
                start.ArgumentList.Add(string.IsNullOrWhiteSpace(sessionName) ? agent.DisplayName : sessionName);
                start.ArgumentList.Add("--startingDirectory");
                start.ArgumentList.Add(workingDir);
                start.ArgumentList.Add("powershell.exe");
                start.ArgumentList.Add("-NoLogo");
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-ExecutionPolicy");
                start.ArgumentList.Add("Bypass");
                start.ArgumentList.Add("-File");
                start.ArgumentList.Add(ScriptPath(roomId));
            }
            else
            {
                // 폴백: WT 미설치 → 일반 콘솔(powershell) 창을 새로 띄운다. GUI 프로세스가 콘솔 앱을
                // CreateNoWindow=false 로 실행하면 OS가 새 conhost 창을 할당한다. 래퍼가 프록시를
                // -NoNewWindow 로 그 콘솔 안에서 실행하므로 WT 경로와 동일하게 동작한다.
                start = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    WorkingDirectory = workingDir,
                };
                start.ArgumentList.Add("-NoLogo");
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-ExecutionPolicy");
                start.ArgumentList.Add("Bypass");
                start.ArgumentList.Add("-File");
                start.ArgumentList.Add(ScriptPath(roomId));
            }

            Process.Start(start);

            var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < timeout)
            {
                if (GetState(roomId) == ExternalSessionState.Running)
                    return (true, null);
                await Task.Delay(100);
            }
            return (false, "외부 세션이 시작되지 않았습니다.");
        }
        catch (Exception ex)
        {
            return (false, $"외부 터미널을 열지 못했습니다.\n{ex.Message}");
        }
    }

    internal static bool IsExpectedProxySpec(string specPath, ExternalSessionProxySpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.RoomId)
            || string.IsNullOrWhiteSpace(spec.Token)
            || string.IsNullOrWhiteSpace(spec.WorkingDirectory))
            return false;

        try
        {
            return PathsEqual(specPath, SpecPath(spec.RoomId))
                   && PathsEqual(spec.LockPath, LockPath(spec.RoomId))
                   && PathsEqual(spec.TicketPath, TicketPath(spec.RoomId))
                   && PathsEqual(spec.ReturnPath, ReturnPath(spec.RoomId))
                   && PathsEqual(spec.RunnerScriptPath, RunnerPath(spec.RoomId))
                   && PathsEqual(spec.OutputPath, OutputPath(spec.RoomId))
                   && PathsEqual(spec.SizePath, SizePath(spec.RoomId))
                   && Directory.Exists(Path.GetFullPath(spec.WorkingDirectory));
        }
        catch { return false; }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string? LoadSessionId(string roomId, string agentId) => agentId switch
    {
        "claude" => SettingsService.LoadClaudeCodeRoomSession(roomId),
        "codex" => SettingsService.LoadCodexRoomSession(roomId),
        "kimi" => SettingsService.LoadKimiRoomSession(roomId),
        "grok" => SettingsService.LoadGrokRoomSession(roomId),
        "opencode" => SettingsService.LoadOpenCodeRoomSession(roomId),
        "gajae" => SettingsService.LoadGajaeRoomSession(roomId),
        "antigravity" => SettingsService.LoadAntigravityRoomSession(roomId),
        "devezvibe" => SettingsService.LoadDevezVibeRoomSession(roomId),
        _ => null,
    };

    private static string[]? BuildResumeArgs(string roomId, string agentId, string sessionId) => agentId switch
    {
        "claude" => BuildClaudeResumeArgs(roomId, sessionId),
        "codex" => new[] { "resume", sessionId },
        "kimi" => new[] { "-S", sessionId },
        "grok" => new[] { "-r", sessionId },
        "opencode" => new[] { "--session", sessionId },
        "gajae" => new[]
        {
            "--session-dir",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DevezCode", "gajae", "sessions", SafeName(roomId)),
            "-r",
            sessionId,
        },
        "antigravity" => new[] { "--conversation", sessionId, "--dangerously-skip-permissions" },
        "devezvibe" => new[] { "-r", sessionId },
        _ => null,
    };

    // 외부 claude 도 방별 --settings(busy/lastmsg/waiting 훅)를 넘겨, 실행 중에도 DevezCode 가
    // 세션 busy 상태를 추적한다(없으면 오버레이 "인앱으로 가져오기"가 응답 중에도 활성화되는 문제).
    private static string[] BuildClaudeResumeArgs(string roomId, string sessionId)
    {
        var args = new List<string> { "--resume", sessionId, "--dangerously-skip-permissions" };
        var roomSettings = Terminal.TerminalSessionManager.GetClaudeRoomSettingsPath(roomId);
        if (roomSettings != null)
        {
            args.Add("--settings");
            args.Add(roomSettings);
        }
        return args.ToArray();
    }

    private static string BuildRunnerScript(
        string roomId,
        string agentId,
        string workingDir,
        string executable,
        IReadOnlyList<string> args,
        string theme)
    {
        static string Q(string value) => "'" + value.Replace("'", "''") + "'";
        var quotedArgs = string.Join(", ", args.Select(Q));
        var command = agentId == "devezvibe"
            ? "chcp 65001 > $null\r\n" +
              $"& {Q(executable)} --theme {Q(theme)} @cliArgs\r\n"
            : $"& {Q(executable)} @cliArgs\r\n";
        return
            "$ErrorActionPreference = 'Stop'\r\n" +
            $"$env:DEVEZCODE_ROOM_ID = {Q(roomId)}\r\n" +
            $"$env:{TrackingEnvironment.VariableName} = {Q(agentId)}\r\n" +
            "$env:FORCE_COLOR = '3'\r\n" +
            "$env:COLORTERM = 'truecolor'\r\n" +
            "Remove-Item Env:NO_COLOR -ErrorAction SilentlyContinue\r\n" +
            $"Set-Location -LiteralPath {Q(workingDir)}\r\n" +
            $"$cliArgs = @({quotedArgs})\r\n" +
            command +
            "$code = $LASTEXITCODE\r\n" +
            "if ($null -eq $code) { $code = 0 }\r\n" +
            "exit $code\r\n";
    }

    private static string BuildWrapperScript(
        string ticketPath,
        string token,
        string roomId,
        string agentId,
        string workingDir,
        string proxyExecutable,
        string specPath)
    {
        static string Q(string value) => "'" + value.Replace("'", "''") + "'";
        return
            "$ErrorActionPreference = 'Stop'\r\n" +
            $"$ticketPath = {Q(ticketPath)}\r\n" +
            $"$expectedToken = {Q(token)}\r\n" +
            "if (-not (Test-Path -LiteralPath $ticketPath) -or " +
            "(Get-Content -LiteralPath $ticketPath -Raw).Trim() -ne $expectedToken) { exit 2 }\r\n" +
            $"$env:DEVEZCODE_ROOM_ID = {Q(roomId)}\r\n" +
            $"$env:{TrackingEnvironment.VariableName} = {Q(agentId)}\r\n" +
            $"$env:DEVEZCODE_EXTERNAL_SPEC = {Q(specPath)}\r\n" +
            $"$env:DEVEZCODE_EXTERNAL_TOKEN = {Q(token)}\r\n" +
            "$env:FORCE_COLOR = '3'\r\n" +
            "$env:COLORTERM = 'truecolor'\r\n" +
            "Remove-Item Env:NO_COLOR -ErrorAction SilentlyContinue\r\n" +
            // 이 래퍼(탭 셸)의 PID 를 프록시에 넘긴다 → 탭이 닫혀 래퍼가 죽으면 프록시가 즉시 감지해 정리.
            "$env:DEVEZCODE_WRAPPER_PID = $PID\r\n" +
            $"Set-Location -LiteralPath {Q(workingDir)}\r\n" +
            $"$proxy = Start-Process -FilePath {Q(proxyExecutable)} " +
            $"-ArgumentList {Q(ExternalSessionProxy.ModeArgument)} -NoNewWindow -Wait -PassThru\r\n" +
            "exit $proxy.ExitCode\r\n";
    }

    private static string? ResolveProxyExecutable()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath)
                && File.Exists(processPath)
                && string.Equals(
                    Path.GetFileNameWithoutExtension(processPath),
                    "DevezCode",
                    StringComparison.OrdinalIgnoreCase))
                return processPath;

            var besideResources = Path.Combine(AppContext.BaseDirectory, "DevezCode.exe");
            return File.Exists(besideResources) ? besideResources : null;
        }
        catch { return null; }
    }

    /// <summary>실행 중인 외부 프록시에 종료(인앱 복귀)를 요청한다. 프록시가 신호 파일을 감지하면
    /// 세션을 정리하고 종료해 lock 을 놓는다 → CheckExternalSessions 가 내부 resume 으로 복귀시킨다.</summary>
    public static void RequestReturn(string roomId)
    {
        try
        {
            Directory.CreateDirectory(RootDir);
            File.WriteAllText(ReturnPath(roomId), "1", Encoding.ASCII);
        }
        catch { /* 실패해도 사용자가 외부 탭을 직접 닫으면 복귀 가능 */ }
    }

    private static void CleanupStoppedFiles(string roomId)
    {
        TryDelete(TicketPath(roomId));
        TryDelete(ReturnPath(roomId));
        TryDelete(LockPath(roomId));
        TryDelete(ScriptPath(roomId));
        TryDelete(RunnerPath(roomId));
        TryDelete(SpecPath(roomId));
        TryDelete(OutputPath(roomId));
        TryDelete(SizePath(roomId));
        TryDelete(SnapshotPath(roomId));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static string? ResolveWindowsTerminalPath()
    {
        try
        {
            var paths = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var directory in paths)
            {
                var candidate = Path.Combine(directory.Trim('"'), "wt.exe");
                if (File.Exists(candidate)) return candidate;
            }

            var windowsApps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "wt.exe");
            if (File.Exists(windowsApps)) return windowsApps;
        }
        catch { }
        return null;
    }
}

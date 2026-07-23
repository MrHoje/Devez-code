using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DevezCode.Services.Terminal;

/// <summary>
/// ConPTY(CreatePseudoConsole) 기반 로컬 터미널 세션.
/// 채팅방마다 1개씩 생성되며 방 전환·탭 토글에도 살아있다. 앱 종료 시 Dispose.
/// </summary>
public sealed class TerminalSession : IDisposable
{
    /// <summary>셸 출력(UTF-8 bytes). 백그라운드 스레드에서 발생.</summary>
    public event Action<byte[]>? OutputReceived;
    /// <summary>셸 프로세스 종료. 백그라운드 스레드에서 발생.</summary>
    public event Action? Exited;

    public bool IsAlive { get; private set; }

    /// <summary>마지막으로 적용된 크기. 재배선 시 "정말 크기가 달라졌는지" 판단해 불필요한
    /// -1/+1 리사이즈 킥(ConPTY 리플로우 부작용 有)을 피하는 데 쓴다.</summary>
    public int Cols { get; private set; }
    public int Rows { get; private set; }

    // 최근 출력(원본 ANSI 포함) 롤링 버퍼 — 재배선 시 HasPriorOutput 으로 준비 상태 복원 판단에 사용.
    private readonly object _recentLock = new();
    private readonly StringBuilder _recent = new();
    private const int RecentCap = 16384;

    /// <summary>이 세션이 이미 출력을 내보낸 적 있는지 — 배선 전에 출력이 흘렀다면(재배선)
    /// 시작 신호(alt-screen 등)를 다시 감지할 수 없으므로 호출부가 준비 상태를 직접 복원해야 한다.</summary>
    public bool HasPriorOutput { get { lock (_recentLock) return _recent.Length > 0; } }

    private void AppendRecent(string text)
    {
        lock (_recentLock)
        {
            _recent.Append(text);
            if (_recent.Length > RecentCap) _recent.Remove(0, _recent.Length - RecentCap);
        }
    }

    /// <summary>표시 허브가 아직 비어 있을 때 쓰는 짧은 원본 ANSI 폴백.</summary>
    public byte[] GetRecentOutputSnapshot()
    {
        lock (_recentLock) return Encoding.UTF8.GetBytes(_recent.ToString());
    }

    /// <summary>셸(직속) 프로세스 ID — graceful 종료 대기에 사용.</summary>
    public int ProcessId { get; private set; }
    /// <summary>직속 셸 프로세스 종료 코드. 아직 실행 중이거나 조회할 수 없으면 null.</summary>
    public int? ExitCode { get; private set; }

    private IntPtr _hPC;                       // pseudoconsole 핸들
    private SafeFileHandle? _inputWrite;       // 우리가 쓰면 셸 stdin으로
    private SafeFileHandle? _outputRead;       // 셸 stdout을 우리가 읽음
    private IntPtr _hProcess;
    private IntPtr _hThread;
    private IntPtr _hJob;                      // 셸+자손 프로세스 트리를 묶는 Job (KILL_ON_JOB_CLOSE)
    private readonly object _pseudoConsoleLock = new();
    private readonly ManualResetEventSlim _outputCompleted = new(false);
    private FileStream? _inputStream;
    private bool _disposed;
    private readonly object _writeLock = new();
    private volatile bool _gracefulExitStarted;
    private bool _gracefulExitSignalsSent;

    public TerminalSession(
        string commandLine,
        string? startingDirectory,
        int cols,
        int rows,
        Action<byte[]>? outputReceived = null,
        Action? exited = null,
        string? colorFgBg = null)
    {
        // 프록시처럼 생성 직후 첫 바이트도 놓치면 안 되는 호출자는 생성 전에 콜백을 넘긴다.
        // 기존 이벤트 구독 방식도 그대로 지원한다.
        if (outputReceived != null) OutputReceived += outputReceived;
        if (exited != null) Exited += exited;

        // WT settings.json 의 commandline 에는 %SystemRoot% 같은 환경변수가 올 수 있다.
        // CreateProcessW 는 환경변수를 확장하지 않으므로 여기서 직접 확장한다.
        commandLine = Environment.ExpandEnvironmentVariables(commandLine);

        // claude 는 WT_SESSION 으로 터미널을 "windows-terminal" 로 식별한다.
        // 한글 IME 조합 위치의 직접 해결책은 아니지만(해결은 terminal.html 의
        // ime-fixed 워크어라운드), 진짜 WT의 claude 와 동일한 환경 조건을 맞춰
        // 터미널 종류에 따른 잠재적 동작 차이를 줄인다.
        Environment.SetEnvironmentVariable("WT_SESSION", Guid.NewGuid().ToString());
        Environment.SetEnvironmentVariable("WT_PROFILE_ID", "{2ece5bfe-50ed-5f3a-ab87-5cd4baafed2b}");

        // DevezCode 자체가 claude 세션 안에서 실행되면(예: claude 터미널에서 앱을 띄움) 프로세스 env 에
        // CLAUDECODE=1 / CLAUDE_CODE_* 가 상속돼 있다. 이 상태로 claude 자식을 띄우면 claude 가 자신을
        // "중첩(child) 세션"으로 판정해 ★새 대화 transcript(.jsonl)를 영속화하지 않는다★ (기존 파일 resume-append 는 됨).
        // 결과: 그 뒤 만든 세션은 재실행 시 resume 불가 + 토큰 사용량 표시 불가. 상속된 nesting 마커를
        // 자식 환경에서 제거해 항상 독립(top-level) claude 로 실행되게 한다. (PEB 로 실측 확인한 실제 원인)
        foreach (var leaked in new[] {
            "CLAUDECODE", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_ENTRYPOINT",
            "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_SSE_PORT" })
            Environment.SetEnvironmentVariable(leaked, null);

        // 색 출력 강제 — 세션마다 재확정한다. App.OnStartup 에서 프로세스 env 로 한 번 깔지만,
        // 그 값이 시작 후 어느 시점에 흐트러지면(User 범위의 빈 FORCE_COLOR="" 상속, NO_COLOR 충돌,
        // 자동업데이트 흐름 등) 이후 실행되는 모든 세션이 monochrome(검정 배경·하이라이트 없는 흰 글자)로
        // 뜨고 앱 재시작 전까지 지속됐다. 세션 생성 시점에 다시 확정해 프로세스 env 상태와 무관하게 항상 색을 켠다.
        // 진단: 재확정 직전 값을 남겨 재발 시 무엇이 흐트러뜨렸는지 못박는다.
        var preForce = Environment.GetEnvironmentVariable("FORCE_COLOR");
        var preNo = Environment.GetEnvironmentVariable("NO_COLOR");
        if (preForce != "3" || preNo != null)
            DevezCode.Services.DiagLog.Write(
                $"terminal env pre-assert: FORCE_COLOR=[{preForce ?? "<unset>"}] NO_COLOR=[{preNo ?? "<unset>"}] cmd={commandLine}");
        Environment.SetEnvironmentVariable("FORCE_COLOR", "3");
        Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
        Environment.SetEnvironmentVariable("NO_COLOR", null); // FORCE_COLOR 와 충돌 시 monochrome 유발 → 자식에서 제거
        // codex 등 TUI 의 라이트/다크 감지 폴백(rxvt 관례 "fg;bg", 0=검정 15=흰색).
        // 1차 감지는 OSC 11 질의(TerminalHostView 가 즉시 프록시 응답)지만, 그 경로가
        // 실패해도 밝기 판별이 앱 테마와 일치하도록 환경변수 폴백을 같이 깔아 둔다.
        // 테마 변경은 세션 재시작을 타므로 세션 생성 시점 값이면 충분하다.
        Environment.SetEnvironmentVariable("COLORFGBG",
            colorFgBg ?? (DevezCode.App.CommittedTheme == "dark" ? "15;0" : "0;15"));

        // 1) 파이프 2쌍: (셸이 읽는 stdin), (셸이 쓰는 stdout)
        if (!CreatePipe(out var inputRead, out var inputWriteRaw, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(input) 실패");
        if (!CreatePipe(out var outputReadRaw, out var outputWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(output) 실패");

        _inputWrite = new SafeFileHandle(inputWriteRaw, ownsHandle: true);
        _outputRead = new SafeFileHandle(outputReadRaw, ownsHandle: true);

        // 2) pseudoconsole 생성 (자식 쪽 핸들 연결)
        Cols = Math.Max(cols, 2);
        Rows = Math.Max(rows, 2);
        var size = new COORD { X = (short)Cols, Y = (short)Rows };
        int hr = CreatePseudoConsole(size, inputRead, outputWrite, 0, out _hPC);
        // ConPTY가 핸들을 복제하므로 자식 쪽 원본은 닫는다
        CloseHandle(inputRead);
        CloseHandle(outputWrite);
        if (hr != 0)
            throw new Win32Exception(hr, "CreatePseudoConsole 실패");

        // 3) PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE 붙여 셸 프로세스 생성
        var attrListSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);
        var attrList = Marshal.AllocHGlobal(attrListSize);
        try
        {
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref attrListSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList 실패");
            if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    _hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute 실패");

            var siEx = new STARTUPINFOEX();
            siEx.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            // ConPTY가 연결되기 전 cmd/gjc 런처의 실제 콘솔 창이 잠깐 생성되는 Windows 레이스를 막는다.
            // pseudoconsole 출력에는 영향 없이, CreateProcess 초기 창 표시만 숨긴다.
            siEx.StartupInfo.dwFlags = STARTF_USESHOWWINDOW;
            siEx.StartupInfo.wShowWindow = SW_HIDE;
            siEx.lpAttributeList = attrList;

            string? cwd = string.IsNullOrWhiteSpace(startingDirectory) ? null
                        : Environment.ExpandEnvironmentVariables(startingDirectory);
            if (cwd != null && !Directory.Exists(cwd)) cwd = null;

            // Job Object: 셸과 그 자손(claude → node, MCP 서버 등) 전체를 한 묶음으로 묶어
            // Dispose 에서 핸들을 닫을 때(KILL_ON_JOB_CLOSE) 트리 전체가 함께 종료되게 한다.
            // ConPTY 종료만으로는 detached 손자 프로세스가 잔류할 수 있어 좀비를 확실히 막는다.
            // 생성 실패 시에도 기존 TerminateProcess 폴백이 직속 셸은 정리하므로 best-effort 로 둔다.
            _hJob = CreateJobObject(IntPtr.Zero, null);
            if (_hJob != IntPtr.Zero)
            {
                var jobInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                jobInfo.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                SetInformationJobObject(_hJob, JobObjectExtendedLimitInformation,
                    ref jobInfo, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
            }

            if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                    IntPtr.Zero, cwd, ref siEx, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"셸 실행 실패: {commandLine}");

            _hProcess = pi.hProcess;
            _hThread = pi.hThread;
            // 셸을 Job 에 편입 — 셸이 곧 띄울 claude/node 등 자손도 같은 Job 에 상속된다.
            // (셸이 initialCmd 로 claude 를 띄우기 전에 편입되므로 자손 누락 경합은 사실상 없음)
            if (_hJob != IntPtr.Zero)
                try { AssignProcessToJobObject(_hJob, _hProcess); } catch (Exception) { }
            IsAlive = true;

            // 4) 입출력 스트림 + 종료 감시
            _inputStream = new FileStream(_inputWrite, FileAccess.Write);
            ProcessId = (int)pi.dwProcessId;
            StartReadLoop();
            StartExitWatch((int)pi.dwProcessId);
        }
        finally
        {
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
        }
    }

    private void StartReadLoop()
    {
        // 진단용: DEVEZCODE_TERM_LOG=1 이면 ConPTY 출력 원본을 파일로 기록
        var logPath = Environment.GetEnvironmentVariable("DEVEZCODE_TERM_LOG") == "1"
            ? Path.Combine(Path.GetTempPath(), "devezcode-conpty.log") : null;

        var stream = new FileStream(_outputRead!, FileAccess.Read);
        var thread = new Thread(() =>
        {
            var buf = new byte[8192];
            try
            {
                while (true)
                {
                    int n = stream.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    var chunk = new byte[n];
                    Buffer.BlockCopy(buf, 0, chunk, 0, n);
                    if (logPath != null)
                    {
                        try { using var fs = new FileStream(logPath, FileMode.Append); fs.Write(chunk, 0, n); }
                        catch (Exception) { }
                    }
                    try { AppendRecent(Encoding.UTF8.GetString(chunk)); } catch { }
                    OutputReceived?.Invoke(chunk);
                }
            }
            catch (Exception) { /* 파이프 닫힘 — 정상 종료 경로 */ }
            finally
            {
                stream.Dispose();
                _outputCompleted.Set();
            }
        })
        { IsBackground = true, Name = "ConPTY-Read" };
        thread.Start();
    }

    private void StartExitWatch(int pid)
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.WaitForExit();
                try { ExitCode = p.ExitCode; } catch { }
            }
            catch (Exception) { /* 이미 종료됨 */ }
            IsAlive = false;
            Exited?.Invoke();
        })
        { IsBackground = true, Name = "ConPTY-ExitWatch" };
        thread.Start();
    }

    /// <summary>종료 시작과 동시에 일반 사용자 입력을 동결한다. WebView 메시지가 늦게 도착해도
    /// 종료 제어키 뒤에 섞이거나 Enter 로 미완성 초안을 전송하지 못하게 하는 경합 방지용.</summary>
    internal void BeginGracefulExit()
    {
        lock (_writeLock) _gracefulExitStarted = true;
    }

    /// <summary>에이전트별 제어키만 보내 graceful 종료를 시도하고 timeout 까지 기다린다.
    /// controlInput 은 표시 문자가 아닌 제어문자이며 CR/LF 는 금지한다. 따라서 종료가 실패해 하드 정리로
    /// 폴백해도 종료 코드 자체가 입력 중이던 초안을 요청으로 제출할 수 없다.</summary>
    public async Task<bool> TryGracefulExitAsync(int timeoutMs, string controlInput, int repeatCount = 1, bool escFirst = false)
    {
        BeginGracefulExit();

        if (string.IsNullOrEmpty(controlInput)
            || controlInput.IndexOf('\r') >= 0
            || controlInput.IndexOf('\n') >= 0
            || controlInput.Any(ch => !char.IsControl(ch)))
            throw new ArgumentException("종료 입력에는 CR/LF 없는 제어문자만 사용할 수 있습니다.", nameof(controlInput));

        bool shouldSignal;
        lock (_writeLock)
        {
            shouldSignal = !_gracefulExitSignalsSent;
            _gracefulExitSignalsSent = true;
        }

        if (_disposed || !IsAlive) return true;
        if (shouldSignal)
        {
            try
            {
                if (escFirst)
                {
                    TryWriteCore("\x1b", allowDuringGracefulExit: true);
                    await Task.Delay(150);
                }

                int count = Math.Max(1, repeatCount);
                for (int i = 0; i < count; i++)
                {
                    TryWriteCore(controlInput, allowDuringGracefulExit: true);
                    if (i + 1 < count) await Task.Delay(120);
                }
            }
            catch { /* 파이프 닫힘 — 이미 종료 중 */ }
        }

        return await WaitForExitAsync(timeoutMs);
    }

    private Task<bool> WaitForExitAsync(int timeoutMs)
    {
        int pid = ProcessId;
        if (pid <= 0) return Task.FromResult(true);
        return Task.Run(() =>
        {
            try { using var p = Process.GetProcessById(pid); return p.WaitForExit(timeoutMs); }
            catch { return true; } // 조회 실패 = 이미 종료
        });
    }

    /// <summary>키 입력 등 텍스트를 셸 stdin으로 전달.</summary>
    public void Write(string text) => TryWrite(text);

    /// <summary>텍스트를 셸 stdin에 기록하고 실제 파이프 쓰기 성공 여부를 반환한다.</summary>
    public bool TryWrite(string text) => TryWriteCore(text, allowDuringGracefulExit: false);

    /// <summary>VT 입력 바이트를 인코딩 변환 없이 셸 stdin에 전달한다. 외부 콘솔 프록시용.</summary>
    public bool TryWrite(byte[] bytes) => TryWriteCore(bytes, allowDuringGracefulExit: false);

    private bool TryWriteCore(string text, bool allowDuringGracefulExit)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return TryWriteCore(bytes, allowDuringGracefulExit);
    }

    private bool TryWriteCore(byte[] bytes, bool allowDuringGracefulExit)
    {
        if (bytes.Length == 0) return true;
        if (_disposed || !IsAlive || _inputStream == null
            || (!allowDuringGracefulExit && _gracefulExitStarted)) return false;
        lock (_writeLock)
        {
            if (_disposed || !IsAlive || _inputStream == null
                || (!allowDuringGracefulExit && _gracefulExitStarted)) return false;
            try
            {
                _inputStream.Write(bytes, 0, bytes.Length);
                _inputStream.Flush();
                return true;
            }
            catch (Exception) { return false; /* 파이프 닫힘 */ }
        }
    }

    public void Resize(int cols, int rows)
    {
        lock (_pseudoConsoleLock)
        {
            if (_disposed || _hPC == IntPtr.Zero) return;
            // 동일 크기는 no-op — 탭 활성화마다 오는 재동기 resize(refitSoon)가 실제 어긋남이 있을 때만
            // ConPTY 를 건드리게 한다. (강제 리페인트가 필요한 곳은 -1→원복 킥을 쓰므로 영향 없음.)
            if (Math.Max(cols, 2) == Cols && Math.Max(rows, 2) == Rows) return;
            Cols = Math.Max(cols, 2);
            Rows = Math.Max(rows, 2);
            var size = new COORD { X = (short)Cols, Y = (short)Rows };
            ResizePseudoConsole(_hPC, size);
        }
    }

    /// <summary>
    /// 직속 프로세스가 종료된 뒤 ConPTY를 닫아 남은 출력이 파이프 EOF까지 모두 배출되도록 기다린다.
    /// 외부 프록시가 종료 직전 마지막 VT 바이트를 확실히 기록할 때 사용한다.
    /// </summary>
    public bool CompleteOutputAfterExit(int timeoutMs)
    {
        if (IsAlive) return false;
        ClosePseudoConsoleOnce();
        try { return _outputCompleted.Wait(Math.Max(0, timeoutMs)); }
        catch { return false; }
    }

    private void ClosePseudoConsoleOnce()
    {
        lock (_pseudoConsoleLock)
        {
            if (_hPC == IntPtr.Zero) return;
            ClosePseudoConsole(_hPC);
            _hPC = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsAlive = false;

        // ConPTY를 먼저 닫으면 conhost가 정리되고 셸도 따라 종료된다
        ClosePseudoConsoleOnce();

        // Job 핸들을 닫으면 KILL_ON_JOB_CLOSE 로 셸+자손(claude/node 등) 트리 전체가 종료된다
        if (_hJob != IntPtr.Zero) { CloseHandle(_hJob); _hJob = IntPtr.Zero; }

        if (_hProcess != IntPtr.Zero)
        {
            TerminateProcess(_hProcess, 0); // Job 미편입 등 만일에 대비한 직속 셸 폴백 (좀비 방지)
            CloseHandle(_hProcess); _hProcess = IntPtr.Zero;
        }
        if (_hThread != IntPtr.Zero) { CloseHandle(_hThread); _hThread = IntPtr.Zero; }

        try { _inputStream?.Dispose(); } catch (Exception) { }
        _inputStream = null;
        _inputWrite?.Dispose(); _inputWrite = null;
        _outputRead?.Dispose(); _outputRead = null;
    }

    // ── P/Invoke ──────────────────────────────────────────────

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int STARTF_USESHOWWINDOW = 0x00000001;
    private const short SW_HIDE = 0;
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute,
        IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, int cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
}

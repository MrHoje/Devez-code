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

    /// <summary>셸(직속) 프로세스 ID — graceful 종료 대기에 사용.</summary>
    public int ProcessId { get; private set; }

    private IntPtr _hPC;                       // pseudoconsole 핸들
    private SafeFileHandle? _inputWrite;       // 우리가 쓰면 셸 stdin으로
    private SafeFileHandle? _outputRead;       // 셸 stdout을 우리가 읽음
    private IntPtr _hProcess;
    private IntPtr _hThread;
    private IntPtr _hJob;                      // 셸+자손 프로세스 트리를 묶는 Job (KILL_ON_JOB_CLOSE)
    private FileStream? _inputStream;
    private bool _disposed;
    private readonly object _writeLock = new();

    public TerminalSession(string commandLine, string? startingDirectory, int cols, int rows)
    {
        // WT settings.json 의 commandline 에는 %SystemRoot% 같은 환경변수가 올 수 있다.
        // CreateProcessW 는 환경변수를 확장하지 않으므로 여기서 직접 확장한다.
        commandLine = Environment.ExpandEnvironmentVariables(commandLine);

        // 1) 파이프 2쌍: (셸이 읽는 stdin), (셸이 쓰는 stdout)
        if (!CreatePipe(out var inputRead, out var inputWriteRaw, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(input) 실패");
        if (!CreatePipe(out var outputReadRaw, out var outputWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(output) 실패");

        _inputWrite = new SafeFileHandle(inputWriteRaw, ownsHandle: true);
        _outputRead = new SafeFileHandle(outputReadRaw, ownsHandle: true);

        // 2) pseudoconsole 생성 (자식 쪽 핸들 연결)
        var size = new COORD { X = (short)Math.Max(cols, 2), Y = (short)Math.Max(rows, 2) };
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

            var environmentBlock = BuildChildEnvironmentBlock();
            try
            {
                if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                        EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                        environmentBlock, cwd, ref siEx, out var pi))
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
                if (environmentBlock != IntPtr.Zero)
                    Marshal.FreeHGlobal(environmentBlock);
            }
        }
        finally
        {
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
        }
    }

    private static IntPtr BuildChildEnvironmentBlock()
    {
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString();
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (key.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)) continue;
            values[key] = entry.Value?.ToString() ?? "";
        }

        values["WT_SESSION"] = Guid.NewGuid().ToString();
        values["WT_PROFILE_ID"] = "{2ece5bfe-50ed-5f3a-ab87-5cd4baafed2b}";

        var block = new StringBuilder();
        foreach (var pair in values)
            block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        block.Append('\0');
        return Marshal.StringToHGlobalUni(block.ToString());
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
                    OutputReceived?.Invoke(chunk);
                }
            }
            catch (Exception) { /* 파이프 닫힘 — 정상 종료 경로 */ }
            finally { stream.Dispose(); }
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
            }
            catch (Exception) { /* 이미 종료됨 */ }
            IsAlive = false;
            Exited?.Invoke();
        })
        { IsBackground = true, Name = "ConPTY-ExitWatch" };
        thread.Start();
    }

    /// <summary>graceful 종료 시도 — claude/codex 가 transcript 를 flush 할 틈을 준다.
    /// stdin 으로 Ctrl+C 2회(에이전트 종료) + exit(셸 종료)를 보낸 뒤 프로세스 트리 종료를 timeout 까지 대기.
    /// 반환: 시간 내 정상 종료했으면 true. (이후 호출부가 Dispose 로 하드 정리 — 폴백)</summary>
    public async Task<bool> TryGracefulExitAsync(int timeoutMs)
    {
        if (_disposed || !IsAlive) return true;
        try
        {
            Write("\x03");           // claude: 1회 = 인터럽트
            await Task.Delay(120);
            Write("\x03");           // 2회 = 종료 (이때 transcript flush 기회)
            await Task.Delay(400);
            Write("exit\r\n");       // 에이전트 종료 후 셸도 닫아 트리 종료
        }
        catch { /* 파이프 닫힘 — 이미 종료 중 */ }
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
    public void Write(string text)
    {
        if (_disposed || !IsAlive || _inputStream == null) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        lock (_writeLock)
        {
            try { _inputStream.Write(bytes, 0, bytes.Length); _inputStream.Flush(); }
            catch (Exception) { /* 파이프 닫힘 */ }
        }
    }

    public void Resize(int cols, int rows)
    {
        if (_disposed || _hPC == IntPtr.Zero) return;
        var size = new COORD { X = (short)Math.Max(cols, 2), Y = (short)Math.Max(rows, 2) };
        ResizePseudoConsole(_hPC, size);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsAlive = false;

        // ConPTY를 먼저 닫으면 conhost가 정리되고 셸도 따라 종료된다
        if (_hPC != IntPtr.Zero) { ClosePseudoConsole(_hPC); _hPC = IntPtr.Zero; }

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

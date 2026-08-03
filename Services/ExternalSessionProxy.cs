using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DevezCode.Services.Terminal;

namespace DevezCode.Services;

internal sealed class ExternalSessionProxySpec
{
    public string Token { get; set; } = "";
    public string RoomId { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string LockPath { get; set; } = "";
    public string TicketPath { get; set; } = "";
    public string ReturnPath { get; set; } = "";
    public string RunnerScriptPath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string SizePath { get; set; } = "";
    public string Theme { get; set; } = "dark";
    public int PreferredCols { get; set; } = 120;
    public int PreferredRows { get; set; } = 30;
}

/// <summary>
/// Windows Terminal 탭 안에서 실행되는 독립 모드.
/// 에이전트를 하나의 ConPTY로 소유하고 원본 VT 출력을 외부 콘솔과 DevezCode 미러 파일에 동시에 보낸다.
/// 메인 DevezCode 프로세스와 독립적이므로 앱을 닫아도 외부 세션은 계속 실행된다.
/// </summary>
internal static class ExternalSessionProxy
{
    public const string ModeArgument = "--external-session-proxy";
    private const string SpecEnvironment = "DEVEZCODE_EXTERNAL_SPEC";
    private const string TokenEnvironment = "DEVEZCODE_EXTERNAL_TOKEN";

    // 콘솔 CTRL 핸들러(탭 닫힘 즉시 감지용). GC 방지 위해 델리게이트 참조 유지.
    private static Process? _proxyChild;
    private static ConsoleCtrlDelegate? _consoleCtrlHandler;
    private delegate bool ConsoleCtrlDelegate(uint ctrlType);

    private static bool HandleConsoleCtrl(uint ctrlType)
    {
        // 2=CLOSE, 5=LOGOFF, 6=SHUTDOWN — 콘솔이 사라지므로 자식(에이전트)을 즉시 정리해 lock 을 빨리 놓는다.
        if (ctrlType is 2 or 5 or 6)
        {
            try { _proxyChild?.Kill(entireProcessTree: true); } catch { }
            return true;
        }
        // 0=Ctrl+C, 1=Break — 에이전트가 직접 처리하도록 두되, 프록시가 기본 동작(종료)으로 죽지 않게 삼킨다.
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate? handler, bool add);
    private const int DefaultCols = 120;
    private const int DefaultRows = 30;
    private const int MinCols = 20;
    private const int MinRows = 5;
    private const int MaxCols = 500;
    private const int MaxRows = 200;

    public static bool IsRequested(IReadOnlyList<string> args)
        => args.Any(arg => string.Equals(arg, ModeArgument, StringComparison.Ordinal));

    public static int RunFromEnvironment()
    {
        TryAttachParentConsole();

        var specPath = Environment.GetEnvironmentVariable(SpecEnvironment);
        var expectedToken = Environment.GetEnvironmentVariable(TokenEnvironment);
        if (string.IsNullOrWhiteSpace(specPath) || string.IsNullOrWhiteSpace(expectedToken))
            return Fail("외부 세션 프록시 시작 정보가 없습니다.");

        ExternalSessionProxySpec? spec;
        try
        {
            spec = JsonSerializer.Deserialize<ExternalSessionProxySpec>(
                File.ReadAllText(specPath, Encoding.UTF8));
        }
        catch (Exception ex)
        {
            return Fail($"외부 세션 프록시 설정을 읽지 못했습니다: {ex.Message}");
        }

        if (spec == null
            || !string.Equals(spec.Token, expectedToken, StringComparison.Ordinal)
            || !ExternalSessionService.IsExpectedProxySpec(specPath, spec))
            return Fail("외부 세션 프록시 시작 정보가 유효하지 않습니다.");

        if (!File.Exists(spec.RunnerScriptPath))
            return Fail("외부 세션 실행 스크립트를 찾을 수 없습니다.");

        Directory.CreateDirectory(Path.GetDirectoryName(spec.OutputPath)!);

        FileStream sessionLock;
        try
        {
            // 프록시 자체가 lock의 유일한 소유자다. 중간 PowerShell이 비정상 종료돼도
            // 프록시/에이전트가 살아 있는 동안에는 DevezCode가 내부 세션을 중복 생성하지 않는다.
            sessionLock = new FileStream(
                spec.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var ticket = File.Exists(spec.TicketPath)
                ? File.ReadAllText(spec.TicketPath, Encoding.ASCII).Trim()
                : null;
            if (!string.Equals(ticket, spec.Token, StringComparison.Ordinal))
            {
                sessionLock.Dispose();
                return Fail("외부 세션 시작 티켓이 만료되었습니다.");
            }
            if (!TryDeleteTicket(spec.TicketPath))
            {
                sessionLock.Dispose();
                return Fail("외부 세션 시작 티켓을 정리하지 못했습니다.");
            }
        }
        catch (Exception ex)
        {
            return Fail($"외부 세션 잠금을 획득하지 못했습니다: {ex.Message}");
        }

        using var sessionLockScope = sessionLock;

        // claude 를 ConPTY 로 감싸지 않고, 이 프록시가 물려받은 WT 콘솔을 자식에게 그대로 상속시켜
        // 직접 렌더링하게 한다(= claude 를 WT 에서 바로 실행한 것과 동일 → ConPTY 재직렬화 잔상 없음).
        // 미러(.output.bin)·크기 파일은 더 이상 아무도 읽지 않으므로 만들지 않는다.
        // 프록시의 남은 역할: lock 소유(독립성·복귀 감지) + "인앱으로 가져오기"(.return) 감시.
        try
        {
            var runner = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false, // 표준 핸들(=WT 콘솔) 상속 → claude 가 WT 에 직접 그린다
                WorkingDirectory = spec.WorkingDirectory,
            };
            runner.ArgumentList.Add("-NoLogo");
            runner.ArgumentList.Add("-NoProfile");
            runner.ArgumentList.Add("-ExecutionPolicy");
            runner.ArgumentList.Add("Bypass");
            runner.ArgumentList.Add("-File");
            runner.ArgumentList.Add(spec.RunnerScriptPath);

            using var child = Process.Start(runner);
            if (child == null) return Fail("외부 세션 에이전트를 시작하지 못했습니다.");

            // 탭(콘솔) 닫힘을 CTRL_CLOSE 로 즉시 감지해 자식을 정리한다. 이게 없으면 claude 가
            // 콘솔 종료를 스스로 알아채고 빠져나올 때까지(2~3s) 기다려 lock 해제가 늦어진다.
            _proxyChild = child;
            _consoleCtrlHandler = HandleConsoleCtrl;
            SetConsoleCtrlHandler(_consoleCtrlHandler, true);

            // 탭(콘솔) 셸 = 이 프록시를 -Wait 로 띄운 래퍼 PowerShell. 탭이 닫히면 래퍼가 먼저 죽는데,
            // WT 가 트리 kill 을 안 하면 프록시는 고아로 남아 claude 가 콘솔 종료를 스스로 알아챌 때까지
            // (2~3s) 기다린다. 래퍼 PID 를 감시해 죽는 즉시 자식을 정리하면 lock 이 바로 풀린다.
            Process? wrapper = null;
            if (int.TryParse(Environment.GetEnvironmentVariable("DEVEZCODE_WRAPPER_PID"), out var wpid))
            {
                try { wrapper = Process.GetProcessById(wpid); } catch { wrapper = null; }
            }

            // 자식 종료(탭 닫힘 포함)는 WaitForExit 로, "인앱으로 가져오기"는 .return 파일로 감지해 정리.
            bool returnRequested = false;
            while (!child.WaitForExit(200))
            {
                bool wrapperGone = false;
                try { wrapperGone = wrapper is { HasExited: true }; } catch { wrapperGone = true; }
                // 복귀 버튼은 .return을 먼저 쓴 뒤 전용 창에도 WM_CLOSE를 보낸다. Claude는 창 종료가
                // 빠르면 wrapperGone이 먼저 관측될 수 있으므로, 파일이 있으면 반드시 정상 복귀로 우선한다.
                bool returnFileExists = !string.IsNullOrEmpty(spec.ReturnPath) && File.Exists(spec.ReturnPath);

                if (returnFileExists || wrapperGone)
                {
                    returnRequested = returnFileExists; // 직접 탭 닫힘만 실제 종료코드를 유지한다.
                    try { child.Kill(entireProcessTree: true); } catch { }
                    child.WaitForExit(3000);
                    break;
                }
            }
            // "인앱으로 가져오기"는 정상 회수이므로 exit 0 을 반환한다 → WT 의 graceful closeOnExit 가
            // 탭을 닫는다(비정상 종료코드면 "[프로세스 종료됨]" 상태로 탭이 남아 에이전트만 죽은 듯 보인다).
            return returnRequested ? 0 : (child.HasExited ? child.ExitCode : 0);
        }
        catch (Exception ex)
        {
            return Fail($"외부 세션 실행 오류: {ex.Message}");
        }
    }

    private static bool TryDeleteTicket(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.Delete(path);
                return !File.Exists(path);
            }
            catch
            {
                if (attempt < 2) Thread.Sleep(20);
            }
        }
        return false;
    }

    private static void RelayInput(
        TerminalSession session,
        Stream input,
        ManualResetEventSlim completed)
    {
        try
        {
            var buffer = new byte[4096];
            while (session.IsAlive && !completed.IsSet)
            {
                int read = input.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                if (!session.TryWrite(chunk)) break;
            }
        }
        catch { /* 콘솔 탭 닫힘/입력 핸들 해제 */ }
        finally
        {
            // 외부 탭이 닫혀 stdin이 끊기면 에이전트도 함께 정리한다.
            completed.Set();
        }
    }

    private sealed class StandardHandleScope : IDisposable
    {
        private readonly IntPtr _input;
        private readonly IntPtr _output;
        private readonly IntPtr _error;
        private bool _disposed;

        private StandardHandleScope(IntPtr input, IntPtr output, IntPtr error)
        {
            _input = input;
            _output = output;
            _error = error;
        }

        public static StandardHandleScope Detach()
        {
            var scope = new StandardHandleScope(
                GetStdHandle(StdInputHandle),
                GetStdHandle(StdOutputHandle),
                GetStdHandle(StdErrorHandle));
            SetStdHandle(StdInputHandle, IntPtr.Zero);
            SetStdHandle(StdOutputHandle, IntPtr.Zero);
            SetStdHandle(StdErrorHandle, IntPtr.Zero);
            return scope;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SetStdHandle(StdInputHandle, _input);
            SetStdHandle(StdOutputHandle, _output);
            SetStdHandle(StdErrorHandle, _error);
        }
    }

    private static int SelectDimension(int preferred, int console, int fallback, int min, int max)
    {
        // 외부 WT 창을 유일한 렌더 기준으로 삼는다. 내부 미러는 정지 스냅샷(블러)이라 논리 폭을
        // 맞출 필요가 없으므로, 예전처럼 min(preferred, console) 로 깎아 WT 정렬을 망가뜨리지 않는다.
        if (console > 0) return Math.Clamp(console, min, max);
        return Math.Clamp(preferred > 0 ? preferred : fallback, min, max);
    }

    private static (int Cols, int Rows) ReadConsoleSize()
    {
        try
        {
            var handle = GetStdHandle(StdOutputHandle);
            if (IsInvalidHandle(handle) || !GetConsoleScreenBufferInfo(handle, out var info))
                return (0, 0);
            int cols = info.Window.Right - info.Window.Left + 1;
            int rows = info.Window.Bottom - info.Window.Top + 1;
            return (cols, rows);
        }
        catch { return (0, 0); }
    }

    private static void WriteSizeAtomic(string path, int cols, int rows)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, $"{cols},{rows}", Encoding.ASCII);
            File.Move(temp, path, overwrite: true);
        }
        catch { /* 크기 정보 실패는 출력 릴레이를 중단하지 않는다 */ }
    }

    private static string QuoteWindowsArgument(string value)
    {
        if (value.Length > 0 && value.All(ch => !char.IsWhiteSpace(ch) && ch != '"'))
            return value;

        var result = new StringBuilder(value.Length + 2);
        result.Append('"');
        int backslashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }
            if (ch == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }
            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(ch);
        }
        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }

    private static int Fail(string message)
    {
        try
        {
            using var error = Console.OpenStandardError();
            var bytes = Encoding.UTF8.GetBytes(message + Environment.NewLine);
            error.Write(bytes, 0, bytes.Length);
            error.Flush();
        }
        catch { }
        return 2;
    }

    private static void TryAttachParentConsole()
    {
        try
        {
            var handle = GetStdHandle(StdOutputHandle);
            if (!IsInvalidHandle(handle) && GetConsoleMode(handle, out _)) return;
            AttachConsole(AttachParentProcess);
        }
        catch { }
    }

    private sealed class ConsoleModeScope : IDisposable
    {
        private readonly IntPtr _input;
        private readonly IntPtr _output;
        private readonly uint? _inputMode;
        private readonly uint? _outputMode;
        private readonly uint? _inputCodePage;
        private readonly uint? _outputCodePage;

        private ConsoleModeScope(
            IntPtr input,
            IntPtr output,
            uint? inputMode,
            uint? outputMode,
            uint? inputCodePage,
            uint? outputCodePage)
        {
            _input = input;
            _output = output;
            _inputMode = inputMode;
            _outputMode = outputMode;
            _inputCodePage = inputCodePage;
            _outputCodePage = outputCodePage;
        }

        public static ConsoleModeScope TryCreate()
        {
            var input = GetStdHandle(StdInputHandle);
            var output = GetStdHandle(StdOutputHandle);
            uint? oldInput = null;
            uint? oldOutput = null;
            uint? oldInputCodePage = null;
            uint? oldOutputCodePage = null;

            if (!IsInvalidHandle(input) && GetConsoleMode(input, out var inputMode))
            {
                oldInput = inputMode;
                var raw = (inputMode | EnableVirtualTerminalInput | EnableExtendedFlags)
                          & ~(EnableProcessedInput | EnableLineInput | EnableEchoInput | EnableQuickEditMode);
                SetConsoleMode(input, raw);
            }

            if (!IsInvalidHandle(output) && GetConsoleMode(output, out var outputMode))
            {
                oldOutput = outputMode;
                SetConsoleMode(output, outputMode | EnableVirtualTerminalProcessing);
            }

            var inputCodePage = GetConsoleCP();
            if (inputCodePage != 0)
            {
                oldInputCodePage = inputCodePage;
                SetConsoleCP(Utf8CodePage);
            }
            var outputCodePage = GetConsoleOutputCP();
            if (outputCodePage != 0)
            {
                oldOutputCodePage = outputCodePage;
                SetConsoleOutputCP(Utf8CodePage);
            }

            return new ConsoleModeScope(
                input, output, oldInput, oldOutput, oldInputCodePage, oldOutputCodePage);
        }

        public void Dispose()
        {
            try { if (_inputMode is { } input) SetConsoleMode(_input, input); } catch { }
            try { if (_outputMode is { } output) SetConsoleMode(_output, output); } catch { }
            try { if (_inputCodePage is { } inputCodePage) SetConsoleCP(inputCodePage); } catch { }
            try { if (_outputCodePage is { } outputCodePage) SetConsoleOutputCP(outputCodePage); } catch { }
        }
    }

    private const uint AttachParentProcess = 0xFFFFFFFF;
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint EnableProcessedInput = 0x0001;
    private const uint EnableLineInput = 0x0002;
    private const uint EnableEchoInput = 0x0004;
    private const uint EnableQuickEditMode = 0x0040;
    private const uint EnableExtendedFlags = 0x0080;
    private const uint EnableVirtualTerminalInput = 0x0200;
    private const uint EnableVirtualTerminalProcessing = 0x0004;
    private const uint Utf8CodePage = 65001;

    private static bool IsInvalidHandle(IntPtr handle)
        => handle == IntPtr.Zero || handle == new IntPtr(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmallRect
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConsoleScreenBufferInfo
    {
        public Coord Size;
        public Coord CursorPosition;
        public ushort Attributes;
        public SmallRect Window;
        public Coord MaximumWindowSize;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int standardHandle, IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr consoleHandle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr consoleHandle, uint mode);

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleCP();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCP(uint codePage);

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleOutputCP();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleOutputCP(uint codePage);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleScreenBufferInfo(
        IntPtr consoleOutput,
        out ConsoleScreenBufferInfo info);
}

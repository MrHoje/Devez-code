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
        using var consoleModes = ConsoleModeScope.TryCreate();
        using var completed = new ManualResetEventSlim(false);
        using var outputLog = new FileStream(
            spec.OutputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        using var standardInput = Console.OpenStandardInput();
        using var standardOutput = Console.OpenStandardOutput();

        Exception? relayFailure = null;
        int processExited = 0;
        // AttachConsole 직후 WT 콘솔 크기가 아직 준비 안 돼 0을 줄 수 있다. 여기서 0으로 시작하면
        // 기본폭(120)으로 에이전트가 뜬 뒤 첫 poll 에서 실제 폭으로 resize → 1회 reflow 가 dim wrap
        // 잔상을 남긴다. 잠깐(≤500ms) 유효 크기를 기다려 처음부터 올바른 폭으로 시작해 reflow 를 없앤다.
        var (consoleCols, consoleRows) = ReadConsoleSize();
        for (int wait = 0; consoleCols <= 0 && wait < 25; wait++)
        {
            Thread.Sleep(20);
            (consoleCols, consoleRows) = ReadConsoleSize();
        }
        int cols = SelectDimension(spec.PreferredCols, consoleCols, DefaultCols, MinCols, MaxCols);
        int rows = SelectDimension(spec.PreferredRows, consoleRows, DefaultRows, MinRows, MaxRows);
        WriteSizeAtomic(spec.SizePath, cols, rows);

        void RelayOutput(byte[] bytes)
        {
            try
            {
                // 미러 파일을 먼저 갱신해 WT 탭이 닫히는 순간의 마지막 출력도 앱이 읽을 수 있게 한다.
                outputLog.Write(bytes, 0, bytes.Length);
                outputLog.Flush();
                standardOutput.Write(bytes, 0, bytes.Length);
                standardOutput.Flush();
            }
            catch (Exception ex)
            {
                relayFailure = ex;
                completed.Set();
            }
        }

        void OnExited() => Interlocked.Exchange(ref processExited, 1);

        TerminalSession? session = null;
        try
        {
            var commandLine =
                "powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File " +
                QuoteWindowsArgument(spec.RunnerScriptPath);
            var colorFgBg = string.Equals(spec.Theme, "dark", StringComparison.OrdinalIgnoreCase)
                ? "15;0"
                : "0;15";

            // 프록시의 WT 표준 핸들이 자식 PowerShell로 직접 새면 실제 텍스트가 ConPTY를
            // 우회해 외부 화면에만 나오고 미러 로그에서는 사라진다. 생성 순간에만 부모 표준
            // 핸들을 분리해 모든 자식 출력이 반드시 ConPTY 파이프를 통과하게 한다.
            using (StandardHandleScope.Detach())
            {
                session = new TerminalSession(
                    commandLine,
                    spec.WorkingDirectory,
                    cols,
                    rows,
                    RelayOutput,
                    OnExited,
                    colorFgBg);
            }

            var inputThread = new Thread(() => RelayInput(session, standardInput, completed))
            {
                IsBackground = true,
                Name = "ExternalProxy-Input",
            };
            inputThread.Start();

            // resize 디바운스: 창 스냅 relayout·외부 앱 종료 등으로 콘솔 폭이 여러 프레임에 걸쳐
            // 흔들릴 때, 매 흔들림마다 ConPTY를 resize 하면 TUI(claude 등)가 매번 reflow 하며
            // 이전 wrap 꼬리를 dim 잔상으로 남긴다. 새 크기가 StableTicks 만큼 연속으로 유지될 때만
            // 한 번 적용해 reflow(=잔상) 횟수를 최소화한다.
            const int ResizeStableTicks = 2; // 100ms poll × 2 ≈ 200ms 안정 후 적용
            int pendingCols = cols, pendingRows = rows, stableTicks = 0;

            while (!completed.Wait(100))
            {
                // 프로세스 종료 통지는 ConPTY read thread보다 먼저 올 수 있다. ConPTY를 닫은 뒤
                // 파이프 EOF까지 실제로 배출해 종료 프레임/프롬프트 바이트를 보존한다.
                if (Volatile.Read(ref processExited) != 0)
                {
                    session.CompleteOutputAfterExit(2_000);
                    break;
                }

                // DevezCode "인앱으로 가져오기" 요청 — 탭 닫기와 동일하게 세션을 정리하고 종료해
                // lock 을 놓는다. 래퍼 PowerShell 이 -Wait 를 벗어나 종료되며 WT 탭도 자동으로 닫힌다.
                if (!string.IsNullOrEmpty(spec.ReturnPath) && File.Exists(spec.ReturnPath))
                {
                    completed.Set();
                    break;
                }

                var (newConsoleCols, newConsoleRows) = ReadConsoleSize();
                int nextCols = SelectDimension(
                    spec.PreferredCols, newConsoleCols, cols, MinCols, MaxCols);
                int nextRows = SelectDimension(
                    spec.PreferredRows, newConsoleRows, rows, MinRows, MaxRows);
                if (nextCols == cols && nextRows == rows)
                {
                    stableTicks = 0;
                    continue;
                }
                if (nextCols != pendingCols || nextRows != pendingRows)
                {
                    // 목표 크기가 계속 바뀌는 중(흔들림) — 안정 카운트 리셋 후 대기.
                    pendingCols = nextCols;
                    pendingRows = nextRows;
                    stableTicks = 0;
                    continue;
                }
                if (++stableTicks < ResizeStableTicks) continue; // 아직 안정되지 않음

                cols = nextCols;
                rows = nextRows;
                stableTicks = 0;
                session.Resize(cols, rows);
                WriteSizeAtomic(spec.SizePath, cols, rows);
            }

            if (relayFailure != null && session.IsAlive)
                session.Dispose();

            return session.ExitCode ?? (relayFailure == null ? 0 : 1);
        }
        catch (Exception ex)
        {
            try
            {
                var message = Encoding.UTF8.GetBytes(
                    $"\r\n\u001b[91m외부 세션 프록시 오류:\r\n{ex.Message}\u001b[0m\r\n");
                RelayOutput(message);
            }
            catch { }
            return 1;
        }
        finally
        {
            session?.Dispose();
            try { outputLog.Flush(); } catch { }
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

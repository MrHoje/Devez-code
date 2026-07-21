using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;

namespace DevezCode.Services;

/// <summary>
/// 전역 저수준 키보드 훅: 한자키 + 좌/우 방향키로 활성 세션 탭을 이전/다음으로 이동.
/// 임베디드 터미널(WebView2=별도 Edge 프로세스)이 키 입력을 점유 중이어도 훅이 먼저 가로채
/// 터미널로 전파되지 않게 차단(return 1)한 뒤 자체 탭 전환을 호출한다.
///
/// devez TerminalTabHotkey 를 단순화 — DevezCode 는 자체 창 안의 xterm 탭이라
/// 외부 Windows Terminal(UIA·wt.exe) 연동이 필요 없다.
///
/// 시스템 전역으로 동작 — 우리 앱이 포그라운드가 아니어도(다른 앱/터미널이 점유 중이어도)
/// 한자+방향키를 가로챈다. 콜백에서 탭 전환 + 창 활성화(앞으로)를 처리한다.
/// (트레이드오프: 한자+좌/우 조합은 모든 앱에서 가로채진다. 한자/방향키 단독은 통과.)
///
/// <para>훅은 전용 스레드(자체 메시지 펌프)에 설치한다. WH_KEYBOARD_LL 콜백은 훅을 설치한
/// 스레드의 메시지 큐에서 처리되므로, UI 스레드에 설치하면 UI 스레드가 바쁠 때(레이아웃/렌더 폭주 등)
/// 시스템 전체 키 입력이 LowLevelHooksTimeout(기본 300ms)까지 밀린다. 전용 스레드에 두면 UI 부하와
/// 무관하게 즉시 서비스돼 다른 앱 키보드가 느려지지 않는다. 콜백의 실제 처리(탭 전환)는 UI 스레드로
/// BeginInvoke 해 넘긴다.</para>
/// </summary>
public static class GlobalTabHotkey
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN     = 0x0100;
    private const int WM_SYSKEYDOWN  = 0x0104;
    private const int WM_KEYUP       = 0x0101;
    private const int WM_SYSKEYUP    = 0x0105;
    private const int WM_QUIT        = 0x0012;

    private const int VK_UP    = 0x26;
    private const int VK_DOWN  = 0x28;
    private const int VK_ENTER = 0x0D;
    private const int VK_ESC   = 0x1B;

    // 사용자 설정 가능 — 기본 한자(0x19) + 좌(0x25)/우(0x27) 방향키. SettingsService 에서 로드.
    private static volatile int _modVk  = 0x19;
    private static volatile int _prevVk = 0x25;
    private static volatile int _nextVk = 0x27;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    private static HookProc? _proc;
    private static IntPtr _hook = IntPtr.Zero;
    private static bool _modDown;             // 훅 스레드에서만 접근
    private static Action<bool>? _onPrevNext; // 인자 true = 다음, false = 이전
    private static Action<int>? _captureCallback; // 키 리바인드 캡처 모드: 다음 키다운 1회를 가로채 콜백

    // 프로젝트 탐색(수식키+↑/↓) — 콜백은 UI 스레드에서 호출. _navActive 는 하이라이트가 떠 있는 동안만 true
    // (앱 활성 시에만 UI 가 켠다) → 그때만 Enter/Esc 를 가로채 터미널로 전파되지 않게 한다.
    private static Action<bool>? _onProjectNav; // 인자 true = 아래, false = 위
    private static Action? _onNavCommit;        // Enter
    private static Action? _onNavCancel;        // Esc
    private static volatile bool _navActive;

    /// <summary>프로젝트 탐색 콜백 등록. 하이라이트 활성 여부는 SetNavActive 로 알린다.</summary>
    public static void ConfigureProjectNav(Action<bool> onNav, Action onCommit, Action onCancel)
    {
        _onProjectNav = onNav; _onNavCommit = onCommit; _onNavCancel = onCancel;
    }

    /// <summary>탐색 하이라이트 활성 상태 갱신 — true 인 동안만 Enter/Esc 를 가로챈다.</summary>
    public static void SetNavActive(bool active) => _navActive = active;

    private static Thread? _hookThread;
    private static uint _hookThreadId;

    /// <summary>키 매핑 적용. 런타임 변경 가능(훅 재설치 불필요).</summary>
    public static void Configure(int modVk, int prevVk, int nextVk)
    {
        _modVk = modVk; _prevVk = prevVk; _nextVk = nextVk;
        _modDown = false;
    }

    /// <summary>훅 설치. onPrevNext = 탭 이동 콜백(UI 스레드에서 호출됨).
    /// 훅은 전용 스레드에 설치돼 UI 스레드 부하와 무관하게 즉시 서비스된다.</summary>
    public static void Install(Action<bool> onPrevNext)
    {
        _onPrevNext = onPrevNext;
        if (_hookThread != null) return;

        using var ready = new ManualResetEventSlim(false);
        _hookThread = new Thread(() =>
        {
            _hookThreadId = GetCurrentThreadId();
            _proc = Callback;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            ready.Set();

            // 저수준 훅 서비스를 위한 메시지 펌프. WM_QUIT(Uninstall) 수신 시 GetMessage 가 0 을 반환해 종료.
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
            _proc = null;
        })
        { IsBackground = true, Name = "GlobalTabHotkey" };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        ready.Wait(2000); // 훅 설치 완료까지 대기(리바인드 캡처가 즉시 동작하도록)
    }

    /// <summary>키 리바인드용 캡처 시작. 다음 키다운 1회를 가로채(전파 차단) vk 를 콜백으로 전달.
    /// 훅이 설치돼 있어야 동작(앱 실행 중엔 항상 설치됨). 콜백은 UI 스레드에서 호출된다.</summary>
    public static void BeginCapture(Action<int> onKey) => _captureCallback = onKey;
    public static void CancelCapture() => _captureCallback = null;

    public static void Uninstall()
    {
        var t = _hookThread;
        if (t == null) return;
        // 훅 스레드의 메시지 루프를 깨워 종료(GetMessage 가 0 반환 → 루프 탈출 → UnhookWindowsHookEx).
        PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        t.Join(2000);
        _hookThread = null;
    }

    private static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vk = Marshal.ReadInt32(lParam);
            bool isDown = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
            bool isUp   = wParam == WM_KEYUP   || wParam == WM_SYSKEYUP;

            // 캡처 모드: 다음 키다운 1회를 가로채 리바인드 콜백으로 전달(전파 차단).
            if (_captureCallback != null && isDown)
            {
                var cap = _captureCallback;
                _captureCallback = null;
                Application.Current?.Dispatcher.BeginInvoke(() => cap(vk));
                return (IntPtr)1;
            }

            // 탐색 하이라이트가 떠 있는 동안 Enter/Esc 는 가로채 터미널로 전파되지 않게 한다.
            // (_navActive 는 앱 활성 시에만 UI 가 켜므로 다른 앱으로 새지 않음.)
            if (_navActive && isDown && (vk == VK_ENTER || vk == VK_ESC))
            {
                var cb = vk == VK_ENTER ? _onNavCommit : _onNavCancel;
                if (cb != null)
                    Application.Current?.Dispatcher.BeginInvoke(() => cb());
                return (IntPtr)1;
            }

            if (vk == _modVk)
            {
                if (isDown) _modDown = true;
                else if (isUp) _modDown = false;
            }
            else if (isDown && _modDown && (vk == _prevVk || vk == _nextVk))
            {
                bool next = vk == _nextVk;
                var cb = _onPrevNext;
                if (cb != null)
                    Application.Current?.Dispatcher.BeginInvoke(() => cb(next));
                return (IntPtr)1; // 모든 앱에서 전파 차단(우리 앱이 포그라운드가 아니어도)
            }
            else if (isDown && _modDown && (vk == VK_UP || vk == VK_DOWN))
            {
                bool down = vk == VK_DOWN;
                var cb = _onProjectNav;
                if (cb != null)
                    Application.Current?.Dispatcher.BeginInvoke(() => cb(down));
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>가상키코드 → 사용자에게 보일 키 이름. 한글 IME 키·방향키는 한국어, 그 외는 WPF Key 이름.</summary>
    public static string KeyName(int vk) => vk switch
    {
        0x19 => "한자",
        0x15 => "한/영",
        0x25 => "←",
        0x26 => "↑",
        0x27 => "→",
        0x28 => "↓",
        0x20 => "Space",
        0x0D => "Enter",
        0x09 => "Tab",
        0x1B => "Esc",
        _ => SafeKeyName(vk),
    };

    private static string SafeKeyName(int vk)
    {
        try
        {
            var key = KeyInterop.KeyFromVirtualKey(vk);
            return key == Key.None ? $"0x{vk:X2}" : key.ToString();
        }
        catch { return $"0x{vk:X2}"; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);
}

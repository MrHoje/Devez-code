using System.Runtime.InteropServices;
using System.Windows;

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
/// </summary>
public static class GlobalTabHotkey
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN     = 0x0100;
    private const int WM_SYSKEYDOWN  = 0x0104;
    private const int WM_KEYUP       = 0x0101;
    private const int WM_SYSKEYUP    = 0x0105;

    private const int VK_HANJA = 0x19;
    private const int VK_LEFT  = 0x25;
    private const int VK_RIGHT = 0x27;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    private static HookProc? _proc;
    private static IntPtr _hook = IntPtr.Zero;
    private static bool _hanjaDown;
    private static Action<bool>? _onPrevNext; // 인자 true = 다음(오른쪽), false = 이전(왼쪽)

    /// <summary>훅 설치. onPrevNext = 탭 이동 콜백(UI 스레드에서 호출됨).</summary>
    public static void Install(Action<bool> onPrevNext)
    {
        _onPrevNext = onPrevNext;
        if (_hook != IntPtr.Zero) return;
        _proc = Callback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
    }

    public static void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
    }

    private static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vk = Marshal.ReadInt32(lParam);
            bool isDown = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
            bool isUp   = wParam == WM_KEYUP   || wParam == WM_SYSKEYUP;

            if (vk == VK_HANJA)
            {
                if (isDown) _hanjaDown = true;
                else if (isUp) _hanjaDown = false;
            }
            else if (isDown && _hanjaDown && (vk == VK_LEFT || vk == VK_RIGHT))
            {
                bool next = vk == VK_RIGHT;
                var cb = _onPrevNext;
                if (cb != null)
                    Application.Current?.Dispatcher.BeginInvoke(() => cb(next));
                return (IntPtr)1; // 모든 앱에서 전파 차단(우리 앱이 포그라운드가 아니어도)
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);
}

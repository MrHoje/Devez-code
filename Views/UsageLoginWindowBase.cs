using System.Windows;

namespace DevezCode.Views;

/// <summary>계정 사용량 공급자 로그인 창의 공통 상태.</summary>
public abstract class UsageLoginWindowBase : BrowserPopupWindowBase
{
    /// <summary>로그인 자격증명 저장 성공 여부.</summary>
    public bool Captured { get; protected set; }

    // 계정 등록에서는 기존 CLI/사용량 인증을 바꾸지 않고 성공 응답만 호출자에게 돌려준다.
    public string? TokenResponse { get; protected set; }

    protected static bool IsOAuthCallback(string address, string expected)
    {
        var callback = new Uri(expected);
        return Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && uri.Scheme == callback.Scheme && uri.Host == callback.Host && uri.Port == callback.Port
            && uri.AbsolutePath == callback.AbsolutePath;
    }

    protected UsageLoginWindowBase(Window? owner, string title) : base(owner, title, 680) { }
}

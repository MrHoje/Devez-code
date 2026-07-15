using System.Windows;

namespace DevezCode.Views;

/// <summary>계정 사용량 공급자 로그인 창의 공통 상태.</summary>
public abstract class UsageLoginWindowBase : BrowserPopupWindowBase
{
    /// <summary>로그인 자격증명 저장 성공 여부.</summary>
    public bool Captured { get; protected set; }

    protected UsageLoginWindowBase(Window? owner, string title) : base(owner, title, 680) { }
}

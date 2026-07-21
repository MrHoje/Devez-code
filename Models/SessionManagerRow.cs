namespace DevezCode.Models;

/// <summary>세션 관리자 팝업의 한 행. 원본 <see cref="SessionItem"/>을 감싸고 선택 상태만 추가로 들고 있다.
/// 이름·상태·숨김·잠금 등은 원본 세션에 직접 바인딩한다.</summary>
public sealed class SessionManagerRow : NotifyBase
{
    public SessionManagerRow(SessionItem session) => Session = session;

    public SessionItem Session { get; }

    /// <summary>잠금 세션은 선택(체크)할 수 없다 — 표시만 한다.</summary>
    public bool Selectable => !Session.IsLocked;

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }
}

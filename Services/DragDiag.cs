namespace DevezCode.Services;

/// <summary>외부 파일 드래그 경로 진단(임시 계측). DragOver 는 초당 수십 번 오므로 같은 메시지는
/// 250ms 안에서 한 번만 기록해 diag.log 가 폭주하지 않게 한다. 원인 확인 후 제거 예정.</summary>
public static class DragDiag
{
    private static string _last = "";
    private static DateTime _lastAt = DateTime.MinValue;

    public static void Log(string msg)
    {
        var now = DateTime.Now;
        if (msg == _last && (now - _lastAt).TotalMilliseconds < 250) return;
        _last = msg;
        _lastAt = now;
        DiagLog.Write("[drag] " + msg);
    }
}

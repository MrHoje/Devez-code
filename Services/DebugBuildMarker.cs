#if DEBUG
using System.Windows.Controls;

namespace DevezCode.Services;

/// <summary>Debug 빌드 전용 표식. 설치본(Release)과 나란히 띄워 개발할 때 두 창을 구분한다.
/// Release 빌드에는 이 파일 전체가 컴파일되지 않는다.</summary>
internal static class DebugBuildMarker
{
    /// <summary>좌상단 타이틀 로고를 빨간 "DEBUG" 로 바꾼다.</summary>
    public static void MarkTitle(TextBlock? title)
    {
        if (title == null) return;
        title.Text = "DEBUG";
        title.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
    }
}
#endif

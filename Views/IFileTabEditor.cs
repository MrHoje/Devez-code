using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace DevezCode.Views;

public interface IFileTabEditor
{
    event EventHandler? CloseRequested;
    event EventHandler? DirtyChanged;
    /// <summary>에디터 표면 클릭/포커스 — 분할 시 이 패널을 포커스 패널로 지정(airspace 우회).</summary>
    event EventHandler? Interacted;
    string? FilePath { get; }
    bool IsDirty { get; }
    bool LoadFile(string path);
    bool Save();
    void RequestClose();
    bool Focus();
    /// <summary>airspace 우회 — 현재 편집기 화면 스냅샷. WebView2 기반(md) 에디터만 비트맵을 주고,
    /// WPF 네이티브 에디터는 null(오버레이가 그대로 덮으므로 캡처 불필요).</summary>
    Task<BitmapSource?> CaptureSnapshotAsync();
}

public static class FileTabEditorExtensions
{
    public static UserControl AsControl(this IFileTabEditor editor) => (UserControl)editor;
}

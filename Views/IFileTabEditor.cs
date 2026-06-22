using System.Windows.Controls;

namespace DevezCode.Views;

public interface IFileTabEditor
{
    event EventHandler? CloseRequested;
    string? FilePath { get; }
    bool IsDirty { get; }
    bool LoadFile(string path);
    void RequestClose();
    bool Focus();
}

public static class FileTabEditorExtensions
{
    public static UserControl AsControl(this IFileTabEditor editor) => (UserControl)editor;
}

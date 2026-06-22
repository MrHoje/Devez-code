using System.Windows.Controls;

namespace DevezCode.Views;

public interface IFileTabEditor
{
    event EventHandler? CloseRequested;
    event EventHandler? DirtyChanged;
    string? FilePath { get; }
    bool IsDirty { get; }
    bool LoadFile(string path);
    bool Save();
    void RequestClose();
    bool Focus();
}

public static class FileTabEditorExtensions
{
    public static UserControl AsControl(this IFileTabEditor editor) => (UserControl)editor;
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using DevezCode.Models;

namespace DevezCode.Views;

public partial class TaskView : INotifyPropertyChanged
{
    public ObservableCollection<TaskItem> AllTasks { get; } = new();

    public TaskView()
    {
        InitializeComponent();
        AllTasks.CollectionChanged += (_, _) =>
        {
            EmptyText.Visibility = AllTasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
    }

    public void SetTasks(System.Collections.Generic.IEnumerable<TaskItem> tasks)
    {
        AllTasks.Clear();
        foreach (var t in tasks) AllTasks.Add(t);
    }

    public void AddOrUpdate(TaskItem item)
    {
        var existing = AllTasks.FirstOrDefault(t => t.Id == item.Id);
        if (existing != null)
        {
            var idx = AllTasks.IndexOf(existing);
            AllTasks[idx] = item;
        }
        else
        {
            AllTasks.Add(item);
        }
    }

    public void Remove(string taskId)
    {
        var existing = AllTasks.FirstOrDefault(t => t.Id == taskId);
        if (existing != null) AllTasks.Remove(existing);
    }

    public void ClearSource(string source)
    {
        for (int i = AllTasks.Count - 1; i >= 0; i--)
            if (AllTasks[i].Source == source) AllTasks.RemoveAt(i);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using DevezCode.Models;

namespace DevezCode.Views;

public partial class TaskView : INotifyPropertyChanged
{
    public ObservableCollection<TaskItem> AllTasks { get; } = new();

    public ObservableCollection<TaskItem> InProgressTasks { get; } = new();
    public ObservableCollection<TaskItem> PendingTasks { get; } = new();
    public ObservableCollection<TaskItem> CompletedTasks { get; } = new();

    public TaskView()
    {
        InitializeComponent();
        AllTasks.CollectionChanged += OnAllTasksChanged;
        UpdateProgressBar();
    }

    public void SetTasks(IEnumerable<TaskItem> tasks)
    {
        AllTasks.Clear();
        foreach (var t in tasks) AllTasks.Add(t);
        Regroup();
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
        Regroup();
    }

    public void Remove(string taskId)
    {
        var existing = AllTasks.FirstOrDefault(t => t.Id == taskId);
        if (existing != null) AllTasks.Remove(existing);
        Regroup();
    }

    public void ClearSource(string source)
    {
        for (int i = AllTasks.Count - 1; i >= 0; i--)
        {
            if (AllTasks[i].Source == source) AllTasks.RemoveAt(i);
        }
        Regroup();
    }

    private void OnAllTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Regroup();
    }

    private void Regroup()
    {
        InProgressTasks.Clear();
        PendingTasks.Clear();
        CompletedTasks.Clear();

        foreach (var t in AllTasks)
        {
            if (t.IsCompleted || t.IsCancelled) CompletedTasks.Add(t);
            else if (t.IsInProgress) InProgressTasks.Add(t);
            else PendingTasks.Add(t);
        }

        UpdateProgressBar();
        UpdateEmptyState();
        OnPropertyChanged(nameof(InProgressTasks));
        OnPropertyChanged(nameof(PendingTasks));
        OnPropertyChanged(nameof(CompletedTasks));
    }

    private void UpdateProgressBar()
    {
        var total = AllTasks.Count;
        if (total == 0)
        {
            ProgressFill.Width = 0;
            return;
        }
        var done = CompletedTasks.Count;
        var pct = done / (double)total;
        ProgressFill.Width = 80 * pct;
    }

    private void UpdateEmptyState()
    {
        EmptyText.Visibility = AllTasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

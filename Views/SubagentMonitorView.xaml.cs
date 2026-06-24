using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using DevezCode.Models;
using DevezCode.Services;

namespace DevezCode.Views;

public partial class SubagentMonitorView : INotifyPropertyChanged
{
    public ObservableCollection<SubagentStatusItem> RunningAgents { get; } = new();
    public ObservableCollection<SubagentStatusItem> CompletedAgents { get; } = new();

    public event EventHandler<string>? OpenTranscriptRequested;

    public SubagentMonitorView()
    {
        InitializeComponent();
        RunningAgents.CollectionChanged += (_, _) => RefreshEmpty();
        CompletedAgents.CollectionChanged += (_, _) => RefreshEmpty();
    }

    private void RefreshEmpty()
    {
        EmptyText.Visibility = RunningAgents.Count == 0 && CompletedAgents.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>SubagentStatusItem 을 Running/Completed 그룹에 추가/갱신.</summary>
    public void AddOrUpdate(SubagentStatusItem item)
    {
        var running = RunningAgents.FirstOrDefault(a => a.AgentId == item.AgentId);
        if (running != null)
        {
            RunningAgents.Remove(running);
            if (!item.IsFinished)
            {
                RunningAgents.Insert(0, item);
                return;
            }
        }
        else
        {
            var completed = CompletedAgents.FirstOrDefault(a => a.AgentId == item.AgentId);
            if (completed != null) CompletedAgents.Remove(completed);
        }

        if (item.IsFinished)
        {
            CompletedAgents.Insert(0, item);
            while (CompletedAgents.Count > 50) CompletedAgents.RemoveAt(CompletedAgents.Count - 1);
        }
        else
        {
            RunningAgents.Insert(0, item);
        }
    }

    /// <summary>에이전트 제거 (파일 삭제 시).</summary>
    public void Remove(string agentId)
    {
        var r = RunningAgents.FirstOrDefault(a => a.AgentId == agentId);
        if (r != null) RunningAgents.Remove(r);
        var c = CompletedAgents.FirstOrDefault(a => a.AgentId == agentId);
        if (c != null) CompletedAgents.Remove(c);
    }

    /// <summary>지정한 방에 속한 모든 에이전트 제거 (세션 전환/종료 시).</summary>
    public void ClearRoom(string roomId)
    {
        for (int i = RunningAgents.Count - 1; i >= 0; i--)
            if (RunningAgents[i].RoomId == roomId) RunningAgents.RemoveAt(i);
        for (int i = CompletedAgents.Count - 1; i >= 0; i--)
            if (CompletedAgents[i].RoomId == roomId) CompletedAgents.RemoveAt(i);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>탭 진입 시 초기화 (브라우저 WebView2 와 동일 패턴).</summary>
    public void EnsureStarted() { }

    private void AgentItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SubagentStatusItem item }) return;
        e.Handled = true;

        var dialog = new SubagentTranscriptView(item);
        if (Application.Current.MainWindow is Window main && main.IsLoaded && main != dialog)
            dialog.Owner = main;
        dialog.ShowDialog();

        if (!string.IsNullOrEmpty(dialog.RequestedFilePath))
            OpenTranscriptRequested?.Invoke(this, dialog.RequestedFilePath);
    }
}

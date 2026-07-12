using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DevezCode.Services;

namespace DevezCode.Views;

public partial class WakeSchedulerWindow : Window
{
    private static readonly DayOfWeek[] EveryDay = Enum.GetValues<DayOfWeek>();
    private readonly List<WakeScheduleEntry> _entries;
    private readonly List<WakeScheduleEntry> _hiddenEntries;
    private WakeScheduleEntry? _selectedSchedule;
    public bool Saved { get; private set; }

    public WakeSchedulerWindow(Window owner)
    {
        InitializeComponent();
        Owner = owner;

        var agents = AgentRegistry.GetEnabledAndInstalled()
            .Where(a => string.Equals(a.Id, "claude", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(a.Id, "codex", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var providerIds = agents.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var schedules = SettingsService.LoadWakeSchedules().Select(Copy).ToList();
        _entries = schedules.Where(e => providerIds.Contains(e.Provider)).ToList();
        _hiddenEntries = schedules.Where(e => !providerIds.Contains(e.Provider)).ToList();

        AgentList.ItemsSource = agents;
        EmptyHint.Visibility = agents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EditorPanel.IsEnabled = agents.Count > 0;
        if (agents.Count > 0) AgentList.SelectedIndex = 0;

        RootContent.SizeChanged += (_, _) => ApplyRoundedClip();
        Loaded += (_, _) =>
        {
            WindowCenter.CenterOverOwner(this);
            ApplyRoundedClip();
        };
    }

    private void AgentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyEditorToSelectedSchedule();

        var agent = AgentList.SelectedItem as AgentDef;
        EditorPanel.DataContext = agent;
        EditorPanel.IsEnabled = agent != null;
        if (agent == null)
        {
            _selectedSchedule = null;
            return;
        }

        _selectedSchedule = _entries.FirstOrDefault(e =>
            string.Equals(e.Provider, agent.Id, StringComparison.OrdinalIgnoreCase));
        if (_selectedSchedule == null)
        {
            _selectedSchedule = new WakeScheduleEntry
            {
                Provider = agent.Id,
                Weekdays = EveryDay.ToList(),
            };
            _entries.Add(_selectedSchedule);
        }

        TimeBox.Text = _selectedSchedule.Time;
        EnabledToggle.IsChecked = _selectedSchedule.Enabled;
        LastStatus.Text = _selectedSchedule.StatusDisplay;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ApplyEditorToSelectedSchedule();
        var invalid = _entries.FirstOrDefault(entry =>
            !TimeSpan.TryParseExact(entry.Time, @"hh\:mm", CultureInfo.InvariantCulture, out _));
        if (invalid != null)
        {
            MessageBox.Show(this, $"{invalid.ProviderDisplayName} 시간을 24시간 형식으로 입력하세요. (예: 09:00)", "깨우기",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!SettingsService.SaveWakeSchedules(_hiddenEntries.Concat(_entries)))
        {
            MessageBox.Show(this, "설정을 저장하지 못했습니다. 잠시 후 다시 시도하세요.", "깨우기",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Saved = true;
        DialogResult = true;
    }

    private void ApplyEditorToSelectedSchedule()
    {
        if (_selectedSchedule == null) return;
        _selectedSchedule.Time = TimeBox.Text.Trim();
        _selectedSchedule.Enabled = EnabledToggle.IsChecked == true;
    }

    private static WakeScheduleEntry Copy(WakeScheduleEntry entry) => new()
    {
        Id = entry.Id,
        Provider = entry.Provider,
        Weekdays = entry.Weekdays?.Distinct().ToList() ?? new List<DayOfWeek>(),
        Time = entry.Time,
        Enabled = entry.Enabled,
        LastOccurrenceKey = entry.LastOccurrenceKey,
        LastResult = entry.LastResult,
    };

    private void ApplyRoundedClip()
    {
        double width = RootContent.ActualWidth;
        double height = RootContent.ActualHeight;
        if (width <= 0 || height <= 0) return;
        RootContent.Clip = new RectangleGeometry(new Rect(0, 0, width, height), 13, 13);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}

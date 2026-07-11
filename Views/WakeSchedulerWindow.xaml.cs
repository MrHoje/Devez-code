using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DevezCode.Services;

namespace DevezCode.Views;

public partial class WakeSchedulerWindow : Window
{
    private readonly ObservableCollection<WakeScheduleEntry> _entries = new();
    private readonly List<WakeScheduleEntry> _hiddenEntries = new();
    private bool _restoringSelection;
    private WakeScheduleEntry? _selected;
    private readonly IReadOnlyList<string> _providers;
    public bool Saved { get; private set; }

    public WakeSchedulerWindow(Window owner) {
        InitializeComponent();
        Owner = owner;
        _providers = AgentRegistry.GetEnabledAndInstalled()
            .Select(a => a.Id)
            .Where(a => string.Equals(a, "claude", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "codex", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ProviderCombo.ItemsSource = _providers;
        foreach (var item in SettingsService.LoadWakeSchedules())
        {
            var copy = Copy(item);
            if (_providers.Contains(copy.Provider, StringComparer.OrdinalIgnoreCase))
                _entries.Add(copy);
            else
                _hiddenEntries.Add(copy);
        }
        ScheduleList.ItemsSource = _entries;
        UpdateEmptyState();
        RootContent.SizeChanged += (_, _) => ApplyRoundedClip();
        Loaded += (_, _) => {
            WindowCenter.CenterOverOwner(this);
            ApplyRoundedClip();
        };
        if (_entries.Count > 0) ScheduleList.SelectedIndex = 0;
        else {
            DeleteButton.IsEnabled = false;
            EditorPanel.IsEnabled = false;
        }
    }

    private static WakeScheduleEntry Copy(WakeScheduleEntry e) => new() {
        Id = e.Id, Provider = e.Provider, Weekdays = e.Weekdays?.Distinct().ToList() ?? new(), Time = e.Time,
        Enabled = e.Enabled, LastOccurrenceKey = e.LastOccurrenceKey, LastResult = e.LastResult
    };

    private void Add_Click(object sender, RoutedEventArgs e) {
        if (_providers.Count == 0) { MessageBox.Show(this, "활성화된 Claude 또는 Codex가 없습니다.", "루틴", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (_selected != null && !ApplyEditor()) return;
        var entry = new WakeScheduleEntry { Provider = _providers[0], Weekdays = new List<DayOfWeek> { DateTime.Now.DayOfWeek } };
        _entries.Add(entry); ScheduleList.SelectedItem = entry;
        UpdateEmptyState();
    }

    private void ScheduleList_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (_restoringSelection) return;
        var next = ScheduleList.SelectedItem as WakeScheduleEntry;
        if (_selected != null && !ReferenceEquals(_selected, next) && !ApplyEditor()) {
            _restoringSelection = true;
            ScheduleList.SelectedItem = _selected;
            _restoringSelection = false;
            return;
        }
        if (_selected != null && !ReferenceEquals(_selected, next))
            ScheduleList.Items.Refresh();

        _selected = next;
        if (_selected == null) {
            DeleteButton.IsEnabled = false;
            EditorPanel.IsEnabled = false;
            return;
        }
        EditorPanel.IsEnabled = true;
        DeleteButton.IsEnabled = true;
        ProviderCombo.SelectedItem = _providers.FirstOrDefault(p => string.Equals(p, _selected.Provider, StringComparison.OrdinalIgnoreCase));
        TimeBox.Text = _selected.Time;
        EnabledToggle.IsChecked = _selected.Enabled;
        LastStatus.Text = _selected.StatusDisplay;
        SetDays(_selected.Weekdays);
    }

    private void Delete_Click(object sender, RoutedEventArgs e) {
        if (_selected == null) return;
        var deleted = _selected;
        var index = _entries.IndexOf(deleted);
        _selected = null;
        _entries.Remove(deleted);
        ScheduleList.SelectedIndex = Math.Min(index, _entries.Count - 1);
        UpdateEmptyState();
    }

    private void Save_Click(object sender, RoutedEventArgs e) {
        if (_selected != null && !ApplyEditor()) return;
        if (_entries.Any(e => e.Weekdays.Count == 0)) { MessageBox.Show(this, "요일을 하나 이상 선택하세요.", "루틴", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (!SettingsService.SaveWakeSchedules(_hiddenEntries.Concat(_entries))) {
            MessageBox.Show(this, "설정을 저장하지 못했습니다. 잠시 후 다시 시도하세요.", "루틴", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        Saved = true;
        DialogResult = true;
    }

    private bool ApplyEditor() {
        if (!TimeSpan.TryParseExact(TimeBox.Text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out _)) {
            MessageBox.Show(this, "시간은 24시간 형식으로 입력하세요. (예: 09:00)", "루틴", MessageBoxButton.OK, MessageBoxImage.Information); return false;
        }
        var days = GetDays();
        if (days.Count == 0) { MessageBox.Show(this, "요일을 하나 이상 선택하세요.", "루틴", MessageBoxButton.OK, MessageBoxImage.Information); return false; }
        if (_providers.Count == 0) { MessageBox.Show(this, "활성화된 Claude 또는 Codex가 없습니다.", "루틴", MessageBoxButton.OK, MessageBoxImage.Information); return false; }
        _selected.Provider = (ProviderCombo.SelectedItem as string) ?? _providers[0];
        _selected.Time = TimeBox.Text.Trim();
        _selected.Weekdays = days;
        _selected.Enabled = EnabledToggle.IsChecked == true;
        // WakeScheduleEntry exposes computed display values without change notifications.
        // Refresh the card immediately so edits are visible before selection changes or save.
        ScheduleList.Items.Refresh();
        return true;
    }

    private List<DayOfWeek> GetDays() => new[] { (Mon, DayOfWeek.Monday), (Tue, DayOfWeek.Tuesday), (Wed, DayOfWeek.Wednesday), (Thu, DayOfWeek.Thursday), (Fri, DayOfWeek.Friday), (Sat, DayOfWeek.Saturday), (Sun, DayOfWeek.Sunday) }.Where(x => x.Item1.IsChecked == true).Select(x => x.Item2).ToList();
    private void SetDays(IEnumerable<DayOfWeek> days) { var set = days.ToHashSet(); Mon.IsChecked = set.Contains(DayOfWeek.Monday); Tue.IsChecked = set.Contains(DayOfWeek.Tuesday); Wed.IsChecked = set.Contains(DayOfWeek.Wednesday); Thu.IsChecked = set.Contains(DayOfWeek.Thursday); Fri.IsChecked = set.Contains(DayOfWeek.Friday); Sat.IsChecked = set.Contains(DayOfWeek.Saturday); Sun.IsChecked = set.Contains(DayOfWeek.Sunday); }
    private void EnabledToggle_Changed(object sender, RoutedEventArgs e) {
        if (_selected == null) return;
        _selected.Enabled = EnabledToggle.IsChecked == true;
        ScheduleList.Items.Refresh();
    }

    private void ApplyRoundedClip() {
        double width = RootContent.ActualWidth;
        double height = RootContent.ActualHeight;
        if (width <= 0 || height <= 0) return;
        RootContent.Clip = new RectangleGeometry(new Rect(0, 0, width, height), 13, 13);
    }
    private void UpdateEmptyState() => EmptyHint.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }
    private void Header_DragMove(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
}

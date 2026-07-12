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
    private readonly Func<string, Task<bool>> _ensureTrust;
    private readonly IReadOnlyList<AgentDef> _agents;
    private readonly List<WakeScheduleEntry> _entries;
    private readonly List<WakeScheduleEntry> _hiddenEntries;
    private WakeScheduleEntry? _selectedSchedule;
    private AgentDef? _untrustedAgent;
    private bool _trustCheckInProgress;
    private bool _timeFormatting;
    public bool Saved { get; private set; }

    public WakeSchedulerWindow(Window owner, Func<string, Task<bool>> ensureTrust)
    {
        InitializeComponent();
        Owner = owner;
        _ensureTrust = ensureTrust;

        _agents = AgentRegistry.GetEnabledAndInstalled()
            .Where(a => string.Equals(a.Id, "claude", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(a.Id, "codex", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var providerIds = _agents.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var schedules = SettingsService.LoadWakeSchedules().Select(Copy).ToList();
        _entries = schedules.Where(e => providerIds.Contains(e.Provider)).ToList();
        _hiddenEntries = schedules.Where(e => !providerIds.Contains(e.Provider)).ToList();
        foreach (var entry in _entries) entry.Weekdays = EveryDay.ToList();

        var times = new List<string>();
        for (int hour = 0; hour < 24; hour++)
            for (int minute = 0; minute < 60; minute += 30)
                times.Add($"{hour:D2}:{minute:D2}");
        TimeCombo.ItemsSource = times;
        TimeCombo.Loaded += TimeCombo_SetupInput;

        AgentList.ItemsSource = _agents;
        EmptyHint.Visibility = _agents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EditorPanel.IsEnabled = _agents.Count > 0;
        if (_agents.Count > 0) AgentList.SelectedIndex = 0;

        RootContent.SizeChanged += (_, _) => ApplyRoundedClip();
        Loaded += async (_, _) =>
        {
            WindowCenter.CenterOverOwner(this);
            ApplyRoundedClip();
            RefreshTrustState();
            await EnsureRequiredTrustAsync();
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

        TimeCombo.Text = _selectedSchedule.Time;
        EnabledToggle.IsChecked = _selectedSchedule.Enabled;
        LastExecutionText.Text = _selectedSchedule.LastExecutionDisplay;
        RefreshTrustState();
        if (IsLoaded) _ = EnsureRequiredTrustAsync();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ApplyEditorToSelectedSchedule();
        RefreshTrustState();
        if (_untrustedAgent != null) return;

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

    private void RefreshTrustState()
    {
        _untrustedAgent = _agents.FirstOrDefault(agent =>
            _entries.Any(entry => entry.Enabled &&
                string.Equals(entry.Provider, agent.Id, StringComparison.OrdinalIgnoreCase)) &&
            !WakeTrustService.IsTrusted(agent.Id));
        bool trusted = _untrustedAgent == null;
        SaveButton.IsEnabled = trusted && _agents.Count > 0;
        TrustPanel.Visibility = trusted ? Visibility.Collapsed : Visibility.Visible;
        if (_untrustedAgent != null)
        {
            TrustMessage.Text = $"{_untrustedAgent.DisplayName}: 프로젝트 경로의 신뢰 설정을 자동으로 확인하고 있습니다.\n" +
                                WakeTrustService.InstallDirectory;
        }
    }

    private void EnabledToggle_Click(object sender, RoutedEventArgs e)
    {
        ApplyEditorToSelectedSchedule();
        RefreshTrustState();
        _ = EnsureRequiredTrustAsync();
    }

    private async Task EnsureRequiredTrustAsync()
    {
        if (_trustCheckInProgress) return;
        _trustCheckInProgress = true;
        try
        {
            while (IsVisible)
            {
                ApplyEditorToSelectedSchedule();
                RefreshTrustState();
                var agent = _untrustedAgent;
                if (agent == null) return;

                TrustMessage.Text = $"{agent.DisplayName}: 프로젝트 경로의 신뢰 설정을 자동으로 처리하고 있습니다.\n" +
                                    WakeTrustService.InstallDirectory;
                if (!await _ensureTrust(agent.Id))
                {
                    RefreshTrustState();
                    TrustMessage.Text = $"{agent.DisplayName} 신뢰 설정을 자동으로 완료하지 못했습니다.";
                    return;
                }
            }
        }
        finally
        {
            _trustCheckInProgress = false;
        }
    }

    private void ApplyEditorToSelectedSchedule()
    {
        if (_selectedSchedule == null) return;
        _selectedSchedule.Time = TimeCombo.Text.Trim();
        _selectedSchedule.Enabled = EnabledToggle.IsChecked == true;
    }

    private void TimeCombo_SetupInput(object sender, RoutedEventArgs e)
    {
        if (TimeCombo.Template.FindName("PART_EditableTextBox", TimeCombo) is not TextBox textBox) return;
        textBox.PreviewTextInput += TimeTextBox_PreviewTextInput;
        textBox.TextChanged += TimeTextBox_AutoFormat;
        DataObject.AddPastingHandler(textBox, TimeTextBox_Pasting);
    }

    private static void TimeTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private void TimeTextBox_AutoFormat(object sender, TextChangedEventArgs e)
    {
        if (_timeFormatting || sender is not TextBox textBox) return;
        var digits = new string(textBox.Text.Where(char.IsDigit).ToArray());
        if (digits.Length > 4) digits = digits[..4];
        var formatted = digits.Length <= 2 ? digits : $"{digits[..2]}:{digits[2..]}";
        if (formatted == textBox.Text) return;
        _timeFormatting = true;
        textBox.Text = formatted;
        textBox.CaretIndex = formatted.Length;
        _timeFormatting = false;
    }

    private static void TimeTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(typeof(string)))
        {
            e.CancelCommand();
            return;
        }

        var digits = new string(((e.DataObject.GetData(typeof(string)) as string) ?? "")
            .Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
        {
            e.CancelCommand();
            return;
        }

        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, digits);
        e.DataObject = data;
    }

    private void TimeCombo_LostFocus(object sender, RoutedEventArgs e)
    {
        var text = TimeCombo.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        var (time, valid) = TryParseTime(text);
        TimeCombo.Text = valid ? $"{time.Hours:D2}:{time.Minutes:D2}" : "23:59";
    }

    private static (TimeSpan time, bool valid) TryParseTime(string text)
    {
        text = text.Trim();
        if (TimeSpan.TryParse(text, out var time) && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1))
            return (new TimeSpan(time.Hours, time.Minutes, 0), true);

        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (digits.Length is 1 or 2 && int.TryParse(digits, out var parsedHour) && parsedHour < 24)
            return (new TimeSpan(parsedHour, 0, 0), true);
        if (digits.Length is 3 or 4)
        {
            var hour = int.Parse(digits[..^2]);
            var minute = int.Parse(digits[^2..]);
            if (hour < 24 && minute < 60) return (new TimeSpan(hour, minute, 0), true);
        }

        return (TimeSpan.Zero, false);
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

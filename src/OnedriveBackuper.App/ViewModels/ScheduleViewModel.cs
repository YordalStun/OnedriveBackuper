using System.Windows.Input;
using OnedriveBackuper.Core;
using OnedriveBackuper.Windows;

namespace OnedriveBackuper.App.ViewModels;

public sealed class DayChoice(DayOfWeek day, bool isChecked, Action changed) : ObservableObject
{
    private bool isChecked = isChecked;

    public DayOfWeek Day { get; } = day;
    public string Name { get; } = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day);

    public bool IsChecked
    {
        get => isChecked;
        set
        {
            if (Set(ref isChecked, value))
            {
                changed();
            }
        }
    }
}

/// <summary>Automatic backups (Windows Task Scheduler) and automatic clean-up of old backups.</summary>
public sealed class ScheduleViewModel : ObservableObject
{
    public const string ScheduledArgument = "--scheduled";

    private readonly MainViewModel main;
    private bool enabled, retentionEnabled, dirty;
    private int frequencyIndex, hour, minute, everyHours, fullEveryDays, keepFullBackups;
    private string statusText = "";
    private Tone tone;

    public ScheduleViewModel(MainViewModel main)
    {
        this.main = main;
        var schedule = main.Settings.Schedule;
        var retention = main.Settings.Retention;
        enabled = schedule.Enabled;
        frequencyIndex = (int)schedule.Frequency;
        hour = schedule.Time.Hour;
        minute = schedule.Time.Minute;
        everyHours = schedule.EveryHours;
        retentionEnabled = retention.Enabled;
        fullEveryDays = retention.FullEveryDays;
        keepFullBackups = retention.KeepFullBackups;
        // Monday first.
        Days = Enumerable.Range(0, 7)
            .Select(i => (DayOfWeek)((i + 1) % 7))
            .Select(d => new DayChoice(d, schedule.Days.Contains(d), Changed))
            .ToList();
        SaveCommand = new RelayCommand(Save);
        UpdateTexts();
    }

    public IReadOnlyList<DayChoice> Days { get; }
    public IReadOnlyList<string> Frequencies { get; } = ["Every day", "On certain days of the week", "Every few hours"];
    public IReadOnlyList<int> Hours { get; } = Enumerable.Range(0, 24).ToList();
    public IReadOnlyList<int> Minutes { get; } = [0, 15, 30, 45];
    public IReadOnlyList<int> HourIntervals { get; } = [1, 2, 3, 4, 6, 8, 12];
    public IReadOnlyList<int> FullEveryChoices { get; } = [7, 14, 30, 60, 90, 180];
    public IReadOnlyList<int> KeepChoices { get; } = [1, 2, 3, 4, 5, 6, 8, 10, 12];

    public ICommand SaveCommand { get; }

    public bool Enabled { get => enabled; set { if (Set(ref enabled, value)) { Changed(); } } }
    public int FrequencyIndex { get => frequencyIndex; set { if (Set(ref frequencyIndex, value)) { Changed(); } } }
    public int Hour { get => hour; set { if (Set(ref hour, value)) { Changed(); } } }
    public int Minute { get => minute; set { if (Set(ref minute, value)) { Changed(); } } }
    public int EveryHours { get => everyHours; set { if (Set(ref everyHours, value)) { Changed(); } } }
    public bool RetentionEnabled { get => retentionEnabled; set { if (Set(ref retentionEnabled, value)) { Changed(); } } }
    public int FullEveryDays { get => fullEveryDays; set { if (Set(ref fullEveryDays, value)) { Changed(); } } }
    public int KeepFullBackups { get => keepFullBackups; set { if (Set(ref keepFullBackups, value)) { Changed(); } } }

    public bool IsWeekly => FrequencyIndex == (int)ScheduleFrequency.Weekly;
    public bool IsEveryFewHours => FrequencyIndex == (int)ScheduleFrequency.EveryFewHours;
    public string TimeLabel => IsEveryFewHours ? "Starting at" : "At";
    public string Summary => Build().Describe();
    public string NextRunText => Enabled && Build().NextRun(DateTime.Now) is { } next ? $"Next backup: {Friendly.When(next)}" : Enabled ? "Choose at least one day." : "";
    public string RetentionSummary =>
        $"You can go back roughly {FullEveryDays * KeepFullBackups} days. Older backups are deleted automatically after each backup, " +
        "but never one that a newer backup still needs.";

    public string StatusText { get => statusText; private set => Set(ref statusText, value); }
    public Tone Tone { get => tone; private set => Set(ref tone, value); }
    public bool Dirty { get => dirty; private set => Set(ref dirty, value); }

    private ScheduleSettings Build() => new()
    {
        Enabled = Enabled,
        Frequency = (ScheduleFrequency)FrequencyIndex,
        Time = new TimeOnly(Hour, Minute),
        Days = Days.Where(d => d.IsChecked).Select(d => d.Day).ToList(),
        EveryHours = EveryHours,
    };

    private void Changed()
    {
        Dirty = true;
        StatusText = "";
        UpdateTexts();
    }

    private void UpdateTexts()
    {
        OnPropertyChanged(nameof(IsWeekly));
        OnPropertyChanged(nameof(IsEveryFewHours));
        OnPropertyChanged(nameof(TimeLabel));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(NextRunText));
        OnPropertyChanged(nameof(RetentionSummary));
    }

    private void Save()
    {
        var schedule = Build();
        if (schedule.Enabled && schedule.NextRun(DateTime.Now) == null)
        {
            Tone = Tone.Warning;
            StatusText = "Choose at least one day of the week.";
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (schedule.Enabled)
                {
                    WindowsTaskScheduler.Register(schedule, Environment.ProcessPath!, ScheduledArgument);
                }
                else
                {
                    WindowsTaskScheduler.Unregister();
                }
            }
            main.Settings.Schedule = schedule;
            main.Settings.Retention = new RetentionSettings
            {
                Enabled = RetentionEnabled,
                FullEveryDays = FullEveryDays,
                KeepFullBackups = KeepFullBackups,
            };
            main.Settings.ScheduledExePath = schedule.Enabled ? Environment.ProcessPath : null;
            main.SaveSettings();
            Dirty = false;
            Tone = Tone.Good;
            StatusText = schedule.Enabled
                ? $"Saved. Windows will start the next backup {Friendly.When(schedule.NextRun(DateTime.Now)!.Value)}."
                : "Saved. Automatic backups are off.";
        }
        catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Tone = Tone.Bad;
            StatusText = ex.Message;
        }
    }

    /// <summary>
    /// Keeps the scheduled task pointing at this copy of the program, in case it was moved or updated.
    /// Called at start-up; quietly does nothing when the schedule is off or already right.
    /// </summary>
    public static void RepairTaskIfMoved(AppSettings settings, Action save)
    {
        if (!OperatingSystem.IsWindows() || !settings.Schedule.Enabled || Environment.ProcessPath is not { } exe ||
            string.Equals(settings.ScheduledExePath, exe, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        try
        {
            WindowsTaskScheduler.Register(settings.Schedule, exe, ScheduledArgument);
            settings.ScheduledExePath = exe;
            save();
        }
        catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // The Schedule page shows the problem when the user next saves.
        }
    }
}

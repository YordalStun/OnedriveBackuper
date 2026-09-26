using System.Text.Json;
using System.Windows.Input;
using OnedriveBackuper.App.Services;
using OnedriveBackuper.Core;

namespace OnedriveBackuper.App.ViewModels;

public enum Page
{
    Home,
    Backups,
    Schedule,
    Settings,
}

public sealed record NavItem(Page Page, string Title, string Glyph);

public sealed class MainViewModel : ObservableObject
{
    private readonly string settingsPath;
    private NavItem selectedNav;
    private Tone tone;
    private string statusHeadline = "", statusDetail = "", nextRunText = "", sourceText = "", sourceFreeText = "", backupText = "", backupFreeText = "";
    private bool needsBackupFolder;

    public MainViewModel(string settingsPath)
    {
        this.settingsPath = settingsPath;
        Settings = AppSettings.Load(settingsPath);
        NavItems =
        [
            new(Page.Home, "Home", ""),
            new(Page.Backups, "Backups", ""),
            new(Page.Schedule, "Schedule", ""),
            new(Page.Settings, "Settings", ""),
        ];
        selectedNav = NavItems[0];

        Run = new RunViewModel(this);
        Backups = new BackupsViewModel(this);
        Schedule = new ScheduleViewModel(this);
        SettingsPage = new SettingsViewModel(this);

        BackUpNowCommand = new RelayCommand(() => StartBackup(forceFull: false), () => CanStartBackup);
        FullBackupNowCommand = new RelayCommand(() => StartBackup(forceFull: true), () => CanStartBackup);
        GoToCommand = new RelayCommand(page => Navigate((Page)page!));
        ChooseBackupFolderCommand = new RelayCommand(() => SettingsPage.BrowseBackupFolder());
        RefreshHome();
    }

    public AppSettings Settings { get; }
    public RunViewModel Run { get; }
    public BackupsViewModel Backups { get; }
    public ScheduleViewModel Schedule { get; }
    public SettingsViewModel SettingsPage { get; }
    public IReadOnlyList<NavItem> NavItems { get; }

    public ICommand BackUpNowCommand { get; }
    public ICommand FullBackupNowCommand { get; }
    public ICommand GoToCommand { get; }
    public ICommand ChooseBackupFolderCommand { get; }

    public NavItem SelectedNav
    {
        get => selectedNav;
        set
        {
            if (value != null && Set(ref selectedNav, value))
            {
                OnPropertyChanged(nameof(IsHome));
                OnPropertyChanged(nameof(IsBackups));
                OnPropertyChanged(nameof(IsSchedule));
                OnPropertyChanged(nameof(IsSettings));
                if (value.Page == Page.Backups)
                {
                    Backups.Load();
                }
                if (value.Page == Page.Home)
                {
                    RefreshHome();
                }
            }
        }
    }

    public bool IsHome => SelectedNav.Page == Page.Home;
    public bool IsBackups => SelectedNav.Page == Page.Backups;
    public bool IsSchedule => SelectedNav.Page == Page.Schedule;
    public bool IsSettings => SelectedNav.Page == Page.Settings;

    /// <summary>Anything running that should stop other actions (a backup, restore, check...).</summary>
    public bool IsBusy => Run.IsRunning || Backups.IsWorking || SettingsPage.IsWorking;

    public bool CanStartBackup => !IsBusy && !NeedsBackupFolder;

    /// <summary>Home shows the "choose a backup folder" card until one is chosen.</summary>
    public bool NeedsBackupFolder { get => needsBackupFolder; private set => Set(ref needsBackupFolder, value); }
    public bool ShowIdle => !Run.IsRunning && !Run.HasResult;
    public Tone Tone { get => tone; private set => Set(ref tone, value); }
    public string StatusHeadline { get => statusHeadline; private set => Set(ref statusHeadline, value); }
    public string StatusDetail { get => statusDetail; private set => Set(ref statusDetail, value); }
    public string NextRunText { get => nextRunText; private set => Set(ref nextRunText, value); }
    public string SourceText { get => sourceText; private set => Set(ref sourceText, value); }
    public string SourceFreeText { get => sourceFreeText; private set => Set(ref sourceFreeText, value); }
    public string BackupText { get => backupText; private set => Set(ref backupText, value); }
    public string BackupFreeText { get => backupFreeText; private set => Set(ref backupFreeText, value); }

    public void Navigate(Page page) => SelectedNav = NavItems.First(n => n.Page == page);

    public void StartBackup(bool forceFull, bool scheduled = false)
    {
        if (IsBusy)
        {
            return;
        }
        Navigate(Page.Home);
        Run.Start(forceFull, scheduled);
    }

    /// <summary>A copy of the settings for the backup thread, so editing settings meanwhile cannot disturb it.</summary>
    public AppSettings SettingsSnapshot() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(Settings, Json.Compact), Json.Compact)!;

    public void SaveSettings()
    {
        try
        {
            Settings.Save(settingsPath);
        }
        catch (IOException)
        {
            // Settings are saved again on the next change.
        }
        RefreshHome();
    }

    public void RecordLastRun(BackupJobResult result, DateTime started, bool scheduled)
    {
        Settings.LastRun = new LastRunInfo
        {
            Started = started,
            Finished = DateTimeOffset.Now,
            Result = result.Result,
            Summary = result.Headline,
            SetId = result.Report?.SetId ?? Settings.LastRun?.SetId,
            Scheduled = scheduled,
        };
        SaveSettings();
        Backups.Load();
    }

    public void OnBusyChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanStartBackup));
        OnPropertyChanged(nameof(ShowIdle));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Updates the home screen: last backup, next automatic backup, folders and free space.</summary>
    public void RefreshHome()
    {
        OnPropertyChanged(nameof(ShowIdle));
        NeedsBackupFolder = string.IsNullOrWhiteSpace(Settings.BackupFolder);
        OnPropertyChanged(nameof(CanStartBackup));

        var last = Settings.LastRun;
        (Tone, StatusHeadline) = last switch
        {
            null => (Tone.Neutral, "Not backed up yet"),
            { Result: RunResult.Succeeded } => (Tone.Good, "Your OneDrive is backed up"),
            { Result: RunResult.SucceededWithProblems } => (Tone.Warning, "Backed up, with some problems"),
            { Result: RunResult.Stopped } => (Tone.Warning, "The last backup was stopped"),
            _ => (Tone.Bad, "The last backup did not work"),
        };
        StatusDetail = last == null
            ? NeedsBackupFolder ? "Choose where to save your backups to get started." : "Press Back up now to make your first backup."
            : $"Last backup: {Friendly.When(last.Finished)}  ·  {last.Summary}{(last.Scheduled ? "  ·  automatic" : "")}";

        var schedule = Settings.Schedule;
        NextRunText = schedule.Enabled && schedule.NextRun(DateTime.Now) is { } next
            ? $"Next automatic backup: {Friendly.When(next)}  ({schedule.Describe().ToLowerInvariant()})"
            : "Automatic backups are off";

        var source = Settings.SourceFolder ?? AppSettings.DetectOneDriveFolder();
        SourceText = source ?? "Not found. Choose it in Settings.";
        SourceFreeText = FreeText(source);
        BackupText = Settings.BackupFolder ?? "Not chosen yet";
        BackupFreeText = FreeText(Settings.BackupFolder);
    }

    /// <summary>For screenshots: fixed free-space texts instead of this machine's drives.</summary>
    public void ShowDemoDrives(string sourceFree, string backupFree)
    {
        SourceFreeText = sourceFree;
        BackupFreeText = backupFree;
    }

    private static string FreeText(string? folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return "";
            }
            var drive = Path.GetPathRoot(Path.GetFullPath(folder));
            return drive != null && Directory.Exists(drive) ? $"{Format.Size(DiskSpace.Free(folder))} free on {drive}" : $"{drive} is not connected";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "";
        }
    }
}

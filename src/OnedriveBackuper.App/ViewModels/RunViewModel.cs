using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using OnedriveBackuper.App.Services;
using OnedriveBackuper.Core;
using OnedriveBackuper.Windows;

namespace OnedriveBackuper.App.ViewModels;

public sealed class StepViewModel(string title, string glyph) : ObservableObject
{
    private StepState state;

    public string Title { get; } = title;
    public string Glyph { get; } = glyph;

    public StepState State
    {
        get => state;
        set => Set(ref state, value);
    }
}

/// <summary>A backup in progress (or just finished): everything the progress screen and the tray icon show.</summary>
public sealed class RunViewModel : ObservableObject
{
    private const int MaxLogLines = 1000;

    private readonly MainViewModel main;
    private readonly DispatcherTimer timer;
    private BackupProgress? progress;
    private UiLog? log;
    private CancellationTokenSource? cancel;
    private DateTime startedAt;
    private DateTime? backingUpSince;
    private DateTime lastDiskCheck;
    private string? measuredFile;
    private bool downloadMeasurable;

    private bool isRunning, isStopping, hasResult, scheduled, onlyProblems;
    private string title = "", phaseText = "", overallPercentText = "", overallDetail = "", timeText = "", checkedText = "";
    private double overallPercent, checkedPercent, currentPercent;
    private bool overallIndeterminate, currentIndeterminate, hasCurrentFile;
    private string currentName = "", currentFolder = "", currentSize = "", currentAction = "";
    private string unchanged = "0", copied = "0", downloaded = "0", freedUp = "0", problemCount = "0", driveFreeText = "";
    private Tone resultTone;
    private string resultHeadline = "", resultSummary = "";
    private string? resultLogPath;

    public RunViewModel(MainViewModel main)
    {
        this.main = main;
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => Refresh();
        LogView = CollectionViewSource.GetDefaultView(Log);
        LogView.Filter = line => !onlyProblems || ((LogLine)line).IsProblem;
        Problems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasProblems));
        StopCommand = new RelayCommand(Stop, () => IsRunning && !IsStopping);
        DismissCommand = new RelayCommand(() => HasResult = false);
        OpenLogCommand = new RelayCommand(() => OpenInShell(resultLogPath), () => File.Exists(resultLogPath));
    }

    public event Action<BackupJobResult, bool>? Finished;

    public ObservableCollection<LogLine> Log { get; } = [];
    public ICollectionView LogView { get; }
    public ObservableCollection<FailedFile> Problems { get; } = [];

    public bool HasProblems => Problems.Count > 0;

    public StepViewModel DownloadStep { get; } = new("Download", "");
    public StepViewModel CopyStep { get; } = new("Copy to backup", "");
    public StepViewModel FreeUpStep { get; } = new("Free up space", "");

    public ICommand StopCommand { get; }
    public ICommand DismissCommand { get; }
    public ICommand OpenLogCommand { get; }

    public bool IsRunning { get => isRunning; private set { if (Set(ref isRunning, value)) { main.OnBusyChanged(); } } }
    public bool IsStopping { get => isStopping; private set => Set(ref isStopping, value); }
    public bool HasResult { get => hasResult; set { if (Set(ref hasResult, value)) { main.RefreshHome(); } } }
    public bool WasScheduled => scheduled;

    public bool OnlyProblems
    {
        get => onlyProblems;
        set
        {
            if (Set(ref onlyProblems, value))
            {
                LogView.Refresh();
            }
        }
    }

    public string Title { get => title; private set => Set(ref title, value); }
    public string PhaseText { get => phaseText; private set => Set(ref phaseText, value); }
    public double OverallPercent { get => overallPercent; private set => Set(ref overallPercent, value); }
    public bool OverallIndeterminate { get => overallIndeterminate; private set => Set(ref overallIndeterminate, value); }
    public string OverallPercentText { get => overallPercentText; private set => Set(ref overallPercentText, value); }
    public string OverallDetail { get => overallDetail; private set => Set(ref overallDetail, value); }
    public string TimeText { get => timeText; private set => Set(ref timeText, value); }
    public double CheckedPercent { get => checkedPercent; private set => Set(ref checkedPercent, value); }
    public string CheckedText { get => checkedText; private set => Set(ref checkedText, value); }
    public bool HasCurrentFile { get => hasCurrentFile; private set => Set(ref hasCurrentFile, value); }
    public string CurrentName { get => currentName; private set => Set(ref currentName, value); }
    public string CurrentFolder { get => currentFolder; private set => Set(ref currentFolder, value); }
    public string CurrentSize { get => currentSize; private set => Set(ref currentSize, value); }
    public string CurrentAction { get => currentAction; private set => Set(ref currentAction, value); }
    public double CurrentPercent { get => currentPercent; private set => Set(ref currentPercent, value); }
    public bool CurrentIndeterminate { get => currentIndeterminate; private set => Set(ref currentIndeterminate, value); }
    public string Unchanged { get => unchanged; private set => Set(ref unchanged, value); }
    public string Copied { get => copied; private set => Set(ref copied, value); }
    public string Downloaded { get => downloaded; private set => Set(ref downloaded, value); }
    public string FreedUp { get => freedUp; private set => Set(ref freedUp, value); }
    public string ProblemCount { get => problemCount; private set => Set(ref problemCount, value); }
    public string DriveFreeText { get => driveFreeText; private set => Set(ref driveFreeText, value); }
    public Tone Tone { get => resultTone; private set => Set(ref resultTone, value); }
    public string ResultHeadline { get => resultHeadline; private set => Set(ref resultHeadline, value); }
    public string ResultSummary { get => resultSummary; private set => Set(ref resultSummary, value); }

    /// <summary>One line for the tray icon's tooltip.</summary>
    public string TrayText => IsRunning
        ? $"OnedriveBackuper: {PhaseText} {(OverallIndeterminate ? "" : OverallPercentText)}".TrimEnd()
        : HasResult ? $"OnedriveBackuper: {ResultHeadline}" : "OnedriveBackuper";

    public void Start(bool forceFull, bool isScheduled)
    {
        if (IsRunning)
        {
            return;
        }
        scheduled = isScheduled;
        progress = new BackupProgress();
        log = new UiLog();
        cancel = new CancellationTokenSource();
        startedAt = DateTime.Now;
        backingUpSince = null;
        measuredFile = null;
        lastDiskCheck = DateTime.MinValue;
        Log.Clear();
        Problems.Clear();
        HasResult = false;
        IsStopping = false;
        IsRunning = true;
        Title = forceFull ? "Full backup" : "Backing up";
        Apply(progress.Snapshot, null);
        timer.Start();

        var settings = main.SettingsSnapshot();
        var job = Task.Run(() => BackupJob.Run(settings, forceFull, progress, log, cancel.Token));
        job.ContinueWith(t => Complete(t.IsFaulted
                ? new BackupJobResult(RunResult.Failed, "The backup stopped because of an unexpected error", t.Exception!.GetBaseException().Message, null, [], null)
                : t.Result),
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void Stop()
    {
        if (IsRunning && !IsStopping)
        {
            IsStopping = true;
            PhaseText = "Stopping after the current file...";
            cancel?.Cancel();
        }
    }

    private void Complete(BackupJobResult result)
    {
        timer.Stop();
        if (progress != null)
        {
            Refresh();
        }
        log?.Dispose();
        cancel?.Dispose();
        cancel = null;

        Tone = result.Tone;
        ResultHeadline = result.Headline;
        ResultSummary = result.Summary;
        resultLogPath = result.LogPath;
        foreach (var problem in result.Problems)
        {
            Problems.Add(problem);
        }
        IsRunning = false;
        IsStopping = false;
        HasResult = true;
        OnPropertyChanged(nameof(TrayText));
        main.RecordLastRun(result, startedAt, scheduled);
        Finished?.Invoke(result, scheduled);
    }

    private void Refresh()
    {
        if (progress == null)
        {
            return;
        }
        var snapshot = progress.Snapshot;
        Apply(snapshot, MeasureDownload(snapshot));

        while (log != null && log.TryTake(out var line))
        {
            Log.Add(line);
        }
        while (Log.Count > MaxLogLines)
        {
            Log.RemoveAt(0);
        }

        if (DateTime.Now - lastDiskCheck > TimeSpan.FromSeconds(2))
        {
            lastDiskCheck = DateTime.Now;
            UpdateDriveFree();
        }
    }

    /// <summary>How much of the file being downloaded has arrived, if Windows can tell us.</summary>
    private long? MeasureDownload(BackupProgressSnapshot s)
    {
        if (s.CurrentStage != FileStage.Downloading || s.CurrentFullPath == null || !OperatingSystem.IsWindows())
        {
            measuredFile = null;
            return null;
        }
        var onDisk = NativeFile.SizeOnDisk(s.CurrentFullPath);
        if (measuredFile != s.CurrentFullPath)
        {
            // If the first reading already looks complete, the drive does not report partial downloads.
            measuredFile = s.CurrentFullPath;
            downloadMeasurable = onDisk is { } first && first < s.CurrentFileSize * 0.95;
        }
        return downloadMeasurable && onDisk is { } bytes ? Math.Min(bytes, s.CurrentFileSize) : null;
    }

    /// <summary>Turns a progress snapshot into what the screen shows. Also used to draw screenshots.</summary>
    public void Apply(BackupProgressSnapshot s, long? downloadedBytes)
    {
        var now = DateTime.Now;
        PhaseText = IsStopping ? "Stopping after the current file..." : s.Phase switch
        {
            BackupPhase.Starting => "Getting ready...",
            BackupPhase.FreeingUpLeftovers => "Freeing up files left downloaded by an interrupted backup...",
            BackupPhase.Scanning => "Looking through your OneDrive (this downloads nothing)...",
            BackupPhase.BackingUp when s.FilesToCopy == 0 => "Checking files: nothing new to copy so far",
            BackupPhase.BackingUp => "Backing up new and changed files",
            BackupPhase.Finishing => "Finishing up...",
            _ => "Done",
        };
        if (s.SetId != null)
        {
            Title = s.Resumed ? "Finishing an interrupted backup" : s.Kind == BackupKind.Full ? "Full backup" : "Incremental backup";
        }

        // Current file: for files that need downloading, the download counts as half the work and the copy as the other half.
        var size = s.CurrentFileSize;
        double currentDone = 0;
        long? known = null;
        switch (s.CurrentStage)
        {
            case FileStage.Downloading:
                known = downloadedBytes;
                currentDone = downloadedBytes is { } d && size > 0 ? 0.5 * d / size : 0;
                break;
            case FileStage.Copying:
                known = s.CurrentFileCopiedBytes;
                currentDone = size > 0 ? (s.CurrentFileNeedsDownload ? 0.5 : 0) + (s.CurrentFileNeedsDownload ? 0.5 : 1) * s.CurrentFileCopiedBytes / size : 0;
                break;
            case FileStage.FreeingUp:
                currentDone = 1;
                break;
        }

        var backingUp = s.Phase == BackupPhase.BackingUp;
        if (backingUp && backingUpSince == null)
        {
            backingUpSince = now;
        }
        var doneBytes = s.CopiedBytes + (long)(currentDone * size);
        OverallIndeterminate = s.Phase < BackupPhase.BackingUp;
        var fraction = s.BytesToCopy > 0 ? Math.Clamp((double)doneBytes / s.BytesToCopy, 0, 1) : s.OverallFraction;
        if (s.Phase >= BackupPhase.Finishing)
        {
            fraction = 1;
        }
        OverallPercent = fraction * 100;
        OverallPercentText = OverallIndeterminate ? "" : $"{fraction:P0}";
        OverallDetail = s.Phase < BackupPhase.BackingUp
            ? ""
            : s.FilesToCopy == 0
                ? "Everything is already backed up"
                : $"{Format.Size(Math.Min(doneBytes, s.BytesToCopy))} of {Format.Size(s.BytesToCopy)} copied  ·  {s.CopiedFiles:N0} of {s.FilesToCopy:N0} files";

        var elapsed = now - startedAt;
        var remaining = "";
        if (backingUpSince is { } since && fraction is > 0.01 and < 1 && now - since > TimeSpan.FromSeconds(10))
        {
            var rate = doneBytes / (now - since).TotalSeconds;
            if (rate > 0)
            {
                remaining = $"  ·  about {Friendly.Roughly(TimeSpan.FromSeconds((s.BytesToCopy - doneBytes) / rate))} left";
            }
        }
        TimeText = $"Running for {Friendly.Clock(elapsed)}{remaining}";

        CheckedPercent = s.TotalFiles > 0 ? 100.0 * s.CheckedFiles / s.TotalFiles : 0;
        CheckedText = s.TotalFiles > 0 ? $"Checked {s.CheckedFiles:N0} of {s.TotalFiles:N0} files in your OneDrive" : "";

        HasCurrentFile = s.CurrentFile != null;
        if (s.CurrentFile != null)
        {
            var path = s.CurrentFile.Replace('\\', '/');
            var slash = path.LastIndexOf('/');
            CurrentName = slash >= 0 ? path[(slash + 1)..] : path;
            CurrentFolder = slash >= 0 ? path[..slash] : "Top of your OneDrive";
            CurrentSize = size > 0 ? Format.Size(size) : "";
            var percent = known is { } k && size > 0 ? 100.0 * k / size : (double?)null;
            CurrentIndeterminate = percent == null;
            CurrentPercent = s.CurrentStage == FileStage.FreeingUp ? 100 : percent ?? 0;
            var percentText = percent is { } p ? $" {p:0}%" : "";
            CurrentAction = s.CurrentStage switch
            {
                FileStage.Downloading => $"Downloading from OneDrive...{percentText}",
                FileStage.Copying => $"Copying to the backup...{percentText}",
                FileStage.FreeingUp => "Freeing up the space again, so it is online-only like before...",
                _ => "",
            };
            DownloadStep.State = !s.CurrentFileNeedsDownload ? StepState.NotNeeded
                : s.CurrentStage == FileStage.Downloading ? StepState.Active : StepState.Done;
            CopyStep.State = s.CurrentStage switch
            {
                FileStage.Copying => StepState.Active,
                FileStage.FreeingUp => StepState.Done,
                _ => StepState.Waiting,
            };
            FreeUpStep.State = !s.CurrentFileNeedsDownload ? StepState.NotNeeded
                : s.CurrentStage == FileStage.FreeingUp ? StepState.Active : StepState.Waiting;
        }

        Unchanged = s.UnchangedFiles.ToString("N0");
        Copied = s.CopiedFiles.ToString("N0");
        Downloaded = s.DownloadedFiles > 0 ? $"{s.DownloadedFiles:N0}" : "0";
        FreedUp = s.FreedUpFiles.ToString("N0");
        ProblemCount = s.FailedFiles.ToString("N0");
        OnPropertyChanged(nameof(TrayText));
    }

    private void UpdateDriveFree()
    {
        var source = main.Settings.SourceFolder ?? AppSettings.DetectOneDriveFolder();
        try
        {
            if (source != null && Directory.Exists(source))
            {
                var drive = Path.GetPathRoot(Path.GetFullPath(source));
                DriveFreeText = $"{drive} has {Format.Size(DiskSpace.Free(source))} free";
            }
        }
        catch (IOException)
        {
            DriveFreeText = "";
        }
    }

    /// <summary>For screenshots: show a finished result without running anything.</summary>
    public void ShowResult(BackupJobResult result)
    {
        IsRunning = false;
        Tone = result.Tone;
        ResultHeadline = result.Headline;
        ResultSummary = result.Summary;
        Problems.Clear();
        foreach (var problem in result.Problems)
        {
            Problems.Add(problem);
        }
        HasResult = true;
    }

    /// <summary>For screenshots: show a backup in progress without running anything.</summary>
    public void ShowDemo(BackupProgressSnapshot snapshot, long downloaded, TimeSpan runningFor, IEnumerable<LogLine> lines, string driveFree)
    {
        startedAt = DateTime.Now - runningFor;
        backingUpSince = startedAt.AddSeconds(20);
        IsRunning = true;
        Apply(snapshot, downloaded);
        Log.Clear();
        foreach (var line in lines)
        {
            Log.Add(line);
        }
        DriveFreeText = driveFree;
    }

    internal static void OpenInShell(string? path)
    {
        if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }
}

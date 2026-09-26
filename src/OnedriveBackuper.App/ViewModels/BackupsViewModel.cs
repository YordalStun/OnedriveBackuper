using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using OnedriveBackuper.App.Services;
using OnedriveBackuper.Core;

namespace OnedriveBackuper.App.ViewModels;

public sealed class BackupSetRow
{
    public BackupSetRow(BackupSet set)
        : this(set.Info, set.IsComplete)
    {
        Set = set;
    }

    /// <summary>Also used for screenshots, without a real backup folder.</summary>
    public BackupSetRow(SetInfo info, bool isComplete)
    {
        Id = info.SetId;
        IsComplete = isComplete;
        When = Friendly.When(info.StartedUtc);
        Kind = !isComplete ? "Unfinished" : info.Kind == BackupKind.Full ? "Full" : "Incremental";
        Files = info.Stats?.TotalFiles.ToString("N0") ?? "-";
        Copied = info.Stats == null ? "-" : $"{info.Stats.CopiedFiles:N0}  ({Format.Size(info.Stats.CopiedBytes)})";
        Problems = info.Stats?.FailedFiles.ToString("N0") ?? "-";
        Status = !isComplete ? "Not finished: the next backup carries on with it"
            : info.Stats?.FailedFiles > 0 ? "Finished with problems" : "Finished";
    }

    public BackupSet? Set { get; }
    public string Id { get; }
    public bool IsComplete { get; }
    public string When { get; }
    public string Kind { get; }
    public string Files { get; }
    public string Copied { get; }
    public string Problems { get; }
    public string Status { get; }
}

/// <summary>The list of backups, with restore, check and delete.</summary>
public sealed class BackupsViewModel : ObservableObject
{
    private readonly MainViewModel main;
    private readonly DispatcherTimer timer;
    private RestoreProgress? progress;
    private CancellationTokenSource? cancel;
    private BackupSetRow? selected;
    private bool isWorking, showRestorePanel, restoreOverwrite;
    private string folderText = "", emptyText = "", workTitle = "", workDetail = "", resultText = "", restoreTarget = "", restoreOnly = "";
    private double workPercent;
    private Tone resultTone;

    public BackupsViewModel(MainViewModel main)
    {
        this.main = main;
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => ShowProgress();

        RefreshCommand = new RelayCommand(Load, () => !IsWorking);
        OpenFolderCommand = new RelayCommand(() => RunViewModel.OpenInShell(main.Settings.BackupFolder));
        BeginRestoreCommand = new RelayCommand(BeginRestore, () => Selected?.IsComplete == true && !main.IsBusy);
        BrowseRestoreTargetCommand = new RelayCommand(BrowseRestoreTarget);
        StartRestoreCommand = new RelayCommand(StartRestore, () => RestoreTarget.Length > 0 && !main.IsBusy);
        CancelRestoreCommand = new RelayCommand(() => ShowRestorePanel = false);
        VerifyCommand = new RelayCommand(StartVerify, () => Selected?.IsComplete == true && Selected.Set != null && !main.IsBusy);
        DeleteCommand = new RelayCommand(Delete, () => Selected?.Set != null && !main.IsBusy);
        StopCommand = new RelayCommand(() => cancel?.Cancel(), () => IsWorking);
    }

    public ObservableCollection<BackupSetRow> Sets { get; } = [];

    public ICommand RefreshCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand BeginRestoreCommand { get; }
    public ICommand BrowseRestoreTargetCommand { get; }
    public ICommand StartRestoreCommand { get; }
    public ICommand CancelRestoreCommand { get; }
    public ICommand VerifyCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand StopCommand { get; }

    public BackupSetRow? Selected { get => selected; set => Set(ref selected, value); }
    public bool IsWorking { get => isWorking; private set { if (Set(ref isWorking, value)) { main.OnBusyChanged(); } } }
    public string FolderText { get => folderText; private set => Set(ref folderText, value); }
    public string EmptyText { get => emptyText; private set => Set(ref emptyText, value); }
    public string WorkTitle { get => workTitle; private set => Set(ref workTitle, value); }
    public string WorkDetail { get => workDetail; private set => Set(ref workDetail, value); }
    public double WorkPercent { get => workPercent; private set => Set(ref workPercent, value); }
    public Tone Tone { get => resultTone; private set => Set(ref resultTone, value); }
    public string ResultText { get => resultText; private set => Set(ref resultText, value); }
    public bool ShowRestorePanel { get => showRestorePanel; set => Set(ref showRestorePanel, value); }
    public string RestoreTarget { get => restoreTarget; set => Set(ref restoreTarget, value); }
    public string RestoreOnly { get => restoreOnly; set => Set(ref restoreOnly, value); }
    public bool RestoreOverwrite { get => restoreOverwrite; set => Set(ref restoreOverwrite, value); }

    public void Load()
    {
        var folder = main.Settings.BackupFolder;
        Sets.Clear();
        if (string.IsNullOrWhiteSpace(folder))
        {
            FolderText = "No backup folder chosen yet.";
            EmptyText = "Choose where to save backups in Settings.";
            return;
        }
        FolderText = $"Saved in {folder}";
        try
        {
            foreach (var set in new BackupRepository(folder).ListSets().Reverse())
            {
                Sets.Add(new BackupSetRow(set));
            }
            EmptyText = Sets.Count == 0 ? "No backups yet. Press Back up now on the Home page to make the first one." : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            EmptyText = $"The backup folder could not be read: {ex.Message}";
        }
    }

    /// <summary>For screenshots.</summary>
    public void ShowDemo(IEnumerable<BackupSetRow> rows, string folder)
    {
        Sets.Clear();
        foreach (var row in rows)
        {
            Sets.Add(row);
        }
        FolderText = $"Saved in {folder}";
        EmptyText = "";
        Selected = Sets.FirstOrDefault();
    }

    private void BeginRestore()
    {
        if (string.IsNullOrEmpty(RestoreTarget))
        {
            RestoreTarget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Restored from backup");
        }
        ShowRestorePanel = true;
    }

    private void BrowseRestoreTarget()
    {
        var dialog = new OpenFolderDialog { Title = "Restore files into this folder" };
        if (dialog.ShowDialog() == true)
        {
            RestoreTarget = dialog.FolderName;
        }
    }

    private void StartRestore()
    {
        if (Selected?.Set is not { } set)
        {
            return;
        }
        var target = RestoreTarget;
        var only = new PathFilter(RestoreOnly.Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var overwrite = RestoreOverwrite;
        var backupFolder = main.Settings.BackupFolder!;
        ShowRestorePanel = false;
        Begin($"Restoring the backup from {Selected.When}");
        var log = new UiLog();
        Task.Run(() => new RestoreEngine(log).Restore(backupFolder, set.Id, target, only, overwrite, cancel!.Token, progress))
            .ContinueWith(t =>
            {
                log.Dispose();
                if (t.IsFaulted)
                {
                    End(Tone.Bad, $"The restore stopped: {t.Exception!.GetBaseException().Message}");
                    return;
                }
                var r = t.Result;
                var skipped = r.SkippedExisting > 0 ? $" {Friendly.Count(r.SkippedExisting, "file was", "files were")} already there and kept." : "";
                if (r.Failed.Count > 0)
                {
                    End(Tone.Warning, $"Restored {Friendly.Count(r.RestoredFiles, "file", "files")} to {target}, but {Friendly.Count(r.Failed.Count, "file", "files")} could not be restored: {r.Failed[0].Path} ({r.Failed[0].Error}){skipped}");
                }
                else
                {
                    End(r.Cancelled ? Tone.Neutral : Tone.Good,
                        $"{(r.Cancelled ? "Stopped. " : "")}Restored {Friendly.Count(r.RestoredFiles, "file", "files")} ({Format.Size(r.RestoredBytes)}) to {target}.{skipped}");
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void StartVerify()
    {
        if (Selected?.Set is not { } set)
        {
            return;
        }
        var backupFolder = main.Settings.BackupFolder!;
        Begin($"Checking the backup from {Selected.When}");
        var log = new UiLog();
        Task.Run(() => new RestoreEngine(log).Verify(backupFolder, set.Id, cancel!.Token, progress))
            .ContinueWith(t =>
            {
                log.Dispose();
                if (t.IsFaulted)
                {
                    End(Tone.Bad, $"The check stopped: {t.Exception!.GetBaseException().Message}");
                    return;
                }
                var r = t.Result;
                End(r.Problems.Count > 0 ? Tone.Bad : r.Cancelled ? Tone.Neutral : Tone.Good, r.Problems.Count > 0
                    ? $"{Friendly.Count(r.Problems.Count, "file is", "files are")} missing or damaged in this backup, for example {r.Problems[0].Path}. Make a new full backup to be safe."
                    : $"{(r.Cancelled ? "Stopped. " : "")}Checked {Friendly.Count(r.CheckedFiles, "file", "files")} ({Format.Size(r.CheckedBytes)}): all match what was backed up.");
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Delete()
    {
        if (Selected?.Set is not { } set || string.IsNullOrWhiteSpace(main.Settings.BackupFolder))
        {
            return;
        }
        var repository = new BackupRepository(main.Settings.BackupFolder);
        try
        {
            var dependents = Retention.Dependents(repository, set);
            if (dependents.Count > 0)
            {
                MessageBox.Show(
                    $"This backup cannot be deleted on its own: {Friendly.Count(dependents.Count, "newer backup still uses", "newer backups still use")} files stored in it.\n\nDelete those first, or let automatic clean-up remove old backups for you (Schedule page).",
                    "OnedriveBackuper", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show($"Delete the backup from {Selected.When}? This cannot be undone.", "OnedriveBackuper",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }
            repository.DeleteSet(set);
            Tone = Tone.Good;
            ResultText = "The backup was deleted.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Tone = Tone.Bad;
            ResultText = $"Could not delete it: {ex.Message}";
        }
        Load();
    }

    private void Begin(string title)
    {
        progress = new RestoreProgress();
        cancel = new CancellationTokenSource();
        WorkTitle = title;
        WorkDetail = "";
        WorkPercent = 0;
        ResultText = "";
        IsWorking = true;
        timer.Start();
    }

    private void End(Tone tone, string text)
    {
        timer.Stop();
        ShowProgress();
        cancel?.Dispose();
        cancel = null;
        IsWorking = false;
        Tone = tone;
        ResultText = text;
    }

    private void ShowProgress()
    {
        if (progress == null)
        {
            return;
        }
        var p = progress.Snapshot;
        WorkPercent = p.Fraction * 100;
        WorkDetail = $"{p.FilesDone:N0} of {p.FilesTotal:N0} files  ·  {Format.Size(p.BytesDone)} of {Format.Size(p.BytesTotal)}{(p.CurrentFile != null ? $"  ·  {p.CurrentFile}" : "")}";
    }
}

using OnedriveBackuper.Core;

namespace OnedriveBackuper.Tests;

public sealed class ProgressTests : IDisposable
{
    private readonly TestFolders t = new();

    public void Dispose() => t.Dispose();

    [Fact]
    public void Progress_shows_the_plan_the_current_file_and_its_stage()
    {
        t.OneDrive.Add("online.bin", new string('o', 3000));
        t.OneDrive.Add("local.txt", new string('l', 1000), onlineOnly: false);
        var progress = new BackupProgress();
        BackupProgressSnapshot? whileDownloading = null, whileFreeingUp = null;
        t.OneDrive.AfterHydrate = _ => whileDownloading = progress.Snapshot;
        t.OneDrive.BeforeDehydrate = _ => whileFreeingUp = progress.Snapshot;

        t.Backup(progress: progress);

        Assert.NotNull(whileDownloading);
        Assert.Equal(BackupPhase.BackingUp, whileDownloading.Phase);
        Assert.Equal(2, whileDownloading.FilesToCopy);
        Assert.Equal(4000, whileDownloading.BytesToCopy);
        Assert.Equal(1, whileDownloading.FilesToDownload);
        Assert.Equal(3000, whileDownloading.BytesToDownload);
        Assert.Equal("online.bin", whileDownloading.CurrentFile);
        Assert.Equal(FileStage.Downloading, whileDownloading.CurrentStage);
        Assert.Equal(3000, whileDownloading.CurrentFileSize);

        Assert.NotNull(whileFreeingUp);
        Assert.Equal(FileStage.FreeingUp, whileFreeingUp.CurrentStage);
        Assert.Equal(3000, whileFreeingUp.CurrentFileCopiedBytes);

        var end = progress.Snapshot;
        Assert.Equal(BackupPhase.Finished, end.Phase);
        Assert.Equal(2, end.CheckedFiles);
        Assert.Equal(2, end.CopiedFiles);
        Assert.Equal(4000, end.CopiedBytes);
        Assert.Equal(1, end.DownloadedFiles);
        Assert.Equal(1, end.FreedUpFiles);
        Assert.Equal(1.0, end.OverallFraction);
        Assert.Null(end.CurrentFile);
    }

    [Fact]
    public void Incremental_progress_only_counts_what_needs_copying()
    {
        t.OneDrive.Add("same.txt", "same");
        t.OneDrive.Add("changes.txt", "v1");
        t.Backup();
        t.OneDrive.Edit("changes.txt", "v2 longer");
        var progress = new BackupProgress();

        t.Backup(progress: progress);

        var end = progress.Snapshot;
        Assert.Equal(1, end.FilesToCopy);
        Assert.Equal(9, end.BytesToCopy);
        Assert.Equal(1, end.UnchangedFiles);
        Assert.Equal(BackupKind.Incremental, end.Kind);
    }

    [Fact]
    public void Overall_fraction_counts_the_file_being_copied()
    {
        var snapshot = new BackupProgressSnapshot { BytesToCopy = 1000, CopiedBytes = 250, CurrentFileCopiedBytes = 250 };

        Assert.Equal(0.5, snapshot.OverallFraction);
        Assert.Equal(0.25, new BackupProgressSnapshot { TotalFiles = 4, CheckedFiles = 1 }.OverallFraction);
    }
}

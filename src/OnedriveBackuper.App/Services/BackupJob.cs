using System.Collections.Concurrent;
using System.Globalization;
using OnedriveBackuper.App.ViewModels;
using OnedriveBackuper.Core;

namespace OnedriveBackuper.App.Services;

public sealed record LogLine(DateTime Time, string Level, string Message)
{
    public bool IsProblem => Level is "WARN" or "ERROR";

    public string TimeText => Time.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
}

/// <summary>Collects log lines from the backup thread for the window, and writes them to the backup's log file.</summary>
public sealed class UiLog : ILog, IDisposable
{
    private readonly ConcurrentQueue<LogLine> lines = new();
    private readonly LogFile file = new();

    public void Info(string message) => Add("INFO", message);

    public void Warn(string message) => Add("WARN", message);

    public void Error(string message) => Add("ERROR", message);

    public void Detail(string message) => Add("DETAIL", message.Trim());

    public void AttachFile(string path) => file.Attach(path);

    public bool TryTake(out LogLine line) => lines.TryDequeue(out line!);

    private void Add(string level, string message)
    {
        if (message.Length > 0)
        {
            lines.Enqueue(new LogLine(DateTime.Now, level, message));
        }
        file.Write(level, message);
    }

    public void Dispose() => file.Dispose();
}

public sealed record BackupJobResult(
    RunResult Result,
    string Headline,
    string Summary,
    BackupReport? Report,
    IReadOnlyList<FailedFile> Problems,
    string? LogPath)
{
    public Tone Tone => Result switch
    {
        RunResult.Succeeded => Tone.Good,
        RunResult.SucceededWithProblems => Tone.Warning,
        RunResult.Stopped => Tone.Neutral,
        _ => Tone.Bad,
    };
}

/// <summary>One backup the way the app does it: settings in, backup and automatic clean-up, a friendly result out.</summary>
public static class BackupJob
{
    public static BackupJobResult Run(AppSettings settings, bool forceFull, BackupProgress progress, UiLog log, CancellationToken cancel)
    {
        var source = settings.SourceFolder is { Length: > 0 } s ? s : AppSettings.DetectOneDriveFolder();
        if (source == null || !Directory.Exists(source))
        {
            return Failed(log, "Your OneDrive folder was not found",
                source == null ? "Choose your OneDrive folder in Settings." : $"{source} does not exist. Check the OneDrive folder in Settings.");
        }
        if (string.IsNullOrWhiteSpace(settings.BackupFolder))
        {
            return Failed(log, "No backup folder chosen", "Choose where to save backups in Settings.");
        }

        var backupRoot = settings.BackupFolder;
        if (!Directory.Exists(backupRoot))
        {
            var drive = Path.GetPathRoot(Path.GetFullPath(backupRoot));
            // A missing drive is an unplugged disk or an offline network share: never create a backup folder somewhere else instead.
            if (drive == null || !Directory.Exists(drive))
            {
                return Failed(log, "The backup folder is not available",
                    $"{backupRoot} could not be found. If it is on a USB disk or network drive, connect it and try again.");
            }
        }

        try
        {
            var repository = new BackupRepository(backupRoot);
            var retention = settings.Retention;
            var fullDue = retention.Enabled && !forceFull && Retention.IsFullBackupDue(repository, DateTimeOffset.UtcNow, retention.FullEveryDays);
            if (fullDue)
            {
                log.Info($"The last full backup is {retention.FullEveryDays} or more days old, so this one is a full backup.");
            }

            var engine = new BackupEngine(CloudFiles.Create(settings.FreeUpMethod), new PendingFreeUps(PendingFreeUps.DefaultPath), log);
            var report = engine.Run(new BackupOptions
            {
                SourceRoot = source,
                BackupRoot = backupRoot,
                ForceFull = forceFull || fullDue,
                Exclude = new PathFilter(settings.Exclude),
                ReserveBytes = settings.ReserveBytes,
                Progress = progress,
            }, cancel);

            IReadOnlyList<string> deleted = [];
            if (!report.Cancelled && retention.Enabled)
            {
                deleted = Retention.Prune(repository, retention.KeepFullBackups, log);
            }
            return Describe(report, deleted, report.SetId == null ? null : repository.LogPathFor(report.SetId));
        }
        catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Failed(log, "The backup could not run", ex.Message);
        }
    }

    private static BackupJobResult Describe(BackupReport r, IReadOnlyList<string> deleted, string? logPath)
    {
        var kind = r.Kind == BackupKind.Full ? "Full backup" : "Incremental backup";
        var copied = $"{Friendly.Count(r.CopiedFiles, "file", "files")} copied ({Format.Size(r.CopiedBytes)})";
        var unchanged = r.Kind == BackupKind.Incremental ? $", {r.UnchangedFiles.ToString("N0", CultureInfo.CurrentCulture)} unchanged" : "";
        var downloaded = r.DownloadedFiles > 0 ? $" {Friendly.Count(r.DownloadedFiles, "file was", "files were")} downloaded from OneDrive for it and freed up again." : "";
        var cleaned = deleted.Count > 0 ? $" Removed {Friendly.Count(deleted.Count, "old backup", "old backups")} to save space." : "";
        var took = $" Took {Friendly.Roughly(r.Elapsed)}.";
        var problems = r.Failed.Count + r.StillDownloaded.Count;

        if (r.Cancelled)
        {
            return new BackupJobResult(RunResult.Stopped, "Backup stopped",
                $"{Friendly.Count(r.CopiedFiles, "file was", "files were")} copied before stopping. Start a backup again to carry on where it stopped.",
                r, r.Failed, logPath);
        }
        if (problems > 0)
        {
            var still = r.StillDownloaded.Count > 0
                ? $" {Friendly.Count(r.StillDownloaded.Count, "file is", "files are")} still downloaded and will be freed up next time."
                : "";
            return new BackupJobResult(RunResult.SucceededWithProblems, $"Backup finished with {Friendly.Count(problems, "problem", "problems")}",
                $"{kind}: {copied}{unchanged}. Files that failed keep their previous backup and are tried again next time.{still}{cleaned}{took}",
                r, [.. r.Failed, .. r.StillDownloaded.Select(p => new FailedFile(p, "Could not be freed up yet (still takes space on this PC)."))], logPath);
        }
        return new BackupJobResult(RunResult.Succeeded, "Backup finished",
            $"{kind}: {copied}{unchanged}.{downloaded}{cleaned}{took}", r, [], logPath);
    }

    private static BackupJobResult Failed(UiLog log, string headline, string detail)
    {
        log.Error($"{headline}: {detail}");
        return new BackupJobResult(RunResult.Failed, headline, detail, null, [], null);
    }
}

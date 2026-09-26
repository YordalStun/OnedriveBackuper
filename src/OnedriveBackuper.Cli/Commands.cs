using System.Globalization;
using System.Reflection;
using OnedriveBackuper.Core;
using OnedriveBackuper.Windows;

namespace OnedriveBackuper.Cli;

internal static class Commands
{
    public const int Ok = 0;
    public const int FinishedWithProblems = 1;
    public const int Error = 2;
    public const int Stopped = 3;

    private const string Help = """
        OnedriveBackuper - full and incremental backups of OneDrive that download one file at a time,
        so your PC only needs free space for the biggest single file, not your whole OneDrive.

        Usage:
          OnedriveBackuper backup <backup-folder> [options]
              Backs up your OneDrive into <backup-folder>. The first backup is full; later ones are
              incremental (only new and changed files). Online-only files are downloaded, copied,
              and freed up again, one at a time.
                --source <folder>     Folder to back up (default: your OneDrive folder)
                --full                Make a new full backup even if there is an earlier one
                --exclude <pattern>   Skip matching files or folders, e.g. "*.tmp" or "Videos" (repeatable)
                --reserve <size>      Free space to always leave on the OneDrive drive (default 1GB)
                --free-method <m>     How files are freed up again: auto (default), direct or unpin
                --dry-run             Show what would be copied and downloaded, without doing it
                --verbose             Show more detail

          OnedriveBackuper restore <backup-folder> <restore-to-folder> [options]
                --set <id>            Backup to restore (default: the latest; see 'list')
                --only <pattern>      Restore only matching files or folders (repeatable)
                --overwrite           Replace files that already exist in <restore-to-folder>
                --verbose             List every restored file

          OnedriveBackuper list <backup-folder>
              Lists the backups in <backup-folder>.

          OnedriveBackuper verify <backup-folder> [--set <id>]
              Re-reads a backup and checks every file against its checksum.

          OnedriveBackuper scan [<onedrive-folder>] [--reserve <size>]
              Shows how much of your OneDrive is online-only and how much free space a backup
              needs. Downloads nothing.

          OnedriveBackuper probe <file>
              Downloads one online-only file and frees it up again, step by step, to check
              everything works on this PC. Good first thing to run.

        Press Ctrl+C once to stop after the current file. Run the same command again to carry on
        where it stopped.
        """;

    public static int Run(string[] args, TextWriter output, TextWriter errors, CancellationToken cancel, string? pendingListPath = null)
    {
        if (args.Length == 0)
        {
            output.WriteLine(Help);
            return Error;
        }

        var rest = args[1..];
        pendingListPath ??= PendingFreeUps.DefaultPath;
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "backup" => Backup(rest, output, errors, cancel, pendingListPath),
                "restore" => Restore(rest, output, errors, cancel),
                "list" => List(rest, output),
                "verify" => Verify(rest, output, errors, cancel),
                "scan" => Scan(rest, output),
                "probe" => Probe(rest, output, pendingListPath),
                "help" or "--help" or "-h" or "/?" => ShowHelp(output),
                "version" or "--version" => ShowVersion(output),
                _ => throw new UsageException($"Unknown command '{args[0]}'."),
            };
        }
        catch (UsageException ex)
        {
            errors.WriteLine(ex.Message);
            errors.WriteLine("Run 'OnedriveBackuper help' to see how to use it.");
            return Error;
        }
        catch (Exception ex) when (ex is BackupException or FormatException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            errors.WriteLine(ex.Message);
            return Error;
        }
    }

    private static int ShowHelp(TextWriter output)
    {
        output.WriteLine(Help);
        return Ok;
    }

    private static int ShowVersion(TextWriter output)
    {
        output.WriteLine(typeof(Commands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
        return Ok;
    }

    private static int Backup(string[] args, TextWriter output, TextWriter errors, CancellationToken cancel, string pendingListPath)
    {
        var a = Arguments.Parse(args, Set("full", "dry-run", "verbose"), Set("source", "exclude", "reserve", "free-method"));
        var backupFolder = Single(a, "<backup-folder>");
        var source = a.Value("source") ?? AppSettings.DetectOneDriveFolder()
            ?? throw new UsageException("Could not find your OneDrive folder. Tell me where it is with --source <folder>.");
        var method = a.Value("free-method")?.ToLowerInvariant() switch
        {
            null or "auto" => FreeUpMethod.Auto,
            "direct" => FreeUpMethod.Direct,
            "unpin" => FreeUpMethod.Unpin,
            var other => throw new UsageException($"--free-method must be auto, direct or unpin, not '{other}'."),
        };

        using var log = new ConsoleLog(output, errors, a.Has("verbose"));
        var engine = new BackupEngine(CloudFiles.Create(method), new PendingFreeUps(pendingListPath), log);
        var report = engine.Run(new BackupOptions
        {
            SourceRoot = source,
            BackupRoot = backupFolder,
            ForceFull = a.Has("full"),
            DryRun = a.Has("dry-run"),
            Exclude = new PathFilter(a.All("exclude")),
            ReserveBytes = a.Value("reserve") is { } reserve ? Format.ParseSize(reserve) : 1L << 30,
        }, cancel);

        PrintSummary(report, log);
        if (report.Cancelled)
        {
            return Stopped;
        }
        return report.Failed.Count > 0 || report.StillDownloaded.Count > 0 ? FinishedWithProblems : Ok;
    }

    private static void PrintSummary(BackupReport r, ILog log)
    {
        log.Info("");
        if (r.DryRun)
        {
            log.Info($"Dry run ({(r.Kind == BackupKind.Full ? "full backup" : $"incremental on top of {r.BaseSetId}")}):");
            log.Info($"  Files found:          {r.ScannedFiles:N0} ({Format.Size(r.ScannedBytes)})");
            log.Info($"  Unchanged:            {r.UnchangedFiles:N0}");
            log.Info($"  Would copy:           {r.CopiedFiles:N0} files ({Format.Size(r.CopiedBytes)})");
            log.Info($"  Would download first: {r.DownloadedFiles:N0} files ({Format.Size(r.DownloadedBytes)}), largest {Format.Size(r.LargestDownload)}");
            return;
        }

        log.Info(r.Cancelled
            ? $"Stopped. Backup {r.SetId} is kept as unfinished: run the same command again to carry on."
            : $"Backup {r.SetId} ({(r.Kind == BackupKind.Full ? "full" : "incremental")}) finished in {Format.Duration(r.Elapsed)}.");
        log.Info($"  Files found:          {r.ScannedFiles:N0} ({Format.Size(r.ScannedBytes)})");
        if (r.Kind == BackupKind.Incremental)
        {
            log.Info($"  Unchanged:            {r.UnchangedFiles:N0}");
        }
        if (r.Resumed)
        {
            log.Info($"  Copied before resume: {r.AlreadyCopiedBeforeResume:N0}");
        }
        log.Info($"  Copied:               {r.CopiedFiles:N0} files ({Format.Size(r.CopiedBytes)})");
        log.Info($"  Downloaded for it:    {r.DownloadedFiles:N0} files ({Format.Size(r.DownloadedBytes)}), largest {Format.Size(r.LargestDownload)}");
        log.Info($"  Freed up again:       {r.FreedUpFiles:N0}");
        if (r.ExcludedCount > 0 || r.SkippedLinkCount > 0)
        {
            log.Info($"  Excluded:             {r.ExcludedCount:N0} (plus {r.SkippedLinkCount:N0} links skipped)");
        }
        if (r.Failed.Count > 0)
        {
            log.Warn($"  Failed:               {r.Failed.Count:N0} (details above). They will be tried again next time.");
        }
        foreach (var path in r.StillDownloaded)
        {
            log.Warn($"  Still downloaded (could not free up yet): {path}");
        }
    }

    private static int Restore(string[] args, TextWriter output, TextWriter errors, CancellationToken cancel)
    {
        var a = Arguments.Parse(args, Set("overwrite", "verbose"), Set("set", "only"));
        if (a.Positional.Count != 2)
        {
            throw new UsageException("restore needs <backup-folder> and <restore-to-folder>.");
        }
        var (backupFolder, target) = (a.Positional[0], a.Positional[1]);
        if (Paths.IsInside(target, backupFolder))
        {
            throw new UsageException("Restore somewhere outside the backup folder.");
        }

        using var log = new ConsoleLog(output, errors, a.Has("verbose"));
        if (AppSettings.DetectOneDriveFolder() is { } oneDrive && Paths.IsInside(target, oneDrive))
        {
            log.Warn("You are restoring into your OneDrive folder, so OneDrive will upload the restored files.");
        }
        var report = new RestoreEngine(log).Restore(backupFolder, a.Value("set"), target, new PathFilter(a.All("only")), a.Has("overwrite"), cancel);

        log.Info("");
        log.Info($"Restored {report.RestoredFiles:N0} files ({Format.Size(report.RestoredBytes)}) from backup {report.SetId}.");
        if (report.SkippedExisting > 0)
        {
            log.Warn($"Skipped {report.SkippedExisting:N0} files that already exist (use --overwrite to replace them).");
        }
        if (report.Failed.Count > 0)
        {
            log.Warn($"{report.Failed.Count:N0} files could not be restored (details above).");
        }
        return report.Cancelled ? Stopped : report.Failed.Count > 0 ? FinishedWithProblems : Ok;
    }

    private static int List(string[] args, TextWriter output)
    {
        var a = Arguments.Parse(args, Set(), Set());
        var repository = new BackupRepository(Single(a, "<backup-folder>"));
        var sets = repository.ListSets();
        if (sets.Count == 0)
        {
            output.WriteLine($"There are no backups in {repository.Root}.");
            return Ok;
        }

        output.WriteLine($"Backups in {repository.Root}:");
        output.WriteLine($"  {"SET",-28} {"KIND",-12} {"STARTED",-17} {"FILES",10} {"COPIED",24} {"FAILED",7}");
        foreach (var set in sets)
        {
            var info = set.Info;
            var kind = !set.IsComplete ? "unfinished" : info.Kind == BackupKind.Full ? "full" : "incremental";
            var started = info.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            var stats = info.Stats;
            var files = stats == null ? "-" : stats.TotalFiles.ToString("N0", CultureInfo.InvariantCulture);
            var copied = stats == null ? "-" : $"{stats.CopiedFiles:N0} ({Format.Size(stats.CopiedBytes)})";
            var failed = stats == null ? "-" : stats.FailedFiles.ToString("N0", CultureInfo.InvariantCulture);
            output.WriteLine($"  {set.Id,-28} {kind,-12} {started,-17} {files,10} {copied,24} {failed,7}");
        }
        return Ok;
    }

    private static int Verify(string[] args, TextWriter output, TextWriter errors, CancellationToken cancel)
    {
        var a = Arguments.Parse(args, Set("verbose"), Set("set"));
        using var log = new ConsoleLog(output, errors, a.Has("verbose"));
        var report = new RestoreEngine(log).Verify(Single(a, "<backup-folder>"), a.Value("set"), cancel);
        log.Info($"Checked {report.CheckedFiles:N0} files ({Format.Size(report.CheckedBytes)}) in backup {report.SetId}.");
        if (report.Problems.Count > 0)
        {
            log.Warn($"{report.Problems.Count:N0} files are missing or damaged (details above).");
            return FinishedWithProblems;
        }
        log.Info("Everything matches.");
        return report.Cancelled ? Stopped : Ok;
    }

    private static int Scan(string[] args, TextWriter output)
    {
        var a = Arguments.Parse(args, Set(), Set("reserve"));
        var root = a.Positional.Count switch
        {
            0 => AppSettings.DetectOneDriveFolder() ?? throw new UsageException("Could not find your OneDrive folder. Pass it: scan <folder>."),
            1 => a.Positional[0],
            _ => throw new UsageException("scan takes at most one folder."),
        };
        var reserve = a.Value("reserve") is { } r ? Format.ParseSize(r) : 1L << 30;

        output.WriteLine($"Scanning {Path.GetFullPath(root)} (this downloads nothing)...");
        var overview = OneDriveOverview.Create(root, CloudFiles.Create(FreeUpMethod.Auto), reserve);
        foreach (var group in overview.Groups)
        {
            output.WriteLine($"  {group.State,-48} {group.Files,10:N0} files {Format.Size(group.Bytes),10}");
        }
        output.WriteLine($"  {"Total",-48} {overview.TotalFiles,10:N0} files {Format.Size(overview.TotalBytes),10}");
        foreach (var problem in overview.Problems)
        {
            output.WriteLine($"  Could not read {problem.Path}: {problem.Error}");
        }

        output.WriteLine();
        output.WriteLine($"Free space on {Path.GetPathRoot(overview.Root)}: {Format.Size(overview.FreeBytes)}");
        if (overview.LargestOnlineOnly is not { } largest)
        {
            output.WriteLine("Nothing is online-only, so a backup does not need to download anything.");
            return Ok;
        }
        output.WriteLine($"Largest online-only file: {largest.RelativePath} ({Format.Size(largest.Size)})");
        output.WriteLine($"A backup needs {Format.Size(overview.NeededBytes)} free (that file plus the {Format.Size(reserve)} reserve): " +
                         (overview.EnoughSpace ? "you have enough." : "you do NOT have enough; that file would be skipped."));
        return Ok;
    }

    private static int Probe(string[] args, TextWriter output, string pendingListPath)
    {
        var a = Arguments.Parse(args, Set(), Set());
        var path = Single(a, "<file>");
        return Windows.Probe.Run(path, output, new PendingFreeUps(pendingListPath)) ? Ok : FinishedWithProblems;
    }

    private static string Single(Arguments a, string name) => a.Positional.Count switch
    {
        1 => a.Positional[0],
        0 => throw new UsageException($"Missing {name}."),
        _ => throw new UsageException($"Expected just one {name}, got: {string.Join(" ", a.Positional)}"),
    };

    private static HashSet<string> Set(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);
}

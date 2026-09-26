using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;

namespace OnedriveBackuper.Core;

public sealed class BackupOptions
{
    public required string SourceRoot { get; init; }
    public required string BackupRoot { get; init; }

    /// <summary>Copy every file, even if an earlier backup already has it.</summary>
    public bool ForceFull { get; init; }

    /// <summary>Only report what would happen. Nothing is downloaded, copied or written.</summary>
    public bool DryRun { get; init; }

    public PathFilter Exclude { get; init; } = PathFilter.None;

    /// <summary>Free space to leave on the OneDrive drive, on top of the file being downloaded.</summary>
    public long ReserveBytes { get; init; } = 1L << 30;

    /// <summary>Waits between attempts to free up a file (antivirus or the search indexer often hold it open briefly).</summary>
    public IReadOnlyList<TimeSpan> FreeUpRetryDelays { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];
}

public sealed class BackupReport
{
    public string? SetId { get; set; }
    public BackupKind Kind { get; set; }
    public string? BaseSetId { get; set; }
    public bool Resumed { get; set; }
    public bool Cancelled { get; set; }
    public bool DryRun { get; set; }
    public int ScannedFiles { get; set; }
    public long ScannedBytes { get; set; }
    public int UnchangedFiles { get; set; }
    public int AlreadyCopiedBeforeResume { get; set; }
    public int CopiedFiles { get; set; }
    public long CopiedBytes { get; set; }
    public int DownloadedFiles { get; set; }
    public long DownloadedBytes { get; set; }
    public int FreedUpFiles { get; set; }
    public long LargestDownload { get; set; }
    public int ExcludedCount { get; set; }
    public int SkippedLinkCount { get; set; }
    public List<FailedFile> Failed { get; } = [];
    public List<string> StillDownloaded { get; } = [];
    public TimeSpan Elapsed { get; set; }
}

/// <summary>
/// Backs up a OneDrive folder one file at a time. A file that is only in the cloud is downloaded,
/// copied to the backup, and immediately freed up again, so the PC only ever needs room for the
/// single file being copied, however big the OneDrive is.
/// </summary>
public sealed class BackupEngine
{
    private readonly ICloudFiles cloud;
    private readonly PendingFreeUps pending;
    private readonly ILog log;
    private readonly TimeProvider time;
    private readonly Func<string, long> freeSpace;

    public BackupEngine(ICloudFiles cloud, PendingFreeUps pending, ILog log, TimeProvider? time = null, Func<string, long>? freeSpace = null)
    {
        this.cloud = cloud;
        this.pending = pending;
        this.log = log;
        this.time = time ?? TimeProvider.System;
        this.freeSpace = freeSpace ?? (path => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace);
    }

    public BackupReport Run(BackupOptions options, CancellationToken cancel)
    {
        var stopwatch = Stopwatch.StartNew();
        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.SourceRoot));
        var backupRoot = Path.GetFullPath(options.BackupRoot);
        CheckFolders(sourceRoot, backupRoot);

        var report = new BackupReport { DryRun = options.DryRun };
        var repository = new BackupRepository(backupRoot);
        using var folderLock = options.DryRun ? null : repository.Lock();

        if (!options.DryRun)
        {
            FreeUpLeftovers(options, report, "an earlier run that was interrupted");
        }

        var (set, baseline) = ChooseSet(repository, options, sourceRoot, report);

        log.Info($"Scanning {sourceRoot} (this downloads nothing)...");
        var scan = SourceScanner.Scan(sourceRoot, options.Exclude);
        report.ScannedFiles = scan.Files.Count;
        report.ScannedBytes = scan.Files.Sum(f => f.Size);
        report.ExcludedCount = scan.ExcludedCount;
        report.SkippedLinkCount = scan.SkippedLinkCount;
        foreach (var problem in scan.Problems)
        {
            log.Error($"Could not read folder {problem.Path}: {problem.Error}");
            report.Failed.Add(new FailedFile(Paths.ToRelative(sourceRoot, problem.Path), $"Could not read folder: {problem.Error}"));
        }
        log.Info($"Found {scan.Files.Count:N0} files ({Format.Size(report.ScannedBytes)}).");

        if (set == null)
        {
            DryRun(scan, baseline, report);
        }
        else
        {
            var entries = new List<FileEntry>();
            // Closed before Complete() deletes it: Windows cannot delete a file that is still open.
            using (var journal = SetJournal.Open(set.JournalPath))
            {
                report.AlreadyCopiedBeforeResume = journal.Entries.Count;
                var context = new RunContext(options, set, baseline, journal, report, sourceRoot);
                for (var i = 0; i < scan.Files.Count; i++)
                {
                    if (cancel.IsCancellationRequested)
                    {
                        report.Cancelled = true;
                        break;
                    }
                    var entry = ProcessFile(context, scan.Files[i], i + 1, scan.Files.Count);
                    if (entry != null)
                    {
                        entries.Add(entry);
                    }
                }
            }

            FreeUpLeftovers(options, report, "this run");
            if (!report.Cancelled)
            {
                KeepFilesInUnreadableFolders(scan, baseline, entries, sourceRoot);
                Complete(set, entries, report);
            }
        }

        report.StillDownloaded.AddRange(pending.Items);
        report.Elapsed = stopwatch.Elapsed;
        return report;
    }

    private static void CheckFolders(string sourceRoot, string backupRoot)
    {
        if (!Directory.Exists(sourceRoot))
        {
            throw new BackupException($"The folder to back up does not exist: {sourceRoot}");
        }
        if (Paths.IsInside(backupRoot, sourceRoot))
        {
            throw new BackupException("The backup folder cannot be inside the folder being backed up (it would be uploaded to OneDrive).");
        }
        if (Paths.IsInside(sourceRoot, backupRoot))
        {
            throw new BackupException("The folder being backed up cannot be inside the backup folder.");
        }
    }

    /// <summary>Resumes an unfinished set, or starts a full or incremental one. Returns no set for a dry run.</summary>
    private (BackupSet? Set, Dictionary<string, FileEntry> Baseline) ChooseSet(
        BackupRepository repository, BackupOptions options, string sourceRoot, BackupReport report)
    {
        var unfinished = repository.LatestIncomplete();
        if (unfinished != null && options.DryRun)
        {
            log.Info($"Note: the unfinished backup {unfinished.Id} will be resumed by the next real run.");
        }
        if (unfinished != null && !options.DryRun)
        {
            report.Resumed = true;
            log.Info($"Resuming unfinished backup {unfinished.Id}. Files it already copied are skipped.");
            if (options.ForceFull && unfinished.Info.Kind != BackupKind.Full)
            {
                log.Warn("--full was ignored: the unfinished backup is finished first. Run with --full again afterwards.");
            }
            return (Adopt(unfinished), LoadBaseline(repository, unfinished.Info.BaseSetId));
        }

        var previous = options.ForceFull ? null : repository.LatestComplete();
        if (previous != null && !Paths.Comparer.Equals(previous.Info.SourceRoot, sourceRoot))
        {
            log.Warn($"The last backup was of {previous.Info.SourceRoot}; this one is of {sourceRoot}. Files are matched by their path inside the folder.");
        }
        var kind = previous == null ? BackupKind.Full : BackupKind.Incremental;
        report.Kind = kind;
        report.BaseSetId = previous?.Id;
        var baseline = LoadBaseline(repository, previous?.Id);

        if (options.DryRun)
        {
            log.Info(kind == BackupKind.Full ? "Dry run of a full backup." : $"Dry run of an incremental backup on top of {previous!.Id}.");
            return (null, baseline);
        }

        var set = repository.CreateSet(kind, previous?.Id, sourceRoot, time.GetUtcNow());
        log.AttachFile(Path.Combine(repository.LogsFolder, set.Id + ".log"));
        report.SetId = set.Id;
        log.Info(kind == BackupKind.Full
            ? $"Starting full backup {set.Id}."
            : $"Starting incremental backup {set.Id}: only files that are new or changed since {previous!.Id} are copied.");
        return (set, baseline);

        BackupSet Adopt(BackupSet set)
        {
            report.SetId = set.Id;
            report.Kind = set.Info.Kind;
            report.BaseSetId = set.Info.BaseSetId;
            log.AttachFile(Path.Combine(repository.LogsFolder, set.Id + ".log"));
            return set;
        }
    }

    private static Dictionary<string, FileEntry> LoadBaseline(BackupRepository repository, string? setId)
    {
        var baseline = new Dictionary<string, FileEntry>(Paths.Comparer);
        if (setId != null)
        {
            foreach (var entry in repository.LoadManifest(repository.GetSet(setId)).Files)
            {
                baseline[entry.Path] = entry;
            }
        }
        return baseline;
    }

    private sealed record RunContext(
        BackupOptions Options,
        BackupSet Set,
        Dictionary<string, FileEntry> Baseline,
        SetJournal Journal,
        BackupReport Report,
        string SourceRoot);

    /// <summary>Returns the file's entry for the new manifest, or null if it should not be in it.</summary>
    private FileEntry? ProcessFile(RunContext run, SourceFile scanned, int number, int total)
    {
        var relativePath = scanned.RelativePath;
        // Re-read the file's details: a big backup can run for hours after the scan.
        var info = new FileInfo(scanned.FullPath);
        if (!info.Exists)
        {
            log.Detail($"{relativePath} was deleted during the backup; skipping it.");
            return null;
        }
        var size = info.Length;
        var lastWrite = info.LastWriteTimeUtc;

        if (run.Journal.Entries.TryGetValue(relativePath, out var done) && done.Size == size && done.LastWriteUtc == lastWrite)
        {
            return done;
        }

        run.Baseline.TryGetValue(relativePath, out var previous);
        if (previous != null && previous.Size == size && previous.LastWriteUtc == lastWrite)
        {
            run.Report.UnchangedFiles++;
            return previous;
        }

        var state = cloud.GetState(scanned.FullPath, info.Attributes);
        var progress = $"[{number}/{total}]";
        try
        {
            var entry = BackUpFile(run, relativePath, scanned.FullPath, size, lastWrite, state, progress);
            run.Journal.Append(entry);
            run.Report.CopiedFiles++;
            run.Report.CopiedBytes += size;
            return entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BackupException or PlatformNotSupportedException)
        {
            log.Error($"{progress} FAILED {relativePath}: {ex.Message}");
            run.Report.Failed.Add(new FailedFile(relativePath, ex.Message));
            // Keep the previous version (if any) in this backup; the file is retried on the next run.
            return previous;
        }
    }

    private FileEntry BackUpFile(RunContext run, string relativePath, string fullPath, long size, DateTime lastWrite, CloudFileState state, string progress)
    {
        var target = run.Set.DataPathFor(relativePath);
        var backupFree = freeSpace(run.Set.Folder);
        if (backupFree < size)
        {
            throw new BackupException($"The backup drive is full: the file needs {Format.Size(size)}, {Format.Size(backupFree)} free.");
        }

        if (!state.IsOnlineOnly)
        {
            log.Info($"{progress} copy     {relativePath} ({Format.Size(size)})");
            return CopyAndCheck(fullPath, target, relativePath, size, lastWrite, run.Set.Id);
        }

        var sourceFree = freeSpace(run.SourceRoot);
        if (sourceFree - size < run.Options.ReserveBytes)
        {
            throw new BackupException(
                $"Not enough free space to download it: needs {Format.Size(size)} plus the {Format.Size(run.Options.ReserveBytes)} reserve, " +
                $"{Format.Size(sourceFree)} free. Free up space on this drive or lower --reserve.");
        }

        log.Info($"{progress} download {relativePath} ({Format.Size(size)})");
        if (state.FreeUpAfterBackup)
        {
            pending.Add(fullPath);
        }
        try
        {
            var downloadTime = Stopwatch.StartNew();
            cloud.Hydrate(fullPath);
            run.Report.DownloadedFiles++;
            run.Report.DownloadedBytes += size;
            run.Report.LargestDownload = Math.Max(run.Report.LargestDownload, size);
            log.Detail($"         downloaded in {Format.Duration(downloadTime.Elapsed)}");
            // Take the reference size and time from the downloaded file, so the "changed while copying" check
            // only catches real edits, even if a sync client touched the metadata while downloading.
            var downloaded = new FileInfo(fullPath);
            return CopyAndCheck(fullPath, target, relativePath, downloaded.Length, downloaded.LastWriteTimeUtc, run.Set.Id);
        }
        finally
        {
            // Whatever happened (even a failed download can leave part of the file on disk), give the space back.
            if (state.FreeUpAfterBackup && FreeUp(run.Options, fullPath, relativePath))
            {
                run.Report.FreedUpFiles++;
            }
        }
    }

    /// <summary>Copies to a temporary file while hashing it, checks nothing changed meanwhile, then moves it into place.</summary>
    internal static FileEntry CopyAndCheck(string source, string target, string relativePath, long size, DateTime lastWrite, string setId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".partial";
        try
        {
            var (copied, sha256) = FileCopy.CopyWithHash(source, temp, size);
            var after = new FileInfo(source);
            if (copied != size || !after.Exists || after.Length != size || after.LastWriteTimeUtc != lastWrite)
            {
                throw new IOException("The file changed while it was being copied. It will be copied again next time.");
            }
            File.Move(temp, target, overwrite: true);
            File.SetLastWriteTimeUtc(target, lastWrite);
            return new FileEntry(relativePath, size, lastWrite, sha256, setId);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Makes the file online-only again, retrying for a while. Returns true once it has been freed up.</summary>
    private bool FreeUp(BackupOptions options, string fullPath, string displayPath)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt <= options.FreeUpRetryDelays.Count; attempt++)
        {
            if (attempt > 0)
            {
                Thread.Sleep(options.FreeUpRetryDelays[attempt - 1]);
            }
            if (!NeedsFreeingUp(fullPath))
            {
                pending.Remove(fullPath);
                return false;
            }
            try
            {
                cloud.Dehydrate(fullPath);
                pending.Remove(fullPath);
                log.Detail($"         freed up again");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or PlatformNotSupportedException)
            {
                lastError = ex;
                log.Detail($"         could not free up yet ({ex.Message})");
            }
        }
        log.Warn($"Could not free up the space used by {displayPath}: {lastError!.Message} It will be tried again at the end of this run and on the next run.");
        return false;
    }

    /// <summary>Frees up files a crashed or interrupted run left downloaded, plus any whose free-up failed earlier.</summary>
    private void FreeUpLeftovers(BackupOptions options, BackupReport report, string origin)
    {
        if (pending.Items.Count == 0)
        {
            return;
        }
        log.Info($"Freeing up {pending.Items.Count} file(s) left downloaded by {origin}...");
        foreach (var path in pending.Items.ToList())
        {
            if (FreeUp(options, path, path))
            {
                report.FreedUpFiles++;
            }
        }
    }

    /// <summary>
    /// False if the file has been deleted, or set to "Always keep on this device" (so the user wants it local now).
    /// </summary>
    private bool NeedsFreeingUp(string fullPath)
    {
        try
        {
            return File.Exists(fullPath) && !cloud.GetState(fullPath, File.GetAttributes(fullPath)).IsPinned;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // Cannot tell; try, and report if it fails.
        }
    }

    /// <summary>
    /// A folder that could not be read this time does not mean its files were deleted:
    /// keep their previous versions in the new backup.
    /// </summary>
    private static void KeepFilesInUnreadableFolders(ScanResult scan, Dictionary<string, FileEntry> baseline, List<FileEntry> entries, string sourceRoot)
    {
        foreach (var problem in scan.Problems)
        {
            var folder = Paths.ToRelative(sourceRoot, problem.Path);
            entries.AddRange(baseline.Values.Where(e => folder == "." || e.Path.StartsWith(folder + "/", Paths.Comparison)));
        }
    }

    private void DryRun(ScanResult scan, Dictionary<string, FileEntry> baseline, BackupReport report)
    {
        foreach (var file in scan.Files)
        {
            if (baseline.TryGetValue(file.RelativePath, out var previous) && previous.Size == file.Size && previous.LastWriteUtc == file.LastWriteUtc)
            {
                report.UnchangedFiles++;
                continue;
            }
            report.CopiedFiles++;
            report.CopiedBytes += file.Size;
            if (cloud.GetState(file.FullPath, file.Attributes).IsOnlineOnly)
            {
                report.DownloadedFiles++;
                report.DownloadedBytes += file.Size;
                report.LargestDownload = Math.Max(report.LargestDownload, file.Size);
                log.Detail($"would download {file.RelativePath} ({Format.Size(file.Size)})");
            }
            else
            {
                log.Detail($"would copy     {file.RelativePath} ({Format.Size(file.Size)})");
            }
        }
    }

    private void Complete(BackupSet set, List<FileEntry> entries, BackupReport report)
    {
        entries.Sort((a, b) => Paths.Comparer.Compare(a.Path, b.Path));
        set.Info.CompletedUtc = time.GetUtcNow();
        var copiedHere = entries.Where(e => e.StoredIn == set.Id).ToList();
        set.Info.Stats = new SetStats
        {
            TotalFiles = entries.Count,
            TotalBytes = entries.Sum(e => e.Size),
            CopiedFiles = copiedHere.Count,
            CopiedBytes = copiedHere.Sum(e => e.Size),
            DownloadedFiles = report.DownloadedFiles,
            DownloadedBytes = report.DownloadedBytes,
            UnchangedFiles = entries.Count - copiedHere.Count,
            FailedFiles = report.Failed.Count,
        };
        Json.WriteAtomically(set.InfoPath, set.Info);
        // The manifest goes last: once it exists, the set counts as complete.
        Json.WriteAtomically(set.ManifestPath, new SetManifest { SetId = set.Id, Files = entries, Failed = report.Failed });
        File.Delete(set.JournalPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftover temp files are overwritten next time.
        }
    }
}

internal static class FileCopy
{
    private const int BufferSize = 1 << 20;

    /// <summary>Copies a file and returns the bytes copied and their SHA-256 in one pass.</summary>
    public static (long Copied, string Sha256) CopyWithHash(string source, string target, long expectedSize)
    {
        using var input = new FileStream(source, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            // Let OneDrive and the user keep working with the file while it is copied.
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan,
            BufferSize = 0,
        });
        using var output = new FileStream(target, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 0,
            PreallocationSize = expectedSize,
        });
        return CopyWithHash(input, output);
    }

    public static (long Copied, string Sha256) CopyWithHash(Stream input, Stream? output)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long total = 0;
        try
        {
            int read;
            while ((read = input.Read(buffer, 0, BufferSize)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                output?.Write(buffer, 0, read);
                total += read;
            }
            if (output is FileStream file)
            {
                file.Flush(flushToDisk: true);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return (total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    public static string HashFile(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.SequentialScan);
        return CopyWithHash(input, null).Sha256;
    }
}

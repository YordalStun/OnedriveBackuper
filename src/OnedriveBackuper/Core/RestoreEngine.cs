namespace OnedriveBackuper.Core;

public sealed class RestoreReport
{
    public string SetId { get; set; } = "";
    public int RestoredFiles { get; set; }
    public long RestoredBytes { get; set; }
    public int SkippedExisting { get; set; }
    public bool Cancelled { get; set; }
    public List<FailedFile> Failed { get; } = [];
}

public sealed class VerifyReport
{
    public string SetId { get; set; } = "";
    public int CheckedFiles { get; set; }
    public long CheckedBytes { get; set; }
    public bool Cancelled { get; set; }
    public List<FailedFile> Problems { get; } = [];
}

/// <summary>Puts the files of one backup set back into a folder, and checks backups for damage.</summary>
public sealed class RestoreEngine(ILog log)
{
    /// <param name="setId">The set to restore, or null for the latest complete one.</param>
    /// <param name="only">Restore only files matching these patterns (or inside matching folders).</param>
    public RestoreReport Restore(string backupRoot, string? setId, string targetRoot, PathFilter only, bool overwrite, CancellationToken cancel)
    {
        var repository = new BackupRepository(backupRoot);
        var set = FindSet(repository, setId);
        var manifest = repository.LoadManifest(set);
        var report = new RestoreReport { SetId = set.Id };
        targetRoot = Path.GetFullPath(targetRoot);
        log.Info($"Restoring backup {set.Id} to {targetRoot}...");

        var sets = new Dictionary<string, BackupSet>(StringComparer.Ordinal);
        foreach (var entry in manifest.Files)
        {
            if (cancel.IsCancellationRequested)
            {
                report.Cancelled = true;
                break;
            }
            if (!only.IsEmpty && !only.MatchesSelfOrAncestor(entry.Path))
            {
                continue;
            }

            try
            {
                var target = Paths.CombineSafely(targetRoot, entry.Path);
                if (File.Exists(target) && !overwrite)
                {
                    report.SkippedExisting++;
                    continue;
                }
                var source = StoredCopy(repository, sets, entry);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temp = target + ".partial";
                try
                {
                    var (_, sha256) = FileCopy.CopyWithHash(source, temp, entry.Size);
                    if (!string.Equals(sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("The backup copy is damaged (its checksum does not match).");
                    }
                    File.Move(temp, target, overwrite: true);
                    File.SetLastWriteTimeUtc(target, entry.LastWriteUtc);
                }
                finally
                {
                    File.Delete(temp);
                }
                report.RestoredFiles++;
                report.RestoredBytes += entry.Size;
                log.Detail($"restored {entry.Path}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or BackupException)
            {
                log.Error($"FAILED {entry.Path}: {ex.Message}");
                report.Failed.Add(new FailedFile(entry.Path, ex.Message));
            }
        }
        return report;
    }

    /// <summary>Re-reads every file a set refers to (including those stored in earlier sets) and checks its checksum.</summary>
    public VerifyReport Verify(string backupRoot, string? setId, CancellationToken cancel)
    {
        var repository = new BackupRepository(backupRoot);
        var set = FindSet(repository, setId);
        var manifest = repository.LoadManifest(set);
        var report = new VerifyReport { SetId = set.Id };
        log.Info($"Checking backup {set.Id} ({manifest.Files.Count:N0} files)...");

        var sets = new Dictionary<string, BackupSet>(StringComparer.Ordinal);
        foreach (var entry in manifest.Files)
        {
            if (cancel.IsCancellationRequested)
            {
                report.Cancelled = true;
                break;
            }
            try
            {
                var source = StoredCopy(repository, sets, entry);
                var sha256 = FileCopy.HashFile(source);
                if (!string.Equals(sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The backup copy is damaged (its checksum does not match).");
                }
                report.CheckedFiles++;
                report.CheckedBytes += entry.Size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or BackupException)
            {
                log.Error($"PROBLEM {entry.Path}: {ex.Message}");
                report.Problems.Add(new FailedFile(entry.Path, ex.Message));
            }
        }
        return report;
    }

    private static BackupSet FindSet(BackupRepository repository, string? setId)
    {
        if (setId == null || setId.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            return repository.LatestComplete()
                ?? throw new BackupException($"There is no finished backup in {repository.Root}.");
        }
        return repository.GetSet(setId);
    }

    private static string StoredCopy(BackupRepository repository, Dictionary<string, BackupSet> sets, FileEntry entry)
    {
        if (!sets.TryGetValue(entry.StoredIn, out var set))
        {
            set = repository.GetSet(entry.StoredIn);
            sets[entry.StoredIn] = set;
        }
        var path = set.DataPathFor(entry.Path);
        if (!File.Exists(path))
        {
            throw new BackupException($"The backup copy is missing: {path}");
        }
        return path;
    }
}

namespace OnedriveBackuper.Core;

/// <summary>How often to start over with a full backup, and how many full backups to keep.</summary>
public sealed class RetentionSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Make the next backup a full one when the newest full backup is at least this old.</summary>
    public int FullEveryDays { get; set; } = 30;

    /// <summary>Keep this many full backups, each with the incremental backups made on top of it.</summary>
    public int KeepFullBackups { get; set; } = 3;
}

/// <summary>
/// Keeps the backup folder from growing forever. Backups form chains: a full backup plus the incremental
/// backups made after it. Clean-up deletes whole old chains, and never a set that a kept backup still needs.
/// </summary>
public static class Retention
{
    /// <summary>True when the newest finished full backup is at least <paramref name="fullEveryDays"/> old.</summary>
    public static bool IsFullBackupDue(BackupRepository repository, DateTimeOffset now, int fullEveryDays)
    {
        if (fullEveryDays <= 0)
        {
            return false;
        }
        var lastFull = repository.ListSets().LastOrDefault(s => s.IsComplete && s.Info.Kind == BackupKind.Full);
        return lastFull != null && now - lastFull.Info.StartedUtc >= TimeSpan.FromDays(fullEveryDays);
    }

    /// <summary>Deletes chains older than the newest <paramref name="keepFullBackups"/> full backups. Returns the deleted set ids.</summary>
    public static IReadOnlyList<string> Prune(BackupRepository repository, int keepFullBackups, ILog log)
    {
        repository.EmptyTrash();
        if (keepFullBackups < 1)
        {
            return [];
        }

        var sets = repository.ListSets();
        var fulls = sets.Where(s => s.IsComplete && s.Info.Kind == BackupKind.Full).ToList();
        if (fulls.Count <= keepFullBackups)
        {
            return [];
        }

        var oldestKept = fulls[^keepFullBackups];
        var keptIndex = sets.ToList().FindIndex(s => s.Id == oldestKept.Id);
        var kept = sets.Skip(keptIndex).ToList();
        var needed = new HashSet<string>(kept.SelectMany(s => SetsUsedBy(repository, s)), StringComparer.Ordinal);

        var deleted = new List<string>();
        foreach (var set in sets.Take(keptIndex))
        {
            if (needed.Contains(set.Id))
            {
                log.Detail($"Keeping old backup {set.Id}: a newer backup still uses files stored in it.");
                continue;
            }
            repository.DeleteSet(set);
            deleted.Add(set.Id);
            log.Info($"Deleted old backup {set.Id}.");
        }
        return deleted;
    }

    /// <summary>The complete sets that store some of this set's files, so this set cannot be deleted on its own.</summary>
    public static IReadOnlyList<string> Dependents(BackupRepository repository, BackupSet set) =>
        repository.ListSets()
            .Where(other => other.Id != set.Id && SetsUsedBy(repository, other).Contains(set.Id))
            .Select(other => other.Id)
            .ToList();

    /// <summary>Other sets this set needs: where its files are stored, and (while unfinished) the set it builds on.</summary>
    private static IEnumerable<string> SetsUsedBy(BackupRepository repository, BackupSet set)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        if (set.Info.BaseSetId != null)
        {
            used.Add(set.Info.BaseSetId);
        }
        if (!set.IsComplete)
        {
            return used;
        }
        if (set.Info.UsesSets != null)
        {
            used.UnionWith(set.Info.UsesSets);
        }
        else
        {
            // Sets made by version 0.1 do not record this; read their manifest instead.
            used.UnionWith(repository.LoadManifest(set).Files.Select(f => f.StoredIn));
        }
        used.Remove(set.Id);
        return used;
    }
}

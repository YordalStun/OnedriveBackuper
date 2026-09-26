namespace OnedriveBackuper.Core;

public sealed record StateGroup(string State, int Files, long Bytes);

/// <summary>What is in a OneDrive folder and whether there is room to back it up. Built from a scan, so it downloads nothing.</summary>
public sealed class OneDriveOverview
{
    public required string Root { get; init; }
    public required IReadOnlyList<StateGroup> Groups { get; init; }
    public required IReadOnlyList<ScanProblem> Problems { get; init; }
    public int TotalFiles { get; init; }
    public long TotalBytes { get; init; }
    public int OnlineOnlyFiles { get; init; }
    public long OnlineOnlyBytes { get; init; }
    public SourceFile? LargestOnlineOnly { get; init; }
    public long FreeBytes { get; init; }
    public long ReserveBytes { get; init; }

    /// <summary>Free space a backup needs: the biggest online-only file plus the reserve.</summary>
    public long NeededBytes => LargestOnlineOnly == null ? 0 : LargestOnlineOnly.Size + ReserveBytes;

    public bool EnoughSpace => FreeBytes >= NeededBytes;

    public static OneDriveOverview Create(string root, ICloudFiles cloud, long reserveBytes, PathFilter? exclude = null, Func<string, long>? freeSpace = null)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            throw new BackupException($"The folder does not exist: {root}");
        }
        var scan = SourceScanner.Scan(root, exclude ?? PathFilter.None);
        var withState = scan.Files.Select(f => (File: f, State: cloud.GetState(f.FullPath, f.Attributes))).ToList();
        var onlineOnly = withState.Where(x => x.State.IsOnlineOnly).Select(x => x.File).ToList();
        return new OneDriveOverview
        {
            Root = root,
            Groups = withState
                .GroupBy(x => x.State.Describe())
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new StateGroup(g.Key, g.Count(), g.Sum(x => x.File.Size)))
                .ToList(),
            Problems = scan.Problems,
            TotalFiles = scan.Files.Count,
            TotalBytes = scan.Files.Sum(f => f.Size),
            OnlineOnlyFiles = onlineOnly.Count,
            OnlineOnlyBytes = onlineOnly.Sum(f => f.Size),
            LargestOnlineOnly = onlineOnly.MaxBy(f => f.Size),
            FreeBytes = (freeSpace ?? DiskSpace.Free)(root),
            ReserveBytes = reserveBytes,
        };
    }
}

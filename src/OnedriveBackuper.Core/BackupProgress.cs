namespace OnedriveBackuper.Core;

public enum BackupPhase
{
    Starting,
    FreeingUpLeftovers,
    Scanning,
    BackingUp,
    Finishing,
    Finished,
}

public enum FileStage
{
    None,
    Downloading,
    Copying,
    FreeingUp,
}

/// <summary>Everything a progress display needs, at one moment. Immutable, so it can be handed between threads.</summary>
public sealed record BackupProgressSnapshot
{
    public BackupPhase Phase { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public string? SetId { get; init; }
    public BackupKind Kind { get; init; }
    public bool Resumed { get; init; }

    public int TotalFiles { get; init; }
    public long TotalBytes { get; init; }
    public int CheckedFiles { get; init; }

    /// <summary>The plan: what this run has to copy (and download first), worked out right after the scan.</summary>
    public int FilesToCopy { get; init; }
    public long BytesToCopy { get; init; }
    public int FilesToDownload { get; init; }
    public long BytesToDownload { get; init; }

    public int CopiedFiles { get; init; }
    public long CopiedBytes { get; init; }
    public int UnchangedFiles { get; init; }
    public int DownloadedFiles { get; init; }
    public long DownloadedBytes { get; init; }
    public int FreedUpFiles { get; init; }
    public int FailedFiles { get; init; }

    public string? CurrentFile { get; init; }
    public string? CurrentFullPath { get; init; }
    public long CurrentFileSize { get; init; }
    public bool CurrentFileNeedsDownload { get; init; }
    public FileStage CurrentStage { get; init; }
    public long CurrentFileCopiedBytes { get; init; }

    /// <summary>How far through the planned copying the run is, from 0 to 1, counting the file being copied.</summary>
    public double OverallFraction =>
        BytesToCopy > 0
            ? Math.Clamp((double)(CopiedBytes + CurrentFileCopiedBytes) / BytesToCopy, 0, 1)
            : TotalFiles > 0 ? (double)CheckedFiles / TotalFiles : 0;
}

/// <summary>Written by the backup (on its own thread), read by whoever shows progress.</summary>
public sealed class BackupProgress
{
    private readonly object gate = new();
    private BackupProgressSnapshot current = new();

    public BackupProgressSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    internal void Update(Func<BackupProgressSnapshot, BackupProgressSnapshot> change)
    {
        lock (gate)
        {
            current = change(current);
        }
    }
}

using OnedriveBackuper.Core;

namespace OnedriveBackuper.Tests;

/// <summary>
/// Pretends to be OneDrive. An online-only file exists on disk with the right size and timestamp but
/// zeros for content, so a backup that forgets to download a file ends up with zeros and the tests notice.
/// Tracks how much the backup has downloaded at once, which is the whole point of the tool.
/// </summary>
internal sealed class FakeOneDrive : ICloudFiles
{
    private readonly Dictionary<string, CloudFile> files = new(Paths.Comparer);
    private readonly HashSet<string> downloadedByBackup = new(Paths.Comparer);

    public FakeOneDrive(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
    }

    public string Root { get; }
    public long DiskCapacity { get; set; } = long.MaxValue / 2;
    public List<string> Hydrated { get; } = [];
    public List<string> Dehydrated { get; } = [];
    public int MostFilesDownloadedAtOnce { get; private set; }
    public long MostBytesDownloadedAtOnce { get; private set; }

    /// <summary>Return an exception to make hydrating that path fail.</summary>
    public Func<string, Exception?> HydrateFailure { get; set; } = _ => null;

    /// <summary>Called after a successful hydrate (for simulating edits or Ctrl+C mid-run).</summary>
    public Action<string> AfterHydrate { get; set; } = _ => { };

    /// <summary>How many more times freeing up each path should fail.</summary>
    public Dictionary<string, int> DehydrateFailures { get; } = new(Paths.Comparer);

    private long DownloadedBytesNow => downloadedByBackup.Sum(p => files[p].Content.Length);

    public long FreeSpace(string _) => DiskCapacity - DownloadedBytesNow;

    public string Add(string relativePath, string content, bool onlineOnly = true, bool pinned = false, DateTime? lastWrite = null)
    {
        var path = FullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        files[path] = new CloudFile
        {
            Content = System.Text.Encoding.UTF8.GetBytes(content),
            OnlineOnly = onlineOnly,
            Pinned = pinned,
            LastWrite = lastWrite ?? new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddSeconds(files.Count),
        };
        WriteToDisk(path);
        return path;
    }

    /// <summary>The user edits the file (which downloads it), and OneDrive later frees it up again.</summary>
    public void Edit(string relativePath, string content, bool leaveOnlineOnly = true)
    {
        var path = FullPath(relativePath);
        var file = files[path];
        file.Content = System.Text.Encoding.UTF8.GetBytes(content);
        file.LastWrite = file.LastWrite.AddHours(1);
        file.OnlineOnly = leaveOnlineOnly;
        WriteToDisk(path);
    }

    public void Delete(string relativePath)
    {
        var path = FullPath(relativePath);
        files.Remove(path);
        File.Delete(path);
    }

    public string Content(string relativePath) => System.Text.Encoding.UTF8.GetString(files[FullPath(relativePath)].Content);

    public bool IsOnlineOnly(string relativePath) => files[FullPath(relativePath)].OnlineOnly;

    public IEnumerable<string> RelativePaths => files.Keys.Select(p => Paths.ToRelative(Root, p));

    public string FullPath(string relativePath) => Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>As if the backup downloaded this file and then crashed before freeing it up.</summary>
    public void SimulateLeftBehindDownload(string relativePath)
    {
        var path = FullPath(relativePath);
        files[path].OnlineOnly = false;
        downloadedByBackup.Add(path);
        WriteToDisk(path);
    }

    public CloudFileState GetState(string fullPath, FileAttributes attributes) =>
        files.TryGetValue(fullPath, out var file) ? new CloudFileState(file.OnlineOnly, file.Pinned, false) : CloudFileState.Local;

    public void Hydrate(string fullPath)
    {
        var file = files[fullPath];
        Hydrated.Add(Paths.ToRelative(Root, fullPath));
        if (HydrateFailure(fullPath) is { } failure)
        {
            throw failure;
        }
        if (FreeSpace(fullPath) < file.Content.Length)
        {
            throw new IOException("Disk full.");
        }
        if (file.OnlineOnly)
        {
            file.OnlineOnly = false;
            downloadedByBackup.Add(fullPath);
            MostFilesDownloadedAtOnce = Math.Max(MostFilesDownloadedAtOnce, downloadedByBackup.Count);
            MostBytesDownloadedAtOnce = Math.Max(MostBytesDownloadedAtOnce, DownloadedBytesNow);
            WriteToDisk(fullPath);
        }
        AfterHydrate(fullPath);
    }

    public void Dehydrate(string fullPath)
    {
        var file = files[fullPath];
        if (file.Pinned)
        {
            throw new IOException("The file is pinned.");
        }
        if (DehydrateFailures.TryGetValue(fullPath, out var remaining) && remaining > 0)
        {
            DehydrateFailures[fullPath] = remaining - 1;
            throw new IOException("The file is in use by another program.");
        }
        Dehydrated.Add(Paths.ToRelative(Root, fullPath));
        file.OnlineOnly = true;
        downloadedByBackup.Remove(fullPath);
        WriteToDisk(fullPath);
    }

    private void WriteToDisk(string path)
    {
        var file = files[path];
        File.WriteAllBytes(path, file.OnlineOnly ? new byte[file.Content.Length] : file.Content);
        File.SetLastWriteTimeUtc(path, file.LastWrite);
    }

    private sealed class CloudFile
    {
        public byte[] Content { get; set; } = [];
        public bool OnlineOnly { get; set; }
        public bool Pinned { get; set; }
        public DateTime LastWrite { get; set; }
    }
}

/// <summary>A temp folder with a fake OneDrive, a backup folder and a restore folder.</summary>
internal sealed class TestFolders : IDisposable
{
    private DateTimeOffset now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    public TestFolders()
    {
        Root = Path.Combine(Path.GetTempPath(), "obk-tests", Guid.NewGuid().ToString("N"));
        OneDrive = new FakeOneDrive(Path.Combine(Root, "OneDrive"));
        BackupRoot = Path.Combine(Root, "Backup");
        PendingListPath = Path.Combine(Root, "state", "pending-free-up.txt");
    }

    public string Root { get; }
    public FakeOneDrive OneDrive { get; }
    public string BackupRoot { get; }
    public string PendingListPath { get; }
    public ListLog Log { get; } = new();
    public long BackupDriveFree { get; set; } = long.MaxValue / 2;

    private long FreeSpace(string path) => Paths.IsInside(path, OneDrive.Root) ? OneDrive.FreeSpace(path) : BackupDriveFree;

    public BackupReport Backup(bool full = false, bool dryRun = false, long reserve = 0, string[]? exclude = null, CancellationToken cancel = default)
    {
        now = now.AddHours(1);
        var engine = new BackupEngine(OneDrive, new PendingFreeUps(PendingListPath), Log, new FixedTime(now), FreeSpace);
        return engine.Run(new BackupOptions
        {
            SourceRoot = OneDrive.Root,
            BackupRoot = BackupRoot,
            ForceFull = full,
            DryRun = dryRun,
            ReserveBytes = reserve,
            Exclude = new PathFilter(exclude ?? []),
            FreeUpRetryDelays = [TimeSpan.Zero, TimeSpan.Zero],
        }, cancel);
    }

    public string Restore(string? setId = null, string[]? only = null, bool overwrite = false, string? into = null)
    {
        var target = into ?? Path.Combine(Root, "Restore-" + Guid.NewGuid().ToString("N"));
        var report = new RestoreEngine(Log).Restore(BackupRoot, setId, target, new PathFilter(only ?? []), overwrite, CancellationToken.None);
        Assert.Empty(report.Failed);
        return target;
    }

    public SetManifest Manifest(string setId)
    {
        var repository = new BackupRepository(BackupRoot);
        return repository.LoadManifest(repository.GetSet(setId));
    }

    public PendingFreeUps PendingList() => new(PendingListPath);

    /// <summary>Every file under a folder as relative path -> content.</summary>
    public static Dictionary<string, string> ReadTree(string folder) =>
        !Directory.Exists(folder)
            ? []
            : Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .ToDictionary(p => Paths.ToRelative(folder, p), File.ReadAllText);

    public Dictionary<string, string> OneDriveContents() =>
        OneDrive.RelativePaths.ToDictionary(p => p, OneDrive.Content);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

internal sealed class ListLog : ILog
{
    public List<string> Lines { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> Errors { get; } = [];

    public void Info(string message) => Lines.Add(message);
    public void Warn(string message) { Lines.Add(message); Warnings.Add(message); }
    public void Error(string message) { Lines.Add(message); Errors.Add(message); }
    public void Detail(string message) => Lines.Add(message);
    public void AttachFile(string path) { }
}

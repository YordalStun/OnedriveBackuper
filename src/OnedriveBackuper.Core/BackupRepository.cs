using System.Globalization;
using System.Text.Json;

namespace OnedriveBackuper.Core;

/// <summary>
/// The backup folder. Layout:
/// <code>
/// backup-folder/
///   sets/20260925T143000Z-full/     one folder per backup run
///     set.json                      kind, base set, times, stats
///     manifest.json                 every file in this backup (written last: its presence means "complete")
///     journal.jsonl                 files copied so far (only while running, used to resume)
///     data/...                      the files this run copied, in their original folder structure
///   logs/20260925T143000Z-full.log
/// </code>
/// </summary>
public sealed class BackupRepository
{
    public BackupRepository(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }
    public string SetsFolder => Path.Combine(Root, "sets");
    public string LogsFolder => Path.Combine(Root, "logs");
    private string TrashFolder => Path.Combine(Root, "trash");

    public string LogPathFor(string setId) => Path.Combine(LogsFolder, setId + ".log");

    /// <summary>
    /// Deletes a set and its log. The folder is first moved out of sets/ in one step, so an interrupted
    /// delete can never leave something behind that looks like an unfinished backup to resume.
    /// </summary>
    public void DeleteSet(BackupSet set)
    {
        Directory.CreateDirectory(TrashFolder);
        var trash = Path.Combine(TrashFolder, $"{set.Id}-{Guid.NewGuid():N}");
        Directory.Move(set.Folder, trash);
        if (File.Exists(LogPathFor(set.Id)))
        {
            File.Delete(LogPathFor(set.Id));
        }
        Directory.Delete(trash, recursive: true);
    }

    /// <summary>Finishes deletes that were interrupted.</summary>
    public void EmptyTrash()
    {
        if (Directory.Exists(TrashFolder))
        {
            foreach (var folder in Directory.EnumerateDirectories(TrashFolder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    /// <summary>All sets, oldest first.</summary>
    public IReadOnlyList<BackupSet> ListSets()
    {
        if (!Directory.Exists(SetsFolder))
        {
            return [];
        }

        var sets = new List<BackupSet>();
        foreach (var folder in Directory.EnumerateDirectories(SetsFolder))
        {
            var infoPath = Path.Combine(folder, BackupSet.InfoFileName);
            if (File.Exists(infoPath))
            {
                sets.Add(new BackupSet(folder, Json.Read<SetInfo>(infoPath)));
            }
        }
        return sets
            .OrderBy(s => s.Info.StartedUtc)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
    }

    public BackupSet? LatestComplete() => ListSets().LastOrDefault(s => s.IsComplete);

    public BackupSet? LatestIncomplete() => ListSets().LastOrDefault(s => !s.IsComplete);

    public BackupSet GetSet(string id)
    {
        var folder = Path.Combine(SetsFolder, id);
        var infoPath = Path.Combine(folder, BackupSet.InfoFileName);
        if (id.IndexOfAny(['/', '\\']) >= 0 || !File.Exists(infoPath))
        {
            throw new BackupException($"There is no backup set called '{id}' in {Root}.");
        }
        return new BackupSet(folder, Json.Read<SetInfo>(infoPath));
    }

    public BackupSet CreateSet(BackupKind kind, string? baseSetId, string sourceRoot, DateTimeOffset now)
    {
        var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var suffix = kind == BackupKind.Full ? "full" : "incr";
        var id = $"{stamp}-{suffix}";
        for (var n = 2; Directory.Exists(Path.Combine(SetsFolder, id)); n++)
        {
            id = $"{stamp}-{suffix}-{n}";
        }

        var folder = Path.Combine(SetsFolder, id);
        Directory.CreateDirectory(Path.Combine(folder, BackupSet.DataFolderName));
        var info = new SetInfo
        {
            SetId = id,
            Kind = kind,
            BaseSetId = baseSetId,
            SourceRoot = sourceRoot,
            StartedUtc = now,
        };
        Json.WriteAtomically(Path.Combine(folder, BackupSet.InfoFileName), info);
        return new BackupSet(folder, info);
    }

    public SetManifest LoadManifest(BackupSet set)
    {
        if (!set.IsComplete)
        {
            throw new BackupException($"Backup set {set.Id} did not finish, so it has no manifest.");
        }
        return Json.Read<SetManifest>(set.ManifestPath);
    }

    /// <summary>Stops two backups from writing into the same backup folder at once.</summary>
    public IDisposable Lock()
    {
        Directory.CreateDirectory(Root);
        var lockPath = Path.Combine(Root, ".lock");
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new BackupException($"Another OnedriveBackuper is already using {Root}. Wait for it to finish.");
        }
    }
}

public sealed class BackupSet
{
    public const string InfoFileName = "set.json";
    public const string ManifestFileName = "manifest.json";
    public const string JournalFileName = "journal.jsonl";
    public const string DataFolderName = "data";

    public BackupSet(string folder, SetInfo info)
    {
        Folder = folder;
        Info = info;
    }

    public string Folder { get; }
    public SetInfo Info { get; }
    public string Id => Info.SetId;
    public string InfoPath => Path.Combine(Folder, InfoFileName);
    public string ManifestPath => Path.Combine(Folder, ManifestFileName);
    public string JournalPath => Path.Combine(Folder, JournalFileName);
    public string DataFolder => Path.Combine(Folder, DataFolderName);
    public bool IsComplete => File.Exists(ManifestPath);

    public string DataPathFor(string relativePath) => Paths.CombineSafely(DataFolder, relativePath);
}

/// <summary>
/// Append-only list of the files a running backup has finished. Each line is flushed to disk,
/// so after a crash or Ctrl+C the next run skips everything already copied.
/// </summary>
public sealed class SetJournal : IDisposable
{
    private readonly StreamWriter writer;

    private SetJournal(StreamWriter writer, Dictionary<string, FileEntry> entries)
    {
        this.writer = writer;
        Entries = entries;
    }

    public IReadOnlyDictionary<string, FileEntry> Entries { get; }

    public static SetJournal Open(string path)
    {
        var entries = new Dictionary<string, FileEntry>(Paths.Comparer);
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path))
            {
                FileEntry? entry = null;
                try
                {
                    entry = JsonSerializer.Deserialize<FileEntry>(line, Json.Compact);
                }
                catch (JsonException)
                {
                    // The last line can be cut short if the PC lost power mid-write. That file is simply copied again.
                }
                if (entry != null)
                {
                    entries[entry.Path] = entry;
                }
            }
        }

        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        // A torn last line is ignored when reading; start on a fresh line so the next entry is not glued to it.
        var writer = new StreamWriter(stream);
        if (stream.Length > 0)
        {
            writer.WriteLine();
        }
        return new SetJournal(writer, entries);
    }

    public void Append(FileEntry entry)
    {
        writer.WriteLine(JsonSerializer.Serialize(entry, Json.Compact));
        writer.Flush();
        ((FileStream)writer.BaseStream).Flush(flushToDisk: true);
    }

    public void Dispose() => writer.Dispose();
}

public sealed class BackupException(string message) : Exception(message);

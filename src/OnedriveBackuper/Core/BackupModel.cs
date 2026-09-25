using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnedriveBackuper.Core;

public enum BackupKind
{
    Full,
    Incremental,
}

/// <summary>One backed-up file. <see cref="StoredIn"/> is the set whose data folder holds its content.</summary>
public sealed record FileEntry(string Path, long Size, DateTime LastWriteUtc, string Sha256, string StoredIn);

public sealed record FailedFile(string Path, string Error);

public sealed class SetStats
{
    public int TotalFiles { get; set; }
    public long TotalBytes { get; set; }
    public int CopiedFiles { get; set; }
    public long CopiedBytes { get; set; }
    public int DownloadedFiles { get; set; }
    public long DownloadedBytes { get; set; }
    public int UnchangedFiles { get; set; }
    public int FailedFiles { get; set; }
}

/// <summary>Written as set.json when a set starts, and rewritten with stats when it completes.</summary>
public sealed class SetInfo
{
    public int FormatVersion { get; set; } = 1;
    public string SetId { get; set; } = "";
    public BackupKind Kind { get; set; }
    public string? BaseSetId { get; set; }
    public string SourceRoot { get; set; } = "";
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public SetStats? Stats { get; set; }
}

/// <summary>
/// manifest.json: every file that existed in the source when the set was made, with where its content lives.
/// A set is complete once its manifest exists. Restoring a set only needs its own manifest.
/// </summary>
public sealed class SetManifest
{
    public int FormatVersion { get; set; } = 1;
    public string SetId { get; set; } = "";
    public List<FileEntry> Files { get; set; } = [];
    public List<FailedFile> Failed { get; set; } = [];
}

internal static class Json
{
    public static JsonSerializerOptions Indented { get; } = Create(indented: true);
    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented) => new()
    {
        WriteIndented = indented,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Indented)
        ?? throw new InvalidDataException($"{path} is empty.");

    /// <summary>Writes to a temporary file first, so a crash never leaves a half-written file behind.</summary>
    public static void WriteAtomically<T>(string path, T value)
    {
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, Indented);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }
}

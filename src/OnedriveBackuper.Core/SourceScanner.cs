namespace OnedriveBackuper.Core;

public sealed record SourceFile(string RelativePath, string FullPath, long Size, DateTime LastWriteUtc, FileAttributes Attributes);

public sealed record ScanProblem(string Path, string Error);

public sealed class ScanResult
{
    public List<SourceFile> Files { get; } = [];
    public List<ScanProblem> Problems { get; } = [];
    public int ExcludedCount { get; set; }
    public int SkippedLinkCount { get; set; }
}

/// <summary>
/// Lists every file under a folder using directory listings only. On Windows the listing returns
/// size, timestamps and attributes (including the OneDrive state bits) without opening any file,
/// so scanning a OneDrive folder downloads nothing.
/// </summary>
public static class SourceScanner
{
    public static ScanResult Scan(string root, PathFilter exclude)
    {
        var result = new ScanResult();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false, // .NET's own recursion skips folders that are reparse points, which OneDrive folders can be.
            AttributesToSkip = 0,          // Back up hidden and system files too.
            IgnoreInaccessible = false,    // Report unreadable folders instead of silently leaving them out.
            ReturnSpecialDirectories = false,
        };

        var folders = new Stack<DirectoryInfo>();
        folders.Push(new DirectoryInfo(root));
        while (folders.TryPop(out var folder))
        {
            List<FileSystemInfo> entries;
            try
            {
                entries = folder.EnumerateFileSystemInfos("*", options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Problems.Add(new ScanProblem(folder.FullName, ex.Message));
                continue;
            }

            foreach (var entry in entries)
            {
                var relativePath = Paths.ToRelative(root, entry.FullName);
                if (exclude.Matches(relativePath))
                {
                    result.ExcludedCount++;
                    continue;
                }
                if (IsLink(entry))
                {
                    result.SkippedLinkCount++;
                    continue;
                }

                if (entry is DirectoryInfo subfolder)
                {
                    folders.Push(subfolder);
                }
                else if (entry is FileInfo file)
                {
                    result.Files.Add(new SourceFile(relativePath, file.FullName, file.Length, file.LastWriteTimeUtc, file.Attributes));
                }
            }
        }

        result.Files.Sort((a, b) => Paths.Comparer.Compare(a.RelativePath, b.RelativePath));
        return result;
    }

    /// <summary>
    /// Symbolic links and junctions are skipped so the backup never wanders outside the folder.
    /// OneDrive placeholders are reparse points too, but they are not links, so they are kept.
    /// </summary>
    private static bool IsLink(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            return false;
        }
        try
        {
            return entry.LinkTarget != null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

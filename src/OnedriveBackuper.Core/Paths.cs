namespace OnedriveBackuper.Core;

/// <summary>
/// Relative paths are stored with '/' separators so backups read the same everywhere.
/// </summary>
public static class Paths
{
    /// <summary>OneDrive, like Windows, treats "Report.docx" and "report.docx" as the same file.</summary>
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string ToRelative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// Joins a stored relative path onto a root, refusing anything that would land outside the root
    /// (for example "../x" in a damaged or tampered manifest).
    /// </summary>
    public static string CombineSafely(string root, string relativePath)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var combined = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInside(combined, fullRoot) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Refusing path that points outside {fullRoot}: {relativePath}");
        }
        return combined;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or somewhere below it.</summary>
    public static bool IsInside(string path, string folder)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return p.Equals(f, Comparison) ||
               p.StartsWith(f + Path.DirectorySeparatorChar, Comparison);
    }
}

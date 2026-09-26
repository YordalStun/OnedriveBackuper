using System.IO.Enumeration;

namespace OnedriveBackuper.Core;

/// <summary>
/// Wildcard patterns (* and ?) matched against relative paths like "Pictures/Camera Roll/img.jpg".
/// A pattern matches when it matches the whole relative path or just the name, so "*.tmp",
/// "Videos" and "Pictures/Camera Roll" all work. Matching is case-insensitive, like OneDrive.
/// </summary>
public sealed class PathFilter
{
    private readonly string[] patterns;

    public PathFilter(IEnumerable<string> patterns)
    {
        this.patterns = patterns
            .Select(p => p.Replace('\\', '/').Trim().Trim('/'))
            .Where(p => p.Length > 0)
            .ToArray();
    }

    public static PathFilter None { get; } = new([]);

    public bool IsEmpty => patterns.Length == 0;

    /// <summary>Does a pattern match this path or its name?</summary>
    public bool Matches(string relativePath)
    {
        var name = relativePath[(relativePath.LastIndexOf('/') + 1)..];
        foreach (var pattern in patterns)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, relativePath, ignoreCase: true) ||
                FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Does a pattern match this path, or any folder it is in?</summary>
    public bool MatchesSelfOrAncestor(string relativePath)
    {
        for (var end = relativePath.IndexOf('/'); end >= 0; end = relativePath.IndexOf('/', end + 1))
        {
            if (Matches(relativePath[..end]))
            {
                return true;
            }
        }
        return Matches(relativePath);
    }
}

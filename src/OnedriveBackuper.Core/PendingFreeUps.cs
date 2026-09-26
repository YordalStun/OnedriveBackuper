namespace OnedriveBackuper.Core;

/// <summary>
/// Files the backup downloaded and has not yet freed up again. A file is added just before it is downloaded
/// and removed once it is online-only again, so if the backup crashes, loses power or is killed, the next run
/// knows exactly which files to free up. Kept per PC (not in the backup folder) because it is about this PC's disk.
/// </summary>
public sealed class PendingFreeUps
{
    private readonly string path;
    private readonly List<string> items;

    public PendingFreeUps(string path)
    {
        this.path = path;
        items = File.Exists(path)
            ? File.ReadAllLines(path).Where(line => line.Length > 0).Distinct(Paths.Comparer).ToList()
            : [];
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OnedriveBackuper",
        "pending-free-up.txt");

    public IReadOnlyList<string> Items => items;

    public void Add(string fullPath)
    {
        if (!items.Contains(fullPath, Paths.Comparer))
        {
            items.Add(fullPath);
            Save();
        }
    }

    public void Remove(string fullPath)
    {
        if (items.RemoveAll(p => Paths.Comparer.Equals(p, fullPath)) > 0)
        {
            Save();
        }
    }

    private void Save()
    {
        if (items.Count == 0)
        {
            File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllLines(temp, items);
        File.Move(temp, path, overwrite: true);
    }
}

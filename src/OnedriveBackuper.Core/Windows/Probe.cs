using System.Diagnostics;
using OnedriveBackuper.Core;

namespace OnedriveBackuper.Windows;

/// <summary>
/// Downloads one online-only file and frees it up again, step by step, reporting what happened.
/// The quickest way to check that everything the backup relies on works on a given PC.
/// </summary>
public static class Probe
{
    /// <returns>True if every step worked.</returns>
    public static bool Run(string path, TextWriter output, PendingFreeUps pending)
    {
        path = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            throw new BackupException("This test needs Windows 10 version 1709 or later.");
        }
        if (!File.Exists(path))
        {
            throw new BackupException($"File not found: {path}");
        }

        var before = new FileInfo(path);
        var state = CloudFileState.FromAttributes(before.Attributes);
        output.WriteLine($"File:        {path}");
        output.WriteLine($"Size:        {Format.Size(before.Length)}, last changed {before.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
        output.WriteLine($"State:       {state.Describe()} (attributes 0x{(int)before.Attributes:X8})");
        if (!state.IsOnlineOnly || state.IsPinned)
        {
            throw new BackupException("Pick a file that is online-only (cloud icon in Explorer) and not set to 'Always keep on this device'.");
        }

        var cloud = new WindowsCloudFiles(FreeUpMethod.Direct);
        var allGood = true;
        pending.Add(path);

        output.Write("1. Download (CfHydratePlaceholder)... ");
        var timer = Stopwatch.StartNew();
        try
        {
            cloud.Hydrate(path);
            output.WriteLine($"OK in {Format.Duration(timer.Elapsed)}. State now: {CurrentState(path)}");
        }
        catch (IOException ex)
        {
            output.WriteLine($"FAILED: {ex.Message}");
            allGood = false;
        }

        output.Write("2. Read whole file (SHA-256)... ");
        timer.Restart();
        try
        {
            var hash = FileCopy.HashFile(path);
            output.WriteLine($"OK in {Format.Duration(timer.Elapsed)}: {hash}");
        }
        catch (IOException ex)
        {
            output.WriteLine($"FAILED: {ex.Message}");
            allGood = false;
        }

        output.Write("3. Free up space (CfUpdatePlaceholder DEHYDRATE)... ");
        var freed = false;
        try
        {
            cloud.DehydrateDirectly(path);
            freed = true;
            output.WriteLine($"OK. State now: {CurrentState(path)}");
        }
        catch (IOException ex)
        {
            output.WriteLine($"FAILED: {ex.Message}");
            allGood = false;
        }

        if (!freed)
        {
            output.Write("3b. Free up space the Explorer way (unpin, wait for OneDrive)... ");
            try
            {
                cloud.DehydrateByUnpinning(path);
                freed = true;
                output.WriteLine($"OK. State now: {CurrentState(path)}");
                output.WriteLine("    -> Set 'How to free up space' to 'Automatic' or 'The Explorer way' on this PC.");
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                output.WriteLine($"FAILED: {ex.Message}");
            }
        }
        if (freed)
        {
            pending.Remove(path);
        }

        var after = new FileInfo(path);
        var unchanged = after.Length == before.Length && after.LastWriteTimeUtc == before.LastWriteTimeUtc;
        output.WriteLine($"4. Size and last-changed time untouched by all this: {(unchanged ? "yes" : "NO (incremental backups would re-copy files)")}");
        allGood &= unchanged && freed;

        output.WriteLine();
        output.WriteLine(allGood
            ? "Everything works on this PC."
            : freed
                ? "It works, but see the steps marked FAILED or NO above."
                : "The file could not be freed up again. Right-click it in Explorer and choose 'Free up space'.");
        return allGood;
    }

    private static string CurrentState(string path) => CloudFileState.FromAttributes(File.GetAttributes(path)).Describe();
}

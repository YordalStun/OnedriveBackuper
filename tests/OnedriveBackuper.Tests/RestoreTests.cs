using System.Text.Json;
using OnedriveBackuper.Core;

namespace OnedriveBackuper.Tests;

public sealed class RestoreTests : IDisposable
{
    private readonly TestFolders t = new();

    public void Dispose() => t.Dispose();

    [Fact]
    public void Every_backup_restores_the_onedrive_exactly_as_it_was_at_that_time()
    {
        t.OneDrive.Add("Documents/a.txt", "a1");
        t.OneDrive.Add("Documents/b.txt", "b1");
        t.OneDrive.Add("c.txt", "c1", onlineOnly: false);
        var full = t.Backup();
        var atFull = t.OneDriveContents();

        t.OneDrive.Edit("Documents/a.txt", "a2");
        t.OneDrive.Delete("Documents/b.txt");
        t.OneDrive.Add("Documents/d.txt", "d2");
        var incr1 = t.Backup();
        var atIncr1 = t.OneDriveContents();

        t.OneDrive.Edit("c.txt", "c3", leaveOnlineOnly: false);
        t.OneDrive.Edit("Documents/a.txt", "a3");
        var incr2 = t.Backup();
        var atIncr2 = t.OneDriveContents();

        Assert.Equal(atFull, TestFolders.ReadTree(t.Restore(full.SetId)));
        Assert.Equal(atIncr1, TestFolders.ReadTree(t.Restore(incr1.SetId)));
        Assert.Equal(atIncr2, TestFolders.ReadTree(t.Restore(incr2.SetId)));
        Assert.Equal(atIncr2, TestFolders.ReadTree(t.Restore()));
    }

    [Fact]
    public void Restored_files_get_their_original_last_changed_time()
    {
        var lastWrite = new DateTime(2024, 2, 29, 23, 59, 58, DateTimeKind.Utc);
        t.OneDrive.Add("a.txt", "a", lastWrite: lastWrite);
        t.Backup();

        var restored = t.Restore();

        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(Path.Combine(restored, "a.txt")));
    }

    [Fact]
    public void Only_restores_matching_files_and_folders()
    {
        t.OneDrive.Add("Documents/Taxes/2025.pdf", "tax");
        t.OneDrive.Add("Documents/letter.docx", "letter");
        t.OneDrive.Add("Pictures/a.jpg", "pic");
        t.Backup();

        var restored = TestFolders.ReadTree(t.Restore(only: ["Documents/Taxes", "*.jpg"]));

        Assert.Equal(["Documents/Taxes/2025.pdf", "Pictures/a.jpg"], restored.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Existing_files_are_kept_unless_overwrite_is_asked_for()
    {
        t.OneDrive.Add("a.txt", "from backup");
        t.Backup();
        var target = Path.Combine(t.Root, "Target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "a.txt"), "mine");

        t.Restore(into: target);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "a.txt")));

        t.Restore(into: target, overwrite: true);
        Assert.Equal("from backup", File.ReadAllText(Path.Combine(target, "a.txt")));
    }

    [Fact]
    public void Verify_finds_damaged_and_missing_backup_copies()
    {
        t.OneDrive.Add("good.txt", "good");
        t.OneDrive.Add("damaged.txt", "damaged");
        t.OneDrive.Add("missing.txt", "missing");
        var report = t.Backup();
        var set = new BackupRepository(t.BackupRoot).GetSet(report.SetId!);
        File.WriteAllText(set.DataPathFor("damaged.txt"), "bit rot");
        File.Delete(set.DataPathFor("missing.txt"));

        var verify = new RestoreEngine(t.Log).Verify(t.BackupRoot, null, CancellationToken.None);

        Assert.Equal(1, verify.CheckedFiles);
        Assert.Equal(["damaged.txt", "missing.txt"], verify.Problems.Select(p => p.Path).Order());
    }

    [Fact]
    public void Restore_refuses_a_damaged_copy()
    {
        t.OneDrive.Add("a.txt", "original");
        var report = t.Backup();
        var set = new BackupRepository(t.BackupRoot).GetSet(report.SetId!);
        File.WriteAllText(set.DataPathFor("a.txt"), "tampered");
        var target = Path.Combine(t.Root, "Target");

        var restore = new RestoreEngine(t.Log).Restore(t.BackupRoot, null, target, PathFilter.None, false, CancellationToken.None);

        Assert.Contains("damaged", Assert.Single(restore.Failed).Error);
        Assert.False(File.Exists(Path.Combine(target, "a.txt")));
        Assert.Empty(Directory.GetFiles(target, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Restore_never_writes_outside_the_target_folder()
    {
        t.OneDrive.Add("a.txt", "a");
        var report = t.Backup();
        var set = new BackupRepository(t.BackupRoot).GetSet(report.SetId!);
        var manifest = t.Manifest(report.SetId!);
        manifest.Files[0] = manifest.Files[0] with { Path = "../escaped.txt" };
        File.WriteAllText(set.ManifestPath, JsonSerializer.Serialize(manifest, Json.Indented));
        var target = Path.Combine(t.Root, "Target");

        var restore = new RestoreEngine(t.Log).Restore(t.BackupRoot, null, target, PathFilter.None, false, CancellationToken.None);

        Assert.Single(restore.Failed);
        Assert.False(File.Exists(Path.Combine(t.Root, "escaped.txt")));
    }
}

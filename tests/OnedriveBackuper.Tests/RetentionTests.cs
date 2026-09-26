using OnedriveBackuper.Core;

namespace OnedriveBackuper.Tests;

public sealed class RetentionTests : IDisposable
{
    private readonly TestFolders t = new();

    public void Dispose() => t.Dispose();

    private BackupRepository Repository => new(t.BackupRoot);

    private IReadOnlyList<string> SetIds() => Repository.ListSets().Select(s => s.Id).ToList();

    [Fact]
    public void A_full_backup_is_due_once_the_last_full_one_is_old_enough()
    {
        t.OneDrive.Add("a.txt", "a");
        Assert.False(Retention.IsFullBackupDue(Repository, t.Now, 30)); // No backups yet: the first one is full anyway.

        t.Backup();
        Assert.False(Retention.IsFullBackupDue(Repository, t.Now, 30));

        t.Wait(TimeSpan.FromDays(29));
        t.Backup();
        Assert.False(Retention.IsFullBackupDue(Repository, t.Now, 30)); // An incremental does not reset the clock...

        t.Wait(TimeSpan.FromDays(2));
        Assert.True(Retention.IsFullBackupDue(Repository, t.Now, 30)); // ...only a full backup does.
        Assert.False(Retention.IsFullBackupDue(Repository, t.Now, 0)); // 0 means never force one.
    }

    [Fact]
    public void Clean_up_keeps_the_newest_full_backups_with_their_incrementals()
    {
        t.OneDrive.Add("a.txt", "a");
        var chains = new List<List<string>>();
        for (var chain = 0; chain < 4; chain++)
        {
            var ids = new List<string> { t.Backup(full: true).SetId! };
            t.OneDrive.Edit("a.txt", $"edit {chain}");
            ids.Add(t.Backup().SetId!);
            chains.Add(ids);
        }

        var deleted = Retention.Prune(Repository, keepFullBackups: 2, t.Log);

        Assert.Equal(chains[0].Concat(chains[1]), deleted);
        Assert.Equal(chains[2].Concat(chains[3]), SetIds());
        foreach (var id in deleted)
        {
            Assert.False(File.Exists(Repository.LogPathFor(id)));
        }
        Assert.Equal(t.OneDriveContents(), TestFolders.ReadTree(t.Restore()));
        Assert.Equal("edit 2", TestFolders.ReadTree(t.Restore(chains[2][1]))["a.txt"]);
    }

    [Fact]
    public void Clean_up_does_nothing_while_there_are_few_enough_full_backups()
    {
        t.OneDrive.Add("a.txt", "a");
        t.Backup();
        t.Backup(full: true);

        Assert.Empty(Retention.Prune(Repository, keepFullBackups: 2, t.Log));
        Assert.Empty(Retention.Prune(Repository, keepFullBackups: 0, t.Log));
        Assert.Equal(2, SetIds().Count);
    }

    [Fact]
    public void Clean_up_never_deletes_a_set_that_a_kept_backup_still_needs()
    {
        t.OneDrive.Add("big-unchanged.bin", "stored once, in the first backup");
        var first = t.Backup().SetId!;
        // A full backup that failed to copy the big file keeps pointing at the first backup's copy.
        t.OneDrive.HydrateFailure = _ => new IOException("offline");
        t.Backup(full: true);
        t.OneDrive.HydrateFailure = _ => null;

        Retention.Prune(Repository, keepFullBackups: 1, t.Log);

        Assert.Contains(first, SetIds());
        Assert.Equal("stored once, in the first backup", TestFolders.ReadTree(t.Restore())["big-unchanged.bin"]);
    }

    [Fact]
    public void Dependents_lists_backups_that_need_a_set()
    {
        t.OneDrive.Add("a.txt", "a");
        t.OneDrive.Add("b.txt", "b");
        var full = t.Backup().SetId!;
        t.OneDrive.Edit("b.txt", "b2");
        var incremental = t.Backup().SetId!;

        Assert.Equal([incremental], Retention.Dependents(Repository, Repository.GetSet(full)));
        Assert.Empty(Retention.Dependents(Repository, Repository.GetSet(incremental)));
    }

    [Fact]
    public void An_interrupted_delete_is_finished_and_never_looks_like_a_backup_to_resume()
    {
        t.OneDrive.Add("a.txt", "a");
        t.Backup();
        var trash = Path.Combine(t.BackupRoot, "trash", "leftover");
        Directory.CreateDirectory(trash);
        File.WriteAllText(Path.Combine(trash, "set.json"), "{}");

        Retention.Prune(Repository, keepFullBackups: 3, t.Log);

        Assert.False(Directory.Exists(trash));
        Assert.False(t.Backup().Resumed);
    }
}

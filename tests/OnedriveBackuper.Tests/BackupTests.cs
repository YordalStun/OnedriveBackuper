using OnedriveBackuper.Core;

namespace OnedriveBackuper.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly TestFolders t = new();

    public void Dispose() => t.Dispose();

    [Fact]
    public void Full_backup_copies_every_file_and_frees_up_online_only_ones_again()
    {
        t.OneDrive.Add("Documents/report.docx", "report");
        t.OneDrive.Add("Documents/notes.txt", "notes");
        t.OneDrive.Add("Pictures/Camera Roll/img1.jpg", "picture one");
        t.OneDrive.Add("local.txt", "already on this PC", onlineOnly: false);
        t.OneDrive.Add("pinned.txt", "always keep", onlineOnly: false, pinned: true);

        var report = t.Backup();

        Assert.Empty(report.Failed);
        Assert.Equal(BackupKind.Full, report.Kind);
        Assert.Equal(5, report.CopiedFiles);
        Assert.Equal(3, report.DownloadedFiles);
        Assert.Equal(3, report.FreedUpFiles);

        var set = new BackupRepository(t.BackupRoot).GetSet(report.SetId!);
        Assert.Equal(t.OneDriveContents(), TestFolders.ReadTree(set.DataFolder));

        // Everything is exactly as the user left it.
        Assert.True(t.OneDrive.IsOnlineOnly("Documents/report.docx"));
        Assert.True(t.OneDrive.IsOnlineOnly("Pictures/Camera Roll/img1.jpg"));
        Assert.False(t.OneDrive.IsOnlineOnly("local.txt"));
        Assert.False(t.OneDrive.IsOnlineOnly("pinned.txt"));
        Assert.DoesNotContain("local.txt", t.OneDrive.Hydrated);
        Assert.DoesNotContain("local.txt", t.OneDrive.Dehydrated);
        Assert.DoesNotContain("pinned.txt", t.OneDrive.Dehydrated);
        Assert.Empty(t.PendingList().Items);

        // Never more than one file downloaded at a time.
        Assert.Equal(1, t.OneDrive.MostFilesDownloadedAtOnce);
    }

    [Fact]
    public void Manifest_records_size_time_and_checksum_of_every_file()
    {
        var lastWrite = new DateTime(2025, 5, 6, 7, 8, 9, DateTimeKind.Utc).AddTicks(1234567);
        t.OneDrive.Add("a.txt", "abc", lastWrite: lastWrite);

        var report = t.Backup();

        var entry = Assert.Single(t.Manifest(report.SetId!).Files);
        Assert.Equal("a.txt", entry.Path);
        Assert.Equal(3, entry.Size);
        Assert.Equal(lastWrite, entry.LastWriteUtc);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", entry.Sha256);
        Assert.Equal(report.SetId, entry.StoredIn);
    }

    [Fact]
    public void Only_needs_free_space_for_the_biggest_file_not_the_whole_onedrive()
    {
        for (var i = 0; i < 20; i++)
        {
            t.OneDrive.Add($"file{i:00}.bin", new string('x', 100 + i));
        }
        var reserve = 50;
        t.OneDrive.DiskCapacity = 119 + reserve; // Room for the largest file only; the whole OneDrive is ~2.2 KB.

        var report = t.Backup(reserve: reserve);

        Assert.Empty(report.Failed);
        Assert.Equal(20, report.CopiedFiles);
        Assert.Equal(119, t.OneDrive.MostBytesDownloadedAtOnce);
    }

    [Fact]
    public void A_file_too_big_for_the_free_space_is_reported_and_the_rest_still_backed_up()
    {
        t.OneDrive.Add("small.txt", "small");
        t.OneDrive.Add("huge.bin", new string('x', 1000));
        t.OneDrive.DiskCapacity = 500;

        var report = t.Backup();

        var failed = Assert.Single(report.Failed);
        Assert.Equal("huge.bin", failed.Path);
        Assert.Contains("Not enough free space", failed.Error);
        Assert.DoesNotContain("huge.bin", t.OneDrive.Hydrated);
        Assert.Equal(["small.txt"], t.Manifest(report.SetId!).Files.Select(f => f.Path));
    }

    [Fact]
    public void Incremental_backup_copies_only_new_and_changed_files_and_downloads_nothing_else()
    {
        t.OneDrive.Add("unchanged.txt", "same");
        t.OneDrive.Add("changed.txt", "old version");
        t.OneDrive.Add("deleted.txt", "gone soon");
        var full = t.Backup();

        t.OneDrive.Edit("changed.txt", "new version");
        t.OneDrive.Add("new.txt", "brand new");
        t.OneDrive.Delete("deleted.txt");
        t.OneDrive.Hydrated.Clear();
        var incremental = t.Backup();

        Assert.Equal(BackupKind.Incremental, incremental.Kind);
        Assert.Equal(full.SetId, incremental.BaseSetId);
        Assert.Equal(1, incremental.UnchangedFiles);
        Assert.Equal(2, incremental.CopiedFiles);
        Assert.Equal(["changed.txt", "new.txt"], t.OneDrive.Hydrated.Order());

        var manifest = t.Manifest(incremental.SetId!);
        Assert.Equal(["changed.txt", "new.txt", "unchanged.txt"], manifest.Files.Select(f => f.Path));
        Assert.Equal(full.SetId, manifest.Files.Single(f => f.Path == "unchanged.txt").StoredIn);
        Assert.Equal(incremental.SetId, manifest.Files.Single(f => f.Path == "changed.txt").StoredIn);

        var set = new BackupRepository(t.BackupRoot).GetSet(incremental.SetId!);
        Assert.Equal(["changed.txt", "new.txt"], TestFolders.ReadTree(set.DataFolder).Keys.Order());
    }

    [Fact]
    public void Full_flag_makes_a_new_full_backup()
    {
        t.OneDrive.Add("a.txt", "a");
        t.Backup();

        var second = t.Backup(full: true);

        Assert.Equal(BackupKind.Full, second.Kind);
        Assert.Equal(1, second.CopiedFiles);
    }

    [Fact]
    public void Stopping_keeps_what_was_copied_and_the_next_run_carries_on()
    {
        for (var i = 0; i < 5; i++)
        {
            t.OneDrive.Add($"f{i}.txt", $"content {i}");
        }
        using var stop = new CancellationTokenSource();
        t.OneDrive.AfterHydrate = path =>
        {
            if (path.EndsWith("f1.txt", StringComparison.Ordinal))
            {
                stop.Cancel(); // Ctrl+C while f1 is being backed up.
            }
        };

        var first = t.Backup(cancel: stop.Token);

        Assert.True(first.Cancelled);
        Assert.Equal(2, first.CopiedFiles); // The file in progress is finished...
        Assert.True(t.OneDrive.IsOnlineOnly("f1.txt")); // ...and freed up again.
        Assert.False(new BackupRepository(t.BackupRoot).GetSet(first.SetId!).IsComplete);

        t.OneDrive.AfterHydrate = _ => { };
        t.OneDrive.Hydrated.Clear();
        var second = t.Backup();

        Assert.True(second.Resumed);
        Assert.Equal(first.SetId, second.SetId);
        Assert.Equal(2, second.AlreadyCopiedBeforeResume);
        Assert.Equal(["f2.txt", "f3.txt", "f4.txt"], t.OneDrive.Hydrated);
        Assert.Equal(5, t.Manifest(second.SetId!).Files.Count);
        Assert.Equal(t.OneDriveContents(), TestFolders.ReadTree(t.Restore()));
    }

    [Fact]
    public void Files_left_downloaded_by_a_crashed_run_are_freed_up_by_the_next_run()
    {
        t.OneDrive.Add("big.bin", "big file");
        t.OneDrive.SimulateLeftBehindDownload("big.bin");
        t.PendingList().Add(t.OneDrive.FullPath("big.bin"));

        var report = t.Backup();

        Assert.True(t.OneDrive.IsOnlineOnly("big.bin"));
        Assert.Contains("big.bin", t.OneDrive.Dehydrated);
        Assert.Empty(t.PendingList().Items);
        Assert.Empty(report.StillDownloaded);
    }

    [Fact]
    public void Freeing_up_is_retried_when_the_file_is_briefly_in_use()
    {
        t.OneDrive.Add("a.txt", "a");
        t.OneDrive.DehydrateFailures[t.OneDrive.FullPath("a.txt")] = 2;

        var report = t.Backup();

        Assert.Empty(report.StillDownloaded);
        Assert.True(t.OneDrive.IsOnlineOnly("a.txt"));
        Assert.Empty(t.Log.Warnings);
    }

    [Fact]
    public void A_file_that_cannot_be_freed_up_is_reported_and_remembered_for_next_time()
    {
        t.OneDrive.Add("stuck.txt", "stuck");
        t.OneDrive.DehydrateFailures[t.OneDrive.FullPath("stuck.txt")] = 100;

        var report = t.Backup();

        Assert.Empty(report.Failed); // The backup copy itself is fine.
        Assert.Equal([t.OneDrive.FullPath("stuck.txt")], report.StillDownloaded);
        Assert.Equal([t.OneDrive.FullPath("stuck.txt")], t.PendingList().Items);
        Assert.NotEmpty(t.Log.Warnings);

        t.OneDrive.DehydrateFailures.Clear();
        var next = t.Backup();

        Assert.Empty(next.StillDownloaded);
        Assert.True(t.OneDrive.IsOnlineOnly("stuck.txt"));
    }

    [Fact]
    public void A_failed_download_is_reported_freed_up_and_retried_next_run()
    {
        t.OneDrive.Add("ok.txt", "ok");
        t.OneDrive.Add("flaky.txt", "flaky");
        t.OneDrive.HydrateFailure = path => path.EndsWith("flaky.txt", StringComparison.Ordinal) ? new IOException("The cloud operation was unsuccessful.") : null;

        var first = t.Backup();

        Assert.Equal("flaky.txt", Assert.Single(first.Failed).Path);
        Assert.Contains("flaky.txt", t.OneDrive.Dehydrated); // Partial downloads get cleaned up too.
        Assert.Equal(["ok.txt"], t.Manifest(first.SetId!).Files.Select(f => f.Path));
        Assert.Empty(t.PendingList().Items);

        t.OneDrive.HydrateFailure = _ => null;
        var second = t.Backup();

        Assert.Empty(second.Failed);
        Assert.Equal(1, second.CopiedFiles);
        Assert.Equal(["flaky.txt", "ok.txt"], t.Manifest(second.SetId!).Files.Select(f => f.Path));
    }

    [Fact]
    public void A_file_that_fails_keeps_its_previous_version_in_the_new_backup()
    {
        t.OneDrive.Add("doc.txt", "version 1");
        var full = t.Backup();
        t.OneDrive.Edit("doc.txt", "version 2");
        t.OneDrive.HydrateFailure = _ => new IOException("offline");

        var incremental = t.Backup();

        Assert.Single(incremental.Failed);
        var entry = Assert.Single(t.Manifest(incremental.SetId!).Files);
        Assert.Equal(full.SetId, entry.StoredIn);
        Assert.Equal("version 1", TestFolders.ReadTree(t.Restore())["doc.txt"]);
    }

    [Fact]
    public void An_edit_made_before_copying_starts_is_what_gets_backed_up()
    {
        t.OneDrive.Add("busy.txt", "before");
        t.OneDrive.AfterHydrate = path => t.OneDrive.Edit("busy.txt", "edited", leaveOnlineOnly: false);

        var report = t.Backup();

        Assert.Empty(report.Failed);
        var entry = Assert.Single(t.Manifest(report.SetId!).Files);
        Assert.Equal(File.GetLastWriteTimeUtc(t.OneDrive.FullPath("busy.txt")), entry.LastWriteUtc);
        Assert.Equal("edited", TestFolders.ReadTree(t.Restore())["busy.txt"]);
    }

    [Fact]
    public void A_file_that_changes_while_being_copied_is_rejected_and_leaves_nothing_behind()
    {
        var source = t.OneDrive.Add("busy.txt", "content", onlineOnly: false);
        var target = Path.Combine(t.Root, "copy", "busy.txt");
        var timeWhenCopyStarted = File.GetLastWriteTimeUtc(source).AddSeconds(-1);

        var ex = Assert.Throws<IOException>(() => BackupEngine.CopyAndCheck(source, target, "busy.txt", 7, timeWhenCopyStarted, "set"));

        Assert.Contains("changed while", ex.Message);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!));
    }

    [Fact]
    public void A_file_deleted_during_the_backup_is_simply_left_out()
    {
        t.OneDrive.Add("a.txt", "a");
        t.OneDrive.Add("b.txt", "b");
        t.OneDrive.AfterHydrate = path =>
        {
            if (path.EndsWith("a.txt", StringComparison.Ordinal))
            {
                t.OneDrive.Delete("b.txt");
            }
        };

        var report = t.Backup();

        Assert.Empty(report.Failed);
        Assert.Equal(["a.txt"], t.Manifest(report.SetId!).Files.Select(f => f.Path));
    }

    [Fact]
    public void Excluded_files_and_folders_are_skipped()
    {
        t.OneDrive.Add("keep.txt", "keep");
        t.OneDrive.Add("temp.tmp", "skip");
        t.OneDrive.Add("Videos/movie.mp4", "skip");
        t.OneDrive.Add("Pictures/Camera Roll/a.jpg", "skip");
        t.OneDrive.Add("Pictures/b.jpg", "keep");

        var report = t.Backup(exclude: ["*.tmp", "Videos", "Pictures/Camera Roll"]);

        Assert.Equal(["Pictures/b.jpg", "keep.txt"], t.Manifest(report.SetId!).Files.Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("Videos/movie.mp4", t.OneDrive.Hydrated);
    }

    [Fact]
    public void Dry_run_touches_nothing()
    {
        t.OneDrive.Add("a.txt", "a");
        t.OneDrive.Add("b.txt", "bb", onlineOnly: false);

        var report = t.Backup(dryRun: true);

        Assert.Equal(2, report.CopiedFiles);
        Assert.Equal(1, report.DownloadedFiles);
        Assert.Empty(t.OneDrive.Hydrated);
        Assert.False(Directory.Exists(t.BackupRoot));
    }

    [Fact]
    public void Backup_folder_inside_onedrive_is_refused()
    {
        var engine = new BackupEngine(t.OneDrive, t.PendingList(), t.Log);
        var ex = Assert.Throws<BackupException>(() => engine.Run(
            new BackupOptions { SourceRoot = t.OneDrive.Root, BackupRoot = Path.Combine(t.OneDrive.Root, "Backup") },
            CancellationToken.None));
        Assert.Contains("inside", ex.Message);
    }

    [Fact]
    public void Two_backups_into_the_same_folder_at_once_are_refused()
    {
        t.OneDrive.Add("a.txt", "a");
        using (new BackupRepository(t.BackupRoot).Lock())
        {
            var ex = Assert.Throws<BackupException>(() => t.Backup());
            Assert.Contains("already", ex.Message);
        }
        Assert.Empty(t.Backup().Failed);
    }

    [Fact]
    public void Symbolic_links_are_not_followed()
    {
        var outside = Path.Combine(t.Root, "Outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "not part of OneDrive");
        t.OneDrive.Add("a.txt", "a");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(t.OneDrive.Root, "link"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // Creating links needs extra rights on some Windows setups.
        }

        var report = t.Backup();

        Assert.Equal(1, report.SkippedLinkCount);
        Assert.Equal(["a.txt"], t.Manifest(report.SetId!).Files.Select(f => f.Path));
    }
}

using OnedriveBackuper.Core;

namespace OnedriveBackuper.Tests;

public sealed class SmallPartsTests
{
    [Theory]
    [InlineData(0x00000020, false, false, false)] // Archive: an ordinary file.
    [InlineData(0x00400020, true, false, false)]  // Online-only.
    [InlineData(0x00500020, true, false, true)]   // Online-only after "Free up space".
    [InlineData(0x00080020, false, true, false)]  // "Always keep on this device".
    [InlineData(0x00480020, true, true, false)]   // Pinned but not downloaded yet.
    public void Reads_onedrive_state_from_attributes(int attributes, bool onlineOnly, bool pinned, bool unpinned)
    {
        var state = CloudFileState.FromAttributes((FileAttributes)attributes);

        Assert.Equal(new CloudFileState(onlineOnly, pinned, unpinned), state);
        Assert.Equal(onlineOnly && !pinned, state.FreeUpAfterBackup);
    }

    [Theory]
    [InlineData("*.tmp", "a/b/file.tmp", true)]
    [InlineData("*.tmp", "a/b/file.txt", false)]
    [InlineData("Videos", "Videos", true)]
    [InlineData("Videos", "Stuff/Videos", true)]
    [InlineData("Videos", "VideosOld", false)]
    [InlineData("pictures/camera roll", "Pictures/Camera Roll", true)]
    [InlineData("Pictures/Camera Roll", "Camera Roll", false)]
    [InlineData(@"Pictures\Camera Roll", "Pictures/Camera Roll", true)]
    public void Path_filter_matches_paths_and_names(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, new PathFilter([pattern]).Matches(path));
    }

    [Fact]
    public void Path_filter_can_match_a_parent_folder()
    {
        var filter = new PathFilter(["Documents/Taxes"]);

        Assert.True(filter.MatchesSelfOrAncestor("Documents/Taxes/2025/return.pdf"));
        Assert.False(filter.MatchesSelfOrAncestor("Documents/letter.docx"));
    }

    [Theory]
    [InlineData("1024", 1024)]
    [InlineData("1KB", 1024)]
    [InlineData("500MB", 500L << 20)]
    [InlineData("2GB", 2L << 30)]
    [InlineData("1.5 gb", 3L << 29)]
    [InlineData("3g", 3L << 30)]
    public void Parses_sizes(string text, long bytes)
    {
        Assert.Equal(bytes, Format.ParseSize(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("lots")]
    [InlineData("-1GB")]
    public void Rejects_bad_sizes(string text)
    {
        Assert.Throws<FormatException>(() => Format.ParseSize(text));
    }

    [Fact]
    public void Journal_survives_a_torn_last_line()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            using (var journal = SetJournal.Open(path))
            {
                journal.Append(new FileEntry("a.txt", 1, DateTime.UnixEpoch, "00", "set"));
            }
            File.AppendAllText(path, "{\"path\":\"b.t"); // Power cut mid-write.

            using (var journal = SetJournal.Open(path))
            {
                Assert.Equal(["a.txt"], journal.Entries.Keys);
                journal.Append(new FileEntry("c.txt", 1, DateTime.UnixEpoch, "00", "set"));
            }
            using (var journal = SetJournal.Open(path))
            {
                Assert.Equal(["a.txt", "c.txt"], journal.Entries.Keys.Order());
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Pending_list_is_saved_and_emptied()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "pending.txt");
        var pending = new PendingFreeUps(path);
        pending.Add("/x/a");
        pending.Add("/x/a");
        pending.Add("/x/b");

        Assert.Equal(["/x/a", "/x/b"], new PendingFreeUps(path).Items);

        pending.Remove("/x/a");
        pending.Remove("/x/b");
        Assert.False(File.Exists(path));
        Directory.Delete(Path.GetDirectoryName(path)!);
    }
}

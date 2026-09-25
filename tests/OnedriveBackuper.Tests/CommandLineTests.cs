using OnedriveBackuper.Cli;

namespace OnedriveBackuper.Tests;

/// <summary>Runs the real command line on a plain folder (no OneDrive needed): backup, list, verify, restore.</summary>
public sealed class CommandLineTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "obk-cli-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private (int Code, string Output) Run(params string[] args)
    {
        var output = new StringWriter();
        var code = Commands.Run(args, output, output, CancellationToken.None, Path.Combine(root, "pending.txt"));
        return (code, output.ToString());
    }

    [Fact]
    public void Backup_list_verify_and_restore_work_end_to_end()
    {
        var source = Path.Combine(root, "Source");
        var backup = Path.Combine(root, "Backup");
        Directory.CreateDirectory(Path.Combine(source, "Sub"));
        File.WriteAllText(Path.Combine(source, "a.txt"), "a");
        File.WriteAllText(Path.Combine(source, "Sub", "b.txt"), "b");

        var (code, output) = Run("backup", backup, "--source", source);
        Assert.True(code == Commands.Ok, output);
        Assert.Contains("(full) finished", output);

        File.WriteAllText(Path.Combine(source, "c.txt"), "c");
        (code, output) = Run("backup", backup, "--source", source);
        Assert.True(code == Commands.Ok, output);
        Assert.Contains("(incremental) finished", output);

        (code, output) = Run("list", backup);
        Assert.Equal(Commands.Ok, code);
        Assert.Contains("full", output);
        Assert.Contains("incremental", output);

        (code, output) = Run("verify", backup);
        Assert.True(code == Commands.Ok, output);
        Assert.Contains("Everything matches", output);

        var restored = Path.Combine(root, "Restored");
        (code, output) = Run("restore", backup, restored);
        Assert.True(code == Commands.Ok, output);
        Assert.Equal("b", File.ReadAllText(Path.Combine(restored, "Sub", "b.txt")));
        Assert.Equal("c", File.ReadAllText(Path.Combine(restored, "c.txt")));
    }

    [Fact]
    public void Scan_reports_what_is_in_the_folder()
    {
        var source = Path.Combine(root, "Source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), "hello");

        var (code, output) = Run("scan", source);

        Assert.Equal(Commands.Ok, code);
        Assert.Contains("locally available", output);
        Assert.Contains("Nothing is online-only", output);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("backup")]
    [InlineData("backup", "x", "--nope")]
    [InlineData("backup", "x", "--free-method", "sideways")]
    [InlineData("restore", "only-one-folder")]
    public void Bad_usage_explains_itself(params string[] args)
    {
        var (code, output) = Run(args);

        Assert.Equal(Commands.Error, code);
        Assert.False(string.IsNullOrWhiteSpace(output));
    }
}

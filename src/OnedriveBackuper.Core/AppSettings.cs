using System.Text.Json;
using OnedriveBackuper.Windows;

namespace OnedriveBackuper.Core;

/// <summary>What the app remembers between runs, in %LOCALAPPDATA%\OnedriveBackuper\settings.json.</summary>
public sealed class AppSettings
{
    public string? SourceFolder { get; set; }
    public string? BackupFolder { get; set; }
    public List<string> Exclude { get; set; } = [];
    public long ReserveBytes { get; set; } = 1L << 30;
    public FreeUpMethod FreeUpMethod { get; set; } = FreeUpMethod.Auto;
    public ScheduleSettings Schedule { get; set; } = new();
    public RetentionSettings Retention { get; set; } = new();

    /// <summary>The program the scheduled task starts, so a moved or updated copy can fix the task.</summary>
    public string? ScheduledExePath { get; set; }
    public LastRunInfo? LastRun { get; set; }

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OnedriveBackuper");

    public static string DefaultPath => Path.Combine(DefaultFolder, "settings.json");

    /// <summary>Loads settings, or returns defaults if there are none yet (or the file is damaged).</summary>
    public static AppSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? Json.Read<AppSettings>(path) : new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            return new AppSettings();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Json.WriteAtomically(path, this);
    }

    /// <summary>OneDrive sets these for the signed-in accounts (personal and work/school).</summary>
    public static string? DetectOneDriveFolder() =>
        new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
            .Select(Environment.GetEnvironmentVariable)
            .FirstOrDefault(path => !string.IsNullOrEmpty(path) && Directory.Exists(path));
}

public enum RunResult
{
    Succeeded,
    SucceededWithProblems,
    Stopped,
    Failed,
}

/// <summary>How the most recent backup went, for the home screen.</summary>
public sealed class LastRunInfo
{
    public DateTimeOffset Started { get; set; }
    public DateTimeOffset Finished { get; set; }
    public RunResult Result { get; set; }
    public string Summary { get; set; } = "";
    public string? SetId { get; set; }
    public bool Scheduled { get; set; }
}

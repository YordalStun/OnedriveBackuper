using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OnedriveBackuper.App.Services;
using OnedriveBackuper.App.ViewModels;
using OnedriveBackuper.App.Views;
using OnedriveBackuper.Core;

namespace OnedriveBackuper.App;

/// <summary>
/// "OnedriveBackuper.exe --screenshots folder" draws every screen, filled with example data, in light and
/// dark mode, and saves them as PNG files. Used by the automated build so the UI can be reviewed without
/// running a real backup. Nothing is read from or written to the real settings or OneDrive.
/// </summary>
internal static class Screenshots
{
    private const int Width = 1120;
    private const int Height = 780;

    public static int Render(string folder)
    {
        Directory.CreateDirectory(folder);
        try
        {
            var settingsPath = Path.Combine(Path.GetTempPath(), $"obk-screens-{Guid.NewGuid():N}", "settings.json");
            DemoSettings().Save(settingsPath);
            var model = new MainViewModel(settingsPath);
            (string Name, Action<MainViewModel> Show)[] screens =
            [
                ("1-home", ShowHome),
                ("2-backing-up", ShowRunning),
                ("3-finished", ShowFinished),
                ("4-backups", ShowBackups),
                ("5-schedule", m => m.Navigate(Page.Schedule)),
                ("6-settings", ShowSettings),
                ("7-first-run", ShowFirstRun),
            ];
            foreach (var (theme, suffix) in new[] { (ThemeMode.Light, "light"), (ThemeMode.Dark, "dark") })
            {
                Application.Current.ThemeMode = theme;
                foreach (var (name, show) in screens)
                {
                    show(model);
                    Save(new ShellView { DataContext = model }, Path.Combine(folder, $"{name}-{suffix}.png"));
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(folder, "error.txt"), ex.ToString());
            return 1;
        }
    }

    private static void Save(FrameworkElement view, string path)
    {
        var size = new Size(Width, Height);
        view.Measure(size);
        view.Arrange(new Rect(size));
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static AppSettings DemoSettings() => new()
    {
        SourceFolder = @"C:\Users\Alex\OneDrive",
        BackupFolder = @"E:\OneDrive Backup",
        Exclude = ["*.tmp", "Videos/Raw footage"],
        Schedule = new ScheduleSettings { Enabled = true, Frequency = ScheduleFrequency.Daily, Time = new TimeOnly(2, 0) },
        LastRun = new LastRunInfo
        {
            Started = DateTimeOffset.Now.Date.AddHours(2),
            Finished = DateTimeOffset.Now.Date.AddHours(2).AddMinutes(14),
            Result = RunResult.Succeeded,
            Summary = "Incremental backup: 23 files copied (412 MB)",
            SetId = "demo",
            Scheduled = true,
        },
    };

    private static void Reset(MainViewModel m)
    {
        m.Run.ShowResult(new BackupJobResult(RunResult.Succeeded, "", "", null, [], null));
        m.Run.HasResult = false;
    }

    private static void ShowHome(MainViewModel m)
    {
        Reset(m);
        m.Navigate(Page.Home);
        m.RefreshHome();
        m.ShowDemoDrives("41.8 GB free on C:\\", "1.62 TB free on E:\\");
    }

    private static BackupProgressSnapshot DemoProgress() => new()
    {
        Phase = BackupPhase.BackingUp,
        SetId = "20260926T020000Z-incr",
        Kind = BackupKind.Incremental,
        TotalFiles = 48_210,
        CheckedFiles = 31_442,
        FilesToCopy = 1_337,
        BytesToCopy = 5_476_083_302,
        FilesToDownload = 1_105,
        BytesToDownload = 5_100_000_000,
        CopiedFiles = 842,
        CopiedBytes = 3_435_973_836,
        UnchangedFiles = 30_599,
        DownloadedFiles = 610,
        DownloadedBytes = 3_100_000_000,
        FreedUpFiles = 610,
        FailedFiles = 1,
        CurrentFile = "Pictures/2025/Summer holiday/IMG_4821.MOV",
        CurrentFullPath = @"C:\Users\Alex\OneDrive\Pictures\2025\Summer holiday\IMG_4821.MOV",
        CurrentFileSize = 1_288_490_188,
        CurrentFileNeedsDownload = true,
        CurrentStage = FileStage.Downloading,
    };

    private static IEnumerable<LogLine> DemoLog()
    {
        var t = DateTime.Today.AddHours(2).AddMinutes(9);
        yield return new(t, "INFO", "[31420/48210] download Documents/Budget 2026.xlsx (84 KB)");
        yield return new(t.AddSeconds(1), "DETAIL", "downloaded in 0.6s");
        yield return new(t.AddSeconds(1), "DETAIL", "freed up again");
        yield return new(t.AddSeconds(3), "ERROR", "[31428/48210] FAILED Documents/Taxes/2025.xlsx: The process cannot access the file because it is being used by another process.");
        yield return new(t.AddSeconds(4), "INFO", "[31436/48210] copy     Desktop/notes.txt (2 KB)");
        yield return new(t.AddSeconds(6), "INFO", "[31442/48210] download Pictures/2025/Summer holiday/IMG_4821.MOV (1.2 GB)");
    }

    private static void ShowRunning(MainViewModel m)
    {
        m.Navigate(Page.Home);
        m.Run.ShowDemo(DemoProgress(), downloaded: 708_669_603, runningFor: TimeSpan.FromMinutes(9).Add(TimeSpan.FromSeconds(12)), DemoLog(), @"C:\ has 40.6 GB free");
    }

    private static void ShowFinished(MainViewModel m)
    {
        m.Navigate(Page.Home);
        m.Run.ShowDemo(DemoProgress() with { CopiedFiles = 1_336, CheckedFiles = 48_210, UnchangedFiles = 46_873, DownloadedFiles = 1_105, FreedUpFiles = 1_105, CurrentFile = null },
            0, TimeSpan.FromMinutes(21), [], "");
        m.Run.ShowResult(new BackupJobResult(RunResult.SucceededWithProblems, "Backup finished with 1 problem",
            "Incremental backup: 1,336 files copied (5.1 GB), 46,873 unchanged. Files that failed keep their previous backup and are tried again next time. Took 21 min.",
            null,
            [new FailedFile("Documents/Taxes/2025.xlsx", "The process cannot access the file because it is being used by another process.")],
            null));
    }

    private static void ShowBackups(MainViewModel m)
    {
        Reset(m);
        m.Navigate(Page.Backups);
        var start = DateTimeOffset.Now.Date.AddHours(2);
        SetInfo Info(int daysAgo, BackupKind kind, int copied, long bytes, int failed) => new()
        {
            SetId = $"demo-{daysAgo}",
            Kind = kind,
            StartedUtc = start.AddDays(-daysAgo),
            Stats = new SetStats { TotalFiles = 48_210 - daysAgo * 12, CopiedFiles = copied, CopiedBytes = bytes, FailedFiles = failed },
        };
        m.Backups.ShowDemo(
        [
            new BackupSetRow(Info(0, BackupKind.Incremental, 23, 432_013_312, 0), true),
            new BackupSetRow(Info(1, BackupKind.Incremental, 1_336, 5_476_083_302, 1), true),
            new BackupSetRow(Info(2, BackupKind.Incremental, 57, 91_226_112, 0), true),
            new BackupSetRow(Info(3, BackupKind.Incremental, 4, 1_048_576, 0), true),
            new BackupSetRow(Info(4, BackupKind.Full, 48_160, 872_415_232_000, 0), true),
            new BackupSetRow(Info(34, BackupKind.Full, 47_805, 861_000_000_000, 0), true),
        ], @"E:\OneDrive Backup");
    }

    private static void ShowSettings(MainViewModel m)
    {
        Reset(m);
        m.Navigate(Page.Settings);
        m.SettingsPage.ShowToolOutput(SettingsViewModel.Describe(new OneDriveOverview
        {
            Root = @"C:\Users\Alex\OneDrive",
            Groups =
            [
                new StateGroup("always keep on this device", 312, 1_932_735_283),
                new StateGroup("locally available", 7_798, 26_843_545_600),
                new StateGroup("online-only", 40_100, 845_000_000_000),
            ],
            Problems = [],
            TotalFiles = 48_210,
            TotalBytes = 873_776_280_883,
            OnlineOnlyFiles = 40_100,
            OnlineOnlyBytes = 845_000_000_000,
            LargestOnlineOnly = new SourceFile("Videos/Wedding/full ceremony.mp4", "", 15_032_385_536, DateTime.UtcNow, FileAttributes.Normal),
            FreeBytes = 44_895_637_504,
            ReserveBytes = 1L << 30,
        }));
    }

    private static void ShowFirstRun(MainViewModel m)
    {
        Reset(m);
        m.Settings.BackupFolder = null;
        m.Settings.LastRun = null;
        m.Settings.Schedule.Enabled = false;
        m.Navigate(Page.Home);
        m.RefreshHome();
        m.ShowDemoDrives("41.8 GB free on C:\\", "");
        // Put things back for the next theme.
        var fresh = DemoSettings();
        m.Settings.BackupFolder = fresh.BackupFolder;
        m.Settings.LastRun = fresh.LastRun;
        m.Settings.Schedule.Enabled = true;
    }
}

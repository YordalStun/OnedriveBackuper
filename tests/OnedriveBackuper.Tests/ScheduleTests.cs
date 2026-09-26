using System.Xml.Linq;
using OnedriveBackuper.Core;

namespace OnedriveBackuper.Tests;

public sealed class ScheduleTests
{
    // A Wednesday.
    private static readonly DateTime Now = new(2026, 9, 23, 14, 30, 0);

    [Fact]
    public void Daily_runs_today_if_the_time_is_still_to_come_otherwise_tomorrow()
    {
        var schedule = new ScheduleSettings { Frequency = ScheduleFrequency.Daily, Time = new TimeOnly(22, 0) };
        Assert.Equal(new DateTime(2026, 9, 23, 22, 0, 0), schedule.NextRun(Now));

        schedule.Time = new TimeOnly(2, 0);
        Assert.Equal(new DateTime(2026, 9, 24, 2, 0, 0), schedule.NextRun(Now));
        Assert.Equal("Every day at 02:00", schedule.Describe());
    }

    [Fact]
    public void Weekly_runs_on_the_next_chosen_day()
    {
        var schedule = new ScheduleSettings
        {
            Frequency = ScheduleFrequency.Weekly,
            Days = [DayOfWeek.Monday, DayOfWeek.Friday],
            Time = new TimeOnly(9, 15),
        };

        Assert.Equal(new DateTime(2026, 9, 25, 9, 15, 0), schedule.NextRun(Now));
        Assert.Equal("Every Monday and Friday at 09:15", schedule.Describe());

        schedule.Days = [DayOfWeek.Wednesday];
        Assert.Equal(new DateTime(2026, 9, 30, 9, 15, 0), schedule.NextRun(Now)); // Today's 09:15 has passed.

        schedule.Days = [];
        Assert.Null(schedule.NextRun(Now));
    }

    [Fact]
    public void Every_few_hours_runs_at_the_next_slot()
    {
        var schedule = new ScheduleSettings { Frequency = ScheduleFrequency.EveryFewHours, EveryHours = 6, Time = new TimeOnly(2, 0) };

        // Slots are 02:00, 08:00, 14:00, 20:00.
        Assert.Equal(new DateTime(2026, 9, 23, 20, 0, 0), schedule.NextRun(Now));
        Assert.Equal("Every 6 hours", schedule.Describe());
    }

    [Fact]
    public void Task_definition_runs_only_while_signed_in_catches_up_and_never_times_out()
    {
        var schedule = new ScheduleSettings { Enabled = true, Frequency = ScheduleFrequency.Daily, Time = new TimeOnly(2, 0) };

        var xml = ScheduledTaskXml.Build(schedule, @"C:\Tools\One & Only\OnedriveBackuper.exe", "--scheduled", @"PC\john", Now);

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", xml);
        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        string Value(string name) => doc.Descendants(ns + name).Single().Value;
        Assert.Equal("2026-09-24T02:00:00", Value("StartBoundary"));
        Assert.Equal("1", Value("DaysInterval"));
        Assert.Equal("InteractiveToken", Value("LogonType"));
        Assert.Equal(@"PC\john", Value("UserId"));
        Assert.Equal("true", Value("StartWhenAvailable"));
        Assert.Equal("PT0S", Value("ExecutionTimeLimit"));
        Assert.Equal("IgnoreNew", Value("MultipleInstancesPolicy"));
        Assert.Equal("false", Value("DisallowStartIfOnBatteries"));
        Assert.Equal(@"C:\Tools\One & Only\OnedriveBackuper.exe", Value("Command"));
        Assert.Equal("--scheduled", Value("Arguments"));
    }

    [Fact]
    public void Task_definition_for_weekly_and_hourly_schedules()
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var weekly = XDocument.Parse(ScheduledTaskXml.Build(
            new ScheduleSettings { Frequency = ScheduleFrequency.Weekly, Days = [DayOfWeek.Friday, DayOfWeek.Monday] },
            "x.exe", "", "u", Now));
        Assert.Equal(["Monday", "Friday"], weekly.Descendants(ns + "DaysOfWeek").Single().Elements().Select(e => e.Name.LocalName));

        var hourly = XDocument.Parse(ScheduledTaskXml.Build(
            new ScheduleSettings { Frequency = ScheduleFrequency.EveryFewHours, EveryHours = 4 },
            "x.exe", "", "u", Now));
        var trigger = hourly.Descendants(ns + "TimeTrigger").Single();
        Assert.Equal("PT4H", trigger.Descendants(ns + "Interval").Single().Value);
        // Task Scheduler's schema wants Repetition before StartBoundary.
        Assert.Equal(["Repetition", "StartBoundary", "Enabled"], trigger.Elements().Select(e => e.Name.LocalName));
    }

    [Fact]
    public void Settings_survive_a_save_and_load()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var settings = new AppSettings
            {
                SourceFolder = @"C:\Users\x\OneDrive",
                BackupFolder = @"E:\Backup",
                Exclude = ["*.tmp", "Videos"],
                FreeUpMethod = OnedriveBackuper.Windows.FreeUpMethod.Unpin,
                Schedule = new ScheduleSettings { Enabled = true, Frequency = ScheduleFrequency.Weekly, Days = [DayOfWeek.Tuesday], Time = new TimeOnly(23, 45) },
                Retention = new RetentionSettings { FullEveryDays = 14, KeepFullBackups = 5 },
                LastRun = new LastRunInfo { Result = RunResult.SucceededWithProblems, Summary = "2 problems", SetId = "x" },
            };
            settings.Save(path);

            var loaded = AppSettings.Load(path);

            Assert.Equal(@"E:\Backup", loaded.BackupFolder);
            Assert.Equal(["*.tmp", "Videos"], loaded.Exclude);
            Assert.Equal(OnedriveBackuper.Windows.FreeUpMethod.Unpin, loaded.FreeUpMethod);
            Assert.Equal([DayOfWeek.Tuesday], loaded.Schedule.Days);
            Assert.Equal(new TimeOnly(23, 45), loaded.Schedule.Time);
            Assert.Equal(14, loaded.Retention.FullEveryDays);
            Assert.Equal(RunResult.SucceededWithProblems, loaded.LastRun!.Result);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Missing_or_damaged_settings_give_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        Assert.Equal(30, AppSettings.Load(path).Retention.FullEveryDays);

        File.WriteAllText(path, "{ not json");
        try
        {
            Assert.Equal(3, AppSettings.Load(path).Retention.KeepFullBackups);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

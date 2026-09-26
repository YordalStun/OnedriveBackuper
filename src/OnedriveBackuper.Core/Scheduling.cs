using System.Globalization;
using System.Xml.Linq;

namespace OnedriveBackuper.Core;

public enum ScheduleFrequency
{
    Daily,
    Weekly,
    EveryFewHours,
}

/// <summary>When automatic backups run. Times are local time.</summary>
public sealed class ScheduleSettings
{
    public bool Enabled { get; set; }
    public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.Daily;

    /// <summary>Time of day for daily and weekly backups; the first run of the day for "every few hours".</summary>
    public TimeOnly Time { get; set; } = new(2, 0);

    public List<DayOfWeek> Days { get; set; } = [DayOfWeek.Sunday];
    public int EveryHours { get; set; } = 6;

    /// <summary>The next time a backup is due after <paramref name="now"/>, or null when it never is.</summary>
    public DateTime? NextRun(DateTime now)
    {
        var todayAtTime = now.Date + Time.ToTimeSpan();
        switch (Frequency)
        {
            case ScheduleFrequency.Daily:
                return todayAtTime > now ? todayAtTime : todayAtTime.AddDays(1);

            case ScheduleFrequency.Weekly:
                if (Days.Count == 0)
                {
                    return null;
                }
                for (var day = 0; day <= 7; day++)
                {
                    var candidate = todayAtTime.AddDays(day);
                    if (candidate > now && Days.Contains(candidate.DayOfWeek))
                    {
                        return candidate;
                    }
                }
                return null;

            case ScheduleFrequency.EveryFewHours:
                var hours = Math.Clamp(EveryHours, 1, 24);
                var first = now.Date + TimeSpan.FromHours(Time.Hour % hours) + TimeSpan.FromMinutes(Time.Minute);
                var next = first;
                while (next <= now)
                {
                    next = next.AddHours(hours);
                }
                return next;

            default:
                return null;
        }
    }

    public string Describe()
    {
        var time = Time.ToString("HH:mm", CultureInfo.InvariantCulture);
        return Frequency switch
        {
            ScheduleFrequency.Daily => $"Every day at {time}",
            ScheduleFrequency.Weekly when Days.Count == 7 => $"Every day at {time}",
            ScheduleFrequency.Weekly when Days.Count > 0 => $"Every {JoinDays(Days)} at {time}",
            ScheduleFrequency.Weekly => "No days chosen",
            ScheduleFrequency.EveryFewHours => Math.Clamp(EveryHours, 1, 24) == 1 ? "Every hour" : $"Every {Math.Clamp(EveryHours, 1, 24)} hours",
            _ => "",
        };
    }

    private static string JoinDays(IEnumerable<DayOfWeek> days)
    {
        // Monday first, as most people read a week.
        var names = days.Distinct()
            .OrderBy(d => ((int)d + 6) % 7)
            .Select(d => CultureInfo.InvariantCulture.DateTimeFormat.GetDayName(d))
            .ToList();
        return names.Count == 1 ? names[0] : string.Join(", ", names[..^1]) + " and " + names[^1];
    }
}

/// <summary>
/// Builds the Windows Task Scheduler definition for automatic backups. The task runs only while the user is
/// signed in (OneDrive runs inside their session and does the downloading), catches up on runs missed while
/// the PC was off or asleep, never stops a long backup, and never starts a second copy.
/// </summary>
public static class ScheduledTaskXml
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static string Build(ScheduleSettings schedule, string exePath, string arguments, string userId, DateTime now)
    {
        var start = schedule.NextRun(now) ?? now.Date.AddDays(1) + schedule.Time.ToTimeSpan();
        var boundary = new XElement(Ns + "StartBoundary", start.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));

        XElement trigger = schedule.Frequency switch
        {
            ScheduleFrequency.Weekly => new XElement(Ns + "CalendarTrigger",
                boundary,
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "ScheduleByWeek",
                    new XElement(Ns + "DaysOfWeek", schedule.Days.Distinct().OrderBy(d => (int)d).Select(d => new XElement(Ns + d.ToString()))),
                    new XElement(Ns + "WeeksInterval", "1"))),
            ScheduleFrequency.EveryFewHours => new XElement(Ns + "TimeTrigger",
                new XElement(Ns + "Repetition",
                    new XElement(Ns + "Interval", $"PT{Math.Clamp(schedule.EveryHours, 1, 24)}H"),
                    new XElement(Ns + "StopAtDurationEnd", "false")),
                boundary,
                new XElement(Ns + "Enabled", "true")),
            _ => new XElement(Ns + "CalendarTrigger",
                boundary,
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "ScheduleByDay", new XElement(Ns + "DaysInterval", "1"))),
        };

        var task = new XElement(Ns + "Task",
            new XAttribute("version", "1.2"),
            new XElement(Ns + "RegistrationInfo",
                new XElement(Ns + "Description", "Automatic OneDrive backup by OnedriveBackuper.")),
            new XElement(Ns + "Triggers", trigger),
            new XElement(Ns + "Principals",
                new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                    new XElement(Ns + "UserId", userId),
                    new XElement(Ns + "LogonType", "InteractiveToken"),
                    new XElement(Ns + "RunLevel", "LeastPrivilege"))),
            new XElement(Ns + "Settings",
                new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                new XElement(Ns + "AllowHardTerminate", "true"),
                new XElement(Ns + "StartWhenAvailable", "true"),
                new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
                new XElement(Ns + "IdleSettings",
                    new XElement(Ns + "StopOnIdleEnd", "false"),
                    new XElement(Ns + "RestartOnIdle", "false")),
                new XElement(Ns + "AllowStartOnDemand", "true"),
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "Hidden", "false"),
                new XElement(Ns + "RunOnlyIfIdle", "false"),
                new XElement(Ns + "WakeToRun", "false"),
                new XElement(Ns + "ExecutionTimeLimit", "PT0S"),
                new XElement(Ns + "Priority", "7")),
            new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                new XElement(Ns + "Exec",
                    new XElement(Ns + "Command", exePath),
                    new XElement(Ns + "Arguments", arguments))));

        return new XDocument(new XDeclaration("1.0", "UTF-16", null), task).Declaration + Environment.NewLine + task;
    }
}

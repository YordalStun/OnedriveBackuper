using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using OnedriveBackuper.Core;

namespace OnedriveBackuper.Windows;

/// <summary>Registers the automatic backup with Windows Task Scheduler (through schtasks.exe, which every Windows has).</summary>
[SupportedOSPlatform("windows")]
public static class WindowsTaskScheduler
{
    public const string TaskName = "OnedriveBackuper";

    /// <summary>Creates or replaces the task. No administrator rights needed: it runs as the current user.</summary>
    public static void Register(ScheduleSettings schedule, string exePath, string arguments)
    {
        var userId = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var xml = ScheduledTaskXml.Build(schedule, exePath, arguments, userId, DateTime.Now);
        var file = Path.Combine(Path.GetTempPath(), $"OnedriveBackuper-task-{Guid.NewGuid():N}.xml");
        try
        {
            // schtasks insists on UTF-16 for task XML.
            File.WriteAllText(file, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
            var (code, output) = Run("/Create", "/TN", TaskName, "/XML", file, "/F");
            if (code != 0)
            {
                throw new BackupException($"Windows Task Scheduler did not accept the schedule: {output.Trim()}");
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static void Unregister()
    {
        if (IsRegistered())
        {
            var (code, output) = Run("/Delete", "/TN", TaskName, "/F");
            if (code != 0)
            {
                throw new BackupException($"Could not remove the automatic backup from Windows Task Scheduler: {output.Trim()}");
            }
        }
    }

    public static bool IsRegistered() => Run("/Query", "/TN", TaskName).Code == 0;

    private static (int Code, string Output) Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new BackupException("Could not start schtasks.exe.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, output.Result + errors.Result);
    }
}

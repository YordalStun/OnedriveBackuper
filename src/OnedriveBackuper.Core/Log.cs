using System.Globalization;

namespace OnedriveBackuper.Core;

public interface ILog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    void Detail(string message);

    /// <summary>Also write everything (including details) to this file from now on.</summary>
    void AttachFile(string path);
}

/// <summary>A timestamped log file. Nothing is written until a file is attached.</summary>
public sealed class LogFile : IDisposable
{
    private readonly object gate = new();
    private StreamWriter? writer;

    public void Attach(string path)
    {
        lock (gate)
        {
            writer?.Dispose();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        }
    }

    public void Write(string level, string message)
    {
        lock (gate)
        {
            writer?.WriteLine($"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {level} {message}");
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            writer?.Dispose();
            writer = null;
        }
    }
}

/// <summary>Writes to the console and, once attached, to a log file with timestamps.</summary>
public sealed class ConsoleLog(TextWriter output, TextWriter errors, bool verbose) : ILog, IDisposable
{
    private readonly LogFile file = new();

    public void Info(string message) => Write("INFO ", message, output, show: true);

    public void Warn(string message) => Write("WARN ", message, errors, show: true, ConsoleColor.Yellow);

    public void Error(string message) => Write("ERROR", message, errors, show: true, ConsoleColor.Red);

    public void Detail(string message) => Write("     ", message, output, show: verbose);

    public void AttachFile(string path) => file.Attach(path);

    private void Write(string level, string message, TextWriter writer, bool show, ConsoleColor? color = null)
    {
        if (show)
        {
            var colored = color.HasValue && ReferenceEquals(writer, Console.Error) && !Console.IsErrorRedirected;
            if (colored)
            {
                Console.ForegroundColor = color!.Value;
            }
            writer.WriteLine(message);
            if (colored)
            {
                Console.ResetColor();
            }
        }
        file.Write(level, message);
    }

    public void Dispose() => file.Dispose();
}

public static class Format
{
    public static string Size(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes"
            : $"{value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    public static string Duration(TimeSpan span) => span.TotalSeconds < 60
        ? $"{span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s"
        : span.TotalHours < 1
            ? $"{(int)span.TotalMinutes}m{span.Seconds:00}s"
            : $"{(int)span.TotalHours}h{span.Minutes:00}m";

    /// <summary>Parses sizes like "500MB", "2GB", "1.5 GB" or a plain number of bytes.</summary>
    public static long ParseSize(string text)
    {
        var trimmed = text.Trim().ToUpperInvariant().Replace(" ", "");
        (string Suffix, long Factor)[] units = [("TB", 1L << 40), ("GB", 1L << 30), ("MB", 1L << 20), ("KB", 1L << 10), ("T", 1L << 40), ("G", 1L << 30), ("M", 1L << 20), ("K", 1L << 10), ("B", 1)];
        foreach (var (suffix, factor) in units)
        {
            if (trimmed.EndsWith(suffix, StringComparison.Ordinal) &&
                double.TryParse(trimmed[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number >= 0)
            {
                return (long)(number * factor);
            }
        }
        if (long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes))
        {
            return bytes;
        }
        throw new FormatException($"'{text}' is not a size. Use something like 500MB or 2GB.");
    }
}

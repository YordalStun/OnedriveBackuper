using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace OnedriveBackuper.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);
}

public enum StepState
{
    Waiting,
    Active,
    Done,
    NotNeeded,
}

public enum Tone
{
    Neutral,
    Good,
    Warning,
    Bad,
}

public sealed class VisibleWhenFalseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class VisibleWhenTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string text && text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Short, friendly dates: "Today 02:00", "Yesterday 14:30", "Mon 22 Sep 09:15".</summary>
public static class Friendly
{
    public static string When(DateTimeOffset time) => When(time.ToLocalTime().DateTime);

    public static string When(DateTime local)
    {
        var today = DateTime.Today;
        var clock = local.ToString("HH:mm", CultureInfo.CurrentCulture);
        if (local.Date == today)
        {
            return $"Today {clock}";
        }
        if (local.Date == today.AddDays(-1))
        {
            return $"Yesterday {clock}";
        }
        if (local.Date == today.AddDays(1))
        {
            return $"Tomorrow {clock}";
        }
        return local.ToString(local.Year == today.Year ? "ddd d MMM HH:mm" : "d MMM yyyy HH:mm", CultureInfo.CurrentCulture);
    }

    public static string Count(int n, string one, string many) =>
        $"{n.ToString("N0", CultureInfo.CurrentCulture)} {(n == 1 ? one : many)}";

    public static string Clock(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
        : $"{span.Minutes}:{span.Seconds:00}";

    public static string Roughly(TimeSpan span) => span.TotalMinutes < 1
        ? "less than a minute"
        : span.TotalHours < 1
            ? $"{Math.Ceiling(span.TotalMinutes):0} min"
            : $"{(int)span.TotalHours} h {span.Minutes:00} min";
}

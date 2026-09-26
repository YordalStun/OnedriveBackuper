using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using OnedriveBackuper.App.Services;
using OnedriveBackuper.App.ViewModels;
using OnedriveBackuper.Core;

namespace OnedriveBackuper.App;

/// <summary>
/// Start-up and lifetime. Opened normally, the window shows. Started by Windows Task Scheduler
/// (--scheduled), the backup runs quietly with a tray icon, pops up a notification when done, and exits.
/// Closing the window during a backup keeps the backup going in the tray.
/// </summary>
public partial class App : Application
{
    private SingleInstance? instance;
    private MainViewModel? model;
    private MainWindow? window;
    private TrayIcon? tray;
    private DispatcherTimer? trayTimer;
    private bool exitWhenIdle;
    private bool toldAboutBackground;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        if (args.Length == 2 && args[0] == "--screenshots")
        {
            Shutdown(Screenshots.Render(args[1]));
            return;
        }

        var scheduled = args.Contains(ScheduleViewModel.ScheduledArgument, StringComparer.OrdinalIgnoreCase);
        instance = SingleInstance.TryStart();
        if (instance == null)
        {
            SingleInstance.Signal(scheduled);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        model = new MainViewModel(AppSettings.DefaultPath);
        model.Run.Finished += OnBackupFinished;

        tray = new TrayIcon();
        tray.OpenRequested += ShowWindow;
        tray.BackUpRequested += () => model.StartBackup(forceFull: false);
        tray.StopRequested += () => model.Run.Stop();
        tray.ExitRequested += ExitFromTray;
        trayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        trayTimer.Tick += (_, _) => tray.SetStatus(model.Run.TrayText, model.Run.IsRunning);
        trayTimer.Start();

        instance.ShowRequested += () => Dispatcher.InvokeAsync(ShowWindow);
        instance.RunScheduledRequested += () => Dispatcher.InvokeAsync(() => StartScheduled(showTray: window?.IsVisible != true));

        if (scheduled)
        {
            // Be a quiet guest: the backup should not slow down whatever the user is doing.
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
            StartScheduled(showTray: true);
        }
        else
        {
            ShowWindow();
            var settings = model.Settings;
            Task.Run(() => ScheduleViewModel.RepairTaskIfMoved(settings, () => Dispatcher.InvokeAsync(model.SaveSettings)));
        }
    }

    private void StartScheduled(bool showTray)
    {
        if (model!.IsBusy)
        {
            return;
        }
        if (showTray)
        {
            tray!.Visible = true;
            exitWhenIdle = true;
        }
        model.StartBackup(forceFull: false, scheduled: true);
    }

    private void ShowWindow()
    {
        if (window == null)
        {
            window = new MainWindow { DataContext = model };
            window.Closing += OnWindowClosing;
        }
        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }
        window.Activate();
        exitWhenIdle = false;
        tray!.Visible = false;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!model!.IsBusy)
        {
            ExitApp();
            return;
        }
        // Keep working in the background; the tray icon brings the window back.
        e.Cancel = true;
        window!.Hide();
        tray!.Visible = true;
        exitWhenIdle = true;
        if (!toldAboutBackground)
        {
            toldAboutBackground = true;
            tray.Notify("Still backing up", "OnedriveBackuper carries on in the background. Click the icon next to the clock to see how it is going.", Tone.Neutral);
        }
    }

    private void OnBackupFinished(BackupJobResult result, bool scheduled)
    {
        if (window?.IsVisible == true)
        {
            return;
        }
        tray!.Visible = true;
        tray.Notify(result.Headline, result.Summary, result.Tone);
        if (exitWhenIdle)
        {
            // Leave the icon up for a minute so the notification can be clicked, then get out of the way.
            var linger = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            linger.Tick += (_, _) =>
            {
                linger.Stop();
                if (window?.IsVisible != true && !model!.IsBusy)
                {
                    ExitApp();
                }
            };
            linger.Start();
        }
    }

    private void ExitFromTray()
    {
        if (model!.Run.IsRunning)
        {
            var answer = MessageBox.Show("A backup is running. Stop it after the current file and exit?", "OnedriveBackuper",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
            model.Run.Finished += (_, _) => ExitApp();
            model.Run.Stop();
            return;
        }
        ExitApp();
    }

    private void ExitApp()
    {
        trayTimer?.Stop();
        tray?.Dispose();
        tray = null;
        instance?.Dispose();
        instance = null;
        Shutdown();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DefaultFolder);
            File.AppendAllText(Path.Combine(AppSettings.DefaultFolder, "errors.log"), $"{DateTime.Now:u} {e.Exception}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        MessageBox.Show($"Something went wrong: {e.Exception.Message}\n\nDetails were written to {AppSettings.DefaultFolder}\\errors.log",
            "OnedriveBackuper", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

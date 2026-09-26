namespace OnedriveBackuper.App.Services;

/// <summary>
/// Keeps one copy of the app per signed-in user. A second launch (from the Start menu, or a scheduled
/// backup while the window is already open) just tells the running copy what to do, then exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string Prefix = @"Local\OnedriveBackuper.";
    private readonly Mutex mutex;
    private readonly EventWaitHandle show = new(false, EventResetMode.AutoReset, Prefix + "Show");
    private readonly EventWaitHandle runScheduled = new(false, EventResetMode.AutoReset, Prefix + "RunScheduled");
    private readonly EventWaitHandle stop = new(false, EventResetMode.ManualReset);
    private readonly Thread listener;

    private SingleInstance(Mutex mutex)
    {
        this.mutex = mutex;
        listener = new Thread(Listen) { IsBackground = true, Name = "SingleInstance" };
        listener.Start();
    }

    public event Action? ShowRequested;

    public event Action? RunScheduledRequested;

    /// <summary>Returns null if another copy is already running.</summary>
    public static SingleInstance? TryStart()
    {
        var mutex = new Mutex(initiallyOwned: true, Prefix + "Instance", out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(mutex);
    }

    /// <summary>Asks the running copy to show its window, or to start the scheduled backup.</summary>
    public static void Signal(bool scheduled)
    {
        if (EventWaitHandle.TryOpenExisting(Prefix + (scheduled ? "RunScheduled" : "Show"), out var handle))
        {
            handle.Set();
            handle.Dispose();
        }
    }

    private void Listen()
    {
        WaitHandle[] handles = [show, runScheduled, stop];
        while (true)
        {
            switch (WaitHandle.WaitAny(handles))
            {
                case 0:
                    ShowRequested?.Invoke();
                    break;
                case 1:
                    RunScheduledRequested?.Invoke();
                    break;
                default:
                    return;
            }
        }
    }

    public void Dispose()
    {
        stop.Set();
        listener.Join(TimeSpan.FromSeconds(2));
        mutex.ReleaseMutex();
        mutex.Dispose();
        show.Dispose();
        runScheduled.Dispose();
        stop.Dispose();
    }
}

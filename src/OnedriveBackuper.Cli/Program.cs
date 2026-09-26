using System.Text;
using OnedriveBackuper.Cli;

try
{
    // OneDrive file names can be in any language.
    Console.OutputEncoding = Encoding.UTF8;
}
catch (IOException)
{
    // No console attached (e.g. output redirected in some hosts); the default encoding is fine then.
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    if (stop.IsCancellationRequested)
    {
        return; // Second Ctrl+C: quit now. The next run frees up anything left downloaded.
    }
    e.Cancel = true;
    stop.Cancel();
    Console.Error.WriteLine("Stopping after the current file... (press Ctrl+C again to quit immediately)");
};

return Commands.Run(args, Console.Out, Console.Error, stop.Token);

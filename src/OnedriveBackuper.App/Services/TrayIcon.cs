using OnedriveBackuper.App.ViewModels;
using Forms = System.Windows.Forms;

namespace OnedriveBackuper.App.Services;

/// <summary>The icon next to the clock: shows progress while backing up in the background, and pops up notifications.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ToolStripItem backUpItem;
    private readonly Forms.ToolStripItem stopItem;
    private readonly System.Drawing.Icon image;

    public TrayIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!;
        using (var stream = resource.Stream)
        {
            image = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open OnedriveBackuper", null, (_, _) => OpenRequested?.Invoke());
        backUpItem = menu.Items.Add("Back up now", null, (_, _) => BackUpRequested?.Invoke());
        stopItem = menu.Items.Add("Stop backup", null, (_, _) => StopRequested?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
        stopItem.Enabled = false;

        icon = new Forms.NotifyIcon { Icon = image, Text = "OnedriveBackuper", ContextMenuStrip = menu, Visible = false };
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                OpenRequested?.Invoke();
            }
        };
        icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
    }

    public event Action? OpenRequested;

    public event Action? BackUpRequested;

    public event Action? StopRequested;

    public event Action? ExitRequested;

    public bool Visible
    {
        get => icon.Visible;
        set => icon.Visible = value;
    }

    public void SetStatus(string text, bool running)
    {
        // Windows limits tray tooltips to 127 characters.
        icon.Text = text.Length > 127 ? text[..124] + "..." : text;
        backUpItem.Enabled = !running;
        stopItem.Enabled = running;
    }

    public void Notify(string title, string text, Tone tone)
    {
        var kind = tone switch
        {
            Tone.Bad => Forms.ToolTipIcon.Error,
            Tone.Warning => Forms.ToolTipIcon.Warning,
            _ => Forms.ToolTipIcon.Info,
        };
        icon.ShowBalloonTip(10_000, title, text, kind);
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        image.Dispose();
    }
}

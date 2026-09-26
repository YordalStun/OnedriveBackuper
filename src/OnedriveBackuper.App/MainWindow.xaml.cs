using System.Windows;

namespace OnedriveBackuper.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Small screens (a VM console is often 1024x768): never open bigger than the screen.
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width - 32);
        Height = Math.Min(Height, area.Height - 32);
    }
}

using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows.Input;
using Microsoft.Win32;
using OnedriveBackuper.Core;
using OnedriveBackuper.Windows;

namespace OnedriveBackuper.App.ViewModels;

/// <summary>Folders, exclusions, free space, and the "check this PC" tools. Changes are saved straight away.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel main;
    private bool isWorking;
    private string toolOutput = "", toolTitle = "";

    public SettingsViewModel(MainViewModel main)
    {
        this.main = main;
        BrowseSourceCommand = new RelayCommand(BrowseSource);
        DetectSourceCommand = new RelayCommand(() => SourceFolder = AppSettings.DetectOneDriveFolder() ?? SourceFolder);
        BrowseBackupCommand = new RelayCommand(BrowseBackupFolder);
        CheckOneDriveCommand = new RelayCommand(CheckOneDrive, () => !main.IsBusy);
        TestFileCommand = new RelayCommand(TestOneFile, () => !main.IsBusy);
        OpenDataFolderCommand = new RelayCommand(() => ViewModels.RunViewModel.OpenInShell(AppSettings.DefaultFolder));
    }

    public ICommand BrowseSourceCommand { get; }
    public ICommand DetectSourceCommand { get; }
    public ICommand BrowseBackupCommand { get; }
    public ICommand CheckOneDriveCommand { get; }
    public ICommand TestFileCommand { get; }
    public ICommand OpenDataFolderCommand { get; }

    public IReadOnlyList<string> FreeUpMethods { get; } =
    [
        "Automatic (recommended): directly, or the Explorer way if that fails",
        "Directly, straight away",
        "The Explorer way (\"Free up space\"), then wait for OneDrive",
    ];

    public string SourceFolder
    {
        get => main.Settings.SourceFolder ?? AppSettings.DetectOneDriveFolder() ?? "";
        set
        {
            main.Settings.SourceFolder = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            Saved();
        }
    }

    public string BackupFolder
    {
        get => main.Settings.BackupFolder ?? "";
        set
        {
            main.Settings.BackupFolder = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            Saved();
        }
    }

    public string ExcludeText
    {
        get => string.Join(Environment.NewLine, main.Settings.Exclude);
        set
        {
            main.Settings.Exclude = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            Saved();
        }
    }

    public string ReserveGb
    {
        get => (main.Settings.ReserveBytes / (double)(1L << 30)).ToString("0.#", CultureInfo.CurrentCulture);
        set
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var gb) && gb >= 0)
            {
                main.Settings.ReserveBytes = (long)(gb * (1L << 30));
                Saved();
            }
        }
    }

    public int FreeUpMethodIndex
    {
        get => (int)main.Settings.FreeUpMethod;
        set
        {
            main.Settings.FreeUpMethod = (FreeUpMethod)value;
            Saved();
        }
    }

    public string BackupFolderWarning
    {
        get
        {
            var backup = main.Settings.BackupFolder;
            var source = main.Settings.SourceFolder ?? AppSettings.DetectOneDriveFolder();
            if (string.IsNullOrWhiteSpace(backup))
            {
                return "Choose a folder on another drive: a USB disk, a second drive or a network folder.";
            }
            try
            {
                if (source != null && (Paths.IsInside(backup, source) || Paths.IsInside(source, backup)))
                {
                    return "The backup folder cannot be inside your OneDrive folder (or the other way round).";
                }
                if (source != null && string.Equals(Path.GetPathRoot(Path.GetFullPath(backup)), Path.GetPathRoot(Path.GetFullPath(source)), StringComparison.OrdinalIgnoreCase))
                {
                    return "Tip: this is the same drive as your OneDrive. A backup on another drive also protects you if this drive fails.";
                }
            }
            catch (ArgumentException)
            {
                return "That does not look like a folder path.";
            }
            return "";
        }
    }

    public bool IsWorking { get => isWorking; private set { if (Set(ref isWorking, value)) { main.OnBusyChanged(); } } }
    public string ToolTitle { get => toolTitle; private set => Set(ref toolTitle, value); }
    public string ToolOutput { get => toolOutput; private set => Set(ref toolOutput, value); }

    public string Version =>
        typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";

    public string DataFolder => AppSettings.DefaultFolder;

    public void BrowseBackupFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose where to save your OneDrive backups" };
        if (dialog.ShowDialog() == true)
        {
            BackupFolder = dialog.FolderName;
        }
    }

    private void BrowseSource()
    {
        var dialog = new OpenFolderDialog { Title = "Choose your OneDrive folder" };
        if (dialog.ShowDialog() == true)
        {
            SourceFolder = dialog.FolderName;
        }
    }

    private void Saved()
    {
        main.SaveSettings();
        OnPropertyChanged(nameof(SourceFolder));
        OnPropertyChanged(nameof(BackupFolder));
        OnPropertyChanged(nameof(BackupFolderWarning));
    }

    private void CheckOneDrive()
    {
        var root = SourceFolder;
        var reserve = main.Settings.ReserveBytes;
        var cloud = CloudFiles.Create(main.Settings.FreeUpMethod);
        var exclude = new PathFilter(main.Settings.Exclude);
        RunTool("Checking your OneDrive (this downloads nothing)...", () => Describe(OneDriveOverview.Create(root, cloud, reserve, exclude)));
    }

    public static string Describe(OneDriveOverview o)
    {
        var text = new StringBuilder();
        text.AppendLine($"{o.Root}: {o.TotalFiles:N0} files, {Format.Size(o.TotalBytes)} in total.");
        foreach (var group in o.Groups)
        {
            text.AppendLine($"   {char.ToUpper(group.State[0])}{group.State[1..]}: {group.Files:N0} files ({Format.Size(group.Bytes)})");
        }
        foreach (var problem in o.Problems)
        {
            text.AppendLine($"   Could not read {problem.Path}: {problem.Error}");
        }
        text.AppendLine();
        if (o.LargestOnlineOnly is not { } largest)
        {
            text.AppendLine("Nothing is online-only, so backups do not need to download anything.");
        }
        else
        {
            text.AppendLine($"Largest online-only file: {largest.RelativePath} ({Format.Size(largest.Size)}).");
            text.AppendLine($"A backup needs {Format.Size(o.NeededBytes)} free on this drive (that file plus the reserve), and there is {Format.Size(o.FreeBytes)} free.");
            text.AppendLine(o.EnoughSpace
                ? "That is enough: every file can be backed up."
                : "That is NOT enough: files bigger than the free space would be skipped. Free up some space first.");
        }
        return text.ToString();
    }

    private void TestOneFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Pick a file that is online-only (cloud icon)",
            InitialDirectory = SourceFolder,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        var path = dialog.FileName;
        RunTool("Testing download and free-up with one file...", () =>
        {
            var output = new StringWriter();
            Probe.Run(path, output, new PendingFreeUps(PendingFreeUps.DefaultPath));
            return output.ToString();
        });
    }

    private void RunTool(string title, Func<string> work)
    {
        IsWorking = true;
        ToolTitle = title;
        ToolOutput = "";
        Task.Run(work).ContinueWith(t =>
        {
            IsWorking = false;
            ToolTitle = "";
            ToolOutput = t.IsFaulted ? t.Exception!.GetBaseException().Message : t.Result;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>For screenshots.</summary>
    public void ShowToolOutput(string text) => ToolOutput = text;
}

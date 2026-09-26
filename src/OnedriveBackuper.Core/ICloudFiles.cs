namespace OnedriveBackuper.Core;

/// <summary>
/// Talks to the sync client that owns the files (OneDrive on Windows). The backup engine only uses
/// this interface, so it can be tested without Windows or OneDrive.
/// </summary>
public interface ICloudFiles
{
    /// <summary>Decodes the file's state from attributes that were already read. Must not touch the file's content.</summary>
    CloudFileState GetState(string fullPath, FileAttributes attributes);

    /// <summary>Downloads the whole file and returns once it is on disk. Throws if it cannot.</summary>
    void Hydrate(string fullPath);

    /// <summary>Frees the local copy so the file is online-only again, and returns once that is done. Throws if it cannot.</summary>
    void Dehydrate(string fullPath);
}

/// <summary>Used for plain folders and on systems without the Cloud Files API: every file is already local.</summary>
public sealed class NoCloudFiles : ICloudFiles
{
    public CloudFileState GetState(string fullPath, FileAttributes attributes) => CloudFileState.FromAttributes(attributes);

    public void Hydrate(string fullPath) =>
        throw new PlatformNotSupportedException("Downloading online-only files needs Windows 10 (1709) or later.");

    public void Dehydrate(string fullPath) =>
        throw new PlatformNotSupportedException("Freeing up online-only files needs Windows 10 (1709) or later.");
}

public static class CloudFiles
{
    /// <summary>The Windows Cloud Files API where available (Windows 10 1709+), otherwise plain-folder behaviour.</summary>
    public static ICloudFiles Create(OnedriveBackuper.Windows.FreeUpMethod method) =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299)
            ? new OnedriveBackuper.Windows.WindowsCloudFiles(method)
            : new NoCloudFiles();
}

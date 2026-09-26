using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OnedriveBackuper.Windows;

[SupportedOSPlatform("windows")]
public static partial class NativeFile
{
    /// <summary>
    /// How much of the file actually takes up disk space. For a OneDrive file being downloaded this grows
    /// as the data arrives, which gives download progress. Null if it cannot be read.
    /// </summary>
    public static long? SizeOnDisk(string path)
    {
        var low = GetCompressedFileSize(CloudFilterApi.ToExtendedLengthPath(path), out var high);
        if (low == uint.MaxValue && Marshal.GetLastPInvokeError() != 0)
        {
            return null;
        }
        return ((long)high << 32) | low;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetCompressedFileSize(string fileName, out uint fileSizeHigh);
}

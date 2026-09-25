using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace OnedriveBackuper.Windows;

/// <summary>
/// The parts of the Windows Cloud Files API (cfapi.h, cldapi.dll, Windows 10 1709+) that any app may call to
/// download or free up files owned by a sync client such as OneDrive.
/// https://learn.microsoft.com/windows/win32/api/cfapi/
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class CloudFilterApi
{
    private const string Library = "cldapi.dll";

    /// <summary>CF_EOF: "to the end of the file" for lengths.</summary>
    public const long EndOfFile = -1;

    public const int CF_HYDRATE_FLAG_NONE = 0;

    public const int CF_OPEN_FILE_FLAG_EXCLUSIVE = 0x1;
    public const int CF_OPEN_FILE_FLAG_WRITE_ACCESS = 0x2;

    public const int CF_UPDATE_FLAG_VERIFY_IN_SYNC = 0x1;
    public const int CF_UPDATE_FLAG_DEHYDRATE = 0x4;

    public const int CF_PIN_STATE_UNPINNED = 2;
    public const int CF_SET_PIN_FLAG_NONE = 0;

    /// <summary>Downloads the given range of a placeholder. Without an OVERLAPPED it returns once the data is on disk.</summary>
    [LibraryImport(Library)]
    public static partial int CfHydratePlaceholder(SafeFileHandle fileHandle, long startingOffset, long length, int hydrateFlags, IntPtr overlapped);

    /// <summary>Opens a file with an oplock, so the handle steps aside instead of blocking other programs.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CfOpenFileWithOplock(string filePath, int flags, out IntPtr protectedHandle);

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool CfReferenceProtectedHandle(IntPtr protectedHandle);

    [LibraryImport(Library)]
    public static partial IntPtr CfGetWin32HandleFromProtectedHandle(IntPtr protectedHandle);

    [LibraryImport(Library)]
    public static partial void CfReleaseProtectedHandle(IntPtr protectedHandle);

    [LibraryImport(Library)]
    public static partial void CfCloseHandle(IntPtr fileHandle);

    /// <summary>With CF_UPDATE_FLAG_DEHYDRATE, frees the placeholder's local data. Needs an exclusive handle.</summary>
    [LibraryImport(Library)]
    public static partial int CfUpdatePlaceholder(
        IntPtr fileHandle,
        IntPtr fsMetadata,
        IntPtr fileIdentity,
        uint fileIdentityLength,
        IntPtr dehydrateRangeArray,
        uint dehydrateRangeCount,
        int updateFlags,
        IntPtr updateUsn,
        IntPtr overlapped);

    /// <summary>Sets the user's intent: pinned ("Always keep on this device") or unpinned ("Free up space").</summary>
    [LibraryImport(Library)]
    public static partial int CfSetPinState(SafeFileHandle fileHandle, int pinState, int pinFlags, IntPtr overlapped);

    public static void ThrowIfFailed(int hresult, string action)
    {
        if (hresult >= 0)
        {
            return;
        }
        var reason = Marshal.GetExceptionForHR(hresult)?.Message.Trim() ?? "unknown error";
        throw new CloudFileException($"{action} failed: {reason} (0x{hresult:X8})", hresult);
    }

    /// <summary>cfapi takes raw Win32 paths, so long OneDrive paths need the \\?\ prefix.</summary>
    public static string ToExtendedLengthPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return full;
        }
        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + full[2..]
            : @"\\?\" + full;
    }
}

public sealed class CloudFileException(string message, int hresult) : IOException(message, hresult);

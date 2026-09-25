using System.Diagnostics;
using System.Runtime.Versioning;
using OnedriveBackuper.Core;
using static OnedriveBackuper.Windows.CloudFilterApi;

namespace OnedriveBackuper.Windows;

public enum FreeUpMethod
{
    /// <summary>Try <see cref="Direct"/>, and if OneDrive refuses, fall back to <see cref="Unpin"/>.</summary>
    Auto,

    /// <summary>Free the space ourselves with CfUpdatePlaceholder(DEHYDRATE). Immediate.</summary>
    Direct,

    /// <summary>Do what Explorer's "Free up space" does (unpin) and wait for OneDrive to free it.</summary>
    Unpin,
}

/// <summary>Downloads and frees up OneDrive files through the Windows Cloud Files API.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCloudFiles(FreeUpMethod method) : ICloudFiles
{
    public TimeSpan UnpinTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public CloudFileState GetState(string fullPath, FileAttributes attributes) => CloudFileState.FromAttributes(attributes);

    public void Hydrate(string fullPath)
    {
        // Opening a file does not download it; only reading its data (or this call) does.
        using var handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var hr = CfHydratePlaceholder(handle, 0, EndOfFile, CF_HYDRATE_FLAG_NONE, IntPtr.Zero);
        ThrowIfFailed(hr, "Downloading the file (CfHydratePlaceholder)");
    }

    public void Dehydrate(string fullPath)
    {
        switch (method)
        {
            case FreeUpMethod.Direct:
                DehydrateDirectly(fullPath);
                break;
            case FreeUpMethod.Unpin:
                DehydrateByUnpinning(fullPath);
                break;
            default:
                try
                {
                    DehydrateDirectly(fullPath);
                }
                catch (IOException direct)
                {
                    try
                    {
                        DehydrateByUnpinning(fullPath);
                    }
                    catch (Exception fallback) when (fallback is IOException or TimeoutException or UnauthorizedAccessException)
                    {
                        throw new IOException($"{direct.Message} Fallback: {fallback.Message}", fallback);
                    }
                }
                break;
        }
    }

    /// <summary>
    /// Frees the file's local data right now. Windows refuses (and nothing is lost) if the file is pinned,
    /// has changes OneDrive has not uploaded yet, or another program has it open.
    /// </summary>
    public void DehydrateDirectly(string fullPath)
    {
        var hr = CfOpenFileWithOplock(ToExtendedLengthPath(fullPath), CF_OPEN_FILE_FLAG_EXCLUSIVE | CF_OPEN_FILE_FLAG_WRITE_ACCESS, out var protectedHandle);
        ThrowIfFailed(hr, "Opening the file to free it up (CfOpenFileWithOplock)");
        try
        {
            if (!CfReferenceProtectedHandle(protectedHandle))
            {
                throw new IOException("Another program opened the file while it was being freed up.");
            }
            try
            {
                var handle = CfGetWin32HandleFromProtectedHandle(protectedHandle);
                hr = CfUpdatePlaceholder(
                    handle,
                    fsMetadata: IntPtr.Zero,
                    fileIdentity: IntPtr.Zero,
                    fileIdentityLength: 0,
                    dehydrateRangeArray: IntPtr.Zero,
                    dehydrateRangeCount: 0,
                    CF_UPDATE_FLAG_VERIFY_IN_SYNC | CF_UPDATE_FLAG_DEHYDRATE,
                    updateUsn: IntPtr.Zero,
                    overlapped: IntPtr.Zero);
                ThrowIfFailed(hr, "Freeing up space (CfUpdatePlaceholder)");
            }
            finally
            {
                CfReleaseProtectedHandle(protectedHandle);
            }
        }
        finally
        {
            CfCloseHandle(protectedHandle);
        }
    }

    /// <summary>
    /// Marks the file "Free up space" (like Explorer does) and waits until OneDrive has freed it.
    /// This leaves the file's pin state as "unpinned", which is what Explorer leaves too.
    /// </summary>
    public void DehydrateByUnpinning(string fullPath)
    {
        using (var handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var hr = CfSetPinState(handle, CF_PIN_STATE_UNPINNED, CF_SET_PIN_FLAG_NONE, IntPtr.Zero);
            ThrowIfFailed(hr, "Marking the file as 'Free up space' (CfSetPinState)");
        }

        // OneDrive frees the space in the background; our handle had to be closed first.
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < UnpinTimeout)
        {
            if (CloudFileState.FromAttributes(File.GetAttributes(fullPath)).IsOnlineOnly)
            {
                return;
            }
            Thread.Sleep(250);
        }
        throw new TimeoutException($"OneDrive did not free up the file within {UnpinTimeout.TotalSeconds:0} seconds.");
    }
}

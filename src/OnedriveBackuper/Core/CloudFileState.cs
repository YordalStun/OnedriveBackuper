namespace OnedriveBackuper.Core;

/// <summary>
/// What OneDrive (or any other Cloud Files API sync client) says about a file's content on this PC.
/// Everything here comes from the file's attributes, and reading attributes never downloads a file.
/// </summary>
public readonly record struct CloudFileState(bool IsOnlineOnly, bool IsPinned, bool IsUnpinned)
{
    // Attribute bits from winnt.h. .NET's FileAttributes enum does not name them, but passes them through.
    public const FileAttributes PinnedAttribute = (FileAttributes)0x00080000;             // FILE_ATTRIBUTE_PINNED ("Always keep on this device")
    public const FileAttributes UnpinnedAttribute = (FileAttributes)0x00100000;           // FILE_ATTRIBUTE_UNPINNED ("Free up space")
    public const FileAttributes RecallOnDataAccessAttribute = (FileAttributes)0x00400000; // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS (content not on disk)

    public static CloudFileState Local { get; } = new(false, false, false);

    public static CloudFileState FromAttributes(FileAttributes attributes) => new(
        IsOnlineOnly: (attributes & RecallOnDataAccessAttribute) != 0,
        IsPinned: (attributes & PinnedAttribute) != 0,
        IsUnpinned: (attributes & UnpinnedAttribute) != 0);

    /// <summary>
    /// True when backing the file up means downloading it, and the space should be freed again afterwards
    /// so the file ends up exactly as the user left it. Pinned files are never freed: the user asked to keep them.
    /// </summary>
    public bool FreeUpAfterBackup => IsOnlineOnly && !IsPinned;

    public string Describe() => (IsOnlineOnly, IsPinned) switch
    {
        (true, true) => "always keep on this device (not downloaded yet)",
        (false, true) => "always keep on this device",
        (true, false) => "online-only",
        (false, false) => "locally available",
    };
}

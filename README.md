# OnedriveBackuper

Full and incremental backups of your OneDrive, **without needing room on your PC for your whole OneDrive**.

With Files On-Demand, most of a big OneDrive is "online-only": the files show in Explorer but are not on
your disk. Ordinary backup programs either skip those files or read them all, which downloads everything
and fills the disk. OnedriveBackuper goes through your OneDrive one file at a time:

1. **Download** the file (only if it is online-only),
2. **copy** it to the backup and check it,
3. **free up** the space again, so the file is online-only exactly as before,
4. move on to the next file.

So the PC only ever needs free space for the **single biggest file** (plus a small reserve), whether your
OneDrive is 50 GB or 5 TB.

> **Status: first version.** The backup logic is covered by automated tests using a simulated OneDrive, but
> the Windows download/free-up calls have not yet been run against a real OneDrive. Follow
> [First run](#first-run-on-a-new-pc) before trusting it with everything.

## Requirements

- Windows 10 version 1709 or later, or Windows 11
- OneDrive running and signed in (it does the actual downloading)
- A backup drive (USB disk, second drive, NAS share, ...) big enough for the backup

## Getting it

Each push builds `OnedriveBackuper.exe` in GitHub Actions: open the latest **Build** run under the
**Actions** tab and download `OnedriveBackuper-win-x64` (or `-win-arm64` for ARM PCs). It is a single
file; nothing to install.

To build it yourself, install the [.NET 10 SDK](https://dotnet.microsoft.com/download) and run:

```
dotnet publish src/OnedriveBackuper -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

## First run on a new PC

1. **See what you have.** This only reads file information and downloads nothing:
   ```
   OnedriveBackuper scan
   ```
   It shows how much is online-only, locally available, and "always keep on this device", your largest
   online-only file, and whether you have enough free space. The online-only numbers should match what
   Explorer shows with the cloud icon.

2. **Test one file.** Pick an online-only file (cloud icon) and run:
   ```
   OnedriveBackuper probe "C:\Users\You\OneDrive\Documents\some file.pdf"
   ```
   It downloads the file, reads it, frees it up again, and reports each step. If step 3 fails but 3b
   works, use `--free-method unpin` for backups.

3. **Dry run.** Shows what a backup would copy and download, without doing it:
   ```
   OnedriveBackuper backup E:\OneDriveBackup --dry-run
   ```

4. **Try a small part first**, e.g. leave out your biggest folders:
   ```
   OnedriveBackuper backup E:\OneDriveBackup --exclude Videos --exclude Pictures
   ```
   Then check the files came back as online-only in Explorer, and try a `restore` into a test folder.

## Everyday use

```
OnedriveBackuper backup E:\OneDriveBackup
```

- The **first** backup into a folder is a **full** backup: every file is copied.
- Every **later** backup is **incremental**: only files that are new or changed since the last backup are
  copied (and only those are downloaded). Unchanged files are recognised by size and last-changed time,
  which needs no download at all.
- `--full` makes a new full backup instead.
- Press **Ctrl+C** to stop after the current file. Run the same command again and it carries on where it
  stopped; nothing already copied is copied again.

All options:

```
OnedriveBackuper backup <backup-folder> [options]
    --source <folder>     Folder to back up (default: your OneDrive folder)
    --full                Make a new full backup even if there is an earlier one
    --exclude <pattern>   Skip matching files or folders, e.g. "*.tmp" or "Videos" (repeatable)
    --reserve <size>      Free space to always leave on the OneDrive drive (default 1GB)
    --free-method <m>     How files are freed up again: auto (default), direct or unpin
    --dry-run             Show what would be copied and downloaded, without doing it
    --verbose             Show more detail

OnedriveBackuper restore <backup-folder> <restore-to-folder> [--set <id>] [--only <pattern>]... [--overwrite]
OnedriveBackuper list    <backup-folder>
OnedriveBackuper verify  <backup-folder> [--set <id>]
OnedriveBackuper scan    [<onedrive-folder>] [--reserve <size>]
OnedriveBackuper probe   <file>
```

Patterns use `*` and `?`, are case-insensitive, and match either a name anywhere (`Videos`, `*.tmp`) or a
path from the top of your OneDrive (`Pictures/Camera Roll`).

Exit codes: `0` all good, `1` finished but some files failed (they are retried next time), `2` could not
run (bad arguments, missing folder, ...), `3` stopped with Ctrl+C.

## Restoring

```
OnedriveBackuper list E:\OneDriveBackup
OnedriveBackuper restore E:\OneDriveBackup D:\Restored                         # latest backup
OnedriveBackuper restore E:\OneDriveBackup D:\Restored --set 20260925T143000Z-full
OnedriveBackuper restore E:\OneDriveBackup D:\Restored --only "Documents/Taxes"
```

Every backup, full or incremental, restores your OneDrive **exactly as it was at that time**, including
files that have since been deleted. Each file's checksum is checked as it is restored. `verify` checks a
whole backup without restoring it.

Restoring straight into your OneDrive folder works, but OneDrive will then upload everything restored.

## How it works

### Knowing a file's state without downloading it

OneDrive uses the Windows Cloud Files API. Each file's state is visible in its attributes, and reading
attributes never downloads anything:

| Explorer shows | Attribute | Backup does |
|---|---|---|
| ☁️ Online-only | `RECALL_ON_DATA_ACCESS` | download, copy, free up again |
| ✅ Locally available | (none of these) | copy |
| ✅ Always keep on this device | `PINNED` | copy (downloading first if needed); never freed up |

### Downloading and freeing up

- **Download:** [`CfHydratePlaceholder`](https://learn.microsoft.com/windows/win32/api/cfapi/nf-cfapi-cfhydrateplaceholder),
  which returns once the whole file is on disk.
- **Free up (`direct`):** [`CfOpenFileWithOplock`](https://learn.microsoft.com/windows/win32/api/cfapi/nf-cfapi-cfopenfilewithoplock)
  plus [`CfUpdatePlaceholder`](https://learn.microsoft.com/windows/win32/api/cfapi/nf-cfapi-cfupdateplaceholder)
  with `DEHYDRATE | VERIFY_IN_SYNC`. Windows refuses (and nothing is lost) if the file has changes OneDrive
  has not uploaded yet, is pinned, or is open in another program.
- **Free up (`unpin`):** [`CfSetPinState(UNPINNED)`](https://learn.microsoft.com/windows/win32/api/cfapi/nf-cfapi-cfsetpinstate),
  the same as Explorer's "Free up space", then waits for OneDrive to do it.
- **`auto`** (default) tries `direct` and falls back to `unpin`. Failed free-ups are retried a few times,
  because antivirus and the search indexer often open a freshly downloaded file for a moment.

### Never leaving files downloaded

Before downloading a file, its path is written to `%LOCALAPPDATA%\OnedriveBackuper\pending-free-up.txt`,
and removed once it is freed up. If the backup crashes, the PC loses power, or you press Ctrl+C twice, the
next run frees those files up first. Anything that still cannot be freed up is listed at the end of the run.

### Backup folder layout

```
E:\OneDriveBackup\
  sets\
    20260925T143000Z-full\      a full backup
      data\...                  copies of the files, in their normal folders (browse them in Explorer)
      manifest.json             every file in this backup: path, size, time, SHA-256, which set holds it
      set.json                  kind, times and totals
    20260926T090000Z-incr\      an incremental backup
      data\...                  only the files that were new or changed
      manifest.json             still lists every file; unchanged ones point at the earlier set
  logs\                         one log per backup
```

A backup that was stopped has no `manifest.json` yet (and has a `journal.jsonl` of files done so far); the
next run finishes it.

### Safety checks

- Every copy is written to a temporary file, checked (size, and that the file did not change while being
  copied), and only then moved into place.
- A file that fails keeps its previous version in the new backup and is tried again next time.
- Before downloading, it checks there is room for the file plus the reserve; if not, that file is skipped
  and reported, and the backup carries on.
- Symbolic links and junctions are not followed. The backup folder cannot be inside your OneDrive.
- Only one backup can write to a backup folder at a time.

## Limitations (for now)

- **Deleting old backups:** there is no `prune` command yet. Do not delete a set folder by hand if later
  incremental backups exist: they may point at files stored in it.
- **Renamed or moved files** are copied (and downloaded) again, because files are matched by path.
- **Empty folders**, file permissions and attributes other than the last-changed time are not backed up.
- **One file at a time** by design, so many small online-only files take a while (one download each).
- Windows can block an app from downloading OneDrive files (Settings > Privacy & security > Automatic file
  downloads). If downloads fail with "access denied", check that OnedriveBackuper is allowed there.
- Other sync clients built on the same Windows API (Dropbox, iCloud, ...) may work too, but only OneDrive
  is what this is designed for.

## Development

```
dotnet test
```

The tests run on any OS: they use a simulated OneDrive (`tests/OnedriveBackuper.Tests/FakeOneDrive.cs`)
that keeps online-only files as zero-filled placeholders, so a backup that forgets to download a file
fails the tests. The Windows-only code is in `src/OnedriveBackuper/Windows`.

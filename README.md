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

> **Status: early.** The backup logic is covered by automated tests using a simulated OneDrive, and the app's
> screens are rendered on Windows by the automated build, but the download/free-up calls have not yet been
> run against a real OneDrive. Use **Settings > Check this PC** (see [First run](#first-run-on-a-new-pc))
> before trusting it with everything.

## Requirements

- Windows 10 version 1709 or later, Windows 11, or Windows Server 2016+ with the desktop
- OneDrive running and signed in (it does the actual downloading)
- A backup drive (USB disk, second drive, NAS share, ...) big enough for the backup

## Getting it

Each push builds the program in GitHub Actions: open the latest **Build** run under the **Actions** tab and
download `OnedriveBackuper-win-x64` (or `-win-arm64` for ARM PCs). Inside are two single-file programs,
nothing to install:

- **`OnedriveBackuper.exe`**: the app, with a window, progress bars, scheduling and a tray icon.
- **`OnedriveBackuper-cli.exe`**: the same engine from the command line, for scripts.

To build it yourself, install the [.NET 10 SDK](https://dotnet.microsoft.com/download) and run:

```
dotnet publish src/OnedriveBackuper.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## The app

It follows Windows' light or dark mode and has four pages:

- **Home**: how the last backup went, when the next automatic one is, your OneDrive and backup folders with
  their free space, and **Back up now** / **Full backup**. While a backup runs, Home shows it live:
  - an overall progress bar (how much of what needs copying is done, file counts, time left),
  - how many of your OneDrive's files have been checked,
  - the file being worked on, with its three steps (**Download**, **Copy to backup**, **Free up space**)
    and its own progress bar,
  - counters (copied, unchanged, downloaded, freed up again, problems),
  - the free space on the OneDrive drive, which stays about the same the whole time,
  - a live activity log (with an "only problems" filter).
- **Backups**: every backup with its date, type and totals. **Restore** into any folder (optionally only
  some files or folders), **Check** a backup against its checksums, or **Delete** one (refused if a newer
  backup still uses files stored in it).
- **Schedule**: back up automatically every day, on chosen days, or every few hours, and clean up old
  backups automatically (see below).
- **Settings**: the folders, files and folders to leave out, how much space to always keep free, how files
  are freed up again, and **Check this PC**.

Closing the window during a backup keeps it going in the background, with an icon next to the clock; click
the icon to bring the window back. Only one copy of the app runs at a time.

### Automatic backups

Turn on **Back up automatically** on the Schedule page and press **Save**. This creates a task in Windows
Task Scheduler (named "OnedriveBackuper"; no administrator rights needed) that:

- runs while you are signed in to Windows, because OneDrive has to be running to download files (a locked
  screen is fine),
- runs as soon as possible if the PC was off or asleep at the scheduled time,
- never stops a long backup halfway, and never starts a second one.

A scheduled backup starts quietly: nothing opens, an icon appears next to the clock (hover it to see
progress, click it to open the window), it runs at below-normal priority so it does not slow you down, and
a notification pops up when it is done. Then it exits, so nothing stays running between backups.

If the backup drive is not connected when a backup is due, it is skipped with a notification (it never
starts a new backup somewhere else instead).

### Old backups

With **Clean up old backups automatically** on (the default), a backup is made a **full** one when the last
full backup is older than a set number of days (default 30), and after each backup the oldest backups are
deleted so that only the last few full backups remain (default 3), each with the incremental backups made
after it. So by default you can go back about three months. A backup that a kept backup still uses is never
deleted.

## First run on a new PC

1. Open the app, and on **Home** choose where to save backups.
2. **Settings > Check my OneDrive** shows how much is online-only, your largest online-only file, and
   whether there is enough free space. It downloads nothing. The online-only numbers should match what
   Explorer shows with the cloud icon.
3. **Settings > Test with one file...**: pick an online-only file (cloud icon). It downloads the file,
   reads it, frees it up again and reports each step. If "Free up space (directly)" fails but "the
   Explorer way" works, choose the Explorer way (or keep Automatic) under **How to free up space**.
4. Try a first backup of a small part: add your biggest folders under **Leave out**, press **Back up now**,
   then check in Explorer that the files are online-only again, and try a **Restore** into a test folder.

## Command line

`OnedriveBackuper-cli.exe` does the same without a window:

```
OnedriveBackuper-cli backup <backup-folder> [options]
    --source <folder>     Folder to back up (default: your OneDrive folder)
    --full                Make a new full backup even if there is an earlier one
    --exclude <pattern>   Skip matching files or folders, e.g. "*.tmp" or "Videos" (repeatable)
    --reserve <size>      Free space to always leave on the OneDrive drive (default 1GB)
    --free-method <m>     How files are freed up again: auto (default), direct or unpin
    --dry-run             Show what would be copied and downloaded, without doing it
    --verbose             Show more detail

OnedriveBackuper-cli restore <backup-folder> <restore-to-folder> [--set <id>] [--only <pattern>]... [--overwrite]
OnedriveBackuper-cli list    <backup-folder>
OnedriveBackuper-cli verify  <backup-folder> [--set <id>]
OnedriveBackuper-cli scan    [<onedrive-folder>] [--reserve <size>]
OnedriveBackuper-cli probe   <file>
```

- The first backup into a folder is full; later ones are incremental. Unchanged files are recognised by size
  and last-changed time, which needs no download at all.
- Press **Ctrl+C** to stop after the current file. Run the same command again and it carries on where it
  stopped; nothing already copied is copied again.
- Patterns use `*` and `?`, are case-insensitive, and match either a name anywhere (`Videos`, `*.tmp`) or a
  path from the top of your OneDrive (`Pictures/Camera Roll`).
- Exit codes: `0` all good, `1` finished but some files failed (they are retried next time), `2` could not
  run (bad arguments, missing folder, ...), `3` stopped with Ctrl+C.

The command line does not clean up old backups; the app does (or delete them from its Backups page).

## Restoring

Every backup, full or incremental, restores your OneDrive **exactly as it was at that time**, including
files that have since been deleted. Each file's checksum is checked as it is restored.

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
  which returns once the whole file is on disk. The app shows download progress from the file's size on disk.
- **Free up (directly):** [`CfOpenFileWithOplock`](https://learn.microsoft.com/windows/win32/api/cfapi/nf-cfapi-cfopenfilewithoplock)
  plus [`CfUpdatePlaceholder`](https://learn.microsoft.com/windows/win32/api/cfapi/nf-cfapi-cfupdateplaceholder)
  with `DEHYDRATE | VERIFY_IN_SYNC`. Windows refuses (and nothing is lost) if the file has changes OneDrive
  has not uploaded yet, is pinned, or is open in another program.
- **Free up (the Explorer way):** [`CfSetPinState(UNPINNED)`](https://learn.microsoft.com/windows/win32/api/cfapi/nf-cfapi-cfsetpinstate),
  the same as Explorer's "Free up space", then waits for OneDrive to do it.
- **Automatic** (default) tries directly and falls back to the Explorer way. Failed free-ups are retried a
  few times, because antivirus and the search indexer often open a freshly downloaded file for a moment.

### Never leaving files downloaded

Before downloading a file, its path is written to `%LOCALAPPDATA%\OnedriveBackuper\pending-free-up.txt`,
and removed once it is freed up. If the backup crashes, the PC loses power, or the app is killed, the next
run frees those files up first. Anything that still cannot be freed up is reported.

### Backup folder layout

```
E:\OneDriveBackup\
  sets\
    20260925T143000Z-full\      a full backup
      data\...                  copies of the files, in their normal folders (browse them in Explorer)
      manifest.json             every file in this backup: path, size, time, SHA-256, which set holds it
      set.json                  kind, times, totals, and which earlier sets it uses
    20260926T090000Z-incr\      an incremental backup
      data\...                  only the files that were new or changed
      manifest.json             still lists every file; unchanged ones point at the earlier set
  logs\                         one log per backup
```

A backup that was stopped has no `manifest.json` yet (and has a `journal.jsonl` of files done so far); the
next backup finishes it. Deleted backups are first moved to `trash\`, so an interrupted delete can never be
mistaken for a backup to carry on with.

### Safety checks

- Every copy is written to a temporary file, checked (size, and that the file did not change while being
  copied), and only then moved into place.
- A file that fails keeps its previous version in the new backup and is tried again next time.
- Before downloading, it checks there is room for the file plus the reserve; if not, that file is skipped
  and reported, and the backup carries on.
- Symbolic links and junctions are not followed. The backup folder cannot be inside your OneDrive.
- Only one backup can write to a backup folder at a time.

## Limitations (for now)

- **Renamed or moved files** are copied (and downloaded) again, because files are matched by path.
- **Empty folders**, file permissions and attributes other than the last-changed time are not backed up.
- **One file at a time** by design, so many small online-only files take a while (one download each).
- Windows can block an app from downloading OneDrive files (Settings > Privacy & security > Automatic file
  downloads). If downloads fail with "access denied", check that OnedriveBackuper is allowed there.
- If you move `OnedriveBackuper.exe`, open it once from its new place so the scheduled task is updated.
- Other sync clients built on the same Windows API (Dropbox, iCloud, ...) may work too, but OneDrive is what
  this is designed for.

## Development

```
dotnet test
```

- `src/OnedriveBackuper.Core`: the backup engine, scheduling and clean-up rules, and the Windows calls
  (`Windows/`).
- `src/OnedriveBackuper.App`: the WPF app. `OnedriveBackuper.exe --screenshots <folder>` renders every
  screen with example data; the automated build does this and uploads the pictures as `screenshots`.
- `src/OnedriveBackuper.Cli`: the command line.

The tests run on any OS: they use a simulated OneDrive (`tests/OnedriveBackuper.Tests/FakeOneDrive.cs`)
that keeps online-only files as zero-filled placeholders, so a backup that forgets to download a file
fails the tests.

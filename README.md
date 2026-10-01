# DiskSweeper

See what's taking up space on your PC, sorted biggest first, and safely delete what you choose, including
leftover game files and folders that refuse to be deleted the normal way.

## Run it

- **App:** double-click `publish\DiskSweeper.exe`, or `dotnet run --project src/DiskSweeper.App`.
  Use **Run as administrator** (bottom of the sidebar) for fast scans and protected folders.
- **Command line:** `publish\dsweep.exe` (see [Command line](#command-line)).

Needs Windows 10/11 and the .NET 9 Desktop Runtime.

## What's in the app

A Windows 11–style app (follows your light/dark theme and accent color) with a sidebar:

| Page | What it shows |
|---|---|
| Overview | Drive usage, how much Cleanup can free (with a one-click **Clean safe items…**), what kinds of files fill the drive, and the largest folders and files. |
| Folders | The whole drive as a tree, biggest first, with size, share of its folder, file count and last change. Click a column header to sort (↓/↑ shows the order). |
| Largest files | The 1,000 largest files, filterable by name or extension, size, age and type. |
| Treemap | Every file as a block sized by disk use, colored by type. Click for details, double-click to zoom in. |
| File types | Space per kind (video, disk images, game data…) and per extension. Double-click an extension to list those files. |
| Cleanup | Junk and leftovers worth cleaning, grouped by category, each with how much it frees and why it's considered safe. |
| Changes | What grew or shrank since an earlier scan (scans are saved automatically). |
| History | Every delete attempt ever made, also saved as a CSV file. |
| Settings | Fast scan, remembering scans, the default delete choice, and where DiskSweeper keeps its data. |

Click any file or folder to see its **details panel** (size, contents, biggest items inside, warnings, and buttons
for Explorer, Treemap and Copy path). Tick items anywhere; a bar appears at the bottom with the total,
**View selection**, and **Review and delete…**.

## Deleting safely

- **Recycle Bin by default.** The delete dialog lets you pick Recycle Bin or permanent each time; permanent needs an extra tick to confirm.
- **Protected places can't be ticked or deleted:** drive roots, `C:\Windows` (except its Temp and update
  cache), WindowsApps, System Volume Information, `pagefile.sys`/`hiberfil.sys`, your user folders themselves
  (their contents are fine), and anything that contains one of these.
- **Links are never followed.** Deleting a folder that contains a junction removes the junction, never what it points to.
- **Warnings** before deleting: OneDrive (deletes the cloud copy too), possible game saves, installed programs.
- **Dry run** shows what would happen without touching anything.
- Big (10 GB+) and permanent deletes need an extra confirmation tick.
- Everything is logged to `%LOCALAPPDATA%\DiskSweeper\deletion-log.csv`.

### When something won't delete

The results window explains why and offers the fix that applies:

| Problem | What DiskSweeper does |
|---|---|
| Read-only file, path over 260 characters, name ending in a dot or space | Handled automatically by a permanent delete |
| A program has it open | Names the program (via the Windows Restart Manager) and offers **Close it and retry** |
| Still locked by a service or driver | **Delete on next restart** (asks for administrator permission) |
| Access denied (owned by TrustedInstaller, SYSTEM, an old account) | **Take ownership and delete** (asks for administrator permission) |
| Too long or oddly named for the Recycle Bin | **Delete permanently instead** |

Admin-only fixes run in a short-lived elevated copy of the app, so the main window doesn't need to restart.

## Cleanup suggestions

- **Junk and caches:** temp files, GPU shader caches (often many GB), crash dumps, error reports, browser and
  app caches, Windows Update cache, launcher caches, the Recycle Bin. The list lives in
  `src/DiskSweeper.Core/SmartFinds/JunkRules.json`; a test makes sure no rule can point at a protected place.
- **Game leftovers:** Steam folders, shader caches and workshop content of games no longer installed (from
  Steam's own library files), games left behind by an uninstalled Epic or GOG launcher.
- **Leftovers of uninstalled programs:** folders in Program Files, ProgramData and AppData that no installed
  program, Store app, service, running app or PATH entry claims, and that haven't changed for 30 days.
- **Saves of games that seem uninstalled**, **old installers and disk images in Downloads**, **developer caches**.
- **Space Windows manages** (page file, hibernation, restore points, WinSxS, Windows Installer cache) is
  explained with the proper Windows tool or command instead of being deleted.

Every item is marked **Safe** (can't break anything) or **Review** (very likely unneeded, look first).
For removing programs completely (registry entries, services, scheduled tasks), the Cleanup page links to
[Bulk Crap Uninstaller](https://github.com/Klocman/Bulk-Crap-Uninstaller), a free open-source uninstaller.

## Scanning

- **Normal scan:** walks folders in parallel. Hidden and system files count, links are skipped, and
  OneDrive online-only files count as 0 (they use no space on this PC).
- **Fast scan (administrator, whole NTFS drives):** reads the drive's Master File Table directly, about
  3 to 5 seconds for 1.5 million files. It's also more accurate for files that are open and growing (like a
  running virtual machine's disk), whose size Windows' folder listings don't update until they're closed.
- When running as administrator, the normal scan also reads folders that block even administrators
  (it uses the read-only backup privilege; it can't change or delete anything with it).

## Command line

```
dsweep                                 biggest folders and files on C:
dsweep D:\ --depth 3 --files 20        another drive, shallower tree
dsweep C:\ --fast --save --compare     fast scan, keep it, show what changed since last time
dsweep finds                           list cleanup suggestions
dsweep --help                          all options
```

The command-line tool only reads; deleting happens in the app.

## Project layout

```
src/DiskSweeper.Core      all logic, no UI
  Scanning/               folder walk, NTFS fast scan, tree model, in-place tree updates
  Deletion/               protected paths, delete service, lock finder, take ownership, delete on restart, log
  SmartFinds/             junk rules, Steam/Epic/GOG readers, installed-software inventory, leftover finder
  Analysis/               file types, treemap layout, saved scans, scan comparison
src/DiskSweeper.App       WPF app (Windows light/dark theme)
src/DiskSweeper.Cli       dsweep command-line tool
tests/                    xUnit tests; destructive tests run only in throwaway folders under %TEMP%
```

## Build and test

```
dotnet build
dotnet test
dotnet publish src/DiskSweeper.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Run `dotnet test` from an **administrator** terminal to include the admin-only tests (fast scan, take
ownership). From a normal terminal they're reported as skipped.

## Good to know

- Recycled items still use space until the Recycle Bin is emptied (Cleanup lists the bin).
- A scan of 1.5 million files uses about 500 MB of memory while the app is open.
- App data (log, settings, the last five scans per drive) is in `%LOCALAPPDATA%\DiskSweeper`.
- "Delete on next restart" hasn't been exercised on a real reboot during development. It uses the standard
  Windows mechanism (`MoveFileEx` with delay-until-reboot).

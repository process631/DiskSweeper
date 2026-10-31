<p align="center">
  <img src="src/DiskSweeper.App/Assets/Logo.png" alt="DiskSweeper logo" width="96" />
</p>

# DiskSweeper

See what is using space on a Windows PC, then delete only what you choose.

DiskSweeper is a desktop disk-usage analyzer and careful cleaner. It scans a drive, shows the largest folders and files, and suggests junk that is usually safe to remove: caches, old installers, and files left behind by games and programs you uninstalled. Deletes go to the Recycle Bin unless you explicitly choose otherwise, and Windows system locations are refused.

## Features

- **Overview** of a drive: used and free space, how much Cleanup can free, file types, and the largest folders and files.
- **Folder tree** sorted biggest first, with size, share of the parent, file count, and last change.
- **Largest files**, filterable by name, extension, size, age, and type.
- **Treemap** of every file, sized by disk use and colored by type. Click for details, double-click to zoom in.
- **File types** grouped by kind (video, disk images, game data) and by extension.
- **Cleanup suggestions** for junk, game leftovers, and folders left by uninstalled programs, each marked Safe or Review.
- **Changes** since an earlier scan. Scans are saved automatically.
- **Deletion history**, also written to a CSV log.
- **Command-line scan** (`dsweep`) that only reads. Deleting happens in the app.
- **Fast NTFS scan** (administrator) by reading the drive's Master File Table, so a large drive finishes in seconds instead of a full folder walk.

## Tech stack

| Piece | Choice |
|---|---|
| Language | C# |
| Runtime | .NET 9 (`net9.0-windows`) |
| App UI | WPF, Windows 11 Fluent theme (follows the system light/dark theme and accent color) |
| CLI | Console app, assembly name `dsweep` |
| Tests | xUnit |
| Windows APIs | NTFS Master File Table, Restart Manager, shell Recycle Bin, `MoveFileEx` (delete on restart), file ownership and ACLs |

There is no account, telemetry, or network client. The only outbound action is opening a page or a Windows settings link when you click it.

## How it works

```
scan ──► FileNode tree ──► WPF pages and dsweep report
                │
                ├─ SmartFinder (junk rules, Steam / Epic / GOG, installed programs)
                └─ DeleteService (protected paths, Recycle Bin or permanent, CSV log)
                         │
                         └─ if a file is locked or access is denied
                            LockFinder → close the program, or a short-lived elevated helper
                            (take ownership, or delete on next restart)
```

1. **Scan.** `DiskScanner` walks folders in parallel. Hidden and system files count. Junctions and symbolic links are recorded and never followed, so a folder is not counted twice and a scan cannot loop. OneDrive online-only files count as 0 bytes because they use no space on this PC. On an NTFS drive, with administrator rights, `MftScanner` reads the Master File Table directly instead (read-only).
2. **Roll up.** `TreeTotals` sums sizes and counts from files up to their parent folders and sorts each folder largest first.
3. **Suggest.** `SmartFinder` applies the rules in `src/DiskSweeper.Core/SmartFinds/JunkRules.json`, reads Steam library files and Epic/GOG install locations, and looks for folders that no installed program, Store app, service, running process, or PATH entry claims.
4. **Delete.** `DeleteService` refuses protected paths, then sends the item to the Recycle Bin or deletes it permanently. Every attempt is appended to the deletion log. Links are removed without touching what they point to.
5. **When a delete fails.** Restart Manager names the program that has the file open. The app can ask that program to close, or a hidden elevated copy of DiskSweeper can take ownership or queue the delete for the next boot. The main window does not need to restart. The elevated copy only writes its result to a `DiskSweeper-{guid}.json` file in the temp folder.

## Requirements

- Windows 10 or Windows 11
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) to build
- .NET 9 Desktop Runtime to run a framework-dependent build (the SDK includes it)

The WPF app and the fast scan, Recycle Bin, and ownership features run only on Windows. The project can compile on other systems (`EnableWindowsTargeting` is set on the app); it still will not run there.

## Build, test, and run

From the repository root, on Windows:

```powershell
dotnet build
dotnet test
dotnet run --project src/DiskSweeper.App
dotnet run --project src/DiskSweeper.Cli -- --help
```

`dotnet test` from an **administrator** terminal also runs the admin-only tests (fast scan and take ownership). From a normal terminal those tests are skipped. GitHub Actions runs the same `dotnet test` on Windows for every push and pull request.

Publish a folder you can zip or copy to another PC (needs the .NET 9 Desktop Runtime on that PC):

```powershell
dotnet publish src/DiskSweeper.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
dotnet publish src/DiskSweeper.Cli -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

That writes `publish\DiskSweeper.exe` and `publish\dsweep.exe`. `publish/` is gitignored. For a fast scan or a protected folder, use **Run as administrator** at the bottom of the sidebar (or start the terminal as administrator for `dsweep --fast`).

## Usage

### App

| Page | What it shows |
|---|---|
| Overview | Drive usage, how much Cleanup can free (with **Clean safe items…**), file kinds, and the largest folders and files. |
| Folders | The scan as a tree, biggest first. Click a column header to sort. |
| Largest files | The 1,000 largest files, with filters. |
| Treemap | One block per file, sized by disk use. |
| File types | Space per kind and per extension. Double-click an extension to list those files. |
| Cleanup | Junk and leftovers, grouped, with why each one is considered safe. |
| Changes | What grew or shrank since an earlier saved scan. |
| History | Every delete attempt, newest first. |
| Settings | Fast scan, remembering scans, the default delete choice, and where data is stored. |

Click a file or folder for its details (size, contents, warnings, and buttons for Explorer, Treemap, and Copy path). Tick items and use **Review and delete…**. The delete dialog chooses Recycle Bin or permanent; permanent, and deletes of 10 GB or more, need an extra confirmation. **Dry run** reports what would happen and does not touch the disk.

### Command line

`dsweep` only reads.

```text
dsweep                                 biggest folders and files on C:
dsweep D:\ --depth 3 --files 20        another drive, shallower tree
dsweep C:\ --fast --save --compare     fast scan, keep it, show what changed
dsweep finds                           list cleanup suggestions
dsweep --help                          all options
```

## Deleting safely

Protected places cannot be selected or deleted:

- Drive roots, and anything that contains a protected path (so `C:\Users` cannot sneak past the rule for your profile).
- `C:\Windows`, except its Temp folder, Windows Update download cache, minidumps, and `MEMORY.DMP`.
- WindowsApps and other Windows components under Program Files.
- System Volume Information, `pagefile.sys`, `hiberfil.sys`, `swapfile.sys`.
- Your profile folders themselves (Desktop, Documents, Downloads, and the other standard folders). Their contents are allowed.
- DiskSweeper's own install folder and its data folder.

Other checks:

- **Recycle Bin by default.** Permanent delete is a separate choice.
- **Links are never followed.** Deleting a folder removes a junction inside it, not the folder the junction points at.
- **Warnings** before delete: OneDrive (the cloud copy goes too), possible game saves, and installed programs.
- The deletion log is `%LOCALAPPDATA%\DiskSweeper\deletion-log.csv`. Paths that start with `=`, `+`, `-`, or `@` are prefixed so Excel does not treat them as formulas.

When something will not delete, the results window says why:

| Problem | What DiskSweeper does |
|---|---|
| Read-only file, path over 260 characters, name ending in a dot or space | Handled by a permanent delete |
| A program has it open | Names the program (Restart Manager) and offers **Close it and retry** |
| Still locked by a service or driver | **Delete on next restart** (administrator permission) |
| Access denied (TrustedInstaller, SYSTEM, an old account) | **Take ownership and delete** (administrator permission) |
| Too long or oddly named for the Recycle Bin | **Delete permanently instead** |

## Cleanup suggestions

- **Junk and caches:** temp files, GPU shader caches, crash dumps, error reports, browser and app caches, the Windows Update cache, launcher caches, and the Recycle Bin. Rules live in `src/DiskSweeper.Core/SmartFinds/JunkRules.json`. A test checks that no rule points at a protected place.
- **Game leftovers:** Steam folders, shader caches, and workshop content for games that are no longer installed, plus games left behind by an uninstalled Epic or GOG launcher.
- **Leftovers of uninstalled programs:** folders in Program Files, ProgramData, and AppData that nothing installed claims, and that have not changed for 30 days.
- **Saves of games that seem uninstalled**, old installers and disk images in Downloads, and developer caches.
- **Space Windows manages** (page file, hibernation, restore points, WinSxS, Windows Installer cache) is explained, with the Windows setting or command, instead of being deleted.

Each item is **Safe** (cleaning it should not break an installed program) or **Review** (very likely unneeded; look first). Cleanup can open [Bulk Crap Uninstaller](https://github.com/Klocman/Bulk-Crap-Uninstaller) when you want registry entries, services, and scheduled tasks removed as well. DiskSweeper does not include that program; it only links to it.

Advice buttons only start commands that match a fixed shape: a `ms-settings:` or `https://` link, a bare system `.exe` name with no arguments, or a short PowerShell command with no shell metacharacters. The commands shipped in `JunkRules.json` are covered by a test.

## Project structure

```
src/DiskSweeper.Core          scanning, deletion, cleanup rules (no UI)
  Scanning/                  folder walk, NTFS fast scan, tree updates
  Deletion/                  protected paths, delete service, locks, ownership, restart delete, log
  SmartFinds/                junk rules, Steam/Epic/GOG, installed software, leftover finder
  Analysis/                  file types, treemap layout, saved scans, scan comparison
src/DiskSweeper.App          WPF app
src/DiskSweeper.Cli          dsweep
tests/DiskSweeper.Core.Tests xUnit. Destructive tests use throwaway folders under %TEMP%
```

App data (log, settings, and the last five scans per drive) is stored in `%LOCALAPPDATA%\DiskSweeper`.

## Good to know

- Items in the Recycle Bin still use space until the bin is emptied. Cleanup lists the bin.
- A scan of about 1.5 million files uses on the order of 500 MB of memory while the app is open.
- "Delete on next restart" uses `MoveFileEx` with delay-until-reboot. It has not been exercised across a real reboot in development.
- Fast scan needs an administrator and a local NTFS drive. It is read-only. Some security software flags raw volume reads.

## License

[MIT](LICENSE). Bulk Crap Uninstaller is a separate project under the Apache License 2.0 and is not vendored here.

# DiskSweeper

See what's taking up space on your PC, sorted biggest first, and safely delete what you choose, including
a whole program or game (Fortnite, Roblox, and the rest) together with the leftover files it leaves behind.

## Download

Windows 10 or 11. The .NET runtime is inside the download.

| Download | Use it when |
|---|---|
| [DiskSweeper-Setup.exe](https://github.com/process631/DiskSweeper/releases/latest/download/DiskSweeper-Setup.exe) | You want a Start menu shortcut and an uninstaller. |
| [DiskSweeper-win-x64.exe](https://github.com/process631/DiskSweeper/releases/latest/download/DiskSweeper-win-x64.exe) | You want one file to double-click, on 64-bit Windows. |
| [DiskSweeper-win-x64.zip](https://github.com/process631/DiskSweeper/releases/latest/download/DiskSweeper-win-x64.zip) | You also want the `dsweep` command-line tool and the license. |
| [DiskSweeper-win-arm64.zip](https://github.com/process631/DiskSweeper/releases/latest/download/DiskSweeper-win-arm64.zip) | You have Windows on ARM. A single [DiskSweeper-win-arm64.exe](https://github.com/process631/DiskSweeper/releases/latest/download/DiskSweeper-win-arm64.exe) is on the release too. |

These links follow the [latest release](https://github.com/process631/DiskSweeper/releases/latest). Each release includes `SHA256SUMS.txt` so you can check the download.

### Install

1. Download **DiskSweeper-Setup.exe** and open it.
2. Accept the Windows prompt that lets the installer copy files into Program Files. After that, DiskSweeper opens as a normal user.
3. Finish the wizard, then open **DiskSweeper** from the Start menu.
4. A desktop icon is an optional checkbox in the installer, off by default. Remove the app later from **Settings > Apps**.

### Portable

Download **DiskSweeper-win-x64.exe** and double-click it. Delete that file when you want it gone. The zip is the same app plus `dsweep.exe`, a short readme, and the license.

The installer is the 64-bit build. It also runs on Windows 11 on ARM under emulation. The ARM zip is the native build.

### Windows SmartScreen

These downloads aren't code-signed. The first time you open one, Windows shows **Windows protected your PC** and an unknown publisher. Choose **More info**, then **Run anyway**.

### Administrator

DiskSweeper opens as a normal user. A fast scan of a whole NTFS drive reads that drive's file table and needs administrator rights. So do **Take ownership and delete** and **Delete on next restart**. **Run as administrator** at the bottom of the sidebar is where Windows asks. The application manifest is `asInvoker`, so that prompt stays on those actions.

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
| Programs | Search installed programs and games, see everything removal would touch, then uninstall and clean up what's left. |
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
- Everything is logged to `%LOCALAPPDATA%\DiskSweeper\deletion-log.csv`, including program uninstalls and registry keys.

### Removing a program or game

**Programs** searches what is installed and, when you pick one, lists what deletion would remove. Nothing is deleted until you confirm, and **Preview** does a dry run that is written to the history.

Where the list comes from:

| Source | What it reads |
|---|---|
| Installed programs | Uninstall registry keys in HKLM and HKCU, both 64-bit and 32-bit (`WOW6432Node`). |
| Steam | Libraries from `libraryfolders.vdf` and each game's `appmanifest`. Shader cache and workshop content for that app id are included. |
| Epic Games | `.item` manifests under `ProgramData\Epic\EpicGamesLauncher\Data\Manifests`. |
| GOG | Installed games under `HKLM\SOFTWARE\WOW6432Node\GOG.com\Games`. |
| Roblox | The per-user install in `%LOCALAPPDATA%\Roblox\Versions` (Player and Studio separately). |
| Microsoft Store | Packages registered for your user, except Windows components, frameworks and resource packs. |

Picking an item shows the install folder, matching leftovers in AppData (Local, LocalLow, Roaming), ProgramData, Documents, Saved Games, temp, desktop and per-user Start menu shortcuts, and related registry keys. Each line has a size. Untick anything you want to keep. The total is the ticked files.

**Delete** runs the official uninstaller first when there is one: a quiet uninstall string, `msiexec /qn`, or an Inno Setup `/VERYSILENT` switch. Anything else opens the program's own uninstaller. Steam games are handed to `steam://uninstall/<appid>`. Store apps are removed with `Remove-AppxPackage`. Files that remain are then moved to the Recycle Bin (or deleted permanently, if you switch). Registry keys are removed after the files.

If the program or its launcher is still running, DiskSweeper asks to close it first. It does not close Steam when Steam itself is doing the uninstall. If Windows needs administrator rights, the uninstaller asks on its own, and HKLM registry keys are removed by the same short elevated helper the rest of the app uses.

Safety limits, on purpose:

- System folders, `WindowsApps`, launcher libraries (`Epic Games`, `steamapps\common`, `GOG Galaxy\Games`) and other shared parents are never deleted.
- A leftover folder has to match the product name, not the publisher. Fortnite does not take folders named Epic, the Epic launcher, or a sibling such as Rocket League. Visual Studio does not take Visual Studio Code. Roblox does not take Roblox Studio, and the shared `%LOCALAPPDATA%\Roblox` folder is kept when both are installed.
- A name that is only a generic word (`Games`, `App`, `Cache`) does not search for leftovers. Only that program's own install folder is offered, and a generic install location is refused.
- A folder that also matches a different installed program is skipped.
- Protected paths still can't be deleted. All-users Start menu shortcuts live under a Windows folder DiskSweeper will not change; the uninstaller usually removes those itself.
- DiskSweeper will not uninstall itself while it is open.
- Leftovers are looked for in the usual places, not by scanning the whole disk. A folder on another drive is removed when it is the install folder or a known owned folder (a Steam shader cache, for example).

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
For removing one installed program or game, use **Programs**. Cleanup still links to
[Bulk Crap Uninstaller](https://github.com/Klocman/Bulk-Crap-Uninstaller) when you want a batch uninstaller
that also removes services and scheduled tasks.

## Scanning

- **Normal scan:** walks folders in parallel. Hidden and system files count, links are skipped, and
  OneDrive online-only files count as 0 (they use no space on this PC).
- **Fast scan (administrator, whole NTFS drives):** reads the drive's Master File Table directly, about
  3 to 5 seconds for 1.5 million files. It's also more accurate for files that are open and growing (like a
  running virtual machine's disk), whose size Windows' folder listings don't update until they're closed.
- When running as administrator, the normal scan also reads folders that block even administrators
  (it uses the read-only backup privilege; it can't change or delete anything with it).

## Command line

`dsweep.exe` ships in the portable zip and in the install folder. From a source checkout: `dotnet run --project src/DiskSweeper.Cli -- --help`.

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
  Programs/               installed programs and games, strict leftover matching, uninstall, registry keys
  SmartFinds/             junk rules, Steam/Epic/GOG readers, installed-software inventory, leftover finder
  Analysis/               file types, treemap layout, saved scans, scan comparison
src/DiskSweeper.App       WPF app (Windows light/dark theme)
src/DiskSweeper.Cli       dsweep command-line tool
tests/                    xUnit tests; destructive tests run only in throwaway folders under %TEMP%
build/Publish-Release.ps1 release exes, zips, and the installer
installer/                Inno Setup script (Start menu shortcut and uninstaller)
.github/workflows/ci.yml  build, test, and the GitHub Release
```

## Build from source

Developers need Windows 10 or 11 and the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```
dotnet build
dotnet test
dotnet run --project src/DiskSweeper.App
```

Run `dotnet test` from an **administrator** terminal to include the admin-only tests (fast scan, take
ownership). From a normal terminal they're reported as skipped.

`dotnet run` uses the SDK on your machine. Release builds bundle the runtime. To produce that executable locally:

```
dotnet publish src/DiskSweeper.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o publish
```

`build/Publish-Release.ps1` builds the app and `dsweep` for win-x64 and win-arm64, zips them, and compiles
`DiskSweeper-Setup.exe`. The installer script needs [Inno Setup 7](https://jrsoftware.org/isdl.php). The
script downloads it when `ISCC.exe` isn't already installed.

### Publishing a release

Pushes to `main` and pull requests build and test. A pull request also uploads the installers as a
workflow artifact. Pushing a version tag, once this workflow is on `main`, publishes the download:

```
git checkout main
git pull
git tag -a v1.0.0 -m "DiskSweeper 1.0.0"
git push origin v1.0.0
```

The tag is `v` plus `major.minor.patch` (a hyphen, as in `v1.0.0-rc.1`, marks a prerelease). GitHub Actions
attaches the executables, the zips, the installer, and `SHA256SUMS.txt` to a GitHub Release and fills in
the notes from the commits. To build those files without a release, open the **CI** workflow and choose
**Run workflow**.

## Good to know

- Recycled items still use space until the Recycle Bin is emptied (Cleanup lists the bin).
- A scan of 1.5 million files uses about 500 MB of memory while the app is open.
- App data (log, settings, the last five scans per drive) is in `%LOCALAPPDATA%\DiskSweeper`.
- "Delete on next restart" hasn't been exercised on a real reboot during development. It uses the standard
  Windows mechanism (`MoveFileEx` with delay-until-reboot).

## License

[MIT](LICENSE). Copyright (c) 2026 process631.

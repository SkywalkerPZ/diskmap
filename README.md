# DiskMap

DiskMap is a free, open-source disk space map for Windows. It shows where the space on a drive went, explains the things it recognizes, and removes only those things.

It sits between two kinds of tools. Space visualizers show everything but let you delete anything, with no explanation. Cleaners remove things blindly. DiskMap shows everything, explains what it can, and offers a Remove action only for items that match a known rule.

Requires Windows 10 (1809 or later) or Windows 11, x64. No installer: unzip and run `DiskMap.exe`. .NET does not need to be installed.

## What it does

- **Explore.** A treemap of the drive: nested boxes sized by the space used. Click a box to see what it is and how big it is. Sizes are allocated size on disk. Hardlinked files count once, and OneDrive cloud-only placeholders count as zero.
- **Clean up.** Recognized items grouped by category, with sizes, a label, and the consequence of removing each one. Safe and Rebuildable items can be ticked and removed together.
- **Quarantine.** Where removed files wait before they are deleted for good. You can restore items or empty it.
- **Settings.** The undo period and the administrator option.

The scan time and file count are always shown. After a removal, DiskMap measures the drive again and shows what was actually freed, or how much moved to quarantine.

## Only removes what it recognizes

Every box on the map can be inspected, but a Remove action exists only for items that match a rule. Anything unrecognized offers "Open in Explorer" and nothing else.

The rules are plain JSON files in `src/DiskMap.Core/Rules/data/`, one per item, so they are easy to review.

### Recognized items

| Item | Label | Consequence |
|---|---|---|
| Windows update leftovers (component store cleanup) | Safe | You can no longer roll back the last update |
| Temp folders (`%TEMP%`) | Safe | Apps recreate them |
| Temp folders (`C:\Windows\Temp`) | Safe | Apps recreate them |
| Browser caches (Chrome, Edge, Firefox) | Safe | Sites load a bit slower once |
| Hibernation file (`hiberfil.sys`) | Safe if you don't use hibernate | Hibernate and Fast Startup stop working |
| `node_modules`, `.gradle`, npm/pip/NuGet caches | Rebuildable | Comes back on the next install or build |
| .NET build output (`bin`, `obj`) | Rebuildable | Comes back on the next install or build |
| GPU and DirectX shader caches (NVIDIA, AMD, DirectX) | Safe | Games stutter a little on first launch |
| Old installers in Downloads (`.exe`, `.msi`, `.zip`, older than 90 days) | Check first | It might be your only copy |
| Recycle Bin | Already deleted | Emptying it is permanent |
| Crash dumps and Windows Error Reporting files (your profile) | Safe | They're only useful for debugging a past crash |
| Crash dumps and Windows Error Reporting files (system) | Safe | They're only useful for debugging a past crash |

`node_modules`, `.gradle`, `bin` and `obj` folders only match when a project file sits next to them (for example `package.json` or a `.csproj`).

## Before you trust it

DiskMap never touches:

- Program Files.
- Anything under `C:\Windows`, except the items named above (the component store through DISM, `Windows\Temp`, crash dumps).
- Other users' profiles. They appear on the map like any other folder, but no rule matches inside them. Per-user rules apply only to the user who started the app, even after an elevated relaunch.
- Files that don't match a rule.
- Whole user folders such as Documents, Desktop or Pictures.

It never follows junctions or symlinks, during the scan or during removal, and it skips files that are in use. Old installers are always "Check first" and are never part of a bulk removal. No setting turns these limits off.

## Undo

"Keep removed files so I can undo" has three values: Off, 3 days, or 7 days. The default is 3 days.

- Removed files are moved into a hidden `DiskMap Quarantine` folder at the root of the same drive, so the move is an instant rename. Each Windows account gets its own subfolder there that only it and administrators can open, and DiskMap refuses to use a quarantine folder whose permissions it didn't set itself. A small manifest records the original path, size and time, so Restore puts things back exactly.
- Quarantined files still take up space until they are deleted. The Quarantine tab shows the total and has an "Empty now" button.
- Expired items are deleted when the app starts. There is no background service.
- Each item keeps the undo period it was removed with. Turning undo Off (or shortening it) affects only new removals: items already in quarantine stay for their own 3 or 7 days, or until you press "Empty now".
- With undo Off, a removal deletes permanently, and its confirmation says so.

Three items can't be undone: the hibernation file, Windows update leftovers, and the Recycle Bin. They always ask for confirmation once, with a "This can't be undone" line, whatever the undo setting is. They are removed one at a time and are never part of a bulk removal.

## Administrator rights

DiskMap is one app with two modes. It starts without administrator rights.

- **Unelevated:** it scans with a normal folder walk, which is slower but works on any drive. Only your own user's items are available. Items that need administrator rights are shown greyed out with "Needs administrator", never hidden.
- **Elevated:** a banner offers "Restart as administrator for the fast scan and system items". Elevated, DiskMap reads the NTFS file table (MFT) for a much faster scan, and the Windows update, hibernation file, `C:\Windows\Temp` and system crash dump items become available. Drives that aren't NTFS always use the folder walk.

A setting, "Always start as administrator", remembers your choice.

Windows update leftovers are removed with `DISM /Online /Cleanup-Image /StartComponentCleanup /ResetBase`, which can take 10 minutes or more. Its size is shown as an estimate, from `DISM /Online /Cleanup-Image /AnalyzeComponentStore`, because Windows shares many of these files. The real amount freed is shown afterwards. The hibernation file is removed with `powercfg /hibernate off`.

## No network

DiskMap makes no network connections. There is no telemetry, no account and no update check. Settings are stored in `%LOCALAPPDATA%\DiskMap\settings.json`.

## Download

Releases are a portable zip, `DiskMap-<version>-win-x64.zip`, on the GitHub Releases page of https://github.com/SkywalkerPZ/diskmap. The app is not code-signed yet, so Windows SmartScreen may warn on first run.

## Build from source

You need the .NET 10 SDK (see `global.json`) and Windows.

```powershell
git clone https://github.com/SkywalkerPZ/diskmap
cd diskmap
dotnet test tests/DiskMap.Core.Tests
./build/publish.ps1
```

`publish.ps1` publishes a self-contained win-x64 build to `artifacts\DiskMap` and zips it to `artifacts\DiskMap-<version>-win-x64.zip`.

Example: if your C: drive is nearly full, as the author's was, put the SDK and caches on another drive before building:

```powershell
$env:NUGET_PACKAGES  = 'D:\nuget\packages'
$env:DOTNET_CLI_HOME = 'D:\dotnet-home'
```

`publish.ps1` sets these two to the values above if they aren't already set, and uses `D:\dotnet\dotnet.exe` when `dotnet` on your PATH isn't .NET 10. Edit the script if your layout differs.

Tests use temporary fixture trees and never touch real user folders. The DISM and powercfg removals should only be tried in a throwaway Windows Sandbox or VM.

## License

MIT. See `LICENSE`.

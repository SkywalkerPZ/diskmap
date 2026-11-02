# DiskMap: design

Date: 2026-11-02. Status: draft for approval. "DiskMap" is a working name.

## Intent

A free, open-source Windows disk space tool that people can trust. It shows
where the space on a drive went, as a map, and lets people remove only the
things it recognizes and can explain in plain words.

Why: the author's own C: drive has been nearly full for weeks (2.5 GB free of
239 GB) and they don't know what is filling it. Existing tools split into two
camps. Space visualizers (WizTree, WinDirStat, TreeSize) show everything but
let you delete anything, with no explanation. Junk cleaners (CCleaner,
BleachBit, Disk Cleanup) clean blindly, and some have lost people's trust. This
app sits between them: see everything, understand it, and remove only what is
known to be safe or rebuildable.

It will be released publicly on GitHub, for free. It is not sold. It should
still look and feel like a finished, professional Windows app, not a script.

## Goals

- Scan a drive in seconds and show a map of where the space went.
- Explain any recognized item in plain words: what it is, how big it is,
  whether it is safe to remove, and what the consequence is.
- Make removal safe by default: undo is on unless the user turns it off.
- Look like a native Windows 11 app (Mica/acrylic, rounded corners, light
  and dark theme following the system).
- Be trustworthy enough for strangers to run: open source, predictable, and
  it never touches anything it can't explain.

## Non-goals (version 1)

- No "clean my PC" one-click optimizer, registry cleaning, startup manager,
  RAM booster, or similar.
- No deleting of unrecognized files or folders from inside the app.
- No duplicate-file finder.
- No scheduled or background cleaning.
- No telemetry, accounts, or network access of any kind (an update check can
  come later, and only as an opt-in).
- No macOS or Linux.
- No payment, licensing, or ads.

## Anti-goals (it fails if any of these happen, even if it works)

- It deletes something a user needed, without a clear warning that matched
  what actually happened.
- It looks or behaves like a scareware "PC cleaner" (alarming red counters,
  "your PC is at risk", nagging).
- The size it promised to free and the size actually freed don't match, with
  no explanation.
- It feels slow or janky next to WizTree.

## Constraints

- Platform: Windows 10 (1809+) and Windows 11, x64. Arm64 is nice to have.
- Language and UI: C# with WinUI 3 (Windows App SDK). Decided by the user.
- The developer PC has 2.5 GB free on C:. All SDKs, NuGet packages, build
  output, and caches must live on D:. Set `NUGET_PACKAGES` to a folder on D:.
  Installed today: .NET SDK 8.0.201. (.NET 8 support ends this month,
  November 2026, so the builder should install the current LTS SDK to D:
  using `dotnet-install.ps1 -InstallDir`. Builder's call on the exact version.)
- Repository: `D:\diskmap`, its own git repo, going to GitHub later.
- License: open source. Builder's call: MIT.

## Approaches considered

| Approach | Trade-off |
|---|---|
| **C# + WinUI 3 (chosen)** | The native Windows 11 look, which builds trust. C# is fast enough to read the NTFS file table directly, and memory-safe for code that deletes files. Windows only, which is fine for this app. |
| Tauri (Rust engine + web UI) | Tiny download and very fast, and the UI would use web tech the user already knows. But it's two languages, and it looks like a website in a window. |
| C++ + Qt | The fastest scanning, but much harder to make look like a native Windows 11 app, the slowest to build, and riskier code for deleting files. |

The user picked C# + WinUI 3 because a native look makes a cleanup tool feel
trustworthy, and raw speed beyond C# isn't needed (the disk is the bottleneck).

## What the user decided

1. **Map first.** The app opens on a map of the drive (a treemap: nested
   boxes sized by space used). Clicking a box selects it and shows a details
   panel. Other screens are tabs: Explore (the map, the default), Clean up
   (recognized items grouped by category), Quarantine, and Settings.
2. **Remove only what it recognizes.** Every box can be inspected, but a
   Remove action exists only for items that match a recognition rule.
   Unrecognized items get "Open in Explorer" and nothing else.
3. **Undo is a setting.** "Keep removed files so I can undo": Off / 3 days /
   7 days. The default is 3 days. Removed items move to a quarantine and are
   deleted for good when their time is up. The Quarantine tab shows the total
   waiting to be deleted, with an "Empty now" button, plus per-item restore.
   The UI must say clearly that quarantined files still take up space until
   they are emptied.
4. **Items that can't be undone** (the hibernation file, Windows update
   leftovers, emptying the Recycle Bin) always ask for confirmation once,
   with a "This can't be undone" line, whatever the setting is.
5. **Version 1 recognition list:**

| Item | Label | Consequence shown to the user |
|---|---|---|
| Windows update leftovers (component store cleanup) | Safe | You can no longer roll back the last update |
| Temp folders (`%TEMP%`, `C:\Windows\Temp`) | Safe | Apps recreate them |
| Browser caches (Chrome, Edge, Firefox) | Safe | Sites load a bit slower once |
| Hibernation file (`hiberfil.sys`) | Safe if you don't use hibernate | Hibernate and Fast Startup stop working |
| `node_modules`, .NET `bin`/`obj`, `.gradle`, npm/pip/NuGet caches | Rebuildable | Comes back on the next install or build |
| GPU and DirectX shader caches (NVIDIA, AMD, DirectX) | Safe | Games stutter a little on first launch |
| Old installers in Downloads (`.exe`, `.msi`, `.zip`, older than 90 days) | Check first | It might be your only copy |
| Recycle Bin | Already deleted | Emptying it is permanent |
| Crash dumps and Windows Error Reporting files | Safe | They're only useful for debugging a past crash |

## My calls (the user hasn't confirmed these; check them)

- **C: is scanned first**, other drives are a dropdown. The scan time and file
  count are always shown ("1.2M files in 3.4 s"), because speed is part of the
  appeal.
- **Fast scan through the NTFS file table** (the MFT), the way WizTree does.
  It needs administrator rights. Without admin, or on non-NTFS drives, the app
  falls back to a normal folder walk that is slower but works. The app asks
  for admin once at start, with a plain explanation, rather than always
  running elevated.
- **Removal goes through the right tool for each item.** Normal files and
  folders are moved into quarantine. Windows update leftovers use
  `DISM /Online /Cleanup-Image /StartComponentCleanup /ResetBase` (the
  `/ResetBase` part is what removes rollback, which matches the warning text).
  It can run for 10 minutes or more, so the UI treats it as a long operation
  with progress and keeps the app usable. Its size is never shown as exact:
  the details panel shows "about X–Y GB (estimate)" from
  `DISM /AnalyzeComponentStore` (run on demand, since it takes minutes), with
  one line explaining that Windows shares many of these files, so the exact
  amount is only known afterwards. After cleanup, the real freed amount is
  shown. The map's raw WinSxS folder size is not used as the promise. The hibernation file
  uses `powercfg /hibernate off`. The Recycle Bin uses the shell API. Each
  rule declares its own removal method and whether it can be undone.
- **Quarantine lives on the same drive** as the item (for example
  `C:\DiskMap Quarantine\`, hidden), so moving into it is an instant rename,
  not a copy. It keeps a small manifest (original path, size, time removed)
  so restore puts things back exactly. Expiry is checked when the app starts.
  There is no background service.
- **Recognition rules are data, not code**: one JSON (or similar) file per
  rule, shipped with the app, holding the match pattern, label, explanation
  text, removal method, and whether undo is possible. This makes rules easy
  to review in pull requests, which matters for an open-source tool where
  trust is the product.
- **Safety rails no setting can turn off**: never touch anything under
  `C:\Windows` except named rules (WinSxS through DISM, `Windows\Temp`), never
  touch Program Files, never follow junctions or symlinks while removing,
  skip files that are in use, and never remove a whole user folder
  (Documents, Desktop, Pictures and so on). Old installers are only ever
  "Check first" and are never part of a bulk action.
- **The Clean up tab** lists the recognized categories with total sizes and
  labels. You can tick and remove items there in bulk, but only "Safe" and
  "Rebuildable" ones can be bulk-selected.
- **Freed space is verified**: after a removal the app re-measures the drive
  and shows what was actually freed, or "X GB moved to quarantine" when undo
  is on.
- **English only** for version 1, with strings kept in resource files so
  translations can come later.
- **Distribution: unpackaged app, portable zip only for version 1.** An MSIX
  can't be installed without a trusted signature, and a self-signed one makes
  strangers import a certificate as admin, which is a bad first impression for
  a trust tool. So version 1 is an unpackaged, self-contained WinUI 3 app
  (Windows App SDK bundled), shipped as a zip on GitHub Releases. Settings and
  the quarantine manifest are plain files (settings under
  `%LOCALAPPDATA%\DiskMap`). Code signing, an installer, Microsoft Store, and
  winget come later.
- **Admin rights: one app, two modes.** The app starts unelevated and works
  without admin rights: it does a folder-walk scan, and only the current
  user's rules are available. A banner offers "Restart as administrator for
  the fast scan and system items", which relaunches the same app elevated.
  A setting, "Always start as administrator", remembers that choice. Items
  that need admin (Windows update leftovers, the hibernation file,
  `C:\Windows\Temp`, crash dumps under `C:\Windows`) are shown greyed out
  with "Needs administrator" when unelevated, never hidden. There is no
  separate helper process in version 1.
- **Only the user who launched the app.** Per-user rules (temp, browser
  caches, Downloads, Recycle Bin, dev caches under the profile) resolve
  against the user who originally started the app, even when the elevated
  relaunch runs under a different admin account. The unelevated instance
  passes its user's SID and profile path to the elevated relaunch. Other
  profiles on the PC appear on the map like any other folder, but no rule
  ever matches inside them, so they can't be removed.
- **Nothing that can't be undone is ever part of a bulk removal.** Bulk
  selection in Clean up is limited to Safe and Rebuildable items that go
  through quarantine. Items that can't be undone are removed one at a time,
  each with its own confirmation.
- **Sizes are allocated size on disk**, counting hardlinked files once and
  OneDrive cloud-only placeholders as zero, so the map and "you can free"
  numbers match what removal really frees.

## Left to the builder

- Solution and project layout, MVVM toolkit choice, and testing framework.
- How the treemap is drawn (Win2D or composition) and how it stays smooth
  with millions of files (for example, aggregating small items below a pixel
  threshold).
- MFT parsing approach (raw parsing or `FSCTL_ENUM_USN_DATA` plus sizes)
  and the folder-walk fallback.
- Exact rule file format and rule matching engine.
- Exact quarantine folder name and manifest format.
- Visual details within the WinUI 3 / Fluent look.
- How to test removal safely: never against the developer's real user
  folders. Use temp fixture trees and a test VM or Windows Sandbox for the
  DISM and powercfg rules.

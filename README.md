<div align="center">

<img src="src/DeskCaddy.App/assets/logo.png" alt="DeskCaddy" width="96"/>

# DeskCaddy

**A macOS-style dock that truly declutters your Windows desktop.**

[![License: GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-blue.svg)](LICENSE)
[![Release](https://img.shields.io/github/v/release/verlpro228/DeskCaddy)](https://github.com/verlpro228/DeskCaddy/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/verlpro228/DeskCaddy/total)](https://github.com/verlpro228/DeskCaddy/releases)
[![Platform](https://img.shields.io/badge/platform-Windows%2011%2022H2%2B-0078D6)](#requirements)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![Third-party packages](https://img.shields.io/badge/dependencies-0-brightgreen)](THIRD-PARTY-NOTICES.md)

English | [简体中文](README.zh-CN.md)

</div>

---

DeskCaddy **moves** the shortcuts littering your desktop into a managed library, then gives
them back to you as a frosted-glass dock pinned to the bottom of your screen — plus a
Launchpad-style panel with search.

Unlike tools that simply hide desktop icons, shortcuts collected by DeskCaddy **physically
leave the desktop** (they are gone from Explorer too) — and every single move is journaled
and reversible.

## Screenshots

**The dock, pinned to the bottom of the desktop:**

![Dock](docs/images/screenshot-dock.png)

**The menu panel (click the grid icon on the left of the dock):**

| Menu panel | Settings |
|---|---|
| ![Menu panel](docs/images/screenshot-launcher.png) | ![Settings](docs/images/screenshot-settings.png) |

## Features

- **Bottom dock** — frosted-glass backdrop (real screen capture + gaussian blur), pill shape
  with capsule corners, hover magnification with a soft highlight, single-click launch.
- **Menu panel** — a Launchpad-style grid of every collected item, with instant search,
  Enter-to-launch, and a configurable double-click mode.
- **Native context menus** — right-click an item in the panel to get the same menu Explorer
  shows, including your installed shell extensions.
- **Drag & drop, both ways** — drag items out to the desktop or any folder (real move);
  drag items within the dock/panel to reorder; drag shortcuts, files or folders **onto** the
  dock to collect them.
- **Safe by design** — every move is recorded (source path + SHA-256), every item can be
  restored individually or in bulk, and removing a "dead" shortcut requires multiple layers
  of verification first.
- **Settings console** — startup with Windows, icon size, hover scale, dock item count,
  menu panel columns and icon size, single/double-click mode.
- **System tray** — toggle items, open the menu panel, open settings, exit.

## Requirements

- Windows 11 22H2 (build 22621) or later, x64
- Nothing else — the published release is **self-contained** (the .NET runtime is bundled)

## Installation

**Grab the latest release:** [**DeskCaddy 1.0.1**](https://github.com/verlpro228/DeskCaddy/releases/latest)
→ `DeskCaddy-1.0.1-win-x64.zip` (58 MB)

1. Download the zip
2. **Verify it** (optional, but recommended — the binary is not code-signed yet):

   ```powershell
   # hash of the file you downloaded
   Get-FileHash .\DeskCaddy-1.0.1-win-x64.zip -Algorithm SHA256

   # the checksum published with the release
   Get-Content .\SHA256SUMS.txt
   ```

   The two hashes must be identical.
3. Extract the zip anywhere you like and run `DeskCaddy.exe`
4. Drag shortcuts from your desktop onto the dock — your desktop is now clean

To restore everything back to the desktop: tray icon → settings → **Restore all**.

> **SmartScreen:** the executable is unsigned, so Windows may warn on first run.
> Choose *More info → Run anyway*. If you are not sure, check the SHA-256 first.

## How it works

```
Desktop shortcut (.lnk)
        │  real file move (rename on the same volume)
        ▼
%LocalAppData%\DeskCaddy\collected\
        │  index + journal updated
        ▼
Dock / Menu panel
```

Safety model:

- The app always runs as `asInvoker` and **never requests administrator rights**
- Every item passes a whitelist gate before entering the library: no reparse points
  (symlinks/junctions), no system files, no reserved names (`desktop.ini`, `thumbs.db`),
  and the item must live directly in your desktop folder
- Every move is appended to an append-only journal (source, destination, SHA-256, size, time)
- Deleting a shortcut that fails to launch requires it to pass five verification layers first,
  and even then it goes to a **quarantine folder** instead of being deleted outright
- The file system is the single source of truth — if DeskCaddy disappears tomorrow, every
  shortcut is still on disk in `collected\`, ready to be moved back by hand

## Data locations

```
%LocalAppData%\DeskCaddy\
├── collected\      the actual moved shortcuts / files / folders
├── state.json      index (name, origin, sort order)
├── settings.json   UI settings
├── journal\        append-only move journal (JSONL)
├── quarantine\     removed shortcuts land here first, never hard-deleted
├── cache\          extracted icons
└── logs\           app log
```

Uninstalling or deleting DeskCaddy **never touches** these files.

## Command line

A CLI ships with the app for scriptable use (no UI required):

```powershell
cd src\DeskCaddy.Cli\bin\Release\net10.0-windows

.\DeskCaddy.Cli.exe scan                  # read-only scan of the desktop
.\DeskCaddy.Cli.exe status                # show the library
.\DeskCaddy.Cli.exe collect <path>        # collect one item (dry run)
.\DeskCaddy.Cli.exe collect <path> --apply
.\DeskCaddy.Cli.exe collect-all           # batch-collect every shortcut (dry run)
.\DeskCaddy.Cli.exe restore <name> --apply
.\DeskCaddy.Cli.exe restore-all --apply
.\DeskCaddy.Cli.exe reconcile --apply     # align index with the file system
.\DeskCaddy.Cli.exe selftest 100          # isolated end-to-end test
```

All destructive operations are **dry runs by default** — nothing moves unless you
explicitly pass `--apply`.

## Building from source

- Windows 11 22H2+
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

```powershell
git clone https://github.com/verlpro228/DeskCaddy.git
cd DeskCaddy
dotnet build DeskCaddy.slnx -c Release

# run the app
.\src\DeskCaddy.App\bin\Release\net10.0-windows\DeskCaddy.exe

# run the self-test (isolated temp directories, safe)
dotnet run --project src\DeskCaddy.Cli -- selftest 100
```

The solution has three projects:

| Project | Purpose |
|---|---|
| `DeskCaddy.Core` | Engine: scanning, moving, journal, index, Shell interop. Zero UI dependencies |
| `DeskCaddy.Cli` | Command-line interface over the engine |
| `DeskCaddy.App` | WPF front-end (dock, menu panel, settings, tray) |

## Roadmap

- [x] Portable release — [v1.0.1](https://github.com/verlpro228/DeskCaddy/releases/tag/v1.0.1) (self-contained zip + SHA-256)
- [ ] Installer (Inno Setup script exists in `build/`, not yet validated end-to-end)
- [ ] Code signing via [SignPath Foundation](https://signpath.org/)
- [ ] Grouping in the menu panel
- [ ] First-run wizard (scan desktop → propose what to collect)
- [ ] Mixed-DPI multi-monitor positioning

## Contributing

Issues and pull requests are welcome. By submitting a patch you certify that you wrote it
or have the right to submit it under GPL-3.0 (DCO): commit with `git commit -s` to add your
`Signed-off-by` line.

Please keep the zero-third-party-dependency policy — bug fixes and features that only need
the .NET base class library are the easiest to accept.

## License

Copyright © 2026 DeskCaddy contributors.

This project is licensed under the **GNU General Public License v3.0** — see
[LICENSE](LICENSE) for the full text.

In short: you may use, modify and distribute it (including commercially), but derived works
must also be licensed under GPL-3.0 and ship with their source code.

## Trademark

**The GPL-3.0 license covers the code — it does not cover the name or the logo.**

- Distributing DeskCaddy **unmodified**: you may keep the name and icon; attribution is appreciated
- Distributing a **modified** version: you must rename it and replace the icon, and must not
  imply endorsement by this project
- The name "DeskCaddy" and everything in `src/DeskCaddy.App/assets/` are **not** licensed
  under GPL-3.0

This clause does not restrict anything you do with the source code — it only prevents
redistributing forks under the "DeskCaddy" brand.

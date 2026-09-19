<p align="center">
  <img src="docs/logo.png" alt="" width="104" height="104">
</p>

<h1 align="center">Shelf</h1>

<p align="center">
  <strong>A Downloads stack for your desktop.</strong><br>
  Drag files out of your Downloads folder without opening Explorer.
</p>

<p align="center">
  <img alt="Windows" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4">
  <img alt=".NET Framework 4.8" src="https://img.shields.io/badge/.NET%20Framework-4.8-512BD4">
  <img alt="No dependencies" src="https://img.shields.io/badge/dependencies-none-success">
  <img alt="MIT" src="https://img.shields.io/badge/license-MIT-blue">
</p>

---

Windows 11 removed taskbar toolbars. That quiet change killed the workflow where
you pinned your Downloads folder to the taskbar and dragged files straight out
of it into whatever app needed them. Shelf brings it back.

A small pill sits above your taskbar. Click it, and a grid of your recent
downloads appears. Drag a file onto Teams, into a browser upload dialog, or
anywhere else that accepts files. The whole point is that drag.

## Features

- **Drag-out** — grab a file and drop it wherever you need it
- **Thumbnails** — see what each file is, not just its name
- **Trash tile** — drop files onto it to send them to the Recycle Bin
- **Context menu** — Open, Show in folder, Copy, Delete
- **Badge** — the pill shows how many recent files are waiting
- **Bounce** — a new download nudges the pill so you know it landed
- **Movable** — drag the pill anywhere on screen; the position is saved
- **Tray icon** — right-click for quick access to the folder or to quit
- **Nothing heavy** — one small exe, no installer, no browser, no Electron

## Install

Download `shelf.exe` from [Releases](../../releases), put it somewhere permanent,
and run it. A pill appears near the bottom-right corner of your screen.

To launch at startup, right-click the tray icon and check **Start with Windows**.

## Build

```powershell
.\build.ps1
```

No SDK and no NuGet. It compiles with the C# compiler that ships in Windows
(`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`) against .NET
Framework 4.8, which is present on every Windows 10 and 11 install.

| Script | Purpose |
|---|---|
| `build.ps1` | Compiles `bin\shelf.exe` |
| `tools\make-icon.ps1` | Generates `assets\shelf.ico` from code |

## Usage

| Action | Result |
|---|---|
| Click the pill | Opens the stack popup |
| Drag a tile | Starts a file drag you can drop anywhere |
| Drop on the trash tile | Sends the file to the Recycle Bin |
| Right-click a tile | Context menu: Open, Show in folder, Copy, Delete |
| Click a tile | Opens the file |
| Click the trash tile | Opens the Recycle Bin |
| Esc, or click away | Closes the popup |
| Drag the pill | Moves it; the position is saved |

## Configuration

Settings live in `%APPDATA%\Shelf\config.json`:

```json
{
  "FolderPath": "",
  "MaxItems": 12,
  "TileSize": 96,
  "DockX": -1,
  "DockY": -1,
  "StartWithWindows": false
}
```

| Key | Meaning |
|---|---|
| `FolderPath` | Folder to watch. Empty means the system Downloads folder. |
| `MaxItems` | How many files to show (1-100). |
| `TileSize` | Tile size in logical pixels at 100% DPI (48-256). |
| `DockX`, `DockY` | Saved pill position. -1 means default bottom-right. |
| `StartWithWindows` | Whether the app launches at login. |

## Known limits

- **No AppBar reservation** — the pill floats above other windows but cannot
  reserve screen edge space the way a real shell AppBar would. Maximised windows
  can cover it.

- **Default drag image** — the drag cursor is the standard Windows file-drag
  image, not a live thumbnail of the file being dragged.

- **Single folder** — watches one folder. If you want a second stack for a
  different folder, run a second copy with a different config.

## Debugging

Unhandled errors append to `%APPDATA%\Shelf\error.log`.

## License

MIT

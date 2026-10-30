<div align="center">
  <img src="src/TaskbarGrouper/Assets/app_icon.png" width="96" height="96" alt="StackBar Icon" />
  <h1>StackBar</h1>
  <p><strong>Native macOS &amp; iOS-style Taskbar Folders for Windows 10 &amp; 11</strong></p>
</div>

---

**StackBar** declutters your Windows taskbar by grouping rarely-used or categorized applications (*Gaming*, *AI & Dev*, *Creative*, *Web*) into sleek pop-up folders anchored directly above your taskbar icons.

## ✨ Highlights

- **Zero Runtime Dependencies**: Re-targeted to **.NET Framework 4.8** (pre-installed on every Windows 10 1903+ and Windows 11 machine). Runs directly out of the box without requiring the .NET Desktop Runtime or any external redistributables.
- **Single Standalone Executable**: Entire application, portable runtime, and installer wizard are packaged into a single ~600 KB binary with no auxiliary DLLs.
- **Portable Edition Supported**: Runs completely self-contained from a folder or USB drive with isolated `./data/` storage, leaving zero registry or AppData footprint. Can run simultaneously alongside an installed copy without IPC collisions.
- **Separate Pinned Taskbar Folders**: Generates custom `.lnk` shortcuts per category with distinct `AppUserModelID`s (`StackBar.Group.<id>`), allowing you to pin multiple StackBar folders side-by-side on the taskbar.
- **Persistent Shortcut Storage**: Shortcuts imported from the pinned taskbar are duplicated to `%AppData%\StackBar\Links` (or `./data/Links` in portable mode) to prevent broken links when unpinning items from Windows.
- **Folder Navigation & Drill-Down**: Drag and drop real folders into a stack. Clicking a folder drills down directly inside the flyout popup, displaying its subfolders and files for instant launching.
- **Drag-and-Drop Reordering**: Drag items within the Studio list to reorder them freely. The top 4 items automatically form the 2×2 folder icon.
- **Dual Icon Engine**:
  - **iOS 2×2 Mini-Grid**: Automatically composites the first 4 apps inside a folder into a version-hashed 256×256 squircle `.ico` (bypassing Windows taskbar icon cache stalls).
  - **Emoji Badge Icons**: Choose crisp category emoji glyphs with customizable accent color borders.
- **Background Tray Daemon**: Sits in the Windows System Tray and listens on a user-scoped named pipe with foreground activation tokens. Rapid clicking toggles the flyout open/closed.
- **Multi-Source Importer**:
  - Pinned Taskbar icons (`.lnk` & `.url`)
  - `shell:AppsFolder` (Start Menu apps, Windows Store / UWP apps like Minecraft Launcher, Calculator)
  - Desktop shortcuts (including Steam `.url` game shortcuts)
- **Keyboard & Admin Shortcuts**:
  - Number keys `1`–`9` to launch apps instantly
  - `Ctrl + Shift + Click` or `Ctrl + Shift + 1..9` to Run as Administrator

---

## 📦 Building & Installation

### Option 1: Run the Standalone Setup
Run `StackBar-Setup.exe` from the `artifacts\` folder:
- Custom installation directory picker (defaults to `%LocalAppData%\Programs\StackBar`)
- Detects existing installations and switches to Update / Reinstall mode
- Creates Desktop & Start Menu shortcuts
- Configures background tray startup on login
- Registers in Windows *Installed Apps* with an automated uninstaller

### Option 2: Portable Edition (No Install)
Extract `StackBar-Portable.zip` (or run `StackBar-Portable.exe` in `artifacts\StackBar-Portable\`):
- Runs entirely out of its local folder with zero installation.
- Saves all configs, copied shortcuts, and icons in a local `./data/` directory.

### Option 3: Build from Source
Requires the .NET SDK (Windows).
```powershell
# Build StackBar.exe, StackBar-Setup.exe, and StackBar-Portable.zip to artifacts/
.\build.ps1
```

---

## 🗂️ Project Structure

```text
├── src/
│   └── TaskbarGrouper/             # Main StackBar Windows Desktop Project (.NET Framework 4.8)
│       ├── Assets/                 # Multi-res app_icon.ico & app_icon.png
│       ├── Common/                 # Polyfills & string utilities
│       ├── Models/                 # AppConfig, AppGroup, GroupItem
│       ├── Services/               # IconHelper, ShortcutService, IpcService, NativePositioningService, ConfigService, AppLogger
│       ├── Themes/                 # Dark.xaml (Dark theme scrollbars, checkboxes, controls)
│       └── Views/                  # MainWindow (Studio), FlyoutWindow, TaskbarImporterWindow, SetupWindow
├── artifacts/                      # Packaged standalone binaries
│   ├── StackBar.exe                # Standard installed app binary
│   ├── StackBar-Setup.exe          # Installer, Updater & Uninstaller wizard
│   ├── StackBar-Portable/          # Portable standalone directory (isolated ./data/)
│   └── StackBar-Portable.zip       # Zipped Portable distribution
├── docs/
│   ├── FIX_PLAN.md                 # Detailed issue breakdown & remediation roadmap
│   └── session_review.md           # Architectural session audit
├── build.ps1                       # Automated build & staging script
└── TaskbarGrouper.sln
```

<div align="center">
  <img src="src/TaskbarGrouper/Assets/app_icon.png" width="96" height="96" alt="StackBar Icon" />
  <h1>StackBar</h1>
  <p><strong>Native macOS &amp; iOS-style Taskbar Folders for Windows 10 &amp; 11</strong></p>
</div>

---

**StackBar** declutters your Windows taskbar by grouping rarely-used or categorized applications (*Gaming*, *AI & Dev*, *Creative*, *Web*) into sleek pop-up folders anchored directly above your taskbar icons.

## ✨ Highlights

- **Zero Runtime Dependencies**: Re-targeted to **.NET Framework 4.8** (pre-installed on every Windows 10 1903+ and Windows 11 machine). Runs directly out of the box without requiring the .NET Desktop Runtime or any external redistributables.
- **Single Standalone Executable**: Entire application and setup are packaged into a single ~580 KB binary with no auxiliary DLLs.
- **Separate Pinned Taskbar Folders**: Generates custom `.lnk` shortcuts per category with distinct `AppUserModelID`s (`StackBar.Group.<id>`), allowing you to pin multiple StackBar folders side-by-side on the taskbar.
- **Persistent Shortcut Storage**: Shortcuts imported from the pinned taskbar are duplicated to `%AppData%\StackBar\Links` to prevent broken links when unpinning items from Windows.
- **Dual Icon Engine**:
  - **iOS 2×2 Mini-Grid**: Automatically composites the first 4 apps inside a folder into a version-hashed 256×256 squircle `.ico` (bypassing Windows taskbar icon cache stalls).
  - **Emoji Badge Icons**: Choose crisp category emoji glyphs with customizable accent color borders.
- **Background Tray Daemon**: Sits in the Windows System Tray and listens on a user-scoped named pipe. Clicking a pinned shortcut delegates to the resident process with foreground window activation rights.
- **Multi-Source Importer**:
  - Pinned Taskbar icons (`.lnk` & `.url`)
  - `shell:AppsFolder` (Start Menu apps, Windows Store / UWP apps like Minecraft, Calculator)
  - Desktop shortcuts (including Steam `.url` game shortcuts)
- **Keyboard & Admin Shortcuts**:
  - Number keys `1`–`9` to launch apps instantly
  - `Ctrl + Shift + Click` or `Ctrl + Shift + 1..9` to Run as Administrator

---

## 📦 Building & Installation

### Option 1: Run the Standalone Setup
Run `StackBar-Setup.exe` from the `artifacts\` folder:
- Installs to `%LocalAppData%\Programs\StackBar\StackBar.exe`
- Creates Desktop & Start Menu shortcuts
- Configures background tray startup on login
- Registers in Windows *Installed Apps* with an automated uninstaller

### Option 2: Build from Source
Requires the .NET SDK (Windows).
```powershell
# Build both StackBar.exe and StackBar-Setup.exe to artifacts/
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
│       └── Views/                  # MainWindow (Studio), FlyoutWindow, TaskbarImporterWindow, SetupWindow
├── artifacts/                      # Packaged standalone binaries (StackBar.exe & StackBar-Setup.exe)
├── docs/
│   └── FIX_PLAN.md                 # Detailed issue breakdown & remediation roadmap
├── build.ps1                       # Automated build & staging script
└── TaskbarGrouper.sln
```

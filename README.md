<div align="center">
  <img src="src/TaskbarGrouper/Assets/app_icon.png" width="96" height="96" alt="StackBar Icon" />
  <h1>StackBar</h1>
  <p><strong>macOS &amp; iOS-style Taskbar Folders for Windows 11</strong></p>
</div>

---

**StackBar** declutters your Windows 11 taskbar by grouping rarely-used or categorized applications (*Gaming*, *AI & Dev*, *Creative*, *Utilities*) into sleek, dark-glassmorphic pop-up folders anchored directly to your taskbar.

## ✨ Key Features

- **📌 Separate Pinned Taskbar Folders**: Generate custom `.lnk` shortcuts per category with distinct `AppUserModelID`s so Windows 11 allows pinning multiple StackBar folders side-by-side on the taskbar.
- **🎨 Dual Taskbar Icon Engine**:
  - **iOS 2×2 Mini-Icon Grid**: Automatically composites the first 4 apps inside a folder into a 256×256 rounded dark-glass squircle `.ico` with live preview inside the Studio.
  - **Emoji Badge Icons**: Render crisp category emoji badges (`🎮`, `✨`, `🌐`, `🗂️`) with customizable accent borders.
- **🚀 < 15ms Instant-Open Daemon**: Runs silently in the Windows System Tray and listens over a Named Pipe (`StackBar_InstantFlyout_Pipe`) so clicking any pinned taskbar folder pops open the pre-warmed flyout instantaneously with zero cold-start lag.
- **⚡ Multi-Source Taskbar & System Importer**: One-click **Import From My Taskbar** scans:
  - Pinned Taskbar shortcuts (`.lnk` & `.url`)
  - Currently running Taskbar applications (*Steam*, *Minecraft*, *Discord*, etc.)
  - Desktop & Start Menu shortcuts (including Steam `.url` game shortcuts with custom icons)
- **⌨️ Power-User Flyout**:
  - Number hotkeys (`1`–`9`) to launch apps immediately (`Ctrl + 1..9` runs as Administrator)
  - Right-click context menu (*Launch*, *Run as Administrator*, *Open File Location*)
  - 6 folder accent color themes (**Ocean**, **Neon**, **Emerald**, **Amber**, **Crimson**, **Slate**)
  - Optional *Run in Tray at Windows Startup* toggle

---

## 📦 Installation & Building

### Option 1: Build the Single-File Setup Installer (`StackBar-Setup.exe`)

Run the included PowerShell packaging script from the repository root:

```powershell
.\build-installer.ps1
```

This compiles `StackBar` and packages it into a standalone graphical installer at **`installer\StackBar-Setup.exe`**, which installs StackBar to `%LocalAppData%\Programs\StackBar`, creates Desktop & Start Menu shortcuts, registers Windows Startup, and includes a built-in Uninstaller.

### Option 2: Build & Run from Source

Requires the [.NET 8.0 SDK (Windows)](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
# Build the solution
dotnet build

# Run StackBar Studio (and start the background Tray listener)
dotnet run --project src/TaskbarGrouper

# Open a specific Stack folder flyout directly via CLI
dotnet run --project src/TaskbarGrouper -- --group gaming
```

---

## 🗂️ Project Structure

```text
├── src/
│   ├── TaskbarGrouper/             # Main StackBar WPF + Win32 Desktop App
│   │   ├── Assets/                 # Multi-res app_icon.ico & app_icon.png
│   │   ├── Models/                 # GroupModels (AppConfig, AppGroup, GroupItem)
│   │   ├── Services/               # IconHelper, ShortcutService, IpcService, NativePositioningService, StartupService
│   │   └── Views/                  # MainWindow (Studio), FlyoutWindow (Popup), TaskbarImporterWindow
│   └── StackBar.Installer/         # Standalone GUI Setup & Uninstaller (StackBar-Setup.exe)
├── build-installer.ps1             # Automated build script producing installer/StackBar-Setup.exe
└── TaskbarGrouper.sln              # Visual Studio / .NET Solution
```

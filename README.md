<div align="center">
  <img src="src/TaskbarGrouper/Assets/app_icon.png" width="96" height="96" alt="StackBar Icon" />
  <h1>StackBar</h1>
  <p><strong>Native macOS &amp; iOS-style Taskbar Folders for Windows 10 &amp; 11</strong></p>
</div>

---

**StackBar** declutters your Windows taskbar by grouping rarely-used or categorized applications (*Gaming*, *AI & Dev*, *Creative*, *Web*) into sleek pop-up folders anchored directly above your taskbar icons.

## ✨ Highlights

- **Zero Runtime Dependencies**: Re-targeted to **.NET Framework 4.8** (pre-installed on every Windows 10 1903+ and Windows 11 machine). Runs directly out of the box without requiring the .NET Desktop Runtime or any external redistributables.
- **Single Standalone Executable**: Entire application, portable runtime, and installer wizard are packaged into a single ~650 KB binary with no auxiliary DLLs.
- **Portable Edition Supported**: Runs completely self-contained from a folder or USB drive with isolated `./data/` storage, leaving zero registry or AppData footprint. Can run simultaneously alongside an installed copy without IPC collisions.
- **Separate Pinned Taskbar Folders**: Generates custom `.lnk` shortcuts per category with distinct `AppUserModelID`s (`StackBar.Group.<id>`), allowing you to pin multiple StackBar folders side-by-side on the taskbar.
- **60-Color Curated Accent Palette**: Select from an expanded 60-color palette (organized into 6 harmonious color rows: Reds, Oranges, Ambers, Limes, Emeralds, Teals, Cyans, Blues, Purples, and Pinks) with live preview on the flyout mockup.
- **Automatic Pinned Taskbar Sync**: Modifying a stack's icon, accent color, or app order automatically updates the shortcut in Windows' `User Pinned\TaskBar` and flushes the shell icon cache via `SHChangeNotify`, eliminating the need to unpin and re-pin.
- **Stack Deletion Safety & Cleanup**: Deleting a stack prompts for confirmation, removes its generated shortcuts and icons, and asks Windows to unpin it from the taskbar.
  If Windows refuses the unpin, Studio tells you to unpin it by hand, and clicking the leftover pin explains that the stack no longer exists.
- **iOS "Jiggle Mode" (Hold to Reorder / Delete)**:
  - **Long-Press or Right-Click**: Hold any app icon for 450ms (or right-click → *Rearrange / Edit Apps*) to enter Jiggle Mode.
  - **Dynamic Wobble Animation**: Icons oscillate gently with smooth hardware-accelerated animations.
  - **Ghost Tile & Live Reordering**: Dragging a tile shows a semi-transparent ghost tile under the cursor, and the other tiles slide out of the way to preview the landing slot.
    The new order is saved when you release the tile; pressing `Escape` during the drag cancels it.
  - **Delete `(✕)` Badge**: Tap the red badge on any jiggling icon to remove it from the stack with a confirmation prompt.
  - **Done Button / Esc**: Tap *Done* or press `Escape` to leave Jiggle Mode.
- **Intelligent Store App & Friendly Name Resolution**: Resolves technical package IDs (`com.lencx`, `Claude_pzs...`, `Microsoft.WindowsTerminal...`) to clean localized application names, and strips internal hex prefixes from imported shortcuts.
- **Typo-Tolerant Application Search**: Importer search features letter-collapse tolerance (e.g. typing "whatsap" matches "WhatsApp") and matches against both application display names and target package paths.
- **Offline On-Demand Launching**: Pinned taskbar shortcuts work independently even when StackBar is not running in the background tray. Clicking any pinned icon cold-starts the app and opens the flyout; shortcut repair and link migration run after the flyout is shown.
- **In-Place Flyout Renaming**: Click the folder title or the `✏` pencil next to it to rename the stack inline.
  Save with the `✓` button or `Enter`, cancel with the `✕` button or `Esc`.
- **Studio Quick-Edit Button**: Click the `⚙` button in the flyout header to open that exact stack in StackBar Studio.
- **Triple Icon Engine**:
  - **Custom Cover Icons**: Pick any `.ico`, `.png`, `.jpg`, `.exe`, or `.dll` file to represent your stack, or click the **★** star button on any app in the list to make its icon the folder's cover.
  - **Visual Emoji Selector & Presets**: Pick from a curated 40+ emoji palette dropdown or trigger the native Windows Emoji Panel (`Win + .`) with one click.
  - **iOS 2×2 Mini-Grid**: Automatically composites the first 4 apps inside a folder into a version-hashed 256×256 squircle `.ico` (bypassing Windows taskbar icon cache stalls).
- **4-Stage Icon Recovery Pipeline**: Combines `IShellItemImageFactory`, target `.exe` binary resolution, and `SHGetFileInfo` so Adobe apps, Steam games, and Store apps keep their icons.
  Transparency is preserved (no black corners), and icons stored at a specific index inside a `.dll`/`.exe` (e.g. `shell32.dll,-16`) are honored.
- **Real Folder Shortcuts**: Add normal filesystem folders (e.g. *Projects*, *Downloads*) into any stack. Clicking a folder launches Windows Explorer directly at that directory with clean 32-bit transparent folder icons.
- **Pixel-Perfect Caret Alignment**: The flyout is positioned in physical pixels on the monitor you clicked, so the caret points at the clicked taskbar icon even on mixed-DPI multi-monitor setups.
  The caret flips for a top taskbar and is hidden for left/right taskbars.
- **Persistent Shortcut Storage**: Imported shortcuts are copied to `%AppData%\StackBar\Links\<id>\<App Name>.lnk` (or `./data/Links` in portable mode), so unpinning an app from Windows doesn't break its StackBar item.
  The copy keeps the app's real name, so pinning an app you started from a flyout gives a normally named taskbar pin.
  Re-importing an app that is already in the stack is skipped.
- **Background Tray Daemon**: Sits in the Windows System Tray and listens on a user-scoped named pipe with foreground activation tokens. Rapid clicking toggles the flyout open/closed.
- **Multi-Source Importer**:
  - Pinned Taskbar icons (`.lnk` & `.url`)
  - `shell:AppsFolder` (Start Menu apps, Windows Store / UWP apps like Minecraft Launcher, Calculator)
  - Desktop shortcuts (including Steam `.url` game shortcuts)
- **Keyboard & Admin Shortcuts**:
  - Number keys `1`–`9` to launch apps instantly
  - `Ctrl + Shift + Click` or `Ctrl + Shift + 1..9` to Run as Administrator

---

## 📦 Installation

**Requirements:** Windows 10 (1903 or later) or Windows 11. Nothing else to install.

Download the latest build from the [Releases page](https://github.com/ilivinskyi/StackBar/releases).

### Option 1: Installer
Run `StackBar-Setup.exe`:
- Custom installation directory picker (defaults to `%LocalAppData%\Programs\StackBar`)
- Detects existing installations and switches to Update / Reinstall mode
- Creates Desktop & Start Menu shortcuts
- Configures background tray startup on login
- Registers in Windows *Installed Apps* with an automated uninstaller (pinned StackBar folders have to be unpinned by hand after uninstalling)

### Option 2: Portable Edition (No Install)
Extract `StackBar-Portable.zip` and run `StackBar-Portable.exe`:
- Runs entirely out of its local folder with zero installation.
- Saves all configs, copied shortcuts, and icons in a local `./data/` directory.

### Option 3: Build from Source
Requires the .NET SDK (Windows). The first build needs network access to restore NuGet packages.
```powershell
# Build StackBar.exe, StackBar-Setup.exe, and StackBar-Portable.zip to artifacts/
dotnet restore TaskbarGrouper.sln
.\build.ps1

# Run the unit tests (test-only NuGet packages; nothing is added to the shipped exe)
dotnet test tests\StackBar.Tests\StackBar.Tests.csproj
```

---

## 🛠️ Troubleshooting & Bug Reports

- **Blank white icon on the taskbar:** this is usually a stale Windows icon cache (common right after an app updates).
  Run `ie4uinit.exe -show`; if the icon is still blank, unpin and re-pin the app.
- **Logs:** `%AppData%\StackBar\logs\stackbar.log` (portable: `data\logs\stackbar.log`).
  Please attach the log when [opening an issue](https://github.com/ilivinskyi/StackBar/issues).
- **Settings:** `%AppData%\StackBar\groups.json`. A corrupt file is kept as `groups.corrupt-<date>.json` and the last good backup is restored.

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
├── artifacts/                      # Created by build.ps1 (not committed)
│   ├── StackBar.exe                # Standard installed app binary
│   ├── StackBar-Setup.exe          # Installer, Updater & Uninstaller wizard
│   ├── StackBar-Portable/          # Portable standalone directory (isolated ./data/)
│   └── StackBar-Portable.zip       # Zipped Portable distribution
├── tests/
│   └── StackBar.Tests/             # xUnit tests (config persistence, shortcut matching, icon alpha)
├── build.ps1                       # Automated build & staging script
└── TaskbarGrouper.sln
```

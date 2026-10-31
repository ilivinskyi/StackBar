# StackBar Code Review: Improvements

**Scope:** the whole repository at commit `a00b899`. I read every file but did not build or run anything (Linux container; the project is .NET Framework 4.8 / WPF).
**What belongs here:** changes that make the app faster, more robust, easier to use or easier to ship. Defects are in `bugs.md`; restructuring with no behaviour change is in `refactoring.md`. Where an improvement also fixes a bug, the bug ID is given.

Priority:
- **P1:** do soon; users notice it, or it protects data
- **P2:** worthwhile
- **P3:** nice to have

Line numbers refer to commit `a00b899`.

---

## Performance and responsiveness

### I-01 · P1 · Move blocking shell, COM and disk work off the UI thread
The dispatcher thread currently does all of the following:

| Work | Where | Cost |
|---|---|---|
| Waiting for Explorer to unpin | `src/TaskbarGrouper/Services/ShortcutService.cs:278-281` (`Thread.Sleep(50)` × 20 per pinned file, from `DeleteGroup_Click`) | up to 1 s per pin, with Studio frozen |
| Startup maintenance (heal pins, migrations, possibly a full app scan) | `src/TaskbarGrouper/App.xaml.cs:85,90-104` | the UI freezes right after the first window appears (see B-07) |
| Pin rewrite + icon render + `SHChangeNotify` | every `SyncGroupAndTaskbarIcon` / `PersistGroupChange` | visible stutter on each click (see B-08) |
| Extracting every item's icon before the flyout is shown | `src/TaskbarGrouper/Views/FlyoutWindow.xaml.cs:199` | slows the open with a cold cache |
| `Directory.Exists(item.TargetPath)` per item | `FlyoutWindow.xaml.cs:187` | a folder item on an unreachable network share or UNC path can stall the open for many seconds |
| Rendering the group bitmap twice per change (preview + `.ico`) | `src/TaskbarGrouper/Services/IconHelper.cs:394,518` | about twice the needed work |

**Suggestion:**
- Add one long-lived background **STA** worker thread (the shell APIs `IShellItemImageFactory`, `Shell.Application` and `IShellLink` are safest on STA) with a work queue.
- Show the flyout immediately with placeholder icons and fill them in asynchronously.
- Trust `Kind == Folder` instead of calling `Directory.Exists` on every open.

### I-02 · P2 · Make the icon cache smarter
`IconHelper._iconCache` (`IconHelper.cs:87-114`) has three gaps:
- It never invalidates entries: after an app update the old icon stays until StackBar restarts, and the resident process runs for days.
- It is unbounded.
- It does **not** remember failures, so broken items run the whole slow four-stage extraction again on every flyout open.

Also, the group preview cache key covers only the first four items (`:380`), but rendering skips items whose icon fails and can use the fifth and later items (`:461`), so the key and the picture can disagree.

**Suggestion:**
- Key entries by path, index and the file's last-write time.
- Cache misses for a short time.
- Use a small LRU limit.
- Build the preview key from the same item list the renderer uses.

### I-03 · P2 · Trim the cold-start path ("click a pin when StackBar isn't running")
- `Program.Main` runs `AppLogger.Initialize()` (create a folder, stat a file, maybe roll the log) and `Process.GetCurrentProcess().MainModule` (enumerates loaded modules) on **every** pinned click, including the fast IPC path (`src/TaskbarGrouper/Program.cs:18,32`). Use `Assembly.GetEntryAssembly().Location` and create the logger lazily.
- `ConfigService.LoadOrCreateDefault` → `SanitizeConfig` can enumerate the whole AppsFolder before the first flyout appears (B-07).
- The IPC server starts only after loading the config and running migrations (`App.xaml.cs:59-69`). A second click during a slow cold start waits 1.5 s and then the command is silently dropped (`Program.cs:93`). Start the pipe listener first and queue requests until the UI is ready.

### I-04 · P3 · Importer: cache, cancel and reuse
- Every time the importer opens it rescans everything and extracts hundreds of icons (`src/TaskbarGrouper/Views/TaskbarImporterWindow.xaml.cs:53-64`).
- Closing the window does not cancel the scan.
- The scan runs on the thread pool (MTA). Some shell extensions misbehave there.

**Suggestion:** Cache the last scan for a few minutes, pass a `CancellationToken`, and run on the STA worker from I-01.

---

## Robustness and data safety

### I-05 · P1 · Repair shortcut copies when the original app updates
Imported shortcuts are snapshot copies in `Links\`. When an app updates its own Start Menu shortcut (new versioned install folder, "App 2024" becoming "App 2025", and so on), the copy points at a path that no longer exists, so the item breaks. `GroupItem.IsBroken` exists (`src/TaskbarGrouper/Models/GroupModels.cs:195`) but nothing uses it.

**Suggestion:**
- When an item's target is missing and `SourcePath` still exists, copy it again from the source.
- Otherwise, try to recover it by name or AUMID (the logic already exists in `MigrateLegacyPinnedTargets`).
- Show broken items as dimmed with a "Fix / Remove" action in the flyout and in Studio.

### I-06 · P1 · Keep the configuration safe and versioned
- `groups.json.bak` is simply the previous save (`src/TaskbarGrouper/Services/ConfigService.cs:170-180`). A bad but valid save, for example one that wipes items, overwrites the backup after two saves. Keep a few dated backups (e.g. one per day, last 5).
- Make `Save` report failure and tell the user (see B-06).
- Add a `schemaVersion` field so migrations (flat `Links`, legacy pinned targets, name cleanup) run once and are recorded, instead of being re-checked on every start.
- Enums are serialized as integers (`GroupModels.cs:8-28`), so reordering an enum would silently change saved data. Give each enum value an explicit number, or serialize enums as strings.

### I-07 · P2 · Handle unexpected errors and log properly
- Only `DispatcherUnhandledException` is handled (`App.xaml.cs:29-39`). Also add `AppDomain.CurrentDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`.
- Limit how often the error dialog can appear (repeated errors show one dialog each).
- Log full exceptions (with stack traces) instead of only `ex.Message` (`ConfigService.cs:188`; `ShortcutService.cs:239,286,664`).
- The log is rolled only at startup (`src/TaskbarGrouper/Services/AppInfo.cs:67-77`); a resident process can grow it without limit. Check the size when writing.

### I-08 · P2 · Harden the IPC pipe
In `src/TaskbarGrouper/Services/IpcService.cs`:
- Add a deny rule for the NETWORK SID (`S-1-5-2`) in the `PipeSecurity` (`:94-98`). .NET Framework has no `PipeOptions.CurrentUserOnly`.
- Add a read timeout and a maximum line length. A client that connects and never writes blocks the single listener forever (`:117-123`), and every later pin click then times out.
- Validate group IDs (e.g. `[A-Za-z0-9-]{1,32}`) and log rejected payloads.

### I-09 · P2 · A safer, clearer uninstaller
Besides fixing B-01:
- Try to unpin the user's stacks during uninstall (reuse `DeleteGroupShortcuts`), and only fall back to "unpin them by hand" when that fails.
- Also remove the legacy `%AppData%\TaskbarGrouper` folder when the user asks to delete data.
- Avoid launching hidden `powershell.exe -ExecutionPolicy Bypass` (`src/TaskbarGrouper/Views/SetupWindow.xaml.cs:233-242`); antivirus heuristics often flag that pattern. A temporary self-deleting `.cmd` or `cmd /c ping -n 2 127.0.0.1 >nul & del ...` deletes only known files without PowerShell.

### I-10 · P2 · Installer polish
- Set `System.AppUserModel.ID = "StackBar.App"` on the Start Menu and Desktop shortcuts. `Program.cs:23` gives the running process that ID; without it on the shortcut, a pinned Studio window and the Start Menu entry show up as separate apps.
- Check that the install folder is writable before stopping running instances.
- Reuse `StartupService` instead of writing the Run key separately (`SetupWindow.xaml.cs:89-95`).
- Install into a dedicated subfolder (B-01).

### I-11 · P3 · Taskbar geometry from the shell, not guessed
Use `SHAppBarMessage` (`ABM_GETTASKBARPOS`, `ABM_GETSTATE` for auto-hide) and the per-monitor taskbar windows to find the edge and taskbar rectangle. Support keyboard activation (Win+1…9) by anchoring to the taskbar instead of the cursor. Fixes B-17.

---

## User experience and features

### I-12 · P1 · Expose settings that exist but cannot be changed
`AppGroup.Columns` (used by the flyout, clamped to 2-8) and `AppGroup.CloseOnLaunch` are saved and respected, but Studio has **no control** for either; the only way to change them is to edit `groups.json` by hand. Add a column count control and a "Close after launching" checkbox. Also consider editing an item's `Arguments` and `WorkingDirectory`, which exist in the model but cannot be edited.

### I-13 · P2 · Studio item list
- Rows show the internal copy path (`%AppData%\StackBar\Links\<hex>\App.lnk`) (`src/TaskbarGrouper/Views/MainWindow.xaml:446`). Show the real target (exe or Store app name) and put the internal path in the tooltip.
- Every edit replaces `ItemsSource` (`src/TaskbarGrouper/Views/MainWindow.xaml.cs:455-459`), which resets scroll position, selection and focus. Use an `ObservableCollection` and `Move`/`Remove`.
- Removing an item in Studio has no confirmation and no undo (the flyout asks for confirmation). Add an undo toast or a confirmation.
- Dropping onto the empty area below the last row does nothing (`MainWindow.xaml.cs:798-799`). Treat it as "move to the end".

### I-14 · P2 · Flyout
- Accept files dragged from Explorer onto the flyout to add them; `RootBorder_DragOver` currently allows only the internal reorder format (`FlyoutWindow.xaml.cs:700-705`).
- Arrow-key navigation between tiles, with Enter to launch.
- Accessibility: give the icon-only buttons (⚙, ✏, ✓, ✕, delete badge) an `AutomationProperties.Name`, and the tiles a meaningful automation name.
- The *Done* and *Save* buttons use a fixed blue (`src/TaskbarGrouper/Views/FlyoutWindow.xaml:99,171`) instead of the stack's accent colour.

### I-15 · P2 · Importer
- Merge entries for the same app found in several places (pinned, AppsFolder, Start Menu, Desktop), keeping the best source. Today each app can appear up to three times.
- Show which apps are already in the current stack, and disable or pre-check them.
- Let the user sort or group by source, and remember the last search.
- Together these fix B-21.

### I-16 · P3 · Renaming a stack does not reach the pinned taskbar button
The pinned `.lnk` keeps its old file name and description (`"Gaming (StackBar)"`), which is what the taskbar tooltip and jump list show. Renaming the pinned file itself would break the pin, so at least update the shortcut's description, and tell the user in Studio that re-pinning refreshes the name (or offer a "Re-create shortcut" button).

### I-17 · P3 · Tray icon
- Single-click should open Studio (today only double-click works).
- `Icon.ExtractAssociatedIcon` returns a 32 px icon that is scaled down for the tray and looks blurry (`App.xaml.cs:170-178`). Load the 16/20/24 px frame from the embedded `.ico` instead.
- Add an "Open stack ▸" submenu.
- Add WinForms per-monitor DPI settings (an `app.config` `System.Windows.Forms.ApplicationConfigurationSection` with `DpiAwareness=PerMonitorV2`) so the tray menu is not bitmap-scaled on high-DPI screens.

### I-18 · P3 · Icon quality
- The generated `.ico` holds a single 256 px PNG frame (`IconHelper.cs:555-575`). Add 16, 20, 24, 32, 40, 48 and 64 px frames rendered at those sizes so the taskbar shows them crisply.
- Emoji covers are drawn with GDI+ `DrawString` (`IconHelper.cs:495-508`). That draws emoji in one colour (white), and as far as I know it does not combine multi-character emoji (variation selector or zero-width-joiner sequences such as 🛠️, ❤️, 🗂️ from the built-in palette). Test it, and consider DirectWrite or Direct2D rendering if colour emoji matter.
- Custom cover images are copied at full size (`ShortcutService.cs:1015-1023`). Downscale them to 256 px when imported. Support `.gif`, `.tif` and `.webp` through WIC (today they produce a generic file icon).
- Clearing a custom cover leaves its `custom_<id>_*.png` file behind (`MainWindow.xaml.cs:561-570`). Delete it, or clean up unreferenced cover files at startup.

---

## Engineering and release

### I-19 · P1 · Continuous integration
Nothing builds or tests the project automatically. Add a GitHub Actions workflow on `windows-latest` that runs `dotnet restore`, `build -c Release`, `dotnet test` and `build.ps1`, and uploads `artifacts/`. Make `build.ps1` stop on build errors (B-10) and optionally run the tests.

### I-20 · P2 · One source for the version number
The version is hard-coded in four places that already disagree with the assembly:
- `src/TaskbarGrouper/app.manifest:3` (`1.1.0.0`)
- `MainWindow.xaml:127` (`v1.1`)
- `MainWindow.xaml.cs:83` (`v1.1`)
- `SetupWindow.xaml.cs:104` (`1.1.0`)

The assembly version itself is the default `1.0.0.0`. Set `<Version>` in the csproj and read it at runtime (`AssemblyInformationalVersion`) everywhere.

### I-21 · P2 · Code signing
The single exe writes into the taskbar pin folder, edits the Run key and launches hidden PowerShell during uninstall. Unsigned, it will trigger SmartScreen and antivirus heuristics. Sign `StackBar.exe` and `StackBar-Setup.exe`.

### I-22 · P3 · Portable zip layout
`build.ps1:34` zips the *contents* of `StackBar-Portable\`, so "Extract here" spills the files into the current folder. Include the top-level folder (`CreateFromDirectory(..., includeBaseDirectory: true)`).

### I-23 · P2 · Test coverage
The current tests cover config persistence, shortcut path helpers and icon alpha handling. The risky logic has no tests:
- recognising pinned shortcuts and deciding when to heal them (B-02)
- portable-mode detection (B-05)
- the uninstall folder safety rules (B-01)
- parsing IPC messages
- the flyout position maths (extract a pure function first; see R-09 in `refactoring.md`)
- `ImportAndPersistShortcut` for each source type
- the migrations, including the "save failed" path (B-06)

`refactoring.md` (R-10) suggests the small seams that make these testable without touching the real `%AppData%` or taskbar.

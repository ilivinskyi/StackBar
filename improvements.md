# StackBar Code Review: Improvements

**Scope:** the whole repository at commit `aa43e0e`. I read every file but did not build or run anything (Linux container; the project is .NET Framework 4.8 / WPF).
**What belongs here:** changes that make the app faster, more robust, easier to use or easier to ship. Defects are in `bugs.md`; restructuring with no behaviour change is in `refactoring.md`. Where an improvement also fixes a bug, the bug ID is given.

**Changes since the previous review (`a00b899`):**
- I-01 now covers the new shortcut-repair work on the UI thread.
- I-05 is narrowed to what the new repair feature does not cover.
- I-24 to I-26 are new (backups, defaults and scope of the repair feature, README accuracy).
- Line numbers are updated throughout.

Priority:
- **P1:** do soon; users notice it, or it protects data
- **P2:** worthwhile
- **P3:** nice to have

---

## Performance and responsiveness

### I-01 · P1 · Move blocking shell, COM and disk work off the UI thread
The dispatcher thread currently does all of the following:

| Work | Where | Cost |
|---|---|---|
| Shortcut repair right before every launch, including full index rebuilds | `src/TaskbarGrouper/Views/FlyoutWindow.xaml.cs:810-814` → `ShortcutRepairService.PrepareLaunch` | the flyout freezes before the app starts (see B-24) |
| Waiting for Explorer to unpin | `src/TaskbarGrouper/Services/ShortcutService.cs:276-279` (`Thread.Sleep(50)` × 20 per pinned file, from `DeleteGroup_Click`) | up to 1 s per pin, with Studio frozen |
| Startup maintenance: heal pins, migrations, refresh own shortcut copies, possibly a full app scan | `src/TaskbarGrouper/App.xaml.cs:85,90-111` | the UI freezes right after the first window appears (see B-07) |
| Rendering the stack icon after every edit | `MainWindow.FlushPendingSave` → `UpdatePinnedTaskbarShortcutForGroup` | stutter while typing names (see B-08) |
| Extracting every item's icon before the flyout is shown | `FlyoutWindow.xaml.cs:199` | slows the open with a cold cache |
| `Directory.Exists(item.TargetPath)` per item | `FlyoutWindow.xaml.cs:187` | a folder item on an unreachable network share or UNC path can stall the open for many seconds |
| `File.Exists(target)` for each shortcut before a launch and at startup | `ShortcutRepairService.cs:75,131` | same network-timeout risk for targets on disconnected drives |
| Rendering the group bitmap twice per change (preview + `.ico`) | `src/TaskbarGrouper/Services/IconHelper.cs:394,518` | about twice the needed work |

**Suggestion:**
- Add one long-lived background **STA** worker thread with a work queue. The shell APIs (`IShellItemImageFactory`, `Shell.Application`, `IShellLink`) are safest on STA.
- Show the flyout immediately with placeholder icons and fill them in asynchronously.
- Trust `Kind == Folder` instead of calling `Directory.Exists` on every open.

### I-02 · P2 · Make the icon cache smarter
`IconHelper._iconCache` (`IconHelper.cs:87-114`) has three gaps:
- It never invalidates entries. After an app update, or after the new shortcut repair has fixed a link, the old icon stays until StackBar restarts, and the resident process runs for days.
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
- The importer scan and the new repair index (`ShortcutRepairService.GetIndex`) each walk the Start Menu and COM-load every `.lnk`.

**Suggestion:** Cache the last scan for a few minutes, pass a `CancellationToken`, and run on the STA worker from I-01. Build one shared, background-refreshed Start Menu index that both features read (see R-02).

---

## Robustness and data safety

### I-05 · P1 · Repair item copies whose target moved
`aa43e0e` added `ShortcutRepairService`, which refreshes a copy whose target was updated **in place** (same path). By design it ignores shortcuts whose target is missing (`IsStale` returns false, `src/TaskbarGrouper/Services/ShortcutRepairService.cs:75`). When an app moves to a new folder (versioned install folders, "App 2024" becoming "App 2025", a reinstall on another drive), the StackBar item still breaks. `GroupItem.IsBroken` exists (`src/TaskbarGrouper/Models/GroupModels.cs:203`) but nothing uses it.

**Suggestion:**
- When an item's target is missing and `SourcePath` still exists, copy it again from the source.
- Otherwise, try to recover it by name or AUMID. The logic exists in `MigrateLegacyPinnedTargets`, and the new index already holds the AUMIDs of all Start Menu shortcuts (`ShortcutRepairService.FindRegisteredShortcuts`).
- Show broken items dimmed, with a "Fix / Remove" action, in the flyout and in Studio.

### I-06 · P1 · Keep the configuration safe and versioned
- `groups.json.bak` is simply the previous save (`src/TaskbarGrouper/Services/ConfigService.cs:170-180`). A bad but valid save, for example one that wipes items, overwrites the backup after two saves. Keep a few dated backups (e.g. one per day, last 5).
- Make `Save` report failure and tell the user (see B-06).
- Add a `schemaVersion` field so migrations (flat `Links`, legacy pinned targets, name cleanup) run once and are recorded, instead of being re-checked on every start.
- Enums are serialized as integers (`GroupModels.cs:8-28`), so reordering an enum would silently change saved data. Give each enum value an explicit number, or serialize enums as strings.

### I-07 · P2 · Handle unexpected errors and log properly
- Only `DispatcherUnhandledException` is handled (`App.xaml.cs:29-39`). Also add `AppDomain.CurrentDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`. The latter matters more now that `ShortcutRepairService.WarmIndexInBackground` uses `Task.Run`.
- Limit how often the error dialog can appear (repeated errors show one dialog each).
- Log full exceptions (with stack traces) instead of only `ex.Message`: `ConfigService.cs:188`; `ShortcutService.cs:237,284,688,751`; `ShortcutRepairService.cs:140,166,213,255`.
- The log is rolled only at startup (`src/TaskbarGrouper/Services/AppInfo.cs:67-77`), so a resident process can grow it without limit. The repair feature can now add two lines per launch (B-25). Check the size when writing.

### I-08 · P2 · Harden the IPC pipe
In `src/TaskbarGrouper/Services/IpcService.cs`:
- Add a deny rule for the NETWORK SID (`S-1-5-2`) in the `PipeSecurity` (`:94-98`). .NET Framework has no `PipeOptions.CurrentUserOnly`.
- Add a read timeout and a maximum line length. A client that connects and never writes blocks the single listener forever (`:117-123`), and every later pin click then times out.
- Validate group IDs (e.g. `[A-Za-z0-9-]{1,32}`) and log rejected payloads.

### I-09 · P2 · A safer, clearer uninstaller
Besides fixing B-01:
- Try to unpin the user's stacks during uninstall (reuse `DeleteGroupShortcuts`), and only fall back to "unpin them by hand" when that fails.
- When the user asks to delete data, also remove the legacy `%AppData%\TaskbarGrouper` folder, and mention that `Backups\Shortcuts` (I-24) goes with it.
- Avoid launching hidden `powershell.exe -ExecutionPolicy Bypass` (`src/TaskbarGrouper/Views/SetupWindow.xaml.cs:233-242`); antivirus heuristics often flag that pattern. A temporary self-deleting `.cmd`, or `cmd /c ping -n 2 127.0.0.1 >nul & del ...`, deletes only known files without PowerShell.

### I-10 · P2 · Installer polish
- Set `System.AppUserModel.ID = "StackBar.App"` on the Start Menu and Desktop shortcuts. `Program.cs:23` gives the running process that ID; without it on the shortcut, a pinned Studio window and the Start Menu entry show up as separate apps.
- Check that the install folder is writable before stopping running instances.
- Reuse `StartupService` instead of writing the Run key separately (`SetupWindow.xaml.cs:89-95`).
- Install into a dedicated subfolder (B-01).

### I-11 · P3 · Taskbar geometry from the shell, not guessed
Use `SHAppBarMessage` (`ABM_GETTASKBARPOS`, and `ABM_GETSTATE` for auto-hide) and the per-monitor taskbar windows to find the edge and the taskbar rectangle. Support keyboard activation (Win+1…9) by anchoring to the taskbar instead of the cursor. Fixes B-17.

### I-24 · P2 · New · Manage shortcut backups
The repair feature backs up every third-party shortcut it changes into `Backups\Shortcuts` (`ShortcutRepairService.cs:155-169, 176`), but backups are a dead end today:
- There is no restore.
- There is no record of where each file came from (B-26).
- Nothing is ever deleted (B-25).

**Suggestion:**
- Write a manifest (original path, backup file, time, reason).
- Keep the newest N backups per original, or 30 days.
- Add a Studio action "Restore a shortcut backup…" (a list with original path and date) plus "Open backups folder".

### I-25 · P2 · New · Rethink the defaults and scope of repairing other apps' shortcuts
`RepairAppShortcuts` is on by default (`GroupModels.cs:52,58`). StackBar therefore edits other apps' Start Menu and pinned shortcuts silently, including in Portable Edition, whose README promises no AppData footprint (`README.md:15`). "Repair App Shortcuts Now" changes *every* stale shortcut StackBar can find, not only apps that are in a stack (`ShortcutRepairService.cs:218-238`).

**Suggestion:**
- Ask once, the first time a stale shortcut is found (and default to off in Portable Edition), or at least show a one-time notice.
- Make "Repair Now" show the list of stale shortcuts with checkboxes before changing anything.
- Keep a user-visible list of the changes made (the manifest from I-24 can drive it).

---

## User experience and features

### I-12 · P1 · Expose settings that exist but cannot be changed
`AppGroup.Columns` (used by the flyout, clamped to 2-8) and `AppGroup.CloseOnLaunch` are saved and respected, but Studio has **no control** for either; the only way to change them is to edit `groups.json` by hand. Add a column count control and a "Close after launching" checkbox. Also consider letting users edit an item's `Arguments` and `WorkingDirectory`, which exist in the model but have no UI.

### I-13 · P2 · Studio item list
- Rows show the internal copy path (`%AppData%\StackBar\Links\<hex>\App.lnk`) (`src/TaskbarGrouper/Views/MainWindow.xaml:464`). Show the real target (the exe, or the Store app's name) and put the internal path in the tooltip.
- Every edit replaces `ItemsSource` (`src/TaskbarGrouper/Views/MainWindow.xaml.cs:456-460`), which resets scroll position, selection and focus. Use an `ObservableCollection` and `Move`/`Remove`.
- Removing an item in Studio has no confirmation and no undo (the flyout asks for confirmation). Add an undo toast or a confirmation.
- Dropping onto the empty area below the last row does nothing (`MainWindow.xaml.cs:851-852`). Treat it as "move to the end".

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
The pinned `.lnk` keeps its old file name (`"Gaming (StackBar).lnk"`), which is the name the taskbar shows. Renaming the pinned file would break the pin. Tell the user in Studio that the old name stays until they re-pin, and offer a "Re-create shortcut" button that creates the new shortcut and opens Explorer on it.

### I-17 · P3 · Tray icon
- Single-click should open Studio (today only double-click works, `App.xaml.cs:200`).
- `Icon.ExtractAssociatedIcon` returns a 32 px icon that is scaled down for the tray and looks blurry (`App.xaml.cs:177-185`). Load the 16/20/24 px frame from the embedded `.ico` instead.
- Add an "Open stack ▸" submenu.
- Add WinForms per-monitor DPI settings (an `app.config` `System.Windows.Forms.ApplicationConfigurationSection` with `DpiAwareness=PerMonitorV2`) so the tray menu is not bitmap-scaled on high-DPI screens.

### I-18 · P3 · Icon quality
- The generated `.ico` holds a single 256 px PNG frame (`IconHelper.cs:555-575`). Add 16, 20, 24, 32, 40, 48 and 64 px frames rendered at those sizes so the taskbar shows them crisply.
- Emoji covers are drawn with GDI+ `DrawString` (`IconHelper.cs:495-508`). That draws emoji in one colour (white), and as far as I know it does not combine multi-character emoji (variation selector or zero-width-joiner sequences such as 🛠️, ❤️, 🗂️ from the built-in palette). Test it, and consider DirectWrite or Direct2D rendering if colour emoji matter.
- Custom cover images are copied at full size (`ShortcutService.cs:1039-1047`). Downscale them to 256 px when imported. Support `.gif`, `.tif` and `.webp` through WIC (today they produce a generic file icon).
- Clearing a custom cover leaves its `custom_<id>_*.png` file behind (`MainWindow.xaml.cs:562-571`). Delete it, or clean up unreferenced cover files at startup.

---

## Engineering and release

### I-19 · P1 · Continuous integration
Nothing builds or tests the project automatically. Add a GitHub Actions workflow on `windows-latest` that runs `dotnet restore`, `build -c Release`, `dotnet test` and `build.ps1`, and uploads `artifacts/`. Make `build.ps1` stop on build errors (B-10) and optionally run the tests. The new `ShortcutRepairServiceTests` create real `.lnk` files through COM, so they need a Windows runner.

### I-20 · P2 · One source for the version number
The version is hard-coded in four places that already disagree with the assembly:
- `src/TaskbarGrouper/app.manifest:3` (`1.1.0.0`)
- `MainWindow.xaml:127` (`v1.1`)
- `MainWindow.xaml.cs:84` (`v1.1`)
- `SetupWindow.xaml.cs:104` (`1.1.0`)

The assembly version itself is the default `1.0.0.0`. The `v1.1.0` git tag points at `ba758e9`, two commits before the current `main`. Set `<Version>` in the csproj and read it at runtime (`AssemblyInformationalVersion`) everywhere.

### I-21 · P2 · Code signing
The single exe writes into the taskbar pin folder, edits other apps' shortcuts, edits the Run key and launches hidden PowerShell during uninstall. Unsigned, it will trigger SmartScreen and antivirus heuristics. Sign `StackBar.exe` and `StackBar-Setup.exe`.

### I-22 · P3 · Portable zip layout
`build.ps1:34` zips the *contents* of `StackBar-Portable\`, so "Extract here" spills the files into the current folder. Include the top-level folder (`CreateFromDirectory(..., includeBaseDirectory: true)`).

### I-23 · P2 · Test coverage
Covered today: config persistence, shortcut path helpers, icon alpha handling, and (new) the basic shortcut-repair flow.

Still without tests:
- recognising pinned shortcuts and deciding when to heal them (B-02)
- portable-mode detection (B-05)
- the uninstall folder safety rules (B-01)
- parsing IPC messages
- the flyout position maths (extract a pure function first; see R-09 in `refactoring.md`)
- `ImportAndPersistShortcut` for each source type
- the migrations, including the "save failed" path (B-06)
- repair edge cases:
  - a read-only shortcut is skipped and not backed up again (B-25)
  - two same-named shortcuts keep two backups (B-26)
  - a config path outside `Links\` is not treated as an own copy (B-27)
  - icon, working directory, hotkey, run-as flag and environment-variable targets survive a refresh (B-28)
  - no index rebuild happens on the launch path (B-24)

`refactoring.md` (R-10) suggests the small seams that make these testable without touching the real `%AppData%` or taskbar.

### I-26 · P2 · New · Make the README match the code
- `README.md:15`: Portable Edition "leaving zero registry or AppData footprint". Repair (on by default), healing pins and updating pins all write to shortcuts under `%AppData%` (Start Menu, `User Pinned\TaskBar`).
- `README.md:15`: "Can run simultaneously alongside an installed copy". True for IPC, but the two copies take over each other's pins (B-02).
- `README.md:30`: "shortcut repair and link migration run after the flyout is shown". `MigrateFlatLinkCopies` and the name clean-up run *before* the first flyout (`App.xaml.cs:60-66`).
- `README.md:45`: "Re-importing an app that is already in the stack is skipped". Only for the same source (B-21).
- `README.md:46`: "Rapid clicking toggles the flyout open/closed". Not reliable on slow starts (B-11).
- `README.md:93`: shortcuts installed for all users "are skipped". They are backed up and retried on every launch (B-25).
- `README.md:108,117`: the project structure doesn't list `ShortcutRepairService` or `StartupService`, nor the repair tests.

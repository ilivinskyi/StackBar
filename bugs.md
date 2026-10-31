# StackBar Code Review: Bugs

**Scope:** the whole repository at commit `aa43e0e` (branch `main`, "Repair outdated app shortcuts to prevent blank taskbar icons"): `src/TaskbarGrouper/**`, `tests/StackBar.Tests/**`, `build.ps1`, `README.md`.
**Method:** static review. I read every source, XAML, test and build file. I did **not** compile or run anything: the review ran in a Linux container, and the project targets .NET Framework 4.8 with WPF.
**Companion files:** `improvements.md` (new behaviour, performance, UX, hardening) and `refactoring.md` (code structure only, no behaviour change).

## What changed since the previous review (commit `a00b899`)

- **Reviewed new code:** `ShortcutRepairService` (refreshes "stale" `.lnk` files before launches and on demand), the Studio setting and button for it, and the smarter pinned-icon update in `ShortcutService`.
- **B-08 is partly fixed:** unchanged pins are no longer rewritten and the icon-cache flush is gone. What remains is described under B-08.
- **New findings:** B-24 to B-30 and T-03. Most are in the new shortcut-repair feature.
- **Everything else** was re-checked against the new code and is still present. Line numbers are updated, and B-05 and B-17 were reworded slightly.
- IDs are unchanged, so references from the previous version still work. Entries are ordered by severity, not by ID.

Severity scale:
- **Critical:** data loss outside the app's own folders
- **High:** wrong behaviour that many users of a supported feature will hit
- **Medium:** wrong behaviour in realistic situations, or a large performance problem
- **Low:** edge cases, cosmetic or misleading behaviour

Each entry lists where the problem is, what goes wrong, and a suggested fix. The fixes are suggestions; keep each change minimal and add a unit test where the logic can be tested.

## Summary

| ID | Severity | Status | Problem |
|---|---|---|---|
| B-01 | Critical | Open | Uninstaller deletes the whole install folder; installer installs straight into the picked folder |
| B-02 | High | Open | Pins are recognised only by `--group <id>`, so one StackBar copy overwrites another's pins |
| B-03 | High (portable) | Open | Absolute paths break Portable Edition when its folder or drive letter changes |
| B-04 | Medium | Open | "Open File Location" opens StackBar's private `Links` folder |
| B-05 | Medium | Open | Portable mode is guessed from a `data` folder or the exe name |
| B-06 | Medium | Open | Save failures are silent, and files are deleted afterwards anyway |
| B-07 | Medium | Open (now does more work) | Startup maintenance can rescan every app on every launch, on the UI thread |
| B-24 | Medium | **New** | A launch can stall while the shortcut index is rebuilt on the UI thread |
| B-25 | Medium | **New** | Shortcuts StackBar cannot write are backed up and retried on every launch |
| B-09 | Low-Medium | Open | Studio sidebar app counts go stale |
| B-26 | Low-Medium | **New** | Shortcut backups can overwrite each other and don't record their origin |
| B-08 | Low | Partly fixed | Studio still re-renders the stack icon after every edit; five handlers save twice |
| B-10 | Low | Open | `build.ps1` packages an old exe when the build fails |
| B-11 | Low | Open | Clicking the pin to close the flyout fails on slow starts |
| B-12 | Low | Open | A rename in progress is discarded when clicking outside the flyout |
| B-13 | Low | Open | Importer ignores search text typed while scanning |
| B-14 | Low | Open | One inaccessible subfolder drops a whole Start Menu root (also in the new index) |
| B-15 | Low | Open | "Run as Administrator" offered where it cannot work |
| B-16 | Low | Open | "Run at startup" checkbox can show the wrong state |
| B-17 | Low | Open | Flyout placement wrong for auto-hide taskbars, keyboard launch, high DPI |
| B-18 | Low | Open | Setup window inconsistencies |
| B-19 | Low | Open | Same-named stacks overwrite each other's generated shortcut |
| B-20 | Low | Open | Studio accepts empty names |
| B-21 | Low | Open | The same app can be imported twice |
| B-22 | Low | Open | Icon lookup can recurse without limit |
| B-23 | Low | Open (likely) | Flyout may stay open if it never gets focus |
| B-27 | Low | **New** | Third-party shortcuts referenced by items are changed without backup, even with the setting off |
| B-28 | Low (verify) | **New** | The refresh may alter environment-variable and MSI "advertised" shortcuts |
| B-29 | Low | **New** | "Repair App Shortcuts Now" reads the config on a worker thread; its dialog can outgrow the screen |
| B-30 | Low | **New** | Visual glitches in the importer and setup windows |
| T-01 | Low | Partly addressed | Tests can write to the developer's real log |
| T-02 | Low | Open | Hidden global inputs in tests |
| T-03 | Low | **New** | Repair tests point at the real Start Menu until their first cleanup |

---

## Critical

### B-01 · Critical · The uninstaller deletes the whole install folder, and the installer installs straight into whatever folder the user picks

- **Where:**
  - `src/TaskbarGrouper/Views/SetupWindow.xaml.cs:39-51`: *Browse…* sets `_installDir = fbd.SelectedPath` as-is.
  - `SetupWindow.xaml.cs:62-63`: `StackBar.exe` goes directly into `_installDir`, with no `StackBar` subfolder.
  - `SetupWindow.xaml.cs:102`: `InstallLocation = _installDir` is written to the registry.
  - `SetupWindow.xaml.cs:149,175` → `ScheduleDirectoryDeletionAfterExit` (`:223-243`), which runs `Remove-Item -LiteralPath '<dir>' -Recurse -Force`.
- **What happens:** If the user picks an existing folder such as `D:\Apps`, `C:\Tools`, Desktop or Documents, StackBar.exe is copied into it. Uninstalling later deletes that entire folder recursively, including every unrelated file in it. The deletion is silent (`-ErrorAction SilentlyContinue`), runs after the process exits, and the "completely uninstalled" message has already been shown.
- **Repro:** Setup → Browse… → choose `D:\Apps` (which contains other programs) → Install → Settings → Apps → StackBar → Uninstall. `D:\Apps` is gone.
- **Suggested fix:**
  1. When the user browses to a folder, append `StackBar` unless the folder is empty or already named `StackBar`.
  2. In the uninstaller, delete only the files StackBar created (`StackBar.exe`, plus anything else you add later), then remove the directory **only if it is empty**.
  3. Refuse to delete a drive root, a known shell folder (Desktop, Documents, Program Files, user profile, Windows), or any `InstallLocation` that does not contain `StackBar.exe`.

---

## High

### B-02 · High · Pinned stacks are recognised only by a `--group <id>` argument, so one copy of StackBar overwrites another copy's pins (and could overwrite other apps' pins)

- **Where:**
  - `src/TaskbarGrouper/Services/ShortcutService.cs:149-179`: `TryGetGroupIdFromArgs` / `IsShortcutForGroup` look at nothing but the arguments.
  - Callers:
    - `HealPinnedTaskbarShortcuts` (`:379-428`) rewrites the target to the current exe whenever `exeMoved` is true.
    - `UpdatePinnedTaskbarShortcutForGroup` (`:289-338`) rewrites the target whenever it differs from the current exe.
    - `DeleteGroupShortcuts` (`:194-240`) unpins the shortcut.
  - The default group IDs are fixed strings: `gaming`, `ai-tools`, `web` (`src/TaskbarGrouper/Services/ConfigService.cs:99,109,119`).
- **What happens:**
  1. **Portable and installed copies fight.** The README says both can run side by side, and both start with the same default IDs. At every start, each copy's `HealPinnedTaskbarShortcuts` sees the other copy's pin `--group "gaming"` as "exe moved" and points it at itself. Clicking the portable pin then opens the installed copy, with different contents, and the other way round. Deleting "Gaming" in one copy also unpins the other copy's "Gaming".
  2. **Running any other copy once takes over every pin.** This includes `Downloads\StackBar.exe` before installing, `artifacts\StackBar.exe`, or a `bin\Debug` build. If that file is deleted later, every pinned stack is broken (blank icon, "item has been moved") until the correct exe runs again.
  3. **Other apps' pins can match.** Any third-party pinned shortcut whose arguments contain `--group web` (or `--group gaming` and so on) is treated as a StackBar pin and re-pointed to StackBar.exe. `IndexOf("--group")` also matches inside longer flags such as `--groups`.
- **Suggested fix:**
  - Identify StackBar pins by their AUMID property (`StackBar.Group.<id>` is already written in `CreateGroupShortcut`, `ShortcutService.cs:108-125`) **and** by the target file name (`StackBar*.exe`). Reading the AUMID already exists in `ShortcutRepairService.ReadLink` (`src/TaskbarGrouper/Services/ShortcutRepairService.cs:296-325`); reuse it (see R-01).
  - Put an edition or instance tag in the AUMID and the arguments (for example a short hash of the data directory) so portable and installed pins can be told apart.
  - Only heal a pin when its current target no longer exists, or when it is a StackBar exe of the *same* edition.
  - Parse `--group` as a whole token.
  - Consider random IDs for the default groups too.

### B-03 · High (portable) · All stored paths are absolute, so Portable Edition breaks when its folder moves or the USB drive gets a new letter

- **Where:**
  - `ShortcutService.NewPersistentLinkPath` (`ShortcutService.cs:698-706`)
  - `ImportAndPersistShortcut` (`:621-637`)
  - `PersistCustomFolderIcon` / `SaveCustomFolderBitmap` (`:1022-1066`)
  - The stored fields `GroupItem.TargetPath`, `GroupItem.CustomIconPath`, `AppGroup.CustomFolderIconPath` (`src/TaskbarGrouper/Models/GroupModels.cs`)
- **What happens:** The README advertises "Runs completely self-contained from a folder or USB drive." Every imported shortcut is saved as `E:\StackBar-Portable\data\Links\<id>\App.lnk`. When the stick comes back as `F:`, or the folder is moved or renamed:
  - every imported item fails to launch with "file not found",
  - item icons are blank,
  - custom covers silently fall back to the emoji.

  `HealPinnedTaskbarShortcuts` repairs the pinned stack shortcuts but not the items.
- **Suggested fix:** Store paths inside the data directory relative to it (or with a `%STACKBAR_DATA%`-style token) and resolve them on load. Alternatively, save the data directory in the config and rewrite the path prefix on load when it has changed. The same problem affects the installed edition if `%AppData%` moves (for example a roaming profile on another machine), though that is rarer.

---

## Medium

### B-04 · Medium · "Open File Location" opens StackBar's private `Links` folder instead of the app's folder

- **Where:** `src/TaskbarGrouper/Views/FlyoutWindow.xaml.cs:769-774`. It calls `ShortcutService.ParseShortcut(target).TargetPath`, but `ParseShortcut` always returns the shortcut itself as `TargetPath` (`ShortcutService.cs:864-872` for `.lnk`, `:812-820` for `.url`). The real target ends up only in `CustomIconPath`, and only sometimes.
- **What happens:** For every imported `.lnk` or `.url` item, the context-menu entry opens Explorer at `%AppData%\StackBar\Links\<hex>\` with the copied `.lnk` selected. The intended "resolve the real target" step does nothing.
- **Suggested fix:** Use a helper that loads the link and calls `IShellLinkW.GetPath`; `ShortcutRepairService.ReadLink` (`ShortcutRepairService.cs:296-325`) already does exactly that, so make it reusable. Select that target. For `.url` items, hide the entry or open the folder of the original `SourcePath`. See also R-12 in `refactoring.md`: `ParseShortcut`'s mixed meaning of `TargetPath` is the root cause.

### B-05 · Medium · Portable mode is guessed from fragile clues: any folder named `data` next to the exe, or "Portable" in the exe name

- **Where:** `src/TaskbarGrouper/Services/AppInfo.cs:27-45`
- **What happens:**
  - Install into a custom folder that already has a `data` subfolder: the *installed* app silently runs as Portable. It reads and writes `<install>\data` and ignores `%AppData%\StackBar`, so the user's stacks appear to be lost. It also uses a different mutex and pipe, and disables the startup toggle.
  - Run `StackBar-Setup.exe` from a folder that happens to contain a `data` subfolder (for example a cluttered Downloads folder): the setup process computes the *portable* pipe name, so `StopRunningInstances`' `EXIT` command (`SetupWindow.xaml.cs:209`) never reaches the installed copy. The installer falls back to `Kill()`, and any pending (debounced) Studio edits are lost. Setup also writes its log into that folder (`data\logs`).
- **Suggested fix:** Use only the explicit marker file (`.portable`, which `build.ps1:28` already creates; optionally also `portable.txt`). Drop the `data`-folder check. If you keep the exe-name check, make it deliberate (for example an exact match on `StackBar-Portable.exe`).

### B-06 · Medium · Save failures are silent, and files are deleted afterwards as if the save had worked

- **Where:**
  - `ConfigService.Save` (`ConfigService.cs:149-190`) catches every exception and only logs `ex.Message`.
  - Code that deletes files assuming the new state is on disk:
    - `ShortcutService.MigrateFlatLinkCopies` deletes the old copies (`ShortcutService.cs:755-759`). `App` only saves afterwards (`src/TaskbarGrouper/App.xaml.cs:63-66`).
    - `DeletePersistedLinkIfUnused` runs before the save in `FlyoutWindow.xaml.cs:723-725`, `src/TaskbarGrouper/Views/MainWindow.xaml.cs:821-823` and `MainWindow.xaml.cs:705-711`.
    - `DeleteOldCustomIcons` runs inside `PersistCustomFolderIcon` before the config referencing the new cover is saved.
- **Concrete failure:** `MigrateFlatLinkCopies` copies `Links\abcd1234_Foo.lnk` to `Links\abcd1234\Foo.lnk` and deletes the old file. If `groups.json` is locked at that moment (antivirus, backup or OneDrive sync), `Save` fails silently. On the next start the config still points at the old path, and `Migrate`'s `!File.Exists(path)` early return (`ShortcutService.cs:726`) means the item is never re-linked to the new copy, which still exists. The item is permanently broken.
- **Suggested fix:**
  - Make `Save` return `bool` (or throw) and delete files only after a successful save.
  - Make the migration idempotent: if the old file is missing but `Links\<hex>\<name>` exists, re-point the item to it.
  - Tell the user when saving fails.

### B-07 · Medium (performance) · Startup work can rescan every installed app on every launch, on the UI thread

- **Where:**
  - `App.xaml.cs:85,90-111` → `ShortcutService.MigrateLegacyPinnedTargets` (`ShortcutService.cs:434-500`)
  - `ConfigService.SanitizeConfig` (`ConfigService.cs:215-231`)
  - New since `aa43e0e`: the same maintenance now also runs `ShortcutRepairService.RefreshOwnCopies` (`App.xaml.cs:101` → `ShortcutRepairService.cs:241-248`). That COM-loads every `.lnk` item and calls `File.Exists` on its target, synchronously on the dispatcher.
- **What happens:**
  - If a legacy item points at a pinned `.lnk` that no longer exists, and no app with the same name is found, nothing is saved (`modified` stays false). Every start then runs `ScanAllTaskbarAndSystemApps()` again on the dispatcher. That scan enumerates the whole AppsFolder over COM, walks the Start Menu recursively and parses every `.lnk` over COM. The result is a freeze of several seconds after each launch, during which the flyout and Studio do not respond.
  - `SanitizeConfig` calls `ResolveShellAppDisplayName`, which enumerates the full AppsFolder, from `LoadOrCreateDefault`. That is **before** the first flyout appears on a cold start, i.e. on the "click a pin" path. If the name cannot be resolved it is not saved, so this also repeats on every start.
  - `RefreshOwnCopies` adds one COM load per item. If an item's target is on a disconnected network share, `File.Exists` can block for the length of the SMB timeout.
- **Suggested fix:** Record that recovery was attempted (clear the legacy marker, or keep a `migrationVersion` in the config) so it runs once. Run maintenance (including `RefreshOwnCopies`) on a background STA thread and marshal config changes back to the UI thread. Do not resolve names on the cold-start path.

### B-24 · Medium (performance) · New · Launching an app can stall while StackBar rebuilds its shortcut index on the UI thread

- **Where:**
  - `FlyoutWindow.xaml.cs:810-814` calls `ShortcutRepairService.PrepareLaunch` synchronously, just before `Process.Start`.
  - `PrepareLaunch` → `FindRegisteredShortcuts` → `GetIndex(forceRebuild: false)` (`ShortcutRepairService.cs:182-215, 259-294`).
  - The index expires after 10 minutes (`IndexMaxAge`, `:45`). When it is expired or missing, `GetIndex` rebuilds it on the calling thread (`:272-292`), COM-loading every `.lnk` in the user's Start Menu, the all-users Start Menu and the pinned-taskbar folder, recursively.
  - The index is warmed only once, at startup (`App.xaml.cs:102-105`), and not at all if the setting is switched on later.
  - The rebuild holds `_indexLock` (`:270`), so a launch also waits for a background warm-up, or for "Repair App Shortcuts Now", which forces a full rebuild (`:225`).
- **What happens:** A tray app is idle for more than 10 minutes most of the time. So the first launch from a flyout after a pause usually freezes the flyout while hundreds of shortcuts are parsed (roughly a fraction of a second, several seconds on a slow disk) before the app even starts. The method comment "Cheap: only matching links are checked" (`:180`) is not true in that case.
- **Suggested fix:**
  - Never build the index on the launch path. If it is missing or old, use what is there (or skip the registered-shortcut part for this launch) and refresh it in the background.
  - Keep the index fresh off the UI thread, with a timer or a `FileSystemWatcher` on the three folders.
  - If `PrepareLaunch` must run before the launch, run it on a worker thread and wait at most about 100-200 ms.
  - Update the comment.

### B-25 · Medium · New · Shortcuts StackBar cannot write are backed up and retried on every launch, so backups and log lines pile up

- **Where:**
  - `ShortcutRepairService.RepairIfStale` (`ShortcutRepairService.cs:151-174`) copies the backup first (`:155-169`) and only then calls `Refresh` (`:171`). `Refresh` needs write access: it loads the link with `STGM_READWRITE` and saves it (`:127, :135`).
  - It runs for every matching registered shortcut on **every launch** (`PrepareLaunch`, `:204-207`) and for every stale shortcut on each "Repair App Shortcuts Now" (`RepairAll`, `:225-235`). Nothing remembers a failure.
- **What happens:**
  - Shortcuts in the all-users Start Menu (`C:\ProgramData\Microsoft\Windows\Start Menu\Programs`) are read-only for standard users, and for administrators without elevation, which is how StackBar runs (`asInvoker`). Consider any app installed for all users whose exe changed after its shortcut was created (common for apps that update themselves). Every launch of it from a flyout writes one more timestamped backup to `Backups\Shortcuts`, fails, and logs two lines ("Could not refresh…", "Could not repair…").
  - Each "Repair Now" click does the same for every such shortcut and lists them all as failures.
  - Backups are never pruned, so the folder grows without limit.
  - The README (line 93) says these shortcuts "are skipped", and the Studio status text calls them "skipped" (`MainWindow.xaml.cs:654`); the code actually backs them up and retries them.
  - The same loop happens for links that can never become fresh. The `.lnk` header stores only the low 32 bits of the target's size, so any target of 4 GiB or more always looks stale (`:80, :102`).
- **Suggested fix:**
  - Check that the link is writable *before* backing it up (not read-only, and can be opened for writing; or skip the all-users Start Menu when not elevated), and report it as skipped.
  - Remember failed links for the session (path plus the target's last-write time), so each is tried at most once per target version.
  - Compare only the low 32 bits of the size.
  - Prune backups (for example keep the newest N per link, or 30 days).

---

## Low-Medium

### B-09 · Low-Medium · Studio's sidebar shows stale app counts after editing

- **Where:** `src/TaskbarGrouper/Views/MainWindow.xaml:213` binds `Items.Count` on a `List<GroupItem>`. `AppGroup.NotifyAppearanceChanged` (`GroupModels.cs:68-72`) raises only `Name` and `Thumbnail`.
- **What happens:** After importing, adding, dropping or removing items in Studio, the sidebar's "N apps in folder" keeps the old number. It is corrected only when `RefreshGroupsList` runs: adding or deleting a stack, or an edit made in a flyout.
- **Suggested fix:** Add an `ItemCount` property and raise it in `NotifyAppearanceChanged` (or raise `nameof(Items)`), or switch to an `ObservableCollection`.

### B-26 · Low-Medium · New · Shortcut backups can overwrite each other and don't record where they came from

- **Where:** `ShortcutRepairService.cs:160-161`: the backup is named `<yyyyMMdd-HHmmss>_<file name>` and copied with `File.Copy(..., overwrite: true)`.
- **What happens:**
  - An app's Start Menu shortcut and its pinned taskbar shortcut usually have the **same file name** (for example "Visual Studio Code.lnk"). `PrepareLaunch` repairs both in the same second, so the second backup overwrites the first and one original is lost.
  - Backups don't record the folder they came from, so restoring one means guessing where it belongs.
- **Suggested fix:** Make names unique, either with a short hash of the full original path or by mirroring the folder structure under `Backups\Shortcuts`, and never overwrite. Keep a small manifest (original path, backup path, time). This also enables a restore feature (I-24).

---

## Low

### B-08 · Low · Partly fixed · Studio still re-renders the stack icon after every edit, and five handlers save twice

- **Fixed in `aa43e0e`:** `UpdatePinnedTaskbarShortcutForGroup` no longer rewrites pins whose target and icon are unchanged, and it notifies without `SHCNF_FLUSH`.
- **Still open:**
  - `MainWindow.FlushPendingSave` (`MainWindow.xaml.cs:109-121`) still runs the pin sync after every 350 ms pause while typing a stack or item name, and after toggling labels. `UpdatePinnedTaskbarShortcutForGroup` calls `IconHelper.GenerateGroupIcoFile` (`ShortcutService.cs:310`) *before* it can tell that nothing changed. That is a full 256 px render (up to four shell icon extractions, PNG encoding, SHA-1) for edits that cannot change the icon.
  - In `OpenCustomIconPicker`, `UseFirstAppIcon_Click`, `ClearCustomIcon_Click`, `EmojiPreset_Click` and `SetAsFolderIcon_Click` (`MainWindow.xaml.cs:533-535, 555-557, 567-569, 579-581, 592-594`), setting `Style*Radio.IsChecked = true` without `_suppressEvents` fires `StyleRadio_Checked`, which calls `SyncGroupAndTaskbarIcon`. The handler then calls it again: two saves and two renders. `ApplyGlyph` (`:304-306`) shows the correct pattern.
  - The new "unchanged" check compares the raw `GetIconLocation` string with the generated path (`ShortcutService.cs:315-316`). `HealPinnedTaskbarShortcuts` expands environment variables first (`:406`). If the shell ever returns the icon path in `%VAR%` form, the check never matches, and every edit rewrites the pin and broadcasts `SHCNE_ASSOCCHANGED` again. Expanding before comparing is cheap insurance.
- **Suggested fix:** Mark a group dirty for pin updates only for changes that affect the icon (icon mode, glyph, accent, cover, the first four items). Suppress the radio button event when setting it from code. Expand environment variables before comparing.

### B-10 · Low · `build.ps1` packages an old exe when the build fails

- **Where:** `build.ps1:6`
- **What happens:** `$ErrorActionPreference = "Stop"` does not apply to native commands' exit codes in Windows PowerShell 5.1. If `dotnet build` fails, the script still copies the *previous* `bin\Release\net48\StackBar.exe` into `artifacts\`, zips it and prints "Success!".
- **Suggested fix:** After `dotnet build`, add `if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }`. Also check that the exe exists and its timestamp is new.

### B-11 · Low · Clicking the pin to close the flyout fails on slow starts

- **Where:** `App.xaml.cs:120-126`: a hard-coded 500 ms window between the flyout closing and the next `OPEN` request.
- **What happens:** The flyout closes when the pin is pressed (it loses activation). The `OPEN` request arrives only after Explorer has started a whole new process (CLR startup, IPC). On a loaded machine or with a cold disk cache this gap can exceed 500 ms, so the flyout immediately reopens and the user cannot dismiss it by clicking the pin.
- **Suggested fix:** Have the client send its own process start time, or a tick count captured at the start of `Main`, in the `OPEN` message. Treat the request as a toggle if the same group's flyout closed after the client process started, minus a small margin.

### B-12 · Low · A rename in progress is silently discarded when you click outside the flyout

- **Where:** `FlyoutWindow.xaml.cs:298-302` closes the flyout on `Deactivated`. Committing relies on `GroupTitleBox_LostFocus` (`:421-424`). When a window deactivates, WPF removes keyboard focus but not logical focus, so `LostFocus` is not raised.
- **What happens:** Clicking a tile *inside* the flyout commits the new name. Clicking *outside* closes the flyout and throws the typed name away without saying so. The two behaviours are inconsistent.
- **Suggested fix:** In `Window_Deactivated` or `Window_Closing`, call `CommitRename()` when `GroupTitleBox` is visible (or explicitly cancel, and document that choice).

### B-13 · Low · The importer ignores search text typed while it is still scanning

- **Where:** `src/TaskbarGrouper/Views/TaskbarImporterWindow.xaml.cs:66-68`
- **What happens:** While loading, `SearchBox_TextChanged` filters the empty list. When the scan finishes, the code sets `_filteredItems = _allItems` and shows every app even though the search box still contains text.
- **Suggested fix:** After loading, run the same filter as `SearchBox_TextChanged` (move it into an `ApplyFilter()` method).

### B-14 · Low · One inaccessible subfolder drops all results for a whole Start Menu root

- **Where:**
  - `ShortcutService.cs:980` uses `Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories)`, and the surrounding `catch` (`:985`) discards everything.
  - The new shortcut index has the same pattern: `ShortcutRepairService.cs:278-279` (`EnumerateFiles(...).ToList()` inside `try { } catch { continue; }`).
- **What happens:** An `UnauthorizedAccessException` or `PathTooLongException` in any single subfolder of `CommonPrograms`, `Programs` or the pinned folder throws away that root's entire list: missing apps in the importer, and unrepaired shortcuts in the index.
- **Suggested fix:** One shared helper that walks the folders manually (or calls `EnumerateFiles` per directory), with a try/catch around each directory. Use it in both places.

### B-15 · Low · "Run as Administrator" is offered where it cannot work

- **Where:** `FlyoutWindow.xaml.cs:196`. It hides the option only for Store apps and folders.
- **What happens:** For `.url` items (Steam games, websites) and for documents, `Verb = "runas"` throws `Win32Exception` "No application is associated with the specified file for this operation", which shows an error dialog (`:839-842`). Ctrl+Shift+click and Ctrl+Shift+1..9 hit the same path.
- **Suggested fix:** Offer elevation only for `.exe`, `.bat`, `.cmd`, `.msc`, or `.lnk` files whose target is one of those. Otherwise ignore the admin modifier.

### B-16 · Low · Studio's "Run at startup" checkbox can show the wrong state

- **Where:**
  - `src/TaskbarGrouper/Services/StartupService.cs:12-53`
  - `MainWindow.xaml.cs:80-88, 606-614`
  - `src/TaskbarGrouper/Themes/Dark.xaml:34-60`
- **What happens:**
  - `IsRunAtStartupEnabled` returns true if *any* `StackBar` Run value exists, even one pointing at an old or moved exe. The box shows "on" while startup is actually broken.
  - In Portable mode the (disabled) box shows the *installed* copy's state.
  - `SetRunAtStartup` swallows errors, but the status text always claims success.
  - The disabled checkbox's explanation tooltip (`MainWindow.xaml.cs:86`) never appears: WPF hides tooltips on disabled controls unless `ToolTipService.ShowOnDisabled="True"`.
  - The custom CheckBox template has no disabled style, so the box looks clickable but does nothing.
- **Suggested fix:** Compare the registered command with the current `AppInfo.ExePath` and offer a repair if they differ. Return success or failure from `SetRunAtStartup`. Set `ShowOnDisabled`. Add an `IsEnabled=False` trigger to the template.

### B-17 · Low · Where the flyout appears is wrong in some taskbar setups

- **Where:** `src/TaskbarGrouper/Services/NativePositioningService.cs:159-162` (edge detection) and `:91` (button size filter)
- **What happens:**
  - The taskbar edge is inferred by comparing the screen's work area with its full bounds. With an **auto-hidden** taskbar, or on a monitor without a taskbar, the two are equal, so the edge is always "Bottom". The flyout for an auto-hidden top, left or right taskbar opens at the bottom of the screen.
  - Opening a pinned stack from the keyboard (Win+1…9) or the Start menu anchors the flyout at wherever the mouse happens to be.
  - The size filter for the taskbar button is in physical pixels (`w ≤ 320`, `h ≤ 120`). Above roughly 250 % display scaling, taskbar buttons are taller than 120 px, so the caret falls back to the cursor's X position instead of the button's center.
- **Suggested fix:** Use `SHAppBarMessage(ABM_GETTASKBARPOS / ABM_GETSTATE)`, or find the taskbar window on the target monitor (`Shell_TrayWnd` / `Shell_SecondaryTrayWnd`). Scale the size thresholds by the monitor's DPI. When the cursor is not over a taskbar, anchor to the taskbar button (via UIA or the pin order) or center on the taskbar.

### B-18 · Low · Setup window inconsistencies

- **Where:** `src/TaskbarGrouper/Views/SetupWindow.xaml.cs`
- **What happens:**
  - Install versus Update mode is decided once, in the constructor (`:24-36`). After *Browse…* the title and buttons are not refreshed.
  - Choosing a new folder during an Update leaves the old installation (exe and shortcuts) behind, while the registry now points at the new folder.
  - The "completely uninstalled" message (`:171`) appears before folder deletion is even attempted. Deletion happens later, in the background, with errors silenced.
  - `StopRunningInstances` kills every process named `StackBar` (`:213`). That includes unrelated dev builds and, when setup runs elevated, other users' instances.
- **Suggested fix:**
  - Recompute the mode after Browse.
  - On Update with a changed folder, offer to remove the old copy.
  - Report the uninstall result honestly.
  - Kill only processes whose image path (`QueryFullProcessImageName`, or `MainModule.FileName`) is under the install folder and whose session ID matches the current session.

### B-19 · Low · Two stacks with the same name overwrite each other's generated shortcut

- **Where:** `ShortcutService.CreateGroupShortcut` (`ShortcutService.cs:93-96`)
- **What happens:** Two stacks named "Gaming" both write `Shortcuts\Gaming (StackBar).lnk`. The second click on "Create shortcut" overwrites the first stack's shortcut before it is pinned.
- **Suggested fix:** Add the ID when names clash, for example `Gaming (StackBar, ab12cd34).lnk`, or always add a short suffix.

### B-20 · Low · Studio accepts empty or whitespace names

- **Where:** `MainWindow.xaml.cs:487` (stack name) and `ManagerItemRowViewModel.Name` (`MainWindow.xaml.cs:27-38`)
- **What happens:** An empty stack name gives a blank flyout title and a shortcut named after the ID. The flyout's own rename trims and rejects empty names (`FlyoutWindow.xaml.cs:389-397`), so the two editors behave differently.
- **Suggested fix:** Trim the value. Do not save an empty name: keep the previous one or show a validation hint.

### B-21 · Low · The same app can be imported twice (the README says re-imports are skipped)

- **Where:** `ShortcutService.GroupContainsSource` (`ShortcutService.cs:649-657`) and `ScanAllTaskbarAndSystemApps` (`:884-1013`)
- **What happens:** One app appears up to three times in the importer (pinned `.lnk`, AppsFolder `shell:` entry, Start Menu `.lnk`), each with a different `SourcePath`. Duplicate detection compares exact `SourcePath`/`TargetPath` only (the name fallback applies only to legacy items), so selecting the app from two sources adds it twice.
- **Suggested fix:** Compare by resolved target (exe path plus arguments) or by AUMID. Collapse duplicates in the importer (see I-15 in `improvements.md`).

### B-22 · Low · Icon lookup can recurse without limit and crash the process

- **Where:** `src/TaskbarGrouper/Services/IconHelper.cs:238-250`: the `.lnk` fallback calls `ExtractBitmap` on the link's icon path, which can itself be a `.lnk`.
- **What happens:** Two shortcuts whose icon locations point at each other recurse until a `StackOverflowException`, which cannot be caught and kills the resident app. This needs a pathological pair of links, and only triggers when the shell image factory fails first, so it is unlikely but cheap to prevent.
- **Suggested fix:** Pass a depth counter (stop after 1-2 levels), or skip the fallback when the icon path is itself a `.lnk`.

### B-23 · Low (likely, not reproduced) · The flyout may stay open if it never gets focus

- **Where:** `FlyoutWindow.xaml.cs:266-275` (activation) and `:298-302` (closing on deactivation)
- **What happens:** Losing activation is the only way a click outside closes the flyout. If `Activate()`/`SetForegroundWindow` is refused (foreground lock, a slow IPC handshake, or `AllowSetForegroundWindow` failing), the top-most flyout never becomes active, so it never deactivates. It stays above everything until the user clicks into it and then out, or presses Esc, which also needs focus.
- **Suggested fix:** After activating, check `GetForegroundWindow() == hwnd`. If not, fall back to closing on the next mouse click outside the window (a short-lived low-level mouse hook or a polling timer).

### B-27 · Low · New · Third-party shortcuts referenced directly by an item are changed without a backup, even with the setting turned off

- **Where:** `ShortcutRepairService` treats every `.lnk` path stored in the config as StackBar's own copy:
  - `RefreshOwnCopies` (`ShortcutRepairService.cs:241-248`) runs at every startup regardless of the setting (`App.xaml.cs:101`).
  - `PrepareLaunch` (`:190-192`).
  - `RepairAll` (`:221-227`): the config entry (no backup) wins over the index entry (with backup) for the same path, because `GroupBy(...).First()` keeps the first.
- **What happens:** An item's `TargetPath` is not always a copy under `Links\`:
  - `ImportAndPersistShortcut` falls back to the original file when copying fails (`ShortcutService.cs:635-638`, e.g. a name too long for `MAX_PATH`).
  - Configs from older versions can point at Desktop or Start Menu shortcuts.

  Those third-party files are rewritten without a backup at every start, even after the user unticks "Repair outdated app shortcuts". That contradicts both the checkbox message ("StackBar will no longer touch other apps' shortcuts", `MainWindow.xaml.cs:623`) and the code's own rule ("Never modify another app's shortcut without a backup", `ShortcutRepairService.cs:165`).
- **Suggested fix:** Decide "own copy" by location (inside `<data>\Links\`), not by "appears in the config". Treat every other path as a third-party shortcut: repair it only when the setting is on, and always with a backup.

### B-28 · Low (risk, not verified) · New · The refresh may change special kinds of shortcuts

- **Where:** `ShortcutRepairService.Refresh` (`:121-147`) re-sets the target from `GetPath(..., 0)` (`:129, :133-134`).
- **What happens:**
  - `GetPath` with flags `0` returns the *expanded* path. A shortcut that stores its target with environment variables (for example `%LOCALAPPDATA%\…`) is therefore probably saved back with a fixed absolute path.
  - For Windows Installer "advertised" shortcuts (older Office and other MSI apps), `GetPath` can return a file under `C:\Windows\Installer\{product}\` rather than the app, and what `SetPath` then does to the advertisement data is untested. In the worst case the shortcut would afterwards open that file instead of the app.
  - The test `Refresh_records_the_current_target_and_keeps_all_other_properties` only checks arguments, description and AUMID.
- **Suggested fix:**
  - Read the link flags with `IShellLinkDataList::GetFlags` and skip links with `SLDF_HAS_DARWINID` (`0x1000`).
  - For links with `SLDF_HAS_EXP_SZ` (`0x200`), read the raw path (`GetPath` with `SLGP_RAWPATH`, `0x4`) and set that back.
  - Extend the test to cover icon location and index, working directory, show command, hotkey, the "Run as administrator" flag (`SLDF_RUNAS_USER`, `0x2000`), and an environment-variable target.

### B-29 · Low · New · "Repair App Shortcuts Now" reads the config on a worker thread, and its result dialog can outgrow the screen

- **Where:**
  - `MainWindow.xaml.cs:633` runs `RepairAll(_config, …)` on the thread pool. `RepairAll` enumerates `config.Groups` and every group's `Items` (`ShortcutRepairService.cs:221-224`) while Studio stays editable.
  - The result is a `MessageBox` that lists every failed path (`MainWindow.xaml.cs:641-655`).
- **What happens:**
  - Adding or removing a stack or item while the repair starts can throw "Collection was modified", reported as "Shortcut repair failed".
  - With many failures (B-25) the message box becomes taller than the screen. Win32 message boxes don't scroll, so the OK button can end up off-screen (Enter still closes it).
  - The button's `SurfaceBtn` style has no disabled look (`MainWindow.xaml:40-64`), so while the repair runs the button looks clickable but does nothing.
- **Suggested fix:** Copy the list of item paths on the UI thread before `Task.Run`. Show a summary with counts and a scrollable "details" view (or point to the log). Add an `IsEnabled=False` trigger to the button styles.

### B-30 · Low · New · Visual glitches in the importer and setup windows

- **Where:**
  - Buttons using WPF's default template: importer "Select All", "Cancel", "Import Checked" (`src/TaskbarGrouper/Views/TaskbarImporterWindow.xaml:83-107`); setup "Browse…", "Uninstall…", "Cancel", "Install & Launch" (`src/TaskbarGrouper/Views/SetupWindow.xaml:41-50, 64-90`).
  - Importer rows: the global CheckBox template (`Dark.xaml:41-46`).
- **What happens:**
  - The default (Aero2) button template swaps in its own light-blue background on hover and press, regardless of the `Background` you set. The white text on those buttons becomes hard to read while hovered. Studio is not affected because it uses its own button templates.
  - The CheckBox template places its content in a horizontal `StackPanel`, which gives the content unlimited width. In importer rows, long app names are therefore never trimmed (`TextTrimming` has no width to trim to), and the source badges (`DockPanel.Dock="Right"`) sit right after each name instead of lining up on the right.
- **Suggested fix:** Move Studio's `PrimaryBtn` / `SurfaceBtn` styles into `Dark.xaml` and use them in every window (R-13). In the CheckBox template, use a two-column `Grid` (`Auto`, `*`) instead of the `StackPanel` so the content can stretch.

---

## Test-suite problems that are bugs in themselves

- **T-01 · Low · Partly addressed:** `AppLogger` is static. `ConfigServiceTests` (`tests/StackBar.Tests/ConfigServiceTests.cs:12-16`) and now `ShortcutRepairServiceTests` (`tests/StackBar.Tests/ShortcutRepairServiceTests.cs:21-24`) point it at a temp folder, but `ShortcutServiceTests` and `IconHelperTests` do not. Depending on test order and xUnit's parallel test classes, test log lines can still end up in the **developer's real** `%AppData%\StackBar\logs\stackbar.log`. Fix: use an assembly-level fixture, or make the log path injectable.
- **T-02 · Low:** `ConfigService.Save` writes `LastExePath = AppInfo.ExePath` (the test host's path) into every config, and `AppInfo.IsPortableMode` is evaluated against the test output folder. Both are hidden global inputs to the tests.
- **T-03 · Low · New:** `ShortcutRepairService.RegisteredShortcutFolders` (`ShortcutRepairService.cs:48`) defaults to the real Start Menu and pinned folders. `ShortcutRepairServiceTests` only replaces it in `Dispose` (`ShortcutRepairServiceTests.cs:26-31`), i.e. after a test has run. Today every test that builds the index sets the folders first. A future test that forgets to, or a test in another class, would scan and **modify the developer's real shortcuts**. Fix: set the folders to the temp directory (or an empty list) in the constructor, and better, pass the folders as a parameter instead of a mutable static (R-10).

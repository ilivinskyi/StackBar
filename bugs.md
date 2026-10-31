# StackBar Code Review: Bugs

**Scope:** the whole repository at commit `a00b899` (branch `main`): `src/TaskbarGrouper/**`, `tests/StackBar.Tests/**`, `build.ps1`.
**Method:** static review. I read every source, XAML, test and build file. I did **not** compile or run anything: the review ran in a Linux container, and the project targets .NET Framework 4.8 with WPF.
**Companion files:** `improvements.md` (new behaviour, performance, UX, hardening) and `refactoring.md` (code structure only, no behaviour change).

Severity scale:
- **Critical:** data loss outside the app's own folders
- **High:** wrong behaviour that many users of a supported feature will hit
- **Medium:** wrong behaviour in realistic situations, or a large performance problem
- **Low:** edge cases, cosmetic or misleading behaviour

Line numbers refer to commit `a00b899`. Each entry lists where the problem is, what goes wrong, and a suggested fix. The fixes are suggestions; keep each change minimal and add a unit test where the logic can be tested.

---

## B-01 · Critical · The uninstaller deletes the whole install folder, and the installer installs straight into whatever folder the user picks

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

## B-02 · High · Pinned stacks are recognised only by a `--group <id>` argument, so one copy of StackBar overwrites another copy's pins (and could overwrite other apps' pins)

- **Where:**
  - `src/TaskbarGrouper/Services/ShortcutService.cs:151-181`: `TryGetGroupIdFromArgs` / `IsShortcutForGroup` look at nothing but the arguments.
  - Callers:
    - `HealPinnedTaskbarShortcuts` (`:352-404`) rewrites the target to the current exe whenever `exeMoved` is true.
    - `UpdatePinnedTaskbarShortcutForGroup` (`:291-333`) always rewrites the target.
    - `DeleteGroupShortcuts` (`:196-242`) unpins the shortcut.
  - The default group IDs are fixed strings: `gaming`, `ai-tools`, `web` (`src/TaskbarGrouper/Services/ConfigService.cs:99,109,119`).
- **What happens:**
  1. **Portable and installed copies fight.** The README says both can run side by side, and both start with the same default IDs. At every start, each copy's `HealPinnedTaskbarShortcuts` sees the other copy's pin `--group "gaming"` as "exe moved" and points it at itself. Clicking the portable pin then opens the installed copy, with different contents, and the other way round. Deleting "Gaming" in one copy also unpins the other copy's "Gaming".
  2. **Running any other copy once takes over every pin.** This includes `Downloads\StackBar.exe` before installing, `artifacts\StackBar.exe`, or a `bin\Debug` build. If that file is deleted later, every pinned stack is broken (blank icon, "item has been moved") until the correct exe runs again.
  3. **Other apps' pins can match.** Any third-party pinned shortcut whose arguments contain `--group web` (or `--group gaming` and so on) is treated as a StackBar pin and re-pointed to StackBar.exe. `IndexOf("--group")` also matches inside longer flags such as `--groups`.
- **Suggested fix:**
  - Identify StackBar pins by their AUMID property (`StackBar.Group.<id>` is already written in `CreateGroupShortcut`, `ShortcutService.cs:108-125`) **and** by the target file name (`StackBar*.exe`).
  - Put an edition or instance tag in the AUMID and the arguments (for example a short hash of the data directory) so portable and installed pins can be told apart.
  - Only heal a pin when its current target no longer exists, or when it is a StackBar exe of the *same* edition.
  - Parse `--group` as a whole token.
  - Consider random IDs for the default groups too.

## B-03 · High (portable) · All stored paths are absolute, so Portable Edition breaks when its folder moves or the USB drive gets a new letter

- **Where:**
  - `ShortcutService.NewPersistentLinkPath` (`ShortcutService.cs:674-682`)
  - `ImportAndPersistShortcut` (`:597-609`)
  - `PersistCustomFolderIcon` / `SaveCustomFolderBitmap` (`:1019-1040`)
  - The stored fields `GroupItem.TargetPath`, `GroupItem.CustomIconPath`, `AppGroup.CustomFolderIconPath` (`src/TaskbarGrouper/Models/GroupModels.cs`)
- **What happens:** The README advertises "Runs completely self-contained from a folder or USB drive." Every imported shortcut is saved as `E:\StackBar-Portable\data\Links\<id>\App.lnk`. When the stick comes back as `F:`, or the folder is moved or renamed:
  - every imported item fails to launch with "file not found",
  - item icons are blank,
  - custom covers silently fall back to the emoji.

  `HealPinnedTaskbarShortcuts` repairs the pinned stack shortcuts but not the items.
- **Suggested fix:** Store paths inside the data directory relative to it (or with a `%STACKBAR_DATA%`-style token) and resolve them on load. Alternatively, save the data directory in the config and rewrite the path prefix on load when it has changed. The same problem affects the installed edition if `%AppData%` moves (for example a roaming profile on another machine), though that is rarer.

## B-04 · Medium · "Open File Location" opens StackBar's private `Links` folder instead of the app's folder

- **Where:** `src/TaskbarGrouper/Views/FlyoutWindow.xaml.cs:769-774`. It calls `ShortcutService.ParseShortcut(target).TargetPath`, but `ParseShortcut` always returns the shortcut itself as `TargetPath` (`ShortcutService.cs:840-848` for `.lnk`, `:788-796` for `.url`). The real target ends up only in `CustomIconPath`, and only sometimes.
- **What happens:** For every imported `.lnk` or `.url` item, the context-menu entry opens Explorer at `%AppData%\StackBar\Links\<hex>\` with the copied `.lnk` selected. The intended "resolve the real target" step does nothing.
- **Suggested fix:** Add a helper (for example `ShortcutService.TryResolveLinkTarget(string lnkPath)`) that loads the link and calls `IShellLinkW.GetPath`. Select that target. For `.url` items, hide the entry or open the folder of the original `SourcePath`. See also R-12 in `refactoring.md`: `ParseShortcut`'s mixed meaning of `TargetPath` is the root cause.

## B-05 · Medium · Portable mode is guessed from fragile clues: any folder named `data` next to the exe, or "Portable" in the exe name

- **Where:** `src/TaskbarGrouper/Services/AppInfo.cs:27-45`
- **What happens:**
  - Install into a custom folder that already has a `data` subfolder: the *installed* app silently runs as Portable. It reads and writes `<install>\data` and ignores `%AppData%\StackBar`, so the user's stacks appear to be lost. It also uses a different mutex and pipe, and disables the startup toggle.
  - Run `StackBar-Setup.exe` from a folder that has a `data` subfolder (common in Downloads): the setup process computes the *portable* pipe name, so `StopRunningInstances`' `EXIT` command (`SetupWindow.xaml.cs:209`) never reaches the installed copy. The installer falls back to `Kill()`, and any pending (debounced) Studio edits are lost.
- **Suggested fix:** Use only the explicit marker file (`.portable`, which `build.ps1:28` already creates; optionally also `portable.txt`). Drop the `data`-folder check. If you keep the exe-name check, make it deliberate (for example an exact match on `StackBar-Portable.exe`).

## B-06 · Medium · Save failures are silent, and files are deleted afterwards as if the save had worked

- **Where:**
  - `ConfigService.Save` (`ConfigService.cs:149-190`) catches every exception and only logs `ex.Message`.
  - Code that deletes files assuming the new state is on disk:
    - `ShortcutService.MigrateFlatLinkCopies` deletes the old copies (`ShortcutService.cs:731-735`). `App` only saves afterwards (`App.xaml.cs:63-66`).
    - `DeletePersistedLinkIfUnused` runs before the save in `FlyoutWindow.xaml.cs:723-725`, `MainWindow.xaml.cs:768-770` and `MainWindow.xaml.cs:652-658`.
    - `DeleteOldCustomIcons` runs inside `PersistCustomFolderIcon` before the config referencing the new cover is saved.
- **Concrete failure:** `MigrateFlatLinkCopies` copies `Links\abcd1234_Foo.lnk` to `Links\abcd1234\Foo.lnk` and deletes the old file. If `groups.json` is locked at that moment (antivirus, backup or OneDrive sync), `Save` fails silently. On the next start the config still points at the old path, and `Migrate`'s `!File.Exists(path)` early return (`ShortcutService.cs:702`) means the item is never re-linked to the new copy, which still exists. The item is permanently broken.
- **Suggested fix:**
  - Make `Save` return `bool` (or throw) and delete files only after a successful save.
  - Make the migration idempotent: if the old file is missing but `Links\<hex>\<name>` exists, re-point the item to it.
  - Tell the user when saving fails.

## B-07 · Medium (performance) · Startup work can rescan every installed app on every launch, on the UI thread

- **Where:**
  - `App.xaml.cs:85,90-104` → `ShortcutService.MigrateLegacyPinnedTargets` (`ShortcutService.cs:410-476`)
  - `ConfigService.SanitizeConfig` (`ConfigService.cs:215-231`)
- **What happens:**
  - If a legacy item points at a pinned `.lnk` that no longer exists, and no app with the same name is found, nothing is saved (`modified` stays false). Every start then runs `ScanAllTaskbarAndSystemApps()` again on the dispatcher. That scan enumerates the whole AppsFolder over COM, walks the Start Menu recursively and parses every `.lnk` over COM. The result is a freeze of several seconds after each launch, during which the flyout and Studio do not respond.
  - `SanitizeConfig` calls `ResolveShellAppDisplayName`, which enumerates the full AppsFolder, from `LoadOrCreateDefault`. That is **before** the first flyout appears on a cold start, i.e. on the "click a pin" path. If the name cannot be resolved it is not saved, so this also repeats on every start.
- **Suggested fix:** Record that recovery was attempted (clear the legacy marker, or keep a `migrationVersion` in the config) so it runs once. Run maintenance on a background STA thread and marshal config changes back to the UI thread. Do not resolve names on the cold-start path.

## B-08 · Medium (performance, side effects) · Every Studio edit rewrites the pinned shortcuts and tells Explorer to refresh all icons

- **Where:**
  - `MainWindow.FlushPendingSave` (`MainWindow.xaml.cs:108-120`) runs after every 350 ms pause while typing a stack name or item name, and after toggling labels.
  - It calls `UpdatePinnedTaskbarShortcutForGroup` (`ShortcutService.cs:291-333`). That method sets `updated = true` for **every** matching pin whether or not anything changed, saves the `.lnk`, touches its timestamp, and broadcasts `SHCNE_ASSOCCHANGED | SHCNF_FLUSH`.
- **What happens:** `SHCNE_ASSOCCHANGED` makes Explorer rebuild its icon cache and refresh every window system-wide, so the desktop and taskbar flicker and CPU spikes. Name and label changes cannot even affect the icon.
- **Related double call:** In `OpenCustomIconPicker`, `UseFirstAppIcon_Click`, `ClearCustomIcon_Click`, `EmojiPreset_Click` and `SetAsFolderIcon_Click` (`MainWindow.xaml.cs:532-534, 554-556, 566-568, 578-580, 591-593`), setting `Style*Radio.IsChecked = true` without `_suppressEvents` fires `StyleRadio_Checked`, which calls `SyncGroupAndTaskbarIcon`. The handler then calls `SyncGroupAndTaskbarIcon` again. That is two saves, two icon renders and two flushes. `ApplyGlyph` (`:303-305`) shows the correct pattern.
- **Suggested fix:**
  - Rewrite a pin only when its icon location (or target) actually differs from the new value.
  - Flush only when something changed, preferably with `SHCNE_UPDATEITEM` on the changed file.
  - Mark a group dirty for pin updates only when the change affects the icon (icon mode, glyph, accent, cover, order of the first four items).
  - Suppress the radio button event when setting it from code.

## B-09 · Low-Medium · Studio's sidebar shows stale app counts after editing

- **Where:** `src/TaskbarGrouper/Views/MainWindow.xaml:195` binds `Items.Count` on a `List<GroupItem>`. `AppGroup.NotifyAppearanceChanged` (`GroupModels.cs:60-64`) raises only `Name` and `Thumbnail`.
- **What happens:** After importing, adding, dropping or removing items in Studio, the sidebar's "N apps in folder" keeps the old number. It is corrected only when `RefreshGroupsList` runs: adding or deleting a stack, or an edit made in a flyout.
- **Suggested fix:** Add an `ItemCount` property and raise it in `NotifyAppearanceChanged` (or raise `nameof(Items)`), or switch to an `ObservableCollection`.

## B-10 · Low · `build.ps1` packages an old exe when the build fails

- **Where:** `build.ps1:6`
- **What happens:** `$ErrorActionPreference = "Stop"` does not apply to native commands' exit codes in Windows PowerShell 5.1. If `dotnet build` fails, the script still copies the *previous* `bin\Release\net48\StackBar.exe` into `artifacts\`, zips it and prints "Success!".
- **Suggested fix:** After `dotnet build`, add `if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }`. Also check that the exe exists and its timestamp is new.

## B-11 · Low · Clicking the pin to close the flyout fails on slow starts

- **Where:** `App.xaml.cs:113-119`: a hard-coded 500 ms window between the flyout closing and the next `OPEN` request.
- **What happens:** The flyout closes when the pin is pressed (it loses activation). The `OPEN` request arrives only after Explorer has started a whole new process (CLR startup, IPC). On a loaded machine or with a cold disk cache this gap can exceed 500 ms, so the flyout immediately reopens and the user cannot dismiss it by clicking the pin.
- **Suggested fix:** Have the client send its own process start time, or a tick count captured at the start of `Main`, in the `OPEN` message. Treat the request as a toggle if the same group's flyout closed after the client process started, minus a small margin.

## B-12 · Low · A rename in progress is silently discarded when you click outside the flyout

- **Where:** `FlyoutWindow.xaml.cs:298-302` closes the flyout on `Deactivated`. Committing relies on `GroupTitleBox_LostFocus` (`:421-424`). Logical `LostFocus` is not raised when the window deactivates; only keyboard focus is lost.
- **What happens:** Clicking a tile *inside* the flyout commits the new name. Clicking *outside* closes the flyout and throws the typed name away without saying so. The two behaviours are inconsistent.
- **Suggested fix:** In `Window_Deactivated` or `Window_Closing`, call `CommitRename()` when `GroupTitleBox` is visible (or explicitly cancel, and document that choice).

## B-13 · Low · The importer ignores search text typed while it is still scanning

- **Where:** `src/TaskbarGrouper/Views/TaskbarImporterWindow.xaml.cs:66-68`
- **What happens:** While loading, `SearchBox_TextChanged` filters the empty list. When the scan finishes, the code sets `_filteredItems = _allItems` and shows every app even though the search box still contains text.
- **Suggested fix:** After loading, run the same filter as `SearchBox_TextChanged` (move it into an `ApplyFilter()` method).

## B-14 · Low · One inaccessible Start Menu subfolder drops all results for that Start Menu root

- **Where:** `ShortcutService.cs:956` uses `Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories)`. The surrounding `catch` (`:961`) discards everything.
- **What happens:** An `UnauthorizedAccessException` or `PathTooLongException` in any single subfolder of `CommonPrograms` or `Programs` throws away that root's entire list of apps.
- **Suggested fix:** Walk the folders manually (or use `EnumerateFiles` per directory) with a try/catch around each directory.

## B-15 · Low · "Run as Administrator" is offered where it cannot work

- **Where:** `FlyoutWindow.xaml.cs:196`. It hides the option only for Store apps and folders.
- **What happens:** For `.url` items (Steam games, websites) and for documents, `Verb = "runas"` throws `Win32Exception` "No application is associated with the specified file for this operation", which shows an error dialog (`:833-836`). Ctrl+Shift+click and Ctrl+Shift+1..9 hit the same path.
- **Suggested fix:** Offer elevation only for `.exe`, `.bat`, `.cmd`, `.msc`, or `.lnk` files whose target is one of those. Otherwise ignore the admin modifier.

## B-16 · Low · Studio's "Run at startup" checkbox can show the wrong state

- **Where:**
  - `src/TaskbarGrouper/Services/StartupService.cs:12-53`
  - `MainWindow.xaml.cs:80-86, 605-613`
  - `src/TaskbarGrouper/Themes/Dark.xaml:34-60`
- **What happens:**
  - `IsRunAtStartupEnabled` returns true if *any* `StackBar` Run value exists, even one pointing at an old or moved exe. The box shows "on" while startup is actually broken.
  - In Portable mode the (disabled) box shows the *installed* copy's state.
  - `SetRunAtStartup` swallows errors, but the status text always claims success.
  - The disabled checkbox's explanation tooltip (`MainWindow.xaml.cs:85`) never appears: WPF hides tooltips on disabled controls unless `ToolTipService.ShowOnDisabled="True"`.
  - The custom CheckBox template has no disabled style, so the box looks clickable but does nothing.
- **Suggested fix:** Compare the registered command with the current `AppInfo.ExePath` and offer a repair if they differ. Return success or failure from `SetRunAtStartup`. Set `ShowOnDisabled`. Add an `IsEnabled=False` trigger to the template.

## B-17 · Low · Where the flyout appears is wrong in some taskbar setups

- **Where:** `src/TaskbarGrouper/Services/NativePositioningService.cs:159-162` (edge detection) and `:91` (button size filter)
- **What happens:**
  - The taskbar edge is inferred by comparing the screen's work area with its full bounds. With an **auto-hidden** taskbar, or on a monitor without a taskbar, the two are equal, so the edge is always "Bottom". The flyout for an auto-hidden top, left or right taskbar opens at the bottom of the screen.
  - Opening a pinned stack from the keyboard (Win+1…9) or the Start menu anchors the flyout at wherever the mouse happens to be.
  - The size filter for the taskbar button is in physical pixels (`w ≤ 320`, `h ≤ 120`). At 250-300 % display scaling, buttons exceed it, so the caret falls back to the cursor's X position instead of the button's center.
- **Suggested fix:** Use `SHAppBarMessage(ABM_GETTASKBARPOS / ABM_GETSTATE)`, or find the taskbar window on the target monitor (`Shell_TrayWnd` / `Shell_SecondaryTrayWnd`). Scale the size thresholds by the monitor's DPI. When the cursor is not over a taskbar, anchor to the taskbar button (via UIA or the pin order) or center on the taskbar.

## B-18 · Low · Setup window inconsistencies

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
  - Kill only processes whose `MainModule.FileName` is under the install folder and whose session ID matches the current session.

## B-19 · Low · Two stacks with the same name overwrite each other's generated shortcut

- **Where:** `ShortcutService.CreateGroupShortcut` (`ShortcutService.cs:93-96`)
- **What happens:** Two stacks named "Gaming" both write `Shortcuts\Gaming (StackBar).lnk`. The second click on "Create shortcut" overwrites the first stack's shortcut before it is pinned.
- **Suggested fix:** Add the ID when names clash, for example `Gaming (StackBar, ab12cd34).lnk`, or always add a short suffix.

## B-20 · Low · Studio accepts empty or whitespace names

- **Where:** `MainWindow.xaml.cs:486` (stack name) and `ManagerItemRowViewModel.Name` (`MainWindow.xaml.cs:27-38`)
- **What happens:** An empty stack name gives a blank flyout title and a shortcut named after the ID. The flyout's own rename trims and rejects empty names (`FlyoutWindow.xaml.cs:389-397`), so the two editors behave differently.
- **Suggested fix:** Trim the value. Do not save an empty name: keep the previous one or show a validation hint.

## B-21 · Low · The same app can be imported twice (the README says re-imports are skipped)

- **Where:** `ShortcutService.GroupContainsSource` (`ShortcutService.cs:625-633`) and `ScanAllTaskbarAndSystemApps` (`:860-989`)
- **What happens:** One app appears up to three times in the importer (pinned `.lnk`, AppsFolder `shell:` entry, Start Menu `.lnk`), each with a different `SourcePath`. Duplicate detection compares exact `SourcePath`/`TargetPath` only (the name fallback applies only to legacy items), so selecting the app from two sources adds it twice.
- **Suggested fix:** Compare by resolved target (exe path plus arguments) or by AUMID. Collapse duplicates in the importer (see I-15 in `improvements.md`).

## B-22 · Low · Icon lookup can recurse without limit and crash the process

- **Where:** `src/TaskbarGrouper/Services/IconHelper.cs:238-250`: the `.lnk` fallback calls `ExtractBitmap` on the link's icon path, which can itself be a `.lnk`.
- **What happens:** Two shortcuts whose icon locations point at each other recurse until a `StackOverflowException`, which cannot be caught and kills the resident app. This needs a pathological pair of links, and only triggers when the shell image factory fails first, so it is unlikely but cheap to prevent.
- **Suggested fix:** Pass a depth counter (stop after 1-2 levels), or skip the fallback when the icon path is itself a `.lnk`.

## B-23 · Low (likely, not reproduced) · The flyout may stay open if it never gets focus

- **Where:** `FlyoutWindow.xaml.cs:266-275` (activation) and `:298-302` (closing on deactivation)
- **What happens:** Losing activation is the only way a click outside closes the flyout. If `Activate()`/`SetForegroundWindow` is refused (foreground lock, a slow IPC handshake, or `AllowSetForegroundWindow` failing), the top-most flyout never becomes active, so it never deactivates. It stays above everything until the user clicks into it and then out, or presses Esc, which also needs focus.
- **Suggested fix:** After activating, check `GetForegroundWindow() == hwnd`. If not, fall back to closing on the next mouse click outside the window (a short-lived low-level mouse hook or a polling timer).

---

### Test-suite problems that are bugs in themselves

- **T-01 · Low:** `AppLogger` is static. `ConfigServiceTests` points it at a temp folder in its constructor (`tests/StackBar.Tests/ConfigServiceTests.cs:12-16`), but `ShortcutServiceTests` and `IconHelperTests` do not. Depending on test order and xUnit's parallel test classes, test log lines can end up in the **developer's real** `%AppData%\StackBar\logs\stackbar.log`. Fix: use an assembly-level fixture, or make the log path injectable.
- **T-02 · Low:** `ConfigService.Save` writes `LastExePath = AppInfo.ExePath` (the test host's path) into every config, and `AppInfo.IsPortableMode` is evaluated against the test output folder. Both are hidden global inputs to the tests.

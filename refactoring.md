# StackBar Code Review: Refactoring

**Scope:** the whole repository at commit `aa43e0e`. I read every file but did not build or run anything (Linux container; the project is .NET Framework 4.8 / WPF).
**What belongs here:** structural changes that should **not** change behaviour. They reduce duplication, make the code testable and make the bug fixes in `bugs.md` easier. Behaviour changes are in `bugs.md` and `improvements.md`.

**Changes since the previous review (`a00b899`):**
- The new `ShortcutRepairService` adds three more hand-written shell-link blocks (R-01) and a third copy of the pinned-folder path (R-03).
- It reaches into `ShortcutService`'s interop types, which were made `internal` for it (R-02, R-04).
- It reads `WIN32_FIND_DATA` by hard-coded byte offsets (R-04).
- It keeps its index in mutable static fields (R-17, new).
- Line numbers are updated throughout.

**Ground rules for whoever implements this:**
- Keep the zero-dependency, single-exe goal: no NuGet packages in the app project.
- Keep .NET Framework 4.8.
- Do one refactoring per commit and run `dotnet test` after each.
- **R-01, R-09, R-10, R-12 and R-17 are the highest-value items.** They unblock fixes for B-02, B-04, B-06, B-24 to B-28 and the missing tests (I-23).

---

## R-01 · Wrap the repeated IShellLink code in one helper
The pattern "create `ShellLink` → cast to `IPersistFile` → `Load` → read or write fields → `Marshal.FinalReleaseComObject`" is now written out eight times:
- `src/TaskbarGrouper/Services/ShortcutService.cs`:
  - `CreateGroupShortcut` (`:99-132`)
  - `LinkFileMatchesGroup` (`:242-261`)
  - `UpdatePinnedTaskbarShortcutForGroup` (`:303-330`)
  - `HealPinnedTaskbarShortcuts` (`:391-420`)
  - `ParseShortcut` (`:830-860`)
- `src/TaskbarGrouper/Services/ShortcutRepairService.cs`:
  - `TryReadStoredTarget` (`:91-114`)
  - `Refresh` (`:123-146`)
  - `ReadLink` (`:298-324`)

The tests repeat it twice more (`tests/StackBar.Tests/ShortcutRepairServiceTests.cs:48-96`). `src/TaskbarGrouper/Views/SetupWindow.xaml.cs:245-261` creates shortcuts yet another way, through late-bound `WScript.Shell`.

**Do:** add an `IDisposable` `ShellLinkFile` class with:
- `static ShellLinkFile Open(path, bool writable = false)` and `static ShellLinkFile Create()`
- properties `TargetPath`, `RawTargetPath` (`SLGP_RAWPATH`), `Arguments`, `WorkingDirectory`, `IconLocation` (path and index), `Description`, `AppUserModelId` (get and set through `IPropertyStore`), `Flags` (`IShellLinkDataList`), and `StoredTargetInfo` (the `WIN32_FIND_DATA` snapshot)
- `Save()` / `Save(path)`

Use it everywhere, including the installer and the tests. That also lets you drop `WScript.Shell`. `ShortcutRepairService.ReadLink` already reads the AUMID, which is what B-02's fix needs; `Flags` and `RawTargetPath` are what B-28's fix needs.

## R-02 · Split `ShortcutService` (1090 lines, about six jobs)
`ShortcutRepairService` was split out in `aa43e0e`, which is the right direction. But it reaches back into `ShortcutService` with `using static` (`ShortcutRepairService.cs:10`) for COM types that had to be made `internal` (`ShortcutService.cs:20-83`). Suggested pieces, kept `static` at first so call sites barely change:

| New class | Moves here |
|---|---|
| `ShellLinkFile` (R-01) plus an `Interop` file (R-04) | the COM interop declarations (`ShellLink`, `IShellLinkW`, `IPropertyStore`, `PROPERTYKEY`, `PROPVARIANT`, `AppUserModelIdKey`) |
| `PinnedTaskbar` | `PinnedTaskbarDirectory`, `TryGetGroupIdFromArgs`, `IsShortcutForGroup`, `CreateGroupShortcut`, `UpdatePinnedTaskbarShortcutForGroup`, `HealPinnedTaskbarShortcuts`, `DeleteGroupShortcuts`, `TryUnpinFromTaskbar`, `NotifyTaskbarIconsChanged`, `NotifyFolderUpdated` |
| `LinkStore` | `ImportAndPersistShortcut`, `NewPersistentLinkPath`, `DeletePersistedLinkIfUnused`, `GroupContainsSource`, plus an `IsOwnCopy(path)` check (needed for B-27) |
| `ConfigMigrations` | `MigrateFlatLinkCopies`, `MigrateLegacyPinnedTargets`, plus `ConfigService.SanitizeConfig` (see R-11) |
| `AppDiscovery` | `ScanAllTaskbarAndSystemApps`, `ResolveShellAppDisplayName`, the AppsFolder name cache, `DiscoveredAppItem` |
| `ShortcutIndex` (R-17) | one shared, background-refreshed index of Start Menu and pinned shortcuts, used by both `AppDiscovery` and the repair feature (today both walk the Start Menu separately: `ShortcutService.cs:969-986` and `ShortcutRepairService.cs:268-294`) |
| `CoverIconStore` | `PersistCustomFolderIcon` (both overloads), `SaveCustomFolderBitmap`, `NewCustomIconPath`, `CustomIconFiles`, `DeleteOldCustomIcons` |

## R-03 · Remove copy-pasted logic
| Duplicate | Where | Replace with |
|---|---|---|
| Stripping the 8-hex-character `_` prefix from names | `ShortcutService.CleanItemName` (`:505-514`), inline copies in `src/TaskbarGrouper/Services/ConfigService.cs:209-213` and `ShortcutService.cs:453-456`, and a file-name variant at `:726` | one `NameCleaner.TryStripHexPrefix(string, out string)` |
| Choosing the data directory (portable vs `%AppData%`) | `AppLogger.Initialize` (`src/TaskbarGrouper/Services/AppInfo.cs:59-61`), `AppLogger.Log` (`:88-90`), `ConfigService.DefaultDataDirectory` (`ConfigService.cs:43-49`) | `AppInfo.DataDirectory` |
| The `"shell:AppsFolder\\"` literal plus `StartsWith` checks | `ConfigService.cs:215`; `ShortcutService.cs:519-520, 602, 779, 955`; `src/TaskbarGrouper/Views/FlyoutWindow.xaml.cs:799` | `ShellPaths.AppsFolderPrefix` and `ShellPaths.IsShellApp(path)` |
| The pinned-taskbar folder path, now written three times | `ShortcutService.cs:141-143`, again at `:908-910`, and `ShortcutRepairService.cs:59-60` | one `ShellPaths.PinnedTaskbar` |
| The list of Start Menu folders | `ShortcutService.cs:969-973` and `ShortcutRepairService.cs:57-58` | one `ShellPaths.StartMenuFolders` |
| "All `.lnk` paths in the config" | `ShortcutRepairService.cs:221-224` and `:243-244` | one `LinkStore.OwnCopies(config)` that also applies the location check from B-27 |
| Enumerating the AppsFolder through `Shell.Application` | `ShortcutService.cs:530-561` and `:927-966` | one `EnumerateAppsFolder()` that also fills the name cache |
| Writing the Run key for startup | `src/TaskbarGrouper/Services/StartupService.cs` and `SetupWindow.xaml.cs:89-95, 157-160` | `StartupService.SetRunAtStartup(bool, string exePath)` |
| The `SHGetFileInfo` → `Icon.FromHandle` → `DestroyIcon` block | `src/TaskbarGrouper/Services/IconHelper.cs:138-160` and `:253-275` | `TryGetShellIcon(path)` |
| Lists of image file extensions | `IconHelper.cs:126-130, 166-169, 184`; `ShortcutService.cs:1039` | one shared `ImageFormats` set |
| Accent colour → background gradient maths | `IconHelper.cs:430` and `FlyoutWindow.xaml.cs:239-243` | `AccentPalette.Background(accent)` |
| "Ctrl+Shift held means run as admin" | `FlyoutWindow.xaml.cs:341` and `:740` | `IsAdminModifierPressed()` |
| Identical launch code for folders and Store apps | `FlyoutWindow.xaml.cs:789-807` | one branch |
| Tray/IPC shutdown cleanup | `src/TaskbarGrouper/App.xaml.cs:203-217` and `:219-233` | `CleanupResidentServices()` |
| The "apply this cover" sequence | `src/TaskbarGrouper/Views/MainWindow.xaml.cs:528-537, 550-559, 587-596` | `ApplyCustomCover(string persistedPath, string statusText)` |
| Choosing an emoji | `EmojiPreset_Click` (`MainWindow.xaml.cs:573-582`) repeats `ApplyGlyph` (`:298-309`), but without the event suppression | call `ApplyGlyph` |
| Move up / move down | `MainWindow.xaml.cs:790-816` | `MoveItem(row, delta)` |

## R-04 · Put all P/Invoke and COM declarations in one place
Native declarations are spread across seven classes:
- `src/TaskbarGrouper/Program.cs:109-110`
- `src/TaskbarGrouper/Services/IpcService.cs:15-22` (it also exposes a **public** `SetForegroundWindow` that `FlyoutWindow` uses)
- `src/TaskbarGrouper/Services/NativePositioningService.cs`
- `ShortcutService.cs:20-83, 137-138` (COM types now `internal` and shared with the repair service)
- `ShortcutRepairService.cs:327-328` (`PropVariantClear`)
- `IconHelper.cs:49-82`
- `MainWindow.xaml.cs:249-254` (`keybd_event`, which is deprecated; use `SendInput`)

Move them into an `internal static class NativeMethods` (or a few, grouped by DLL) plus an `Interop` file for the COM interfaces. Wrap `HICON` and `HBITMAP` in `SafeHandle` subclasses so the try/finally `DestroyIcon`/`DeleteObject` blocks disappear.

Replace the hand-computed `WIN32_FIND_DATA` reads in `ShortcutRepairService.cs:22, 91, 101-103` (buffer size `592`, byte offsets `20`, `24`, `28`, `32`) with a `[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] WIN32_FIND_DATAW` struct and an `IShellLinkW.GetPath` overload that takes `out WIN32_FIND_DATAW`. The offsets are correct today, but a struct is self-documenting, zero-initialised, and can't drift.

## R-05 · Use typed interop instead of `dynamic` COM
`dynamic` and `Activator.CreateInstance(Type.GetTypeFromProgID(...))` are used for `Shell.Application` (`ShortcutService.cs:267-273, 532-541, 929-938`) and `WScript.Shell` (`SetupWindow.xaml.cs:249-258`). Late binding is slow, the COM objects are never released, and errors only appear at runtime.

- Use `SHGetKnownFolderItem(FOLDERID_AppsFolder)` + `IShellItem` / `IEnumShellItems` for the AppsFolder.
- Use `IContextMenu` (or keep one small typed `Shell32.Folder` wrapper) for the unpin verb.
- Use R-01 for creating shortcuts.

Afterwards the `Microsoft.CSharp` reference (`src/TaskbarGrouper/TaskbarGrouper.csproj:18`) can go. The hand-written `IAccessible` (`NativePositioningService.cs:41-78`) can be replaced by referencing `Accessibility.dll`, which is part of the framework.

## R-06 · Delete dead code and unused members
- `GroupItem.IsBroken` (`src/TaskbarGrouper/Models/GroupModels.cs:203`): never read or written. Delete it, or put it to use (I-05).
- `AppConfig.LastExePath` (`GroupModels.cs:33-34`): written in `ConfigService.cs:94,153`, never read. Delete it, or use it to detect that the exe moved (instead of comparing every pin's target).
- `Polyfills.ContainsIgnoreCase` / `EqualsIgnoreCase` (`src/TaskbarGrouper/Common/Polyfills.cs:26-35`): unused.
- The `IsExternalInit` polyfill (`Polyfills.cs:1-4`): no `init` accessors exist (keep it if you adopt R-12's record).
- `FlyoutBackgroundBrush`, `FlyoutBorderBrush`, `ItemHoverBrush` (`src/TaskbarGrouper/Themes/Dark.xaml:4-6`): unused. Either delete them or make the hard-coded colours in the XAML refer to them (see R-13).
- Unused `SIIGBF` flags and probably unused `using` directives (e.g. `System.Windows` and `System.Windows.Interop` in `IconHelper.cs`). Let the IDE's "remove unused usings" decide rather than removing them by hand.

## R-07 · One type per file and consistent names
- `AppLogger` lives in `AppInfo.cs`.
- `DiscoveredAppItem` lives in `ShortcutService.cs`.
- `FlyoutItemViewModel` and `DragAdorner` live in `FlyoutWindow.xaml.cs`.
- `ManagerItemRowViewModel` lives in `MainWindow.xaml.cs`.
- `SelectableShortcutViewModel` lives in `TaskbarImporterWindow.xaml.cs`.

Move each into its own file (`ViewModels/`, `Controls/`).

Naming is inconsistent: the product is *StackBar*, but the project, root namespace and solution are `TaskbarGrouper`, and `Polyfills` sits in `StackBar.Common`. Pick one, presumably `StackBar`; this is a mechanical rename that changes nothing at runtime. Note that the assembly name `StackBar` and the AUMIDs `StackBar.App` / `StackBar.Group.*` must stay as they are.

## R-08 · Thinner code-behind, real view models (step by step)
`MainWindow.xaml.cs` (902 lines) keeps controls and model in sync by hand, using a `_suppressEvents` flag that is easy to forget (the cause of B-08's double save). It also rebuilds `ItemsSource` after every change, and uses bindings that do not notify (the cause of B-09).

**Suggested steps (not a rewrite):**
1. Add `StackViewModel` (wrapping `AppGroup`, implements `INotifyPropertyChanged`, has `ItemCount`, `Thumbnail` and an `ObservableCollection<ItemRowViewModel>`) and bind the sidebar and the editor to it.
2. Move the save and pin-sync policy into one `StackEditor` service with `MarkChanged(group, ChangeKind)`. `ChangeKind` tells it whether the change affects the icon, so the rest of B-08 is fixed in one place.
3. Use commands (or keep Click handlers that call one view-model method each).

The flyout already has a view model (`FlyoutItemViewModel`); bring its `Name`/`Icon` properties in line so they notify too.

`AppGroup.Thumbnail` (`GroupModels.cs:139`) calls `IconHelper` from inside the data model. Move that to the view model so the model stays a plain data object.

## R-09 · Separate the flyout position maths from Win32 calls
`NativePositioningService.PositionWindow` (`NativePositioningService.cs:194-238`) mixes Win32 calls with pure geometry. Extract:

```csharp
internal static (int leftPx, int topPx, double caretOffsetDip) ComputeFlyoutPlacement(
    TaskbarEdge edge, Rectangle workArea, int targetCenterX, int cursorY, Size windowPx, double scale);
```

and leave only `SetWindowPos` / `GetWindowRect` in `PositionWindow`. Do the same for the caret clamp in `FlyoutWindow.Window_Loaded` (`FlyoutWindow.xaml.cs:261-265`). Its constants `14` and `8` silently depend on the XAML margin and the polygon width; use named constants or read the values from the elements. Both become unit-testable.

## R-10 · Seams that make the risky logic testable
The pin, migration, link-store, repair and uninstall logic is all `static` and writes straight to real locations (`%AppData%`, the Start Menu, `User Pinned\TaskBar`, HKCU). Without changing behaviour:
- Pass the pinned-taskbar directory, the Start Menu folders and the data directory in as parameters. Most methods already take `appDataDir`.
- `ShortcutRepairService.RegisteredShortcutFolders` (`ShortcutRepairService.cs:48`) shows the idea, but as a mutable static it leaks between tests (T-03). Prefer a parameter or an options object.
- Hide the shell-link calls behind a tiny interface (`IShellLinkReader` / `Writer` from R-01) so tests can use in-memory links.
- Make portable-mode detection a pure function: `bool IsPortable(string baseDir, string exeName, Func<string,bool> fileExists, Func<string,bool> dirExists)`.
- Make uninstall path validation a pure function: `bool IsSafeToDelete(string dir, string expectedExe)`.
- Make IPC message parsing a pure function: `bool TryParse(string line, out IpcCommand cmd)`.
- Make `AppLogger`'s path injectable, or give it a test hook (fixes T-01 in `bugs.md`).

## R-11 · Clearer contracts for `ConfigService`
- `Save` should return `bool`, or throw a typed exception, instead of catching and logging only the message (`ConfigService.cs:149-190`). Callers decide what to do.
- `SanitizeConfig` is a migration step (strip prefixes, resolve names) that runs on every load. Move it next to the other migrations (R-02) behind a schema version (I-06).
- The two-properties-per-field scheme for old capitalised JSON names (`GroupModels.cs:39-45, 77-78, 83-84, …`) doubles the model's size. Alternatives: a one-time migration that rewrites old files with lowercase names once (then remove the `Legacy*` properties in a later release), or a small `IDataContractSurrogate`. Add explicit enum values while you are there (I-06).
- `LoadOrCreateDefault` mixes four things: loading, recovering from corruption, creating the default stacks, and saving. Split into `TryLoad`, `RecoverFromBackup` and `CreateDefaultConfig()`.

## R-12 · Replace `ParseShortcut`'s overloaded `GroupItem` with a dedicated result type
`ParseShortcut` returns a `GroupItem` whose `TargetPath` is the **shortcut itself**, while the real target sometimes ends up in `CustomIconPath` (`ShortcutService.cs:763-882`). That mixed meaning is how B-04 happened. Return a `ShortcutInfo` instead:

```csharp
record ShortcutInfo(string LinkPath, string? Target, string Arguments, string WorkingDirectory,
                    string? IconPath, int IconIndex, string DisplayName, ShortcutKind Kind);
```

and build the `GroupItem` from it in `ImportAndPersistShortcut`. Records need `IsExternalInit`, which is already polyfilled; if you don't want a record, use a small class. `ShortcutRepairService.LinkInfo` (`ShortcutRepairService.cs:35-40`) is a smaller version of the same idea; merge the two.

## R-13 · XAML and styling
- `MainWindow.xaml`, `FlyoutWindow.xaml` and the setup and importer windows repeat many literal colours (`#3A6DF0` alone appears 22 times under `src/`) and button templates. Move them to `Themes/Dark.xaml` (`AccentBrush`, `SurfaceBrush`, `PrimaryBtn`, `SurfaceBtn`, `IconButton`, …) and use them in every window. That fixes the unreadable hover state in the importer and setup windows (B-30), and makes the accent-coloured buttons from I-14 a one-line change.
- None of Studio's button styles (`MainWindow.xaml:15-87`) has a disabled look (part of B-29), and the `DarkTextBox` template (`:89-110`) never applies the style's `Padding="8,5"` (`:95`) because `PART_ContentHost` (`:100`) ignores it.
- The global `ScrollBar` style (`Dark.xaml:9-31`) sets `Width=8` and has a vertical-only `Track`, with no trigger for horizontal orientation. Any horizontal scrollbar would render incorrectly. All are disabled today, so this is a trap for later. Add an `Orientation=Horizontal` trigger or set the style's key explicitly.
- The global `CheckBox` template (`Dark.xaml:34-60`) has no disabled or keyboard-focus visuals (part of B-16), and its horizontal `StackPanel` prevents content from stretching (part of B-30).
- Hard-coded tile sizes (`82`/`62` in `FlyoutWindow.xaml.cs:194` and `Width="82"` in the XAML) belong in one resource.

## R-14 · Name the magic numbers
| Value | Where | Meaning |
|---|---|---|
| `450` ms | `FlyoutWindow.xaml.cs:479` | long-press delay |
| `500` ms | `App.xaml.cs:122` | click-toggle window (also see B-11) |
| `350` ms | `MainWindow.xaml.cs:66` | save debounce |
| `100` / `1500` ms | `IpcService.SendCommand` default and `Program.cs:93` | IPC connect timeouts |
| `-36` | `FlyoutWindow.xaml.cs:120` | half the size of the 72 px ghost tile |
| `1024 * 1024` | `AppInfo.cs:71` | log roll size |
| `20` × `50` ms | `ShortcutService.cs:276-279` | unpin wait loop |

Put them in a `Timings` / `Layout` static class, or as `const`s next to where they are used, with a one-line comment each. (`ShortcutRepairService` already names its own timings, e.g. `IndexMaxAge` and `TimestampTolerance`, which is the pattern to follow.)

## R-15 · Smaller cleanups inside the flyout
- A new `DispatcherTimer` is created on every press (`FlyoutWindow.xaml.cs:478-485`). Create one in the constructor and restart it.
- Move the drag-and-drop reorder code (about 200 lines: press tracking, the OLE drag loop, the slide animation, the ghost tile) into a reusable `ReorderBehavior` class attached to the `ItemsControl`. `FlyoutWindow` then only deals with launching and renaming.
- `PopulateViewModels` clears and rebuilds everything after each reorder or delete. With R-08, update the collection in place.
- Move the launch logic, including the new `PrepareLaunch` call (`FlyoutWindow.xaml.cs:784-846`), into a `LaunchService` that is testable and can run the pre-launch repair off the UI thread (B-24).

## R-16 · Build script
`build.ps1` hard-codes `bin\Release\net48` and duplicates the staging logic.
- Read the output path from MSBuild (`dotnet build -getProperty:TargetPath` on newer SDKs, or set `-o` explicitly).
- Fail on errors (B-10).
- Optionally run `dotnet test` before staging.
- Take the version from the csproj (I-20).

## R-17 · New · Turn the repair service's static state into objects
`ShortcutRepairService` keeps its index in static fields (`_index`, `_indexBuiltUtc`, `_indexLock`, `ShortcutRepairService.cs:42-45`), with a mutable static test seam (`:48`) and a reset method that exists only for tests (`:50-53`). It also mixes four jobs:
- detecting stale links (`IsStale`, `TryReadStoredTarget`),
- changing links (`Refresh`),
- managing backups (inline in `RepairIfStale`),
- indexing the Start Menu (`GetIndex`).

**Split:**
- `ShortcutIndex`: owned by `App`, created with the folders to watch. It refreshes in the background (timer or `FileSystemWatcher`), and exposes a snapshot plus `Invalidate()`. It can also feed the importer (R-02).
- `ShortcutBackupStore`: takes the backups folder. Handles unique names, the manifest, pruning and restore (B-26, I-24).
- `ShortcutRepairer`: takes the index, the backup store and an "is this an own copy" predicate (B-27). Has `PrepareLaunch`, `RepairAll`, and a per-session list of links it could not change (B-25).

No behaviour has to change for the split itself. The B-24 to B-27 fixes then each touch a single class, and tests no longer need a static reset.

---

### Suggested order
1. **R-01 → R-12 → R-02 (partly: `PinnedTaskbar`, `LinkStore`)**, then fix B-02, B-04 and B-06 on top.
2. **R-17** (with R-01 in place), then fix B-24 to B-28 on top.
3. **R-10 + R-09**, then add the missing tests (I-23).
4. **R-03, R-04, R-06, R-14** (mechanical, low risk).
5. **R-08, R-11, R-13, R-15** (larger; do them gradually and check the UI by hand after each).
6. **R-05, R-07, R-16** whenever convenient.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using TaskbarGrouper.Models;
using TaskbarGrouper.Services;
using TaskbarGrouper.Views;

namespace TaskbarGrouper
{
    public partial class App : Application
    {
        private ConfigService _configService = null!;
        private AppConfig _config = null!;
        private IpcService? _ipcService;
        private System.Windows.Forms.NotifyIcon? _trayIcon;
        private MainWindow? _mainWindow;
        private FlyoutWindow? _activeFlyout;
        private bool _isExiting;
        private string? _lastClosedGroupId;
        private DateTime? _lastClosedTimeUtc;
        private bool _lastClosedByTaskbarClick;

        public void RunApplication(string? groupId, bool startMinimized, bool isSetup, bool isUninstall)
        {
            InitializeComponent();

            DispatcherUnhandledException += (_, args) =>
            {
                if (_isExiting)
                {
                    args.Handled = true;
                    return;
                }
                AppLogger.Log("Unhandled UI exception: " + args.Exception);
                MessageBox.Show($"StackBar encountered an error:\n{args.Exception.Message}", "StackBar", MessageBoxButton.OK, MessageBoxImage.Warning);
                args.Handled = true;
            };

            if (isUninstall)
            {
                SetupWindow.PerformUninstall();
                Shutdown();
                return;
            }

            if (isSetup)
            {
                var setup = new SetupWindow();
                setup.Closed += (_, __) => Shutdown();
                setup.Show();
                Run();
                return;
            }

            // One config instance is shared by Studio and every flyout, so an edit in one window
            // can never be overwritten by a stale copy saved from another.
            _configService = new ConfigService();
            _config = _configService.LoadOrCreateDefault();
            OfferLegacyDataImport();

            // Cheap (a few file copies, once) and must finish before any flyout launches an item.
            // The old copies are deleted only once the config pointing at the new ones is safely on disk.
            if (ShortcutService.MigrateFlatLinkCopies(_config, _configService.AppDataDirectory, out var obsoleteCopies) &&
                _configService.Save(_config))
            {
                ShortcutService.DeleteFiles(obsoleteCopies);
            }

            // Start resident IPC listener
            _ipcService = new IpcService(OnIpcOpenGroupRequested, () => ShowManagerWindow(null), ExitApplication);
            InitializeTrayIcon();

            if (!string.IsNullOrEmpty(groupId))
            {
                // Cold start from a pinned folder (no resident instance was running)
                NativePositioningService.GetCursorPos(out var pt);
                OpenGroupFlyout(groupId!, pt.X, pt.Y);
            }
            else if (!startMinimized)
            {
                ShowManagerWindow(null);
            }

            // Healing pinned shortcuts and migrating links touches every pinned .lnk and may scan all
            // installed apps; do it once the first window is up so a cold-start click opens instantly.
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(RunStartupMaintenance));

            Run();
        }

        /// <summary>
        /// Older versions treated any install folder that had a "data" subfolder as Portable Edition and kept
        /// the stacks there. Since portable mode now needs the explicit marker, those stacks would seem to
        /// vanish; offer (once) to bring them over.
        /// </summary>
        private void OfferLegacyDataImport()
        {
            if (AppInfo.IsPortableMode) return;
            try
            {
                string legacyDir = Path.Combine(AppInfo.BaseDirectory, "data");
                string marker = Path.Combine(_configService.AppDataDirectory, "legacy-data-checked");
                if (File.Exists(marker) || !File.Exists(Path.Combine(legacyDir, "groups.json"))) return;
                File.WriteAllText(marker, legacyDir);

                var answer = MessageBox.Show(
                    $"StackBar found stacks saved by an earlier version in:\n{legacyDir}\n\n" +
                    "Load them now? Your current stacks will be replaced (the old folder is left untouched).",
                    "StackBar", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;

                var imported = _configService.TryImportFrom(legacyDir);
                if (imported != null)
                {
                    _config = imported;
                }
                else
                {
                    MessageBox.Show("Those stacks could not be imported (see the log for details).", "StackBar",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log("Legacy data import failed: " + ex);
            }
        }

        private void RunStartupMaintenance()
        {
            try
            {
                // One-time: may scan every installed app, so it must not run again on each start
                if (_config.MigrationVersion < AppConfig.CurrentMigrationVersion)
                {
                    if (ShortcutService.MigrateLegacyPinnedTargets(_config, _configService))
                    {
                        _configService.RaiseGroupChanged(string.Empty);
                    }
                    _config.MigrationVersion = AppConfig.CurrentMigrationVersion;
                    _configService.Save(_config);
                }

                // Everything else touches many files over COM: run it off the UI thread, on independent copies
                var groups = _config.Groups.Select(g => g.CloneForReading()).ToList();
                var itemPaths = groups.SelectMany(g => g.Items).Select(i => i.TargetPath).ToList();
                var technicalNames = groups.SelectMany(g => g.Items).Where(ConfigService.NeedsFriendlyName)
                    .Select(i => i.TargetPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                bool repairAppShortcuts = _config.RepairAppShortcuts;
                string dataDir = _configService.AppDataDirectory;

                RunOnStaThread(() =>
                {
                    ShortcutService.HealPinnedTaskbarShortcuts(groups, dataDir);
                    // StackBar's own copies are always kept current; other apps' shortcuts only if the user allows it
                    ShortcutRepairService.RefreshOwnCopies(itemPaths, dataDir);
                    if (repairAppShortcuts) ShortcutRepairService.RefreshIndexInBackground();

                    var resolved = technicalNames
                        .Select(t => new { Target = t, Name = ShortcutService.ResolveShellAppDisplayName(t) })
                        .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                        .ToDictionary(x => x.Target, x => x.Name!, StringComparer.OrdinalIgnoreCase);
                    if (resolved.Count > 0)
                    {
                        Dispatcher.BeginInvoke(new Action(() => ApplyFriendlyNames(resolved)));
                    }
                });
            }
            catch (Exception ex)
            {
                AppLogger.Log("Startup maintenance failed: " + ex);
            }
        }

        private static void RunOnStaThread(Action work)
        {
            var thread = new System.Threading.Thread(() =>
            {
                try { work(); }
                catch (Exception ex) { AppLogger.Log("Background maintenance failed: " + ex); }
            })
            {
                IsBackground = true,
                Name = "StackBar_Maintenance"
            };
            // Shell.Application and the shell link objects are apartment-threaded COM
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
        }

        private void ApplyFriendlyNames(Dictionary<string, string> namesByTarget)
        {
            bool changed = false;
            foreach (var item in _config.Groups.SelectMany(g => g.Items))
            {
                if (ConfigService.NeedsFriendlyName(item) && namesByTarget.TryGetValue(item.TargetPath, out var name) && name != item.Name)
                {
                    item.Name = name;
                    changed = true;
                }
            }
            if (changed && _configService.Save(_config))
            {
                _configService.RaiseGroupChanged(string.Empty);
            }
        }

        private void OnIpcOpenGroupRequested(string groupId, int cursorX, int cursorY, DateTime? clientStartUtc)
        {
            Dispatcher.BeginInvoke(new Action(() => OpenGroupFlyout(groupId, cursorX, cursorY, clientStartUtc)));
        }

        /// <summary>
        /// Clicking the pin while the flyout is open must close it, not reopen it. The click first deactivates
        /// (closes) the flyout, then Explorer starts a new StackBar process that sends OPEN. That can take well
        /// over half a second on a busy machine, so instead of a fixed window the client's own start time is
        /// used: a close that happened around or after the client started was caused by the same click.
        /// </summary>
        /// <param name="closedByTaskbarClick">
        /// The flyout lost focus to the taskbar. Only then may a close well before the client started count
        /// (the pin click deactivates the flyout before Explorer even starts the process); a flyout dismissed
        /// anywhere else gets a tight window, so re-clicking the pin right after dismissing it opens it again.
        /// </param>
        internal static bool IsToggleClose(DateTime? lastClosedUtc, DateTime? clientStartUtc, DateTime nowUtc, bool closedByTaskbarClick)
        {
            if (!lastClosedUtc.HasValue) return false;
            if (clientStartUtc.HasValue)
            {
                var allowance = closedByTaskbarClick ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(300);
                return lastClosedUtc.Value >= clientStartUtc.Value - allowance &&
                       nowUtc - lastClosedUtc.Value < TimeSpan.FromSeconds(15);
            }
            return nowUtc - lastClosedUtc.Value < TimeSpan.FromMilliseconds(500); // older clients send no start time
        }

        private void OpenGroupFlyout(string groupId, int cursorX, int cursorY, DateTime? clientStartUtc = null)
        {
            if (string.Equals(_lastClosedGroupId, groupId, StringComparison.OrdinalIgnoreCase) &&
                IsToggleClose(_lastClosedTimeUtc, clientStartUtc, DateTime.UtcNow, _lastClosedByTaskbarClick))
            {
                _lastClosedGroupId = null;
                return;
            }

            var group = _config.Groups.FirstOrDefault(g => g.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
            if (group == null)
            {
                MessageBox.Show(
                    "This StackBar folder no longer exists.\n\nRight-click its taskbar icon and choose \"Unpin from taskbar\".",
                    "StackBar", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_activeFlyout != null && _activeFlyout.IsVisible)
            {
                _activeFlyout.Close();
            }

            var flyout = new FlyoutWindow(group, _config, _configService, cursorX, cursorY, id => ShowManagerWindow(id));
            _activeFlyout = flyout;
            flyout.Closed += (_, __) =>
            {
                _lastClosedGroupId = groupId;
                _lastClosedTimeUtc = DateTime.UtcNow;
                _lastClosedByTaskbarClick = flyout.ClosedByTaskbarClick;
                if (_activeFlyout == flyout) _activeFlyout = null;
            };
            flyout.Show();
        }

        private void ShowManagerWindow(string? selectGroupId)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_mainWindow != null && _mainWindow.IsVisible)
                {
                    if (_mainWindow.WindowState == WindowState.Minimized)
                    {
                        _mainWindow.WindowState = WindowState.Normal;
                    }
                    if (!string.IsNullOrEmpty(selectGroupId))
                    {
                        _mainWindow.SelectGroupById(selectGroupId);
                    }
                    _mainWindow.Activate();
                    return;
                }

                _mainWindow = new MainWindow(_config, _configService, selectGroupId);
                _mainWindow.Show();
                _mainWindow.Activate();
            }));
        }

        private void InitializeTrayIcon()
        {
            Icon trayIco = SystemIcons.Application;
            try
            {
                var extracted = Icon.ExtractAssociatedIcon(AppInfo.ExePath);
                if (extracted != null) trayIco = extracted;
            }
            catch { }

            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "StackBar — Taskbar Folders",
                Icon = trayIco,
                Visible = true
            };

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open StackBar Studio", null, (_, __) => ShowManagerWindow(null));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Exit StackBar", null, (_, __) => ExitApplication());

            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (_, __) => ShowManagerWindow(null);
        }

        private void ExitApplication()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isExiting) return;
                _isExiting = true;
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                }
                _ipcService?.Dispose();
                Shutdown();
            }));
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (!_isExiting)
            {
                _isExiting = true;
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                }
                _ipcService?.Dispose();
            }
            base.OnExit(e);
        }
    }
}

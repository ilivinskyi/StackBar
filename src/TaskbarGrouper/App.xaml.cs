using System;
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
        private DateTime _lastClosedTime = DateTime.MinValue;

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

            // Cheap (a few file copies, once) and must finish before any flyout launches an item
            if (ShortcutService.MigrateFlatLinkCopies(_config, _configService.AppDataDirectory))
            {
                _configService.Save(_config);
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

        private void RunStartupMaintenance()
        {
            try
            {
                ShortcutService.HealPinnedTaskbarShortcuts(_config, _configService.AppDataDirectory);
                if (ShortcutService.MigrateLegacyPinnedTargets(_config, _configService))
                {
                    _configService.RaiseGroupChanged(string.Empty);
                }

                // StackBar's own copies are always kept current; other apps' shortcuts only if the user allows it
                ShortcutRepairService.RefreshOwnCopies(_config);
                if (_config.RepairAppShortcuts)
                {
                    ShortcutRepairService.WarmIndexInBackground();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log("Startup maintenance failed: " + ex);
            }
        }

        private void OnIpcOpenGroupRequested(string groupId, int cursorX, int cursorY)
        {
            Dispatcher.BeginInvoke(new Action(() => OpenGroupFlyout(groupId, cursorX, cursorY)));
        }

        private void OpenGroupFlyout(string groupId, int cursorX, int cursorY)
        {
            // Click-to-toggle: if the same group's flyout just closed (because the pin click deactivated it), keep it closed
            if (string.Equals(_lastClosedGroupId, groupId, StringComparison.OrdinalIgnoreCase) &&
                (DateTime.Now - _lastClosedTime).TotalMilliseconds < 500)
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
                _lastClosedTime = DateTime.Now;
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

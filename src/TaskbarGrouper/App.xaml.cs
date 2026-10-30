using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows;
using TaskbarGrouper.Models;
using TaskbarGrouper.Services;
using TaskbarGrouper.Views;

namespace TaskbarGrouper
{
    public partial class App : Application
    {
        private ConfigService _configService = null!;
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

            _configService = new ConfigService();
            var config = _configService.LoadOrCreateDefault();

            // Auto-heal pinned taskbar shortcuts if exe path changed
            ShortcutService.HealPinnedTaskbarShortcuts(config, _configService.AppDataDirectory);

            // Fast path (cold start fallback when no tray resident was running)
            if (!string.IsNullOrEmpty(groupId))
            {
                NativePositioningService.GetCursorPos(out var pt);
                var group = config.Groups.FirstOrDefault(g => g.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))
                            ?? config.Groups.FirstOrDefault();
                if (group != null)
                {
                    var flyout = new FlyoutWindow(group, pt.X, pt.Y, ShowManagerWindow);
                    flyout.Closed += (_, __) =>
                    {
                        // Keep resident in background
                    };
                    flyout.Show();
                }
            }

            // Start resident IPC listener
            _ipcService = new IpcService(OnIpcOpenGroupRequested, ShowManagerWindow, ExitApplication);
            InitializeTrayIcon();

            if (!startMinimized && string.IsNullOrEmpty(groupId))
            {
                ShowManagerWindow();
            }

            Run();
        }

        private void OnIpcOpenGroupRequested(string groupId, int cursorX, int cursorY)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                // Click-to-toggle logic: If same group was just closed within 500ms, keep it closed
                if (string.Equals(_lastClosedGroupId, groupId, StringComparison.OrdinalIgnoreCase) &&
                    (DateTime.Now - _lastClosedTime).TotalMilliseconds < 500)
                {
                    _lastClosedGroupId = null;
                    return;
                }

                var config = _configService.LoadOrCreateDefault();
                var group = config.Groups.FirstOrDefault(g => g.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))
                            ?? config.Groups.FirstOrDefault();
                if (group == null) return;

                if (_activeFlyout != null && _activeFlyout.IsVisible)
                {
                    _activeFlyout.Close();
                }

                _activeFlyout = new FlyoutWindow(group, cursorX, cursorY, ShowManagerWindow);
                _activeFlyout.Closed += (_, __) =>
                {
                    _lastClosedGroupId = groupId;
                    _lastClosedTime = DateTime.Now;
                };
                _activeFlyout.Show();
            }));
        }

        private void ShowManagerWindow()
        {
            Dispatcher.Invoke(new Action(() =>
            {
                if (_mainWindow != null && _mainWindow.IsVisible)
                {
                    _mainWindow.Activate();
                    return;
                }

                var config = _configService.LoadOrCreateDefault();
                _mainWindow = new MainWindow(config, _configService);
                _mainWindow.Show();
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
            menu.Items.Add("Open StackBar Studio", null, (_, __) => ShowManagerWindow());
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Exit StackBar", null, (_, __) => ExitApplication());

            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (_, __) => ShowManagerWindow();
        }

        private void ExitApplication()
        {
            Dispatcher.Invoke(new Action(() =>
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

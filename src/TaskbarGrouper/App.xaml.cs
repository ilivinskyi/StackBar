using System.Drawing;
using System.Windows;
using TaskbarGrouper.Models;
using TaskbarGrouper.Services;
using TaskbarGrouper.Views;

namespace TaskbarGrouper;

public partial class App : System.Windows.Application
{
    private ConfigService _configService = null!;
    private IpcService? _ipcService;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private FlyoutWindow? _activeFlyout;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _configService = new ConfigService();

        // Check command line args: e.g., --group "gaming"
        string? groupId = null;
        bool startMinimized = false;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i].Equals("--group", StringComparison.OrdinalIgnoreCase) && i + 1 < e.Args.Length)
            {
                groupId = e.Args[i + 1];
            }
            else if (e.Args[i].Equals("--tray", StringComparison.OrdinalIgnoreCase))
            {
                startMinimized = true;
            }
        }

        // Fast-path: If launched with --group <id>, try signaling the warm background instance (<15ms) and exit immediately
        if (!string.IsNullOrEmpty(groupId))
        {
            NativePositioningService.GetCursorPos(out var pt);
            if (IpcService.TrySignalWarmInstance(groupId, pt.X, pt.Y))
            {
                Shutdown();
                return;
            }

            // No warm instance running yet: show the flyout directly
            var config = _configService.LoadOrCreateDefault();
            var group = config.Groups.FirstOrDefault(g => g.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))
                        ?? config.Groups.First();
            var flyout = new FlyoutWindow(group, pt.X, pt.Y);
            flyout.Closed += (_, _) => Shutdown();
            flyout.Show();
            return;
        }

        // Resident / Manager Mode: Start Named Pipe listener + System Tray icon for <15ms instant flyout response
        _ipcService = new IpcService(OnIpcOpenGroupRequested);
        InitializeTrayIcon();

        if (!startMinimized)
        {
            ShowManagerWindow();
        }
    }

    private void OnIpcOpenGroupRequested(string groupId, int cursorX, int cursorY)
    {
        Dispatcher.Invoke(() =>
        {
            var config = _configService.LoadOrCreateDefault();
            var group = config.Groups.FirstOrDefault(g => g.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))
                        ?? config.Groups.FirstOrDefault();
            if (group == null) return;

            if (_activeFlyout is { IsVisible: true })
            {
                _activeFlyout.Close();
            }

            _activeFlyout = new FlyoutWindow(group, cursorX, cursorY);
            _activeFlyout.Show();
        });
    }

    private void ShowManagerWindow()
    {
        if (_mainWindow is { IsVisible: true })
        {
            _mainWindow.Activate();
            return;
        }

        var config = _configService.LoadOrCreateDefault();
        _mainWindow = new MainWindow(config, _configService);
        _mainWindow.Show();
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "StackBar — Instant Taskbar Folders",
            Icon = SystemIcons.Application,
            Visible = true
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open StackBar Manager", null, (_, _) => Dispatcher.Invoke(ShowManagerWindow));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit StackBar", null, (_, _) => Dispatcher.Invoke(() =>
        {
            _trayIcon.Visible = false;
            _ipcService?.Dispose();
            Shutdown();
        }));

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowManagerWindow);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _ipcService?.Dispose();
        base.OnExit(e);
    }
}

using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;

namespace StackBar.Installer;

public partial class InstallerWindow : Window
{
    private readonly string _installDir;

    public InstallerWindow()
    {
        InitializeComponent();
        _installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "StackBar");
        StatusLabel.Text = $"Install location: {_installDir}";
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        InstallBtn.IsEnabled = false;
        StatusLabel.Text = "Stopping running StackBar instances...";

        try
        {
            foreach (var proc in Process.GetProcessesByName("StackBar"))
            {
                try { proc.Kill(); proc.WaitForExit(1500); } catch { }
            }

            Directory.CreateDirectory(_installDir);
            StatusLabel.Text = "Extracting files...";

            bool createDesktop = DesktopShortcutCheck.IsChecked == true;
            bool createStartMenu = StartMenuCheck.IsChecked == true;
            bool runAtStartup = StartupCheck.IsChecked == true;

            await Task.Run(() =>
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("StackBar.Installer.Payload.payload.zip");
                if (stream == null)
                    throw new InvalidOperationException("Embedded payload.zip was not found in the installer.");

                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                archive.ExtractToDirectory(_installDir, overwriteFiles: true);
            });

            string exePath = Path.Combine(_installDir, "StackBar.exe");

            if (createDesktop)
            {
                string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "StackBar.lnk");
                CreateShortcut(desktopLnk, exePath);
            }

            if (createStartMenu)
            {
                string startMenuLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "StackBar.lnk");
                CreateShortcut(startMenuLnk, exePath);
            }

            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
            {
                if (runAtStartup)
                    key?.SetValue("StackBar", $"\"{exePath}\" --tray");
                else
                    key?.DeleteValue("StackBar", throwOnMissingValue: false);
            }

            // Register in Windows Installed Apps (HKCU Uninstall)
            using (var uninst = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\StackBar"))
            {
                uninst.SetValue("DisplayName", "StackBar — Taskbar Folder Studio");
                uninst.SetValue("DisplayIcon", exePath);
                uninst.SetValue("InstallLocation", _installDir);
                uninst.SetValue("Publisher", "StackBar");
                uninst.SetValue("DisplayVersion", "1.0.0");
                uninst.SetValue("UninstallString", $"\"{Environment.ProcessPath}\"");
            }

            StatusLabel.Text = "Installation complete! Launching StackBar...";
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
            Close();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Installation failed:\n{ex.Message}", "StackBar Setup", MessageBoxButton.OK, MessageBoxImage.Error);
            InstallBtn.IsEnabled = true;
        }
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("StackBar"))
            {
                try { proc.Kill(); proc.WaitForExit(1500); } catch { }
            }

            if (Directory.Exists(_installDir))
                Directory.Delete(_installDir, recursive: true);

            string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "StackBar.lnk");
            if (File.Exists(desktopLnk)) File.Delete(desktopLnk);

            string startMenuLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "StackBar.lnk");
            if (File.Exists(startMenuLnk)) File.Delete(startMenuLnk);

            using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            runKey?.DeleteValue("StackBar", throwOnMissingValue: false);

            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\StackBar", throwOnMissingSubKey: false);

            System.Windows.MessageBox.Show("StackBar has been uninstalled.", "StackBar Setup", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Uninstall error:\n{ex.Message}", "StackBar Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private static void CreateShortcut(string lnkPath, string targetExePath)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            shortcut.TargetPath = targetExePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(targetExePath);
            shortcut.IconLocation = $"{targetExePath},0";
            shortcut.Description = "StackBar — Taskbar Folder Studio";
            shortcut.Save();
        }
        catch { }
    }
}

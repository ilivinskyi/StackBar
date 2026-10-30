using System;
using System.IO;
using System.Windows;
using System.Diagnostics;
using Microsoft.Win32;
using TaskbarGrouper.Services;

namespace TaskbarGrouper.Views
{
    public partial class SetupWindow : Window
    {
        private string _installDir;

        public SetupWindow()
        {
            InitializeComponent();
            _installDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "StackBar");

            StatusLabel.Text = _installDir;

            // Check if already installed
            string targetExe = Path.Combine(_installDir, "StackBar.exe");
            if (File.Exists(targetExe))
            {
                SetupTitle.Text = "Update / Reinstall StackBar";
                InstallBtn.Content = "Update StackBar";
                UninstallBtn.Visibility = Visibility.Visible;
            }
            else
            {
                SetupTitle.Text = "Install StackBar";
                InstallBtn.Content = "Install & Launch";
                UninstallBtn.Visibility = Visibility.Collapsed;
            }
        }

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
            {
                fbd.Description = "Select StackBar installation folder";
                fbd.SelectedPath = _installDir;
                if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
                {
                    _installDir = fbd.SelectedPath;
                    StatusLabel.Text = _installDir;
                }
            }
        }

        private void Install_Click(object sender, RoutedEventArgs e)
        {
            InstallBtn.IsEnabled = false;
            StatusLabel.Text = "Closing running instances...";

            try
            {
                // Graceful exit via IPC
                IpcService.SendCommand("EXIT");
                System.Threading.Thread.Sleep(300);

                // Force terminate if any lingering
                foreach (var proc in Process.GetProcessesByName("StackBar"))
                {
                    try { proc.Kill(); proc.WaitForExit(1000); } catch { }
                }

                Directory.CreateDirectory(_installDir);
                string targetExe = Path.Combine(_installDir, "StackBar.exe");

                // Copy self
                string currentExe = AppInfo.ExePath;
                if (!string.Equals(currentExe, targetExe, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(currentExe, targetExe, true);
                }

                bool createDesktop = DesktopShortcutCheck.IsChecked == true;
                bool createStartMenu = StartMenuCheck.IsChecked == true;
                bool runAtStartup = StartupCheck.IsChecked == true;
                bool launchNow = LaunchAfterCheck.IsChecked == true;

                if (createDesktop)
                {
                    string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "StackBar.lnk");
                    CreateAppShortcut(desktopLnk, targetExe);
                }

                if (createStartMenu)
                {
                    string startMenuLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "StackBar.lnk");
                    CreateAppShortcut(startMenuLnk, targetExe);
                }

                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (runAtStartup)
                        key?.SetValue("StackBar", $"\"{targetExe}\" --tray");
                    else
                        key?.DeleteValue("StackBar", false);
                }

                // Register in Windows "Installed apps"
                using (var uninst = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\StackBar"))
                {
                    uninst.SetValue("DisplayName", "StackBar — Taskbar Folder Studio");
                    uninst.SetValue("DisplayIcon", targetExe);
                    uninst.SetValue("InstallLocation", _installDir);
                    uninst.SetValue("Publisher", "StackBar");
                    uninst.SetValue("DisplayVersion", "1.1.0");
                    uninst.SetValue("UninstallString", $"\"{targetExe}\" --uninstall");
                    uninst.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    uninst.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }

                if (launchNow)
                {
                    StatusLabel.Text = "Launching StackBar...";
                    Process.Start(new ProcessStartInfo(targetExe) { UseShellExecute = true });
                }
                else
                {
                    MessageBox.Show("StackBar installation was successful!", "StackBar Setup", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Installation failed:\n{ex.Message}", "StackBar Setup", MessageBoxButton.OK, MessageBoxImage.Error);
                InstallBtn.IsEnabled = true;
            }
        }

        private void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            PerformUninstall();
            Close();
        }

        public static void PerformUninstall()
        {
            try
            {
                var result = MessageBox.Show(
                    "Are you sure you want to uninstall StackBar?\n\nNote: If you have StackBar folders pinned to your taskbar, please unpin them from your taskbar.",
                    "Uninstall StackBar",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes) return;

                IpcService.SendCommand("EXIT");
                System.Threading.Thread.Sleep(300);

                foreach (var proc in Process.GetProcessesByName("StackBar"))
                {
                    try { proc.Kill(); proc.WaitForExit(1000); } catch { }
                }

                string installDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "StackBar");

                string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "StackBar.lnk");
                if (File.Exists(desktopLnk)) File.Delete(desktopLnk);

                string startMenuLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "StackBar.lnk");
                if (File.Exists(startMenuLnk)) File.Delete(startMenuLnk);

                using (var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    runKey?.DeleteValue("StackBar", false);
                }

                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\StackBar", false);

                var dataRes = MessageBox.Show("Do you also want to remove your saved StackBar configurations and custom folders?", "Delete Data", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (dataRes == MessageBoxResult.Yes)
                {
                    string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StackBar");
                    if (Directory.Exists(appData)) Directory.Delete(appData, true);
                }

                // Self-deletion of install dir
                if (Directory.Exists(installDir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{installDir}\"",
                        WindowStyle = ProcessWindowStyle.Hidden,
                        CreateNoWindow = true
                    });
                }

                MessageBox.Show("StackBar has been completely uninstalled.", "StackBar", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Uninstall note: " + ex.Message, "StackBar", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private static void CreateAppShortcut(string lnkPath, string targetExePath)
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
}

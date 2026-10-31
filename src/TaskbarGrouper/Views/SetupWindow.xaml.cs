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
        // Where StackBar is installed right now (before this run), to detect a move to another folder
        private readonly string _previousInstallDir;

        private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\StackBar";

        public SetupWindow()
        {
            InitializeComponent();
            _previousInstallDir = GetInstalledDirectory();
            _installDir = _previousInstallDir;
            UpdateModeUi();
        }

        private bool IsInstalledIn(string dir) => File.Exists(Path.Combine(dir, InstallLocation.ExeName));

        private void UpdateModeUi()
        {
            StatusLabel.Text = _installDir;
            if (IsInstalledIn(_installDir))
            {
                SetupTitle.Text = "Update / Reinstall StackBar";
                InstallBtn.Content = "Update StackBar";
                UninstallBtn.Visibility = Visibility.Visible;
            }
            else
            {
                SetupTitle.Text = "Install StackBar";
                InstallBtn.Content = "Install & Launch";
                // Uninstall still makes sense for an existing install elsewhere
                UninstallBtn.Visibility = IsInstalledIn(_previousInstallDir) ? Visibility.Visible : Visibility.Collapsed;
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
                    // A non-empty or system folder gets a StackBar subfolder: never install among unrelated files
                    _installDir = InstallLocation.ResolveChosenFolder(fbd.SelectedPath);
                    UpdateModeUi();
                }
            }
        }

        private void Install_Click(object sender, RoutedEventArgs e)
        {
            InstallBtn.IsEnabled = false;
            StatusLabel.Text = "Closing running instances...";

            try
            {
                StopRunningInstances(_installDir, _previousInstallDir);

                Directory.CreateDirectory(_installDir);
                string targetExe = Path.Combine(_installDir, InstallLocation.ExeName);

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
                using (var uninst = Registry.CurrentUser.CreateSubKey(UninstallKeyPath))
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

                OfferToRemovePreviousCopy(targetExe);

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

        /// <summary>Installing into a different folder than before would otherwise leave the old copy behind.</summary>
        private void OfferToRemovePreviousCopy(string newExe)
        {
            string oldExe = Path.Combine(_previousInstallDir, InstallLocation.ExeName);
            if (string.Equals(Path.GetFullPath(oldExe), Path.GetFullPath(newExe), StringComparison.OrdinalIgnoreCase) || !File.Exists(oldExe)) return;

            var answer = MessageBox.Show(
                $"StackBar was previously installed in:\n{_previousInstallDir}\n\nRemove that old copy?",
                "StackBar Setup", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            try
            {
                // Same rule as the uninstaller: StackBar.exe only, then the folder only if nothing else is in it
                if (!InstallLocation.IsSafeToClean(_previousInstallDir, out _)) return;
                File.Delete(oldExe);
                if (Directory.GetFileSystemEntries(_previousInstallDir).Length == 0)
                {
                    Directory.Delete(_previousInstallDir);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not remove the old copy:\n" + ex.Message, "StackBar Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
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

                string installDir = GetInstalledDirectory();
                StopRunningInstances(installDir);

                string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "StackBar.lnk");
                if (File.Exists(desktopLnk)) File.Delete(desktopLnk);

                string startMenuLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "StackBar.lnk");
                if (File.Exists(startMenuLnk)) File.Delete(startMenuLnk);

                using (var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    runKey?.DeleteValue("StackBar", false);
                }

                Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false);

                var dataRes = MessageBox.Show("Do you also want to remove your saved StackBar configurations and custom folders?", "Delete Data", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (dataRes == MessageBoxResult.Yes)
                {
                    string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StackBar");
                    if (Directory.Exists(appData)) Directory.Delete(appData, true);
                }

                bool cleanup = InstallLocation.IsSafeToClean(installDir, out string reason);
                MessageBox.Show(
                    cleanup
                        ? $"StackBar has been uninstalled.\n\nStackBar.exe is removed from\n{installDir}\nas soon as this window closes (the folder too, if nothing else is in it)."
                        : $"StackBar has been uninstalled, but its program file was left in place ({reason}):\n{installDir}",
                    "StackBar", MessageBoxButton.OK, MessageBoxImage.Information);

                // Started last: the uninstaller usually runs from inside the install folder, and its exe
                // can only be deleted after this process has exited.
                if (cleanup) ScheduleCleanupAfterExit(installDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Uninstall note: " + ex.Message, "StackBar", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>Install folder recorded at install time (it may be a custom one), else the default location.</summary>
        private static string GetInstalledDirectory()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath, false))
                {
                    if (key?.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location))
                        return location;
                }
            }
            catch { }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "StackBar");
        }

        /// <summary>
        /// Closes the resident app (gracefully via IPC, then forcefully). Only processes running from the
        /// install folder(s) in this Windows session are killed; dev builds, portable copies and the current
        /// process (the uninstaller registered in Windows is StackBar.exe itself) are left alone.
        /// </summary>
        private static void StopRunningInstances(params string[] installDirs)
        {
            IpcService.SendCommand("EXIT");
            System.Threading.Thread.Sleep(300);

            var self = Process.GetCurrentProcess();
            foreach (var proc in Process.GetProcessesByName("StackBar"))
            {
                using (proc)
                {
                    try
                    {
                        if (proc.Id == self.Id || proc.SessionId != self.SessionId) continue;
                        string? image = proc.MainModule?.FileName;
                        bool installed = false;
                        foreach (var dir in installDirs)
                        {
                            if (InstallLocation.IsInFolder(image, dir)) installed = true;
                        }
                        if (!installed) continue;

                        proc.Kill();
                        proc.WaitForExit(2000);
                    }
                    catch { }
                }
            }
        }

        private static void ScheduleCleanupAfterExit(string directory)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{InstallLocation.BuildCleanupScript(directory, Process.GetCurrentProcess().Id)}\"",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false,
                // Not inside the folder being cleaned, or the helper itself would keep it locked
                WorkingDirectory = Path.GetTempPath()
            });
        }

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

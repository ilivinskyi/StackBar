using Microsoft.Win32;
using System.Diagnostics;

namespace TaskbarGrouper.Services;

public static class StartupService
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "StackBar";

    public static bool IsRunAtStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
            var val = key?.GetValue(AppValueName) as string;
            return !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return false;
        }
    }

    public static void SetRunAtStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true);
            if (key == null) return;

            if (enable)
            {
                string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(AppValueName, $"\"{exePath}\" --tray");
                }
            }
            else
            {
                key.DeleteValue(AppValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Ignore registry permission errors
        }
    }
}

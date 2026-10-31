using System;
using Microsoft.Win32;

namespace TaskbarGrouper.Services
{
    public static class StartupService
    {
        private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppValueName = "StackBar";

        public enum StartupState
        {
            Off,
            /// <summary>Registered and pointing at this exe.</summary>
            On,
            /// <summary>Registered, but for another (old or moved) exe: startup is effectively broken.</summary>
            Stale
        }

        public static StartupState GetState()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, false))
                {
                    return ClassifyCommand(key?.GetValue(AppValueName) as string, AppInfo.ExePath);
                }
            }
            catch
            {
                return StartupState.Off;
            }
        }

        internal static StartupState ClassifyCommand(string? command, string exePath)
        {
            if (string.IsNullOrWhiteSpace(command)) return StartupState.Off;
            return command!.IndexOf("\"" + exePath + "\"", StringComparison.OrdinalIgnoreCase) >= 0
                ? StartupState.On
                : StartupState.Stale;
        }

        /// <returns>false if the registry could not be changed.</returns>
        public static bool SetRunAtStartup(bool enable)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, true))
                {
                    if (key == null) return false;

                    if (enable)
                    {
                        key.SetValue(AppValueName, $"\"{AppInfo.ExePath}\" --tray");
                    }
                    else
                    {
                        key.DeleteValue(AppValueName, false);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Log("Could not change Windows startup registration: " + ex.Message);
                return false;
            }
        }
    }
}

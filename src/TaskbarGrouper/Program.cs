using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Runtime.CompilerServices;
using TaskbarGrouper.Services;

namespace TaskbarGrouper
{
    public static class Program
    {
        private static Mutex? _instanceMutex;

        [STAThread]
        public static void Main(string[] args)
        {
            AppLogger.Initialize();

            // Set AUMID so StackBar's own windows don't attach to clicked pinned shortcuts
            try
            {
                SetCurrentProcessExplicitAppUserModelID("StackBar.App");
            }
            catch { }

            string? groupId = null;
            bool startMinimized = false;
            bool isSetup = false;
            bool isUninstall = false;

            string exeName = Path.GetFileName(AppInfo.ExePath);
            if (exeName.IndexOf("Setup", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                isSetup = true;
            }

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("--group", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    groupId = args[i + 1];
                }
                else if (args[i].Equals("--tray", StringComparison.OrdinalIgnoreCase))
                {
                    startMinimized = true;
                }
                else if (args[i].Equals("--install", StringComparison.OrdinalIgnoreCase))
                {
                    isSetup = true;
                }
                else if (args[i].Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    isUninstall = true;
                }
            }

            // Fast Path: Clicking a pinned folder
            if (!string.IsNullOrEmpty(groupId))
            {
                NativePositioningService.GetCursorPos(out var pt);
                if (IpcService.SendCommand($"OPEN|{groupId}|{pt.X}|{pt.Y}"))
                {
                    return;
                }
            }
            else if (!isSetup && !isUninstall && !startMinimized)
            {
                // Plain launch: If resident instance is running, tell it to show the manager
                if (IpcService.SendCommand("MANAGER"))
                {
                    return;
                }
            }

            // Only the resident app owns the single-instance mutex. Setup/uninstall must not take it,
            // otherwise the app that setup launches sees the mutex as taken and exits silently.
            if (!isSetup && !isUninstall)
            {
                string userSid = WindowsIdentity.GetCurrent().User?.Value ?? "User";
                string modePrefix = AppInfo.IsPortableMode ? "Portable" : "Installed";
                string mutexName = $"Local\\StackBar.{modePrefix}.{userSid}";
                _instanceMutex = new Mutex(true, mutexName, out bool createdNew);

                if (!createdNew)
                {
                    // Already resident (e.g. --tray at login while running): nothing to show
                    if (startMinimized && string.IsNullOrEmpty(groupId)) return;

                    // Another instance became resident in the meantime: give its pipe server time to come up
                    NativePositioningService.GetCursorPos(out var cursor);
                    string command = string.IsNullOrEmpty(groupId) ? "MANAGER" : $"OPEN|{groupId}|{cursor.X}|{cursor.Y}";
                    IpcService.SendCommand(command, connectTimeoutMs: 1500);
                    return;
                }
            }

            // Now load WPF subsystem in separate method to keep JIT lean
            StartWpfApplication(groupId, startMinimized, isSetup, isUninstall);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void StartWpfApplication(string? groupId, bool startMinimized, bool isSetup, bool isUninstall)
        {
            var app = new App();
            app.RunApplication(groupId, startMinimized, isSetup, isUninstall);
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", PreserveSig = false)]
        private static extern void SetCurrentProcessExplicitAppUserModelID([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string AppID);
    }
}

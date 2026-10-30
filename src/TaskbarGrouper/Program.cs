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
                    // Resident instance handled it immediately (<15ms)
                    return;
                }
            }
            else if (!isSetup && !isUninstall)
            {
                // Plain launch: If resident instance is running, tell it to show the manager
                if (IpcService.SendCommand("MANAGER"))
                {
                    return;
                }
            }

            // Check single instance mutex for the resident tray runner
            string userSid = WindowsIdentity.GetCurrent().User?.Value ?? "User";
            string mutexName = $"Local\\StackBar.Resident.{userSid}";
            bool createdNew;
            _instanceMutex = new Mutex(true, mutexName, out createdNew);

            if (!createdNew && !isSetup && !isUninstall)
            {
                // Another instance became active in the meantime
                IpcService.SendCommand(string.IsNullOrEmpty(groupId) ? "MANAGER" : $"OPEN|{groupId}|0|0");
                return;
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

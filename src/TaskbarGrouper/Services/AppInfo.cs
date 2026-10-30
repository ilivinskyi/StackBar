using System;
using System.IO;
using System.Diagnostics;

namespace TaskbarGrouper.Services
{
    public static class AppInfo
    {
        private static string? _exePath;

        public static string ExePath
        {
            get
            {
                if (_exePath == null)
                {
                    _exePath = Process.GetCurrentProcess().MainModule?.FileName 
                               ?? AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\') + "\\StackBar.exe";
                }
                return _exePath;
            }
        }
    }

    public static class AppLogger
    {
        private static readonly object _lock = new object();
        private static string? _logPath;

        public static void Initialize()
        {
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var dir = Path.Combine(appData, "StackBar", "logs");
                Directory.CreateDirectory(dir);
                _logPath = Path.Combine(dir, "stackbar.log");

                // Roll if > 1MB
                if (File.Exists(_logPath))
                {
                    var fi = new FileInfo(_logPath);
                    if (fi.Length > 1024 * 1024)
                    {
                        var old = Path.Combine(dir, "stackbar.old.log");
                        File.Delete(old);
                        File.Move(_logPath, old);
                    }
                }
            }
            catch { }
        }

        public static void Log(string message)
        {
            try
            {
                if (_logPath == null) Initialize();
                lock (_lock)
                {
                    File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}

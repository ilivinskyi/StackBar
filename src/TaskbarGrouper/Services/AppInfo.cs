using System;
using System.IO;
using System.Diagnostics;

namespace TaskbarGrouper.Services
{
    public static class AppInfo
    {
        private static string? _exePath;
        private static bool? _isPortable;

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

        public static string BaseDirectory => AppDomain.CurrentDomain.BaseDirectory;

        public static bool IsPortableMode
        {
            get
            {
                if (!_isPortable.HasValue)
                {
                    // Portable if:
                    // 1. A .portable or portable.txt flag file exists next to the executable
                    // 2. OR the executable name ends with -Portable (e.g. StackBar-Portable.exe)
                    // 3. OR a "data" directory exists in the base directory
                    string exeName = Path.GetFileNameWithoutExtension(ExePath);
                    _isPortable = File.Exists(Path.Combine(BaseDirectory, ".portable"))
                                  || File.Exists(Path.Combine(BaseDirectory, "portable.txt"))
                                  || Directory.Exists(Path.Combine(BaseDirectory, "data"))
                                  || exeName.IndexOf("Portable", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                return _isPortable.Value;
            }
        }
    }

    public static class AppLogger
    {
        private static readonly object _lock = new object();
        private static string? _logPath;

        public static void Initialize(string? dataDir = null)
        {
            try
            {
                if (string.IsNullOrEmpty(dataDir))
                {
                    dataDir = AppInfo.IsPortableMode 
                        ? Path.Combine(AppInfo.BaseDirectory, "data")
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StackBar");
                }
                var dir = Path.Combine(dataDir, "logs");
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
                if (_logPath == null)
                {
                    var defaultDir = AppInfo.IsPortableMode 
                        ? Path.Combine(AppInfo.BaseDirectory, "data")
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StackBar");
                    Initialize(defaultDir);
                }
                lock (_lock)
                {
                    File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}

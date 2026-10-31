using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Reflection;

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

        /// <summary>
        /// Product version shown to users ("1.2.0"). Release builds stamp it from the git tag via -p:Version;
        /// local builds use the default &lt;Version&gt; in TaskbarGrouper.csproj.
        /// </summary>
        public static string DisplayVersion => FormatVersion(typeof(AppInfo).Assembly.GetName().Version);

        internal static string FormatVersion(Version? version) =>
            version == null ? "0.0.0" : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";

        public static bool IsPortableMode
        {
            get
            {
                if (!_isPortable.HasValue)
                {
                    _isPortable = DetectPortable(BaseDirectory, ExePath, BuildEdition);
                }
                return _isPortable.Value;
            }
        }

        /// <summary>"Portable" in StackBar-Portable.exe (built with -p:StackBarEdition=Portable), otherwise null.</summary>
        internal static string? BuildEdition => typeof(AppInfo).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "StackBarEdition")?.Value;

        /// <summary>
        /// Portable only on an explicit signal: the portable build of the exe (works under any file name),
        /// or, for portable folders made by older versions, the ".portable" / "portable.txt" marker or the exact
        /// portable exe name. A folder that merely contains a "data" subfolder (a custom install folder,
        /// a cluttered Downloads folder) must not silently switch an installed copy to portable.
        /// </summary>
        internal static bool DetectPortable(string baseDirectory, string exePath, string? buildEdition = null)
        {
            return string.Equals(buildEdition, "Portable", StringComparison.OrdinalIgnoreCase)
                   || File.Exists(Path.Combine(baseDirectory, ".portable"))
                   || File.Exists(Path.Combine(baseDirectory, "portable.txt"))
                   || string.Equals(Path.GetFileNameWithoutExtension(exePath), "StackBar-Portable", StringComparison.OrdinalIgnoreCase);
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

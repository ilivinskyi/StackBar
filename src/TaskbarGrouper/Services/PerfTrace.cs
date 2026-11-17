using System;
using System.Diagnostics;
using System.IO;

namespace TaskbarGrouper.Services
{
    /// <summary>
    /// Timing marks for measuring the click-to-flyout path across the launcher and resident processes.
    /// Off unless STACKBAR_PERF names a file; marks use the system-wide performance counter, so marks from
    /// different processes can be compared directly.
    /// </summary>
    internal static class PerfTrace
    {
        private static readonly string? _path = Environment.GetEnvironmentVariable("STACKBAR_PERF");
        private static readonly object _lock = new object();

        public static bool Enabled => _path != null;

        public static void Mark(string name)
        {
            if (_path == null) return;
            long now = Stopwatch.GetTimestamp();
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(_path, $"{now}|{Stopwatch.Frequency}|{Process.GetCurrentProcess().Id}|{name}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}

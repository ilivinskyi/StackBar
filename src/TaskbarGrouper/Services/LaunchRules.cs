using System;
using System.IO;
using TaskbarGrouper.Models;

namespace TaskbarGrouper.Services
{
    /// <summary>What the flyout may offer for an item ("Run as Administrator", "Open File Location").</summary>
    public static class LaunchRules
    {
        private static readonly string[] ElevatableExtensions = { ".exe", ".bat", ".cmd", ".com", ".msc" };

        /// <summary>
        /// The "runas" verb only exists for programs and scripts; for .url items or documents it throws
        /// "No application is associated with the specified file for this operation".
        /// </summary>
        public static bool CanRunAsAdmin(GroupItem item, Func<string, string?> resolveLinkTarget)
        {
            if (item.Kind == ItemKind.ShellApp || item.Kind == ItemKind.Folder) return false;
            string path = item.TargetPath;
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                path = resolveLinkTarget(path) ?? string.Empty;
            }
            return Array.Exists(ElevatableExtensions, e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The file to select in Explorer for "Open File Location": the app the shortcut starts, not StackBar's
        /// private copy in Links\. Internet shortcuts fall back to the original file they were imported from.
        /// </summary>
        /// <returns>null when there is no meaningful location (the menu entry is then hidden).</returns>
        public static string? FileLocationTarget(GroupItem item, string appDataDir, Func<string, string?> resolveLinkTarget)
        {
            if (item.Kind == ItemKind.ShellApp) return null;
            string path = item.TargetPath;
            if (Directory.Exists(path)) return path;

            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string? target = resolveLinkTarget(path);
                return !string.IsNullOrEmpty(target) && (File.Exists(target) || Directory.Exists(target)) ? target : null;
            }

            if (ShortcutRepairService.IsOwnCopy(path, appDataDir))
            {
                // e.g. a copied .url: point at where it came from, if that still exists
                return !string.IsNullOrEmpty(item.SourcePath) && File.Exists(item.SourcePath) ? item.SourcePath : null;
            }

            return File.Exists(path) ? path : null;
        }
    }
}

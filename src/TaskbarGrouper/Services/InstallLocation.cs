using System;
using System.IO;
using System.Linq;

namespace TaskbarGrouper.Services
{
    /// <summary>
    /// Rules for where StackBar may be installed and what the uninstaller may delete.
    /// StackBar only ever owns StackBar.exe inside its install folder; it never deletes a folder recursively.
    /// </summary>
    public static class InstallLocation
    {
        public const string ExeName = "StackBar.exe";
        private const string FolderName = "StackBar";

        /// <summary>
        /// Maps the folder picked in "Browse..." to the actual install folder: an existing, non-empty folder
        /// (D:\Apps, Desktop...) gets a StackBar subfolder so the app never lands among unrelated files.
        /// </summary>
        public static string ResolveChosenFolder(string selected)
        {
            string full = Path.GetFullPath(selected);
            // Keep "C:\" as is: trimmed to "C:" it would combine into the drive-relative path "C:StackBar"
            if (full.Length > 3) full = full.TrimEnd('\\');
            if (string.Equals(Path.GetFileName(full), FolderName, StringComparison.OrdinalIgnoreCase)) return full;
            if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any()) return Path.Combine(full, FolderName);
            if (IsProtectedFolder(full)) return Path.Combine(full, FolderName);
            return full;
        }

        /// <summary>True if the folder may be removed after deleting StackBar.exe (and only if it ends up empty).</summary>
        public static bool IsSafeToClean(string? dir, out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(dir)) { reason = "no folder"; return false; }

            string full;
            try { full = Path.GetFullPath(dir).TrimEnd('\\'); }
            catch { reason = "invalid path"; return false; }

            if (IsProtectedFolder(full)) { reason = "system or user folder"; return false; }
            if (!File.Exists(Path.Combine(full, ExeName))) { reason = "StackBar.exe not found there"; return false; }
            return true;
        }

        /// <summary>Drive roots and well-known shell folders are never cleaned or used directly.</summary>
        public static bool IsProtectedFolder(string fullPath)
        {
            string path = fullPath.TrimEnd('\\');
            string? root = Path.GetPathRoot(fullPath)?.TrimEnd('\\');
            if (string.IsNullOrEmpty(path) || string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) return true;

            var protectedFolders = new[]
            {
                Environment.SpecialFolder.Desktop, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolder.Programs, Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.MyMusic,
                Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos
            }
            .Select(Environment.GetFolderPath)
            .Concat(new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            })
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p.TrimEnd('\\'));

            return protectedFolders.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// PowerShell run after the uninstaller exits: deletes StackBar.exe, then the folder only if it is empty.
        /// </summary>
        public static string BuildCleanupScript(string dir, int waitForProcessId)
        {
            string escapedDir = dir.Replace("'", "''");
            string escapedExe = Path.Combine(dir, ExeName).Replace("'", "''");
            return $"Wait-Process -Id {waitForProcessId} -ErrorAction SilentlyContinue; " +
                   "Start-Sleep -Milliseconds 500; " +
                   $"Remove-Item -LiteralPath '{escapedExe}' -Force -ErrorAction SilentlyContinue; " +
                   $"if (-not (Get-ChildItem -LiteralPath '{escapedDir}' -Force -ErrorAction SilentlyContinue)) " +
                   $"{{ Remove-Item -LiteralPath '{escapedDir}' -Force -ErrorAction SilentlyContinue }}";
        }

        /// <summary>True if the process image lives in the given install folder (used to stop only real installs).</summary>
        public static bool IsInFolder(string? imagePath, string dir)
        {
            if (string.IsNullOrEmpty(imagePath)) return false;
            string folder = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            return Path.GetFullPath(imagePath).StartsWith(folder, StringComparison.OrdinalIgnoreCase);
        }
    }
}

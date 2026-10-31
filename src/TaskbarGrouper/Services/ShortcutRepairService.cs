using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TaskbarGrouper.Models;
using static TaskbarGrouper.Services.ShortcutService;

namespace TaskbarGrouper.Services
{
    /// <summary>
    /// Repairs "stale" shortcuts: a .lnk stores a snapshot of its target (ID list, size, timestamps).
    /// When an app updates itself in place, that snapshot no longer matches the exe and Windows can end up
    /// drawing a blank white icon for the app on the taskbar. Re-pointing the shortcut at its own target makes
    /// Windows record the current file again; every other property (app ID, icon, arguments...) is kept.
    /// </summary>
    public static class ShortcutRepairService
    {
        private const int Win32FindDataSize = 592;
        private const int STGM_READWRITE = 0x2;
        private const uint SLGP_RAWPATH = 0x4;
        internal const uint SLDF_HAS_EXP_SZ = 0x200;
        internal const uint SLDF_HAS_DARWINID = 0x1000;

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("45e2b4ae-b1c3-11d0-b92f-00a0c90312e1")]
        internal interface IShellLinkDataList
        {
            [PreserveSig] int AddDataBlock(IntPtr dataBlock);
            [PreserveSig] int CopyDataBlock(uint signature, out IntPtr dataBlock);
            [PreserveSig] int RemoveDataBlock(uint signature);
            [PreserveSig] int GetFlags(out uint flags);
            [PreserveSig] int SetFlags(uint flags);
        }

        private static uint GetLinkFlags(object link)
        {
            return link is IShellLinkDataList data && data.GetFlags(out uint flags) == 0 ? flags : 0;
        }

        /// <summary>Windows Installer "advertised" shortcut (older Office and other MSI apps): never refreshed.</summary>
        internal static bool IsAdvertised(string lnkPath)
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                ((IPersistFile)link).Load(lnkPath, 0);
                return (GetLinkFlags(link) & SLDF_HAS_DARWINID) != 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }

        // Target-written timestamps are compared with some slack (FAT-style rounding in stored data)
        private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan IndexMaxAge = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan BackupMaxAge = TimeSpan.FromDays(30);
        private const int BackupMaxCount = 300;

        public enum RepairOutcome
        {
            /// <summary>Shortcut is current (or not a file shortcut): nothing to do.</summary>
            NotNeeded,
            Repaired,
            /// <summary>Stale, but StackBar can't write it (e.g. all-users Start Menu) or already gave up on it.</summary>
            Skipped,
            Failed
        }

        public sealed class RepairReport
        {
            public List<string> Repaired { get; } = new List<string>();
            public List<string> Skipped { get; } = new List<string>();
            public List<string> Failed { get; } = new List<string>();
            public int Checked { get; set; }
        }

        private sealed class LinkInfo
        {
            public string Path = string.Empty;
            public string Target = string.Empty;
            public string? AppUserModelId;
        }

        private static readonly object _indexLock = new object();
        private static List<LinkInfo>? _index;
        private static DateTime _indexBuiltUtc;
        private static int _indexRefreshRunning;

        // Shortcuts that could not be repaired, keyed by path, with the target's write time at that moment:
        // they are not retried (and not backed up again) until the app is updated once more.
        private static readonly Dictionary<string, DateTime> _gaveUp = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static int _backupsPruned;

        /// <summary>Folders whose shortcuts Windows uses to pick an app's taskbar icon (replaceable in tests).</summary>
        internal static Func<IEnumerable<string>> RegisteredShortcutFolders = DefaultShortcutFolders;

        internal static void ResetState()
        {
            lock (_indexLock) { _index = null; }
            lock (_gaveUp) { _gaveUp.Clear(); }
        }

        private static IEnumerable<string> DefaultShortcutFolders()
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        }

        /// <summary>StackBar's own shortcut copies live in &lt;data&gt;\Links; everything else belongs to someone else.</summary>
        public static bool IsOwnCopy(string path, string appDataDir)
        {
            try
            {
                string links = Path.Combine(Path.GetFullPath(appDataDir).TrimEnd('\\'), "Links") + "\\";
                return Path.GetFullPath(path).StartsWith(links, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// True if the shortcut's stored target data (size / last write time) no longer matches the target file.
        /// Shortcuts to folders, missing targets, and shortcuts without stored data are never reported as stale.
        /// </summary>
        public static bool IsStale(string lnkPath)
        {
            return TryReadStoredTarget(lnkPath, out var target, out uint storedSize, out DateTime storedWriteUtc) &&
                   IsStale(target, storedSize, storedWriteUtc);
        }

        private static bool IsStale(string target, uint storedSize, DateTime storedWriteUtc)
        {
            if (string.IsNullOrEmpty(target) || !File.Exists(target)) return false;
            // No stored snapshot at all (some tools write none): nothing to compare
            if (storedSize == 0 && storedWriteUtc == DateTime.MinValue) return false;

            var actual = new FileInfo(target);
            // A .lnk stores only the low 32 bits of the size; comparing all 64 would make every target over 4 GiB look stale forever
            return (uint)(actual.Length & 0xFFFFFFFF) != storedSize ||
                   (actual.LastWriteTimeUtc - storedWriteUtc).Duration() > TimestampTolerance;
        }

        private static bool TryReadStoredTarget(string lnkPath, out string target, out uint storedSize, out DateTime storedWriteUtc)
        {
            target = string.Empty;
            storedSize = 0;
            storedWriteUtc = DateTime.MinValue;
            if (!lnkPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !File.Exists(lnkPath)) return false;

            IntPtr findData = Marshal.AllocHGlobal(Win32FindDataSize);
            var link = (IShellLinkW)new ShellLink();
            try
            {
                ((IPersistFile)link).Load(lnkPath, 0);
                var sb = new StringBuilder(1024);
                // WIN32_FIND_DATA filled from the data stored in the link, not from the file on disk
                if (link.GetPath(sb, sb.Capacity, findData, 0) != 0) return false;
                target = sb.ToString();

                long writeTime = ((long)(uint)Marshal.ReadInt32(findData, 24) << 32) | (uint)Marshal.ReadInt32(findData, 20);
                storedSize = (uint)Marshal.ReadInt32(findData, 32); // nFileSizeLow
                storedWriteUtc = writeTime > 0 ? DateTime.FromFileTimeUtc(writeTime) : DateTime.MinValue;
                return target.Length > 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(findData);
                Marshal.FinalReleaseComObject(link);
            }
        }

        /// <summary>
        /// Rewrites the shortcut's stored target data in place. Setting the same path again is a no-op for the
        /// shell (nothing is marked dirty), so the link is briefly pointed elsewhere and then back.
        /// </summary>
        public static bool Refresh(string lnkPath)
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                var persist = (IPersistFile)link;
                persist.Load(lnkPath, STGM_READWRITE);
                uint flags = GetLinkFlags(link);
                // Windows Installer "advertised" shortcuts resolve through the installer, not their stored path:
                // re-setting the path would turn them into links to an installer cache file
                if ((flags & SLDF_HAS_DARWINID) != 0) return false;

                var sb = new StringBuilder(1024);
                if (link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0) != 0) return false;
                string target = sb.ToString();
                if (!File.Exists(target)) return false;

                // A target stored with environment variables (%LOCALAPPDATA%\...) is set back in that form,
                // so the shortcut keeps working for other users and after profile moves
                string pathToSet = target;
                if ((flags & SLDF_HAS_EXP_SZ) != 0)
                {
                    var raw = new StringBuilder(1024);
                    if (link.GetPath(raw, raw.Capacity, IntPtr.Zero, SLGP_RAWPATH) == 0 && raw.Length > 0) pathToSet = raw.ToString();
                }

                link.SetPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                if (link.SetPath(pathToSet) != 0) return false;
                persist.Save(null, true);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Log("Could not refresh shortcut " + lnkPath + ": " + ex.Message);
                return false;
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }

        /// <summary>
        /// Refreshes the shortcut if it is stale. Shortcuts StackBar can't write are skipped before anything is
        /// backed up, and a shortcut that failed is not retried until its target changes again.
        /// </summary>
        /// <param name="backupDir">null for StackBar's own copies; otherwise the original is backed up first.</param>
        public static RepairOutcome RepairIfStale(string lnkPath, string? backupDir)
        {
            if (!TryReadStoredTarget(lnkPath, out var target, out uint storedSize, out DateTime storedWriteUtc) ||
                !IsStale(target, storedSize, storedWriteUtc) ||
                IsAdvertised(lnkPath))
            {
                return RepairOutcome.NotNeeded;
            }

            DateTime targetWrite = File.GetLastWriteTimeUtc(target);
            lock (_gaveUp)
            {
                if (_gaveUp.TryGetValue(lnkPath, out var seen) && seen == targetWrite) return RepairOutcome.Skipped;
            }

            if (!CanWrite(lnkPath))
            {
                Remember(lnkPath, targetWrite);
                AppLogger.Log("Skipping stale shortcut (no write access): " + lnkPath);
                return RepairOutcome.Skipped;
            }

            if (backupDir != null && !TryBackup(lnkPath, backupDir))
            {
                // Never modify another app's shortcut without a backup
                Remember(lnkPath, targetWrite);
                return RepairOutcome.Failed;
            }

            bool ok = Refresh(lnkPath) && !IsStale(lnkPath);
            if (!ok) Remember(lnkPath, targetWrite);
            AppLogger.Log((ok ? "Repaired stale shortcut: " : "Could not repair stale shortcut: ") + lnkPath);
            return ok ? RepairOutcome.Repaired : RepairOutcome.Failed;
        }

        private static void Remember(string lnkPath, DateTime targetWrite)
        {
            lock (_gaveUp) { _gaveUp[lnkPath] = targetWrite; }
        }

        internal static bool CanWrite(string path)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) return false;
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)) { }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string BackupDirectory(string appDataDir) => Path.Combine(appDataDir, "Backups", "Shortcuts");

        /// <summary>
        /// Copies the original into the backup folder under a name that is unique per source path (an app's
        /// Start Menu and pinned shortcuts usually share a file name) and records it in backups.tsv.
        /// </summary>
        internal static bool TryBackup(string lnkPath, string backupDir)
        {
            try
            {
                Directory.CreateDirectory(backupDir);
                PruneBackupsOnce(backupDir);

                string fullPath = Path.GetFullPath(lnkPath);
                string pathTag;
                using (var sha = System.Security.Cryptography.SHA1.Create())
                {
                    pathTag = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(fullPath.ToLowerInvariant())))
                        .Replace("-", "").Substring(0, 8);
                }

                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string baseName = $"{stamp}_{pathTag}_{Path.GetFileNameWithoutExtension(lnkPath)}";
                string backup = Path.Combine(backupDir, baseName + ".lnk");
                for (int n = 2; File.Exists(backup); n++)
                {
                    backup = Path.Combine(backupDir, $"{baseName}_{n}.lnk");
                }

                File.Copy(lnkPath, backup, overwrite: false);
                File.AppendAllText(Path.Combine(backupDir, "backups.tsv"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{fullPath}\t{Path.GetFileName(backup)}{Environment.NewLine}");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Log("Skipping shortcut repair, backup failed for " + lnkPath + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>Keeps the backup folder bounded: drops backups older than 30 days and keeps at most 300.</summary>
        internal static void PruneBackups(string backupDir, DateTime nowUtc)
        {
            var backups = new DirectoryInfo(backupDir).GetFiles("*.lnk").OrderByDescending(f => f.CreationTimeUtc).ToList();
            foreach (var file in backups.Where((f, index) => index >= BackupMaxCount || nowUtc - f.CreationTimeUtc > BackupMaxAge))
            {
                try { file.Delete(); } catch { }
            }
        }

        private static void PruneBackupsOnce(string backupDir)
        {
            if (Interlocked.Exchange(ref _backupsPruned, 1) == 0) PruneBackups(backupDir, DateTime.UtcNow);
        }

        /// <summary>
        /// Called right before the flyout launches an item: repairs the shortcut the item launches and, if the
        /// user allows it, the app's registered shortcuts (Start Menu / pinned) that Windows uses for the
        /// taskbar icon. Never scans the disk here: the shortcut index is refreshed in the background.
        /// </summary>
        public static void PrepareLaunch(GroupItem item, string appDataDir, bool repairAppShortcuts)
        {
            try
            {
                if (item.Kind == ItemKind.ShellApp || item.Kind == ItemKind.Folder) return;

                string? target = null;
                string? aumid = null;
                bool repaired = false;
                if (item.TargetPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    if (IsOwnCopy(item.TargetPath, appDataDir))
                        repaired |= RepairIfStale(item.TargetPath, backupDir: null) == RepairOutcome.Repaired;
                    else if (repairAppShortcuts)
                        repaired |= RepairIfStale(item.TargetPath, BackupDirectory(appDataDir)) == RepairOutcome.Repaired;

                    var info = ReadLink(item.TargetPath);
                    target = info?.Target;
                    aumid = info?.AppUserModelId;
                }
                else if (item.TargetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    target = item.TargetPath;
                }

                if (repairAppShortcuts && !string.IsNullOrEmpty(target))
                {
                    foreach (var link in FindRegisteredShortcuts(GetIndexSnapshot(), target!, aumid))
                    {
                        repaired |= RepairIfStale(link, BackupDirectory(appDataDir)) == RepairOutcome.Repaired;
                    }
                }

                // Lets an already-open window of this app pick up the fixed icon too
                if (repaired) NotifyTaskbarIconsChanged();
            }
            catch (Exception ex)
            {
                AppLogger.Log("Shortcut repair before launch failed: " + ex.Message);
            }
        }

        /// <summary>Repairs every stale shortcut StackBar can see. Runs on a worker thread.</summary>
        /// <param name="itemLinkPaths">Paths of the items' shortcuts, copied on the UI thread beforehand.</param>
        public static RepairReport RepairAll(IReadOnlyCollection<string> itemLinkPaths, string appDataDir)
        {
            var report = new RepairReport();
            var candidates = itemLinkPaths
                .Where(p => p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                .Concat(BuildIndex().Select(l => l.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var path in candidates)
            {
                report.Checked++;
                // Own copies need no backup; every other shortcut is backed up first
                string? backup = IsOwnCopy(path, appDataDir) ? null : BackupDirectory(appDataDir);
                switch (RepairIfStale(path, backup))
                {
                    case RepairOutcome.Repaired: report.Repaired.Add(path); break;
                    case RepairOutcome.Skipped: report.Skipped.Add(path); break;
                    case RepairOutcome.Failed: report.Failed.Add(path); break;
                }
            }
            if (report.Repaired.Count > 0) NotifyTaskbarIconsChanged();
            return report;
        }

        /// <summary>Refreshes StackBar's own shortcut copies (its own files, so no backups). Runs on a worker thread.</summary>
        public static void RefreshOwnCopies(IEnumerable<string> itemLinkPaths, string appDataDir)
        {
            foreach (var path in itemLinkPaths.Where(p => IsOwnCopy(p, appDataDir)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                RepairIfStale(path, backupDir: null);
            }
        }

        /// <summary>Starts a background index rebuild unless one is already running.</summary>
        public static void RefreshIndexInBackground()
        {
            if (Interlocked.Exchange(ref _indexRefreshRunning, 1) == 1) return;
            Task.Run(() =>
            {
                try { BuildIndex(); }
                catch (Exception ex) { AppLogger.Log("Shortcut index build failed: " + ex.Message); }
                finally { Interlocked.Exchange(ref _indexRefreshRunning, 0); }
            });
        }

        /// <summary>
        /// Current index without waiting: an outdated index is still used (and refreshed in the background);
        /// if there is none yet, the registered-shortcut check is skipped for this launch.
        /// </summary>
        private static List<LinkInfo> GetIndexSnapshot()
        {
            List<LinkInfo>? index;
            DateTime builtUtc;
            lock (_indexLock)
            {
                index = _index;
                builtUtc = _indexBuiltUtc;
            }
            if (index == null || DateTime.UtcNow - builtUtc > IndexMaxAge) RefreshIndexInBackground();
            return index ?? new List<LinkInfo>();
        }

        /// <summary>Rebuilds the index and searches it. Slow: for tests and worker threads only.</summary>
        internal static IEnumerable<string> FindRegisteredShortcuts(string target, string? aumid)
        {
            return FindRegisteredShortcuts(BuildIndex(), target, aumid);
        }

        private static IEnumerable<string> FindRegisteredShortcuts(List<LinkInfo> index, string target, string? aumid)
        {
            return index
                .Where(l => string.Equals(l.Target, target, StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrEmpty(aumid) && string.Equals(l.AppUserModelId, aumid, StringComparison.OrdinalIgnoreCase)))
                .Select(l => l.Path)
                .ToList();
        }

        /// <summary>Scans the registered shortcut folders. Slow (one COM load per shortcut): never call on the UI thread.</summary>
        private static List<LinkInfo> BuildIndex()
        {
            var list = new List<LinkInfo>();
            foreach (var folder in RegisteredShortcutFolders().Where(Directory.Exists))
            {
                foreach (var file in EnumerateFilesSafely(folder, "*.lnk"))
                {
                    var info = ReadLink(file);
                    // StackBar's own pinned stacks are maintained by HealPinnedTaskbarShortcuts
                    if (info != null && !Path.GetFileName(info.Target).StartsWith("StackBar", StringComparison.OrdinalIgnoreCase))
                        list.Add(info);
                }
            }

            lock (_indexLock)
            {
                _index = list;
                _indexBuiltUtc = DateTime.UtcNow;
            }
            return list;
        }

        private static LinkInfo? ReadLink(string lnkPath)
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                ((IPersistFile)link).Load(lnkPath, 0);
                var sb = new StringBuilder(1024);
                if (link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0) != 0 || sb.Length == 0) return null;
                return new LinkInfo { Path = lnkPath, Target = sb.ToString(), AppUserModelId = ReadAppUserModelId(link) };
            }
            catch
            {
                return null;
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }

        /// <summary>Real target of a .lnk (the app it starts), or null for links without a file-system target.</summary>
        public static string? ResolveLinkTarget(string lnkPath) => ReadLink(lnkPath)?.Target;
    }
}

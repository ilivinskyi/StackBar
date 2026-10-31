using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
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

        // Target-written timestamps are compared with some slack (FAT-style rounding in stored data)
        private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

        public sealed class RepairReport
        {
            public List<string> Repaired { get; } = new List<string>();
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
        private static readonly TimeSpan IndexMaxAge = TimeSpan.FromMinutes(10);

        /// <summary>Folders whose shortcuts Windows uses to pick an app's taskbar icon (replaceable in tests).</summary>
        internal static Func<IEnumerable<string>> RegisteredShortcutFolders = DefaultShortcutFolders;

        internal static void ResetIndex()
        {
            lock (_indexLock) { _index = null; }
        }

        private static IEnumerable<string> DefaultShortcutFolders()
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        }

        /// <summary>
        /// True if the shortcut's stored target data (size / last write time) no longer matches the target file.
        /// Shortcuts to folders, missing targets, and shortcuts without stored data are never reported as stale.
        /// </summary>
        public static bool IsStale(string lnkPath)
        {
            return TryReadStoredTarget(lnkPath, out var target, out long storedSize, out DateTime storedWriteUtc) &&
                   IsStale(target, storedSize, storedWriteUtc);
        }

        private static bool IsStale(string target, long storedSize, DateTime storedWriteUtc)
        {
            if (string.IsNullOrEmpty(target) || !File.Exists(target)) return false;
            // No stored snapshot at all (some tools write none): nothing to compare
            if (storedSize == 0 && storedWriteUtc == DateTime.MinValue) return false;

            var actual = new FileInfo(target);
            return actual.Length != storedSize ||
                   (actual.LastWriteTimeUtc - storedWriteUtc).Duration() > TimestampTolerance;
        }

        private static bool TryReadStoredTarget(string lnkPath, out string target, out long storedSize, out DateTime storedWriteUtc)
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
                storedSize = ((long)(uint)Marshal.ReadInt32(findData, 28) << 32) | (uint)Marshal.ReadInt32(findData, 32);
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
                var sb = new StringBuilder(1024);
                if (link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0) != 0) return false;
                string target = sb.ToString();
                if (!File.Exists(target)) return false;

                link.SetPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                if (link.SetPath(target) != 0) return false;
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

        /// <summary>Backs up (when a backup folder is given) and refreshes the shortcut if it is stale.</summary>
        /// <returns>null if nothing was needed, true if repaired, false if repair failed (e.g. no write access).</returns>
        public static bool? RepairIfStale(string lnkPath, string? backupDir)
        {
            if (!IsStale(lnkPath)) return null;

            if (backupDir != null)
            {
                try
                {
                    Directory.CreateDirectory(backupDir);
                    string backup = Path.Combine(backupDir, $"{DateTime.Now:yyyyMMdd-HHmmss}_{Path.GetFileName(lnkPath)}");
                    File.Copy(lnkPath, backup, true);
                }
                catch (Exception ex)
                {
                    // Never modify another app's shortcut without a backup
                    AppLogger.Log("Skipping shortcut repair, backup failed for " + lnkPath + ": " + ex.Message);
                    return false;
                }
            }

            bool ok = Refresh(lnkPath) && !IsStale(lnkPath);
            AppLogger.Log((ok ? "Repaired stale shortcut: " : "Could not repair stale shortcut: ") + lnkPath);
            return ok;
        }

        public static string BackupDirectory(string appDataDir) => Path.Combine(appDataDir, "Backups", "Shortcuts");

        /// <summary>
        /// Called right before the flyout launches an item: repairs StackBar's own copy and the app's registered
        /// shortcuts (Start Menu / pinned) that Windows uses for the taskbar icon. Cheap: only matching links are checked.
        /// </summary>
        public static void PrepareLaunch(GroupItem item, string appDataDir)
        {
            try
            {
                if (item.Kind == ItemKind.ShellApp || item.Kind == ItemKind.Folder) return;

                string? target = null;
                string? aumid = null;
                if (item.TargetPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    RepairIfStale(item.TargetPath, backupDir: null); // StackBar's own copy: no backup needed
                    var own = ReadLink(item.TargetPath);
                    target = own?.Target;
                    aumid = own?.AppUserModelId;
                }
                else if (item.TargetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    target = item.TargetPath;
                }

                if (string.IsNullOrEmpty(target)) return;
                bool repaired = false;
                foreach (var link in FindRegisteredShortcuts(target!, aumid))
                {
                    repaired |= RepairIfStale(link, BackupDirectory(appDataDir)) == true;
                }
                // Lets an already-open window of this app pick up the fixed icon too
                if (repaired) NotifyTaskbarIconsChanged();
            }
            catch (Exception ex)
            {
                AppLogger.Log("Shortcut repair before launch failed: " + ex.Message);
            }
        }

        /// <summary>Repairs every stale shortcut StackBar can see: its own copies plus registered app shortcuts.</summary>
        public static RepairReport RepairAll(AppConfig config, string appDataDir)
        {
            var report = new RepairReport();
            var links = config.Groups.SelectMany(g => g.Items)
                .Select(i => i.TargetPath)
                .Where(p => p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                .Select(p => (Path: p, Backup: (string?)null))
                .Concat(GetIndex(forceRebuild: true).Select(l => (l.Path, (string?)BackupDirectory(appDataDir))))
                .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First());

            foreach (var (path, backup) in links)
            {
                report.Checked++;
                var result = RepairIfStale(path, backup);
                if (result == true) report.Repaired.Add(path);
                else if (result == false) report.Failed.Add(path);
            }
            if (report.Repaired.Count > 0) NotifyTaskbarIconsChanged();
            return report;
        }

        /// <summary>Refreshes StackBar's own shortcut copies (its own files, so no backups) and warms the index.</summary>
        public static void RefreshOwnCopies(AppConfig config)
        {
            foreach (var path in config.Groups.SelectMany(g => g.Items).Select(i => i.TargetPath)
                         .Where(p => p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                RepairIfStale(path, backupDir: null);
            }
        }

        public static void WarmIndexInBackground()
        {
            Task.Run(() =>
            {
                try { GetIndex(forceRebuild: false); }
                catch (Exception ex) { AppLogger.Log("Shortcut index build failed: " + ex.Message); }
            });
        }

        internal static IEnumerable<string> FindRegisteredShortcuts(string target, string? aumid)
        {
            return GetIndex(forceRebuild: false)
                .Where(l => string.Equals(l.Target, target, StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrEmpty(aumid) && string.Equals(l.AppUserModelId, aumid, StringComparison.OrdinalIgnoreCase)))
                .Select(l => l.Path)
                .ToList();
        }

        private static List<LinkInfo> GetIndex(bool forceRebuild)
        {
            lock (_indexLock)
            {
                if (!forceRebuild && _index != null && DateTime.UtcNow - _indexBuiltUtc < IndexMaxAge) return _index;

                var list = new List<LinkInfo>();
                foreach (var folder in RegisteredShortcutFolders().Where(Directory.Exists))
                {
                    IEnumerable<string> files;
                    try { files = Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories).ToList(); }
                    catch { continue; }

                    foreach (var file in files)
                    {
                        var info = ReadLink(file);
                        // StackBar's own pinned stacks are maintained by HealPinnedTaskbarShortcuts
                        if (info != null && !string.Equals(info.Target, AppInfo.ExePath, StringComparison.OrdinalIgnoreCase))
                            list.Add(info);
                    }
                }

                _index = list;
                _indexBuiltUtc = DateTime.UtcNow;
                return list;
            }
        }

        private static LinkInfo? ReadLink(string lnkPath)
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                ((IPersistFile)link).Load(lnkPath, 0);
                var sb = new StringBuilder(1024);
                if (link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0) != 0 || sb.Length == 0) return null;

                string? aumid = null;
                if (link is IPropertyStore store)
                {
                    var key = AppUserModelIdKey;
                    if (store.GetValue(ref key, out PROPVARIANT value) == 0)
                    {
                        if (value.vt == 31 && value.pwszVal != IntPtr.Zero) aumid = Marshal.PtrToStringUni(value.pwszVal);
                        PropVariantClear(ref value);
                    }
                }
                return new LinkInfo { Path = lnkPath, Target = sb.ToString(), AppUserModelId = aumid };
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

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PROPVARIANT pvar);
    }
}

using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using TaskbarGrouper.Models;

namespace TaskbarGrouper.Services
{
    public class DiscoveredAppItem
    {
        public GroupItem Item { get; set; } = null!;
        public string SourceBadge { get; set; } = "Pinned";
    }

    public static class ShortcutService
    {
        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        internal class ShellLink { }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        internal interface IShellLinkW
        {
            [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
            [PreserveSig] int GetIDList(out IntPtr ppidl);
            [PreserveSig] int SetIDList(IntPtr pidl);
            [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            [PreserveSig] int GetHotkey(out short pwHotkey);
            [PreserveSig] int SetHotkey(short wHotkey);
            [PreserveSig] int GetShowCmd(out int piShowCmd);
            [PreserveSig] int SetShowCmd(int iShowCmd);
            [PreserveSig] int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            [PreserveSig] int Resolve(IntPtr hwnd, uint fFlags);
            [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        internal interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint cProps);
            [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
            [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
            [PreserveSig] int Commit();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        internal struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PROPVARIANT
        {
            public ushort vt;
            public ushort wReserved1;
            public ushort wReserved2;
            public ushort wReserved3;
            public IntPtr pwszVal;
            public IntPtr ptr2;
        }

        internal static readonly PROPERTYKEY AppUserModelIdKey = new PROPERTYKEY
        {
            fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
            pid = 5
        };

        /// <summary>
        /// AppUserModelID prefix for pinned stacks of this StackBar edition. A portable copy adds its instance
        /// ID, so the installed and portable editions can tell their pins apart.
        /// </summary>
        internal static string GroupAumidPrefix(string appDataDir) => AppInfo.IsPortableMode
            ? $"StackBar.Portable.{PortableInstanceId(appDataDir)}.Group."
            : "StackBar.Group.";

        private static readonly Dictionary<string, string> _instanceIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Random ID created once and stored in the data folder. It travels with the folder, so the portable
        /// copy still recognises its own pins after the folder moves or the USB drive gets another letter
        /// (a tag derived from the folder's path would change with it).
        /// </summary>
        internal static string PortableInstanceId(string appDataDir)
        {
            lock (_instanceIds)
            {
                if (_instanceIds.TryGetValue(appDataDir, out var cached)) return cached;

                string file = Path.Combine(appDataDir, "instance.id");
                string? id = null;
                try
                {
                    if (File.Exists(file))
                    {
                        string stored = File.ReadAllText(file).Trim();
                        if (stored.Length == 8 && IsHexPrefix(stored)) id = stored;
                    }
                    if (id == null)
                    {
                        id = Guid.NewGuid().ToString("N").Substring(0, 8);
                        Directory.CreateDirectory(appDataDir);
                        File.WriteAllText(file, id);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log("Could not read or create the portable instance id: " + ex.Message);
                    id ??= "00000000";
                }

                _instanceIds[appDataDir] = id!;
                return id!;
            }
        }

        /// <param name="nameIsShared">Another stack has the same name: add the id so the files don't overwrite each other.</param>
        public static string CreateGroupShortcut(AppGroup group, string appDataDir, bool nameIsShared = false)
        {
            string iconsDir = Path.Combine(appDataDir, "Icons");
            string shortcutsDir = Path.Combine(appDataDir, "Shortcuts");
            Directory.CreateDirectory(iconsDir);
            Directory.CreateDirectory(shortcutsDir);

            string icoPath = IconHelper.GenerateGroupIcoFile(group, iconsDir);
            string safeName = string.Join("_", group.Name.Split(Path.GetInvalidFileNameChars())).Trim();
            if (string.IsNullOrEmpty(safeName)) safeName = group.Id;

            string lnkPath = Path.Combine(shortcutsDir, nameIsShared ? $"{safeName} (StackBar, {group.Id}).lnk" : $"{safeName} (StackBar).lnk");
            string exePath = AppInfo.ExePath;

            var link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetPath(exePath);
                link.SetArguments($"--group \"{group.Id}\"");
                link.SetWorkingDirectory(Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory);
                link.SetDescription($"Open {group.Name} StackBar Folder");
                link.SetIconLocation(icoPath, 0);

                if (link is IPropertyStore propStore)
                {
                    var key = AppUserModelIdKey;
                    var pv = new PROPVARIANT
                    {
                        vt = 31, // VT_LPWSTR
                        pwszVal = Marshal.StringToCoTaskMemUni(GroupAumidPrefix(appDataDir) + group.Id)
                    };
                    try
                    {
                        propStore.SetValue(ref key, ref pv);
                        propStore.Commit();
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(pv.pwszVal);
                    }
                }

                ((IPersistFile)link).Save(lnkPath, false);
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }

            return lnkPath;
        }

        [DllImport("shell32.dll")]
        internal static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PROPVARIANT pvar);

        private static string PinnedTaskbarDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

        /// <summary>
        /// Extracts the group id from shortcut arguments such as <c>--group "abc123"</c>.
        /// "--group" must be a whole argument (so "--groups" doesn't match), and the id is the next argument.
        /// </summary>
        public static bool TryGetGroupIdFromArgs(string? args, out string groupId)
        {
            groupId = string.Empty;
            if (string.IsNullOrWhiteSpace(args)) return false;

            var tokens = System.Text.RegularExpressions.Regex.Matches(args!, "\"[^\"]*\"?|\\S+")
                .Cast<System.Text.RegularExpressions.Match>()
                .Select(m => m.Value)
                .ToList();

            for (int i = 0; i < tokens.Count - 1; i++)
            {
                if (!tokens[i].Equals("--group", StringComparison.OrdinalIgnoreCase)) continue;
                groupId = tokens[i + 1].Trim('"').Trim();
                return groupId.Length > 0;
            }
            return false;
        }

        internal enum PinOwnership
        {
            /// <summary>Not a StackBar stack pin of this edition, or one that another existing StackBar copy uses.</summary>
            NotOurs,
            /// <summary>Points at this exe.</summary>
            Ours,
            /// <summary>One of our pins whose exe no longer exists (StackBar was moved or reinstalled elsewhere).</summary>
            Orphaned
        }

        /// <summary>
        /// Decides whether a pinned shortcut is one of this StackBar's stacks. Arguments alone are not enough:
        /// a third-party pin could use "--group web", and a portable copy, a dev build or a downloaded exe
        /// must not take over the installed copy's pins (or the other way round).
        /// </summary>
        internal static PinOwnership ClassifyPin(string args, string target, string? aumid, string ourExe, string aumidPrefix, out string groupId)
        {
            if (!TryGetGroupIdFromArgs(args, out groupId)) return PinOwnership.NotOurs;

            string fileName = Path.GetFileName(target ?? string.Empty);
            if (!fileName.StartsWith("StackBar", StringComparison.OrdinalIgnoreCase) ||
                !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return PinOwnership.NotOurs;
            }

            bool isOurExe = string.Equals(target, ourExe, StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(aumid))
            {
                // Without an app ID we can only be sure about pins that already point at us
                return isOurExe ? PinOwnership.Ours : PinOwnership.NotOurs;
            }
            if (!string.Equals(aumid, aumidPrefix + groupId, StringComparison.OrdinalIgnoreCase))
            {
                // Pins created before editions were tagged all use "StackBar.Group.<id>"; keep managing those
                // only while they already point at this exe
                bool legacyPin = string.Equals(aumid, "StackBar.Group." + groupId, StringComparison.OrdinalIgnoreCase);
                return legacyPin && isOurExe ? PinOwnership.Ours : PinOwnership.NotOurs;
            }

            if (isOurExe) return PinOwnership.Ours;
            return File.Exists(target) ? PinOwnership.NotOurs : PinOwnership.Orphaned;
        }

        private static string ReadLinkArguments(IShellLinkW link)
        {
            var argsSb = new StringBuilder(1024);
            link.GetArguments(argsSb, argsSb.Capacity);
            return argsSb.ToString();
        }

        private static PinOwnership ClassifyLoadedPin(IShellLinkW link, string appDataDir, out string groupId)
        {
            var pathSb = new StringBuilder(1024);
            link.GetPath(pathSb, pathSb.Capacity, IntPtr.Zero, 0);
            return ClassifyPin(ReadLinkArguments(link), pathSb.ToString(), ReadAppUserModelId(link), AppInfo.ExePath,
                GroupAumidPrefix(appDataDir), out groupId);
        }

        internal static string? ReadAppUserModelId(object link)
        {
            if (!(link is IPropertyStore store)) return null;
            var key = AppUserModelIdKey;
            if (store.GetValue(ref key, out PROPVARIANT value) != 0) return null;
            try
            {
                return value.vt == 31 && value.pwszVal != IntPtr.Zero ? Marshal.PtrToStringUni(value.pwszVal) : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }

        /// <summary>
        /// Removes a deleted stack's shortcuts. Windows has no supported unpin API, so the pinned copy is
        /// unpinned through the shell's "taskbarunpin" verb; deleting the .lnk file directly would only
        /// leave a broken taskbar button behind.
        /// </summary>
        /// <returns>false if the stack is still pinned and the user has to unpin it manually.</returns>
        public static bool DeleteGroupShortcuts(string groupId, string appDataDir)
        {
            bool allUnpinned = true;
            try
            {
                string pinnedDir = PinnedTaskbarDirectory;
                if (Directory.Exists(pinnedDir))
                {
                    foreach (var file in Directory.GetFiles(pinnedDir, "*.lnk"))
                    {
                        if (!LinkFileMatchesGroup(file, groupId, appDataDir)) continue;
                        if (!TryUnpinFromTaskbar(pinnedDir, file))
                        {
                            allUnpinned = false;
                        }
                    }
                }

                string shortcutsDir = Path.Combine(appDataDir, "Shortcuts");
                if (Directory.Exists(shortcutsDir))
                {
                    foreach (var file in Directory.GetFiles(shortcutsDir, "*.lnk"))
                    {
                        if (LinkFileMatchesGroup(file, groupId, appDataDir))
                        {
                            try { File.Delete(file); } catch { }
                        }
                    }
                }

                string iconsDir = Path.Combine(appDataDir, "Icons");
                if (Directory.Exists(iconsDir))
                {
                    foreach (var file in Directory.GetFiles(iconsDir, $"stackbar_{groupId}_*.ico").Concat(CustomIconFiles(iconsDir, groupId)))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }

                if (Directory.Exists(pinnedDir)) NotifyFolderUpdated(pinnedDir);
            }
            catch (Exception ex)
            {
                AppLogger.Log("DeleteGroupShortcuts failed: " + ex.Message);
            }
            return allUnpinned;
        }

        private static bool LinkFileMatchesGroup(string file, string groupId, string appDataDir)
        {
            try
            {
                var link = (IShellLinkW)new ShellLink();
                try
                {
                    ((IPersistFile)link).Load(file, 0);
                    return ClassifyLoadedPin(link, appDataDir, out var id) != PinOwnership.NotOurs &&
                           id.Equals(groupId, StringComparison.OrdinalIgnoreCase);
                }
                finally
                {
                    Marshal.FinalReleaseComObject(link);
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryUnpinFromTaskbar(string pinnedDir, string file)
        {
            try
            {
                Type? shellAppType = Type.GetTypeFromProgID("Shell.Application");
                if (shellAppType == null) return false;
                dynamic? shell = Activator.CreateInstance(shellAppType);
                dynamic? folder = shell?.NameSpace(pinnedDir);
                dynamic? folderItem = folder?.ParseName(Path.GetFileName(file));
                if (folderItem == null) return false;
                folderItem.InvokeVerb("taskbarunpin");

                // Explorer removes the pinned .lnk asynchronously
                for (int i = 0; i < 20 && File.Exists(file); i++)
                {
                    System.Threading.Thread.Sleep(50);
                }
                return !File.Exists(file);
            }
            catch (Exception ex)
            {
                AppLogger.Log("Unpin failed for " + file + ": " + ex.Message);
                return false;
            }
        }

        private static bool SameIconLocation(string stored, string expected)
        {
            return string.Equals(Environment.ExpandEnvironmentVariables(stored ?? string.Empty), expected, StringComparison.OrdinalIgnoreCase);
        }

        public static void UpdatePinnedTaskbarShortcutForGroup(AppGroup group, string appDataDir)
        {
            try
            {
                string pinnedDir = PinnedTaskbarDirectory;

                if (!Directory.Exists(pinnedDir)) return;

                string? icoPath = null;
                bool changed = false;
                foreach (var file in Directory.GetFiles(pinnedDir, "*.lnk"))
                {
                    try
                    {
                        var link = (IShellLinkW)new ShellLink();
                        try
                        {
                            ((IPersistFile)link).Load(file, 0);
                            if (ClassifyLoadedPin(link, appDataDir, out var id) == PinOwnership.NotOurs ||
                                !id.Equals(group.Id, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            // The .ico name is versioned by content, so an unchanged look means an unchanged path
                            icoPath ??= IconHelper.GenerateGroupIcoFile(group, Path.Combine(appDataDir, "Icons"));
                            var pathSb = new StringBuilder(1024);
                            link.GetPath(pathSb, pathSb.Capacity, IntPtr.Zero, 0);
                            var iconSb = new StringBuilder(1024);
                            link.GetIconLocation(iconSb, iconSb.Capacity, out _);
                            if (string.Equals(pathSb.ToString(), AppInfo.ExePath, StringComparison.OrdinalIgnoreCase) &&
                                SameIconLocation(iconSb.ToString(), icoPath))
                            {
                                continue;
                            }

                            link.SetPath(AppInfo.ExePath);
                            link.SetIconLocation(icoPath, 0);
                            ((IPersistFile)link).Save(file, false);
                            try { File.SetLastWriteTimeUtc(file, DateTime.UtcNow); } catch { }
                            changed = true;
                        }
                        finally
                        {
                            Marshal.FinalReleaseComObject(link);
                        }
                    }
                    catch { }
                }

                if (changed) NotifyTaskbarIconsChanged();
            }
            catch { }
        }

        private const uint SHCNE_UPDATEDIR = 0x00001000;
        private const uint SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;
        private const uint SHCNF_PATHW = 0x0005;

        /// <summary>
        /// Makes the taskbar reload pinned/app icons. The taskbar ignores per-item notifications
        /// (SHCNE_UPDATEITEM by path or ID list), so this uses SHCNE_ASSOCCHANGED - asynchronously
        /// (no SHCNF_FLUSH) and only when an icon really changed, not on every edit.
        /// </summary>
        internal static void NotifyTaskbarIconsChanged()
        {
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }

        private static void NotifyFolderUpdated(string path)
        {
            IntPtr p = Marshal.StringToHGlobalUni(path);
            try { SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, p, IntPtr.Zero); }
            finally { Marshal.FreeHGlobal(p); }
        }

        public static bool IsHexPrefix(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length != 8) return false;
            for (int i = 0; i < 8; i++)
            {
                char c = s[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Repairs this StackBar's pinned stacks: re-points orphaned pins (the exe was moved or reinstalled)
        /// and regenerates missing icons. Pins that point at another existing StackBar copy are left alone,
        /// so running a second copy (portable, dev build, a downloaded exe) never takes them over.
        /// </summary>
        public static void HealPinnedTaskbarShortcuts(IReadOnlyList<AppGroup> groups, string appDataDir)
        {
            try
            {
                string pinnedDir = PinnedTaskbarDirectory;
                if (!Directory.Exists(pinnedDir)) return;

                bool changed = false;
                string iconsDir = Path.Combine(appDataDir, "Icons");
                var iconsInUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string taggedPrefix = GroupAumidPrefix(appDataDir);
                foreach (var file in Directory.GetFiles(pinnedDir, "*.lnk"))
                {
                    try
                    {
                        var link = (IShellLinkW)new ShellLink();
                        try
                        {
                            ((IPersistFile)link).Load(file, 0);
                            var ownership = ClassifyLoadedPin(link, appDataDir, out var groupId);
                            if (ownership == PinOwnership.NotOurs) continue;

                            var iconSb = new StringBuilder(1024);
                            link.GetIconLocation(iconSb, iconSb.Capacity, out _);
                            string currentIcon = Environment.ExpandEnvironmentVariables(iconSb.ToString());
                            iconsInUse.Add(currentIcon);

                            var group = groups.FirstOrDefault(g => g.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
                            if (group == null) continue;

                            // Portable pins made before pins were tagged carry "StackBar.Group.<id>": tag them now,
                            // so they are still recognised after the portable folder moves
                            bool retag = AppInfo.IsPortableMode &&
                                         !string.Equals(ReadAppUserModelId(link), taggedPrefix + group.Id, StringComparison.OrdinalIgnoreCase);
                            bool iconMissing = !File.Exists(currentIcon);
                            if (ownership == PinOwnership.Ours && !iconMissing && !retag) continue;

                            if (ownership == PinOwnership.Orphaned || iconMissing)
                            {
                                string icoPath = IconHelper.GenerateGroupIcoFile(group, iconsDir);
                                iconsInUse.Add(icoPath);
                                link.SetPath(AppInfo.ExePath);
                                link.SetWorkingDirectory(Path.GetDirectoryName(AppInfo.ExePath) ?? string.Empty);
                                link.SetIconLocation(icoPath, 0);
                            }
                            if (retag) SetAppUserModelId(link, taggedPrefix + group.Id);
                            ((IPersistFile)link).Save(file, false);
                            try { File.SetLastWriteTimeUtc(file, DateTime.UtcNow); } catch { }
                            changed = true;
                        }
                        finally
                        {
                            Marshal.FinalReleaseComObject(link);
                        }
                    }
                    catch { }
                }

                if (changed) NotifyTaskbarIconsChanged();

                // Generated shortcuts not pinned yet (Shortcuts\) use these icons too
                string shortcutsDir = Path.Combine(appDataDir, "Shortcuts");
                if (Directory.Exists(shortcutsDir))
                {
                    foreach (var file in Directory.GetFiles(shortcutsDir, "*.lnk"))
                    {
                        try
                        {
                            var link = (IShellLinkW)new ShellLink();
                            try
                            {
                                ((IPersistFile)link).Load(file, 0);
                                var iconSb = new StringBuilder(1024);
                                link.GetIconLocation(iconSb, iconSb.Capacity, out _);
                                iconsInUse.Add(Environment.ExpandEnvironmentVariables(iconSb.ToString()));
                            }
                            finally
                            {
                                Marshal.FinalReleaseComObject(link);
                            }
                        }
                        catch { }
                    }
                }
                CleanUpUnusedGroupIcons(groups, iconsDir, iconsInUse);
            }
            catch { }
        }

        /// <summary>
        /// Removes stack icons left over from bursts of edits (each edit writes a new versioned .ico; files
        /// younger than two minutes are kept at write time). Keeps every icon a pin uses and each stack's newest.
        /// </summary>
        internal static void CleanUpUnusedGroupIcons(IEnumerable<AppGroup> groups, string iconsDir, ISet<string> iconsInUse)
        {
            if (!Directory.Exists(iconsDir)) return;
            foreach (var group in groups)
            {
                var files = Directory.GetFiles(iconsDir, $"stackbar_{group.Id}_*.ico")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();
                foreach (var file in files.Skip(1))
                {
                    if (iconsInUse.Contains(file.FullName)) continue;
                    if (DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromMinutes(2)) continue;
                    try { file.Delete(); } catch { }
                }
            }
        }

        private static void SetAppUserModelId(object link, string aumid)
        {
            if (!(link is IPropertyStore store)) return;
            var key = AppUserModelIdKey;
            var value = new PROPVARIANT { vt = 31, pwszVal = Marshal.StringToCoTaskMemUni(aumid) };
            try
            {
                store.SetValue(ref key, ref value);
                store.Commit();
            }
            finally
            {
                Marshal.FreeCoTaskMem(value.pwszVal);
            }
        }

        /// <summary>
        /// Migrates legacy items pointing into User Pinned\TaskBar to persistent copies, so unpinning won't break them.
        /// </summary>
        /// <returns>true if the config was changed (and saved).</returns>
        public static bool MigrateLegacyPinnedTargets(AppConfig config, ConfigService configService)
        {
            string pinnedDir = PinnedTaskbarDirectory;
            string linksDir = Path.Combine(configService.AppDataDirectory, "Links");
            Directory.CreateDirectory(linksDir);

            // The full app scan is expensive: run it at most once, and only if something needs recovering
            List<DiscoveredAppItem>? scanned = null;
            bool modified = false;
            foreach (var group in config.Groups)
            {
                foreach (var item in group.Items)
                {
                    if (item.TargetPath.StartsWith(pinnedDir, StringComparison.OrdinalIgnoreCase))
                    {
                        if (File.Exists(item.TargetPath))
                        {
                            string ext = Path.GetExtension(item.TargetPath);
                            string cleanName = item.Name;
                            if (cleanName.Length > 9 && cleanName[8] == '_' && IsHexPrefix(cleanName.Substring(0, 8)))
                            {
                                cleanName = cleanName.Substring(9);
                            }
                            item.Name = cleanName;

                            string persistentLnk = NewPersistentLinkPath(linksDir, cleanName, ext);
                            try
                            {
                                File.Copy(item.TargetPath, persistentLnk, true);
                                item.TargetPath = persistentLnk;
                                if (!string.IsNullOrEmpty(item.CustomIconPath) && item.CustomIconPath!.StartsWith(pinnedDir, StringComparison.OrdinalIgnoreCase))
                                {
                                    item.CustomIconPath = persistentLnk;
                                }
                                modified = true;
                            }
                            catch { }
                        }
                        else
                        {
                            // Target shortcut was already deleted from Windows pinned folder!
                            // Try to re-resolve from Start Menu or ProgramFiles by App name
                            scanned ??= ScanAllTaskbarAndSystemApps();
                            var recovered = scanned
                                .Select(a => a.Item)
                                .FirstOrDefault(a => a.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase) &&
                                                     !a.TargetPath.StartsWith(pinnedDir, StringComparison.OrdinalIgnoreCase));
                            if (recovered != null)
                            {
                                item.TargetPath = recovered.TargetPath;
                                item.WorkingDirectory = recovered.WorkingDirectory;
                                item.CustomIconPath = recovered.CustomIconPath;
                                item.CustomIconIndex = recovered.CustomIconIndex;
                                item.Kind = recovered.Kind;
                                modified = true;
                            }
                        }
                    }
                }
            }

            if (modified)
            {
                configService.Save(config);
            }
            return modified;
        }

        private static readonly Dictionary<string, string> _shellAppNameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static bool _shellAppNamesLoaded;

        public static string CleanItemName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;
            string name = rawName.Trim();
            if (name.Length > 9 && name[8] == '_' && IsHexPrefix(name.Substring(0, 8)))
            {
                name = name.Substring(9).Trim();
            }
            return name;
        }

        public static string? ResolveShellAppDisplayName(string targetUri)
        {
            if (string.IsNullOrWhiteSpace(targetUri)) return null;
            string path = targetUri.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase)
                ? targetUri.Substring("shell:AppsFolder\\".Length)
                : targetUri;

            lock (_shellAppNameCache)
            {
                if (_shellAppNameCache.TryGetValue(path, out var cached)) return cached;
                // The whole AppsFolder was already enumerated once: a miss stays a miss (no COM scan per lookup)
                if (_shellAppNamesLoaded) return null;
            }

            try
            {
                Type? shellAppType = Type.GetTypeFromProgID("Shell.Application");
                if (shellAppType != null)
                {
                    dynamic? shell = Activator.CreateInstance(shellAppType);
                    if (shell != null)
                    {
                        dynamic? folder = shell.NameSpace("shell:AppsFolder");
                        if (folder != null)
                        {
                            foreach (dynamic it in folder.Items())
                            {
                                try
                                {
                                    string itPath = it.Path;
                                    string itName = it.Name;
                                    if (!string.IsNullOrWhiteSpace(itPath) && !string.IsNullOrWhiteSpace(itName))
                                    {
                                        lock (_shellAppNameCache)
                                        {
                                            _shellAppNameCache[itPath] = itName;
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch { }

            lock (_shellAppNameCache)
            {
                _shellAppNamesLoaded = true;
                return _shellAppNameCache.TryGetValue(path, out var name) ? name : null;
            }
        }

        public static GroupItem ImportAndPersistShortcut(string sourceFile, string appDataDir, string? overrideFriendlyName = null)
        {
            // If it's a Directory/Folder
            if (Directory.Exists(sourceFile))
            {
                string dirName = !string.IsNullOrWhiteSpace(overrideFriendlyName)
                    ? CleanItemName(overrideFriendlyName!)
                    : new DirectoryInfo(sourceFile).Name;

                return new GroupItem
                {
                    Name = dirName,
                    TargetPath = sourceFile,
                    WorkingDirectory = sourceFile,
                    Kind = ItemKind.Folder,
                    SourcePath = sourceFile
                };
            }

            string linksDir = Path.Combine(appDataDir, "Links");
            Directory.CreateDirectory(linksDir);

            // Parse source first to extract original clean application name and target
            var item = ParseShortcut(sourceFile);
            item.SourcePath = sourceFile;
            string originalCleanName = !string.IsNullOrWhiteSpace(overrideFriendlyName)
                ? CleanItemName(overrideFriendlyName!)
                : CleanItemName(item.Name);

            string ext = Path.GetExtension(sourceFile);

            // Shell App URI (shell:AppsFolder\...)
            if (sourceFile.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
            {
                item.Name = originalCleanName;
                item.TargetPath = sourceFile;
                item.Kind = ItemKind.ShellApp;
                return item;
            }

            // If it's an executable or file, reference directly
            if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                item.Name = originalCleanName;
                item.TargetPath = sourceFile;
                item.WorkingDirectory = Path.GetDirectoryName(sourceFile) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                item.Kind = ItemKind.File;
                return item;
            }

            // Copy shortcut to persistent Links folder so unpinning won't destroy it!
            string persistentLnkPath = NewPersistentLinkPath(linksDir, originalCleanName, ext);

            try
            {
                File.Copy(sourceFile, persistentLnkPath, true);
                item.TargetPath = persistentLnkPath;

                // The icon must not depend on the source file either (it disappears when the app is unpinned)
                if (string.Equals(item.CustomIconPath, sourceFile, StringComparison.OrdinalIgnoreCase))
                {
                    item.CustomIconPath = persistentLnkPath;
                }
            }
            catch
            {
                item.TargetPath = sourceFile;
            }

            item.Name = originalCleanName;
            return item;
        }

        /// <summary>
        /// True if the group already holds an item imported from the same source (or pointing at the same target).
        /// Needed because every .lnk/.url import is copied under a new unique name, so comparing
        /// the stored TargetPath alone never detects a re-import.
        /// </summary>
        public static bool GroupContainsSource(AppGroup group, string sourcePath, string? displayName = null)
        {
            return new DuplicateIndex(group).Contains(sourcePath, displayName);
        }

        /// <summary>
        /// Duplicate check for one import batch: each existing item's shortcut is read once, not once per
        /// candidate (importing many apps into a big stack would otherwise re-read every shortcut each time).
        /// </summary>
        public sealed class DuplicateIndex
        {
            private readonly AppGroup _group;
            private readonly HashSet<string> _identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public DuplicateIndex(AppGroup group)
            {
                _group = group;
                foreach (var item in group.Items) _identities.UnionWith(LaunchIdentities(item.TargetPath));
            }

            public bool Contains(string sourcePath, string? displayName = null)
            {
                string name = CleanItemName(displayName ?? Path.GetFileNameWithoutExtension(sourcePath));
                if (_group.Items.Any(existing =>
                        string.Equals(existing.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(existing.TargetPath, sourcePath, StringComparison.OrdinalIgnoreCase) ||
                        // Items imported before SourcePath existed: fall back to the display name
                        (existing.SourcePath == null && existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase))))
                {
                    return true;
                }

                // The same app is offered by several sources (pinned .lnk, Start Menu .lnk, AppsFolder entry),
                // each with its own path: compare what they launch instead
                return LaunchIdentities(sourcePath).Overlaps(_identities);
            }

            /// <summary>Records an item added during the batch.</summary>
            public void Add(GroupItem item) => _identities.UnionWith(LaunchIdentities(item.TargetPath));
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

        /// <summary>
        /// AppsFolder ids of desktop apps without an app ID look like "{1AC14E77-...}\cmd.exe": a known-folder
        /// GUID (here: System32) plus the rest of the path. Returns the real path, or null if it isn't one.
        /// </summary>
        internal static string? ExpandKnownFolderPath(string id)
        {
            if (!id.StartsWith("{")) return null;
            int close = id.IndexOf("}\\", StringComparison.Ordinal);
            if (close < 0 || !Guid.TryParse(id.Substring(0, close + 1), out var folderId)) return null;

            IntPtr ptr = IntPtr.Zero;
            try
            {
                if (SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out ptr) != 0) return null;
                return Path.Combine(Marshal.PtrToStringUni(ptr)!, id.Substring(close + 2));
            }
            catch
            {
                return null;
            }
            finally
            {
                if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
            }
        }

        /// <summary>
        /// Keys describing what a path launches: "exe:&lt;target&gt;|&lt;arguments&gt;" for programs and shortcuts,
        /// "app:&lt;AppUserModelID&gt;" for shortcuts with an app ID and AppsFolder entries, "url:&lt;URL&gt;" for .url files.
        /// </summary>
        internal static HashSet<string> LaunchIdentities(string path)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(path)) return keys;

            const string appsFolder = "shell:AppsFolder\\";
            if (path.StartsWith(appsFolder, StringComparison.OrdinalIgnoreCase))
            {
                // A desktop app's AppsFolder id is its app ID, its exe path, or a known-folder-relative exe path
                string id = path.Substring(appsFolder.Length);
                keys.Add("app:" + id);
                keys.Add("exe:" + (ExpandKnownFolderPath(id) ?? id) + "|");
                return keys;
            }

            try
            {
                if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                {
                    var link = (IShellLinkW)new ShellLink();
                    try
                    {
                        ((IPersistFile)link).Load(path, 0);
                        var target = new StringBuilder(1024);
                        link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
                        if (target.Length > 0) keys.Add("exe:" + target + "|" + ReadLinkArguments(link).Trim());
                        string? aumid = ReadAppUserModelId(link);
                        if (!string.IsNullOrEmpty(aumid)) keys.Add("app:" + aumid);
                    }
                    finally
                    {
                        Marshal.FinalReleaseComObject(link);
                    }
                }
                else if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                {
                    string? url = File.ReadAllLines(path)
                        .FirstOrDefault(l => l.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))?.Substring(4).Trim();
                    if (!string.IsNullOrEmpty(url)) keys.Add("url:" + url);
                }
                else if (File.Exists(path))
                {
                    keys.Add("exe:" + Path.GetFullPath(path) + "|");
                }
            }
            catch
            {
                // Unreadable source: fall back to the path-based checks only
            }
            return keys;
        }

        /// <summary>
        /// Deletes the persistent Links\ copy of a removed item unless another item still uses it.
        /// </summary>
        public static void DeletePersistedLinkIfUnused(GroupItem removed, AppConfig config, string appDataDir)
        {
            try
            {
                string linksDir = Path.Combine(appDataDir, "Links") + Path.DirectorySeparatorChar;
                string path = removed.TargetPath;
                if (!path.StartsWith(linksDir, StringComparison.OrdinalIgnoreCase)) return;

                bool stillUsed = config.Groups.Any(g => g.Items.Any(i =>
                    string.Equals(i.TargetPath, path, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(i.CustomIconPath, path, StringComparison.OrdinalIgnoreCase)));
                if (!stillUsed && File.Exists(path))
                {
                    File.Delete(path);

                    // Remove the per-copy folder (Links\<id>\) once it is empty
                    string? folder = Path.GetDirectoryName(path);
                    if (folder != null && folder.Length + 1 > linksDir.Length &&
                        Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    {
                        Directory.Delete(folder);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log("Could not delete persisted link: " + ex.Message);
            }
        }

        /// <summary>
        /// Path for a persistent shortcut copy: <c>Links\&lt;id&gt;\&lt;App Name&gt;.lnk</c>.
        /// The file name stays clean because Windows shows it as the taskbar name when the user pins
        /// an app that was started from the flyout (Windows pins a copy of the launching shortcut).
        /// The unique folder keeps two copies of the same app apart.
        /// </summary>
        internal static string NewPersistentLinkPath(string linksDir, string displayName, string ext)
        {
            string safeName = string.Join("_", CleanItemName(displayName).Split(Path.GetInvalidFileNameChars())).Trim();
            if (safeName.Length == 0) safeName = "Shortcut";

            string folder = Path.Combine(linksDir, Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, safeName + ext);
        }

        /// <summary>
        /// Moves copies made by older versions (<c>Links\&lt;hex&gt;_&lt;Name&gt;.lnk</c>) to
        /// <c>Links\&lt;hex&gt;\&lt;Name&gt;.lnk</c> and re-points the items, so pins created from now on get clean names.
        /// </summary>
        /// <param name="obsoleteFiles">
        /// The old copies. The caller deletes them only after the updated config was saved successfully,
        /// otherwise a failed save would leave items pointing at deleted files.
        /// </param>
        /// <returns>true if any item was changed.</returns>
        public static bool MigrateFlatLinkCopies(AppConfig config, string appDataDir, out List<string> obsoleteFiles)
        {
            obsoleteFiles = new List<string>();
            string linksDir = Path.Combine(appDataDir, "Links");
            if (!Directory.Exists(linksDir)) return false;

            var moved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? Migrate(string? path)
            {
                if (string.IsNullOrEmpty(path)) return path;
                if (moved.TryGetValue(path!, out var already)) return already;
                if (!string.Equals(Path.GetDirectoryName(path), linksDir, StringComparison.OrdinalIgnoreCase)) return path;

                string fileName = Path.GetFileName(path);
                if (fileName.Length <= 9 || fileName[8] != '_' || !IsHexPrefix(fileName.Substring(0, 8))) return path;

                string target = Path.Combine(linksDir, fileName.Substring(0, 8), fileName.Substring(9));
                if (File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (!File.Exists(target)) File.Copy(path, target);
                }
                else if (!File.Exists(target))
                {
                    return path; // neither copy exists: nothing to re-point to
                }
                // Old file already gone but the new copy exists (an earlier run's save failed): re-point anyway
                moved[path!] = target;
                return target;
            }

            bool modified = false;
            foreach (var item in config.Groups.SelectMany(g => g.Items))
            {
                try
                {
                    string newTarget = Migrate(item.TargetPath) ?? item.TargetPath;
                    string? newIcon = Migrate(item.CustomIconPath);
                    if (newTarget != item.TargetPath || newIcon != item.CustomIconPath)
                    {
                        item.TargetPath = newTarget;
                        item.CustomIconPath = newIcon;
                        modified = true;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log("Could not migrate link copy " + item.TargetPath + ": " + ex.Message);
                }
            }

            obsoleteFiles.AddRange(moved.Keys.Where(File.Exists));
            return modified;
        }

        /// <summary>
        /// Recursive file listing that skips folders it can't read, instead of losing the whole tree
        /// (Directory.GetFiles(AllDirectories) throws on the first inaccessible or too-long subfolder).
        /// </summary>
        internal static IEnumerable<string> EnumerateFilesSafely(string root, string pattern)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                string[] files, subdirs;
                try { files = Directory.GetFiles(dir, pattern); } catch { files = new string[0]; }
                try { subdirs = Directory.GetDirectories(dir); } catch { subdirs = new string[0]; }
                foreach (var file in files) yield return file;
                foreach (var sub in subdirs) pending.Push(sub);
            }
        }

        /// <summary>Deletes files that a successfully saved config no longer references.</summary>
        public static void DeleteFiles(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                try { File.Delete(path); } catch (Exception ex) { AppLogger.Log("Could not delete " + path + ": " + ex.Message); }
            }
        }

        public static GroupItem ParseShortcut(string lnkOrFilePath)
        {
            string rawName = Path.GetFileNameWithoutExtension(lnkOrFilePath);
            string name = CleanItemName(rawName);

            if (Directory.Exists(lnkOrFilePath))
            {
                return new GroupItem
                {
                    Name = new DirectoryInfo(lnkOrFilePath).Name,
                    TargetPath = lnkOrFilePath,
                    WorkingDirectory = lnkOrFilePath,
                    Kind = ItemKind.Folder
                };
            }

            if (lnkOrFilePath.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
            {
                string friendly = ResolveShellAppDisplayName(lnkOrFilePath) ?? name;
                return new GroupItem
                {
                    Name = friendly,
                    TargetPath = lnkOrFilePath,
                    Kind = ItemKind.ShellApp
                };
            }

            if (lnkOrFilePath.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
            {
                string? urlIcon = null;
                int urlIconIndex = 0;
                try
                {
                    foreach (var line in File.ReadAllLines(lnkOrFilePath))
                    {
                        if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                        {
                            string candidate = Environment.ExpandEnvironmentVariables(line.Substring("IconFile=".Length).Trim());
                            if (File.Exists(candidate))
                                urlIcon = candidate;
                        }
                        else if (line.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase))
                        {
                            int.TryParse(line.Substring("IconIndex=".Length).Trim(), out urlIconIndex);
                        }
                    }
                }
                catch { }

                return new GroupItem
                {
                    Name = name,
                    TargetPath = lnkOrFilePath,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    CustomIconPath = urlIcon ?? lnkOrFilePath,
                    CustomIconIndex = urlIcon != null ? urlIconIndex : 0,
                    Kind = ItemKind.Shortcut
                };
            }

            if (lnkOrFilePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string workDir = string.Empty;
                string customIcon = lnkOrFilePath;
                int customIconIndex = 0;
                try
                {
                    var link = (IShellLinkW)new ShellLink();
                    try
                    {
                        ((IPersistFile)link).Load(lnkOrFilePath, 0);
                        var workDirSb = new StringBuilder(260);
                        link.GetWorkingDirectory(workDirSb, workDirSb.Capacity);
                        workDir = workDirSb.ToString();

                        var iconSb = new StringBuilder(1024);
                        link.GetIconLocation(iconSb, iconSb.Capacity, out int iconIndex);
                        string rawIcon = Environment.ExpandEnvironmentVariables(iconSb.ToString());
                        if (File.Exists(rawIcon))
                        {
                            customIcon = rawIcon;
                            customIconIndex = iconIndex;
                        }

                        // Also extract target executable as icon candidate!
                        var pathSb = new StringBuilder(1024);
                        link.GetPath(pathSb, pathSb.Capacity, IntPtr.Zero, 0);
                        string targetPath = pathSb.ToString();
                        if (File.Exists(targetPath) && (customIcon == lnkOrFilePath || !File.Exists(customIcon)))
                        {
                            customIcon = targetPath;
                            customIconIndex = 0;
                        }
                    }
                    finally
                    {
                        Marshal.FinalReleaseComObject(link);
                    }
                }
                catch { }

                return new GroupItem
                {
                    Name = name,
                    TargetPath = lnkOrFilePath,
                    WorkingDirectory = Directory.Exists(workDir) ? workDir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    CustomIconPath = customIcon,
                    CustomIconIndex = customIconIndex,
                    Kind = ItemKind.Shortcut
                };
            }

            return new GroupItem
            {
                Name = name,
                TargetPath = lnkOrFilePath,
                WorkingDirectory = Path.GetDirectoryName(lnkOrFilePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Kind = ItemKind.File
            };
        }

        public static List<DiscoveredAppItem> ScanAllTaskbarAndSystemApps()
        {
            var results = new List<DiscoveredAppItem>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddIfUnique(GroupItem? item, string badge)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.TargetPath))
                    return;
                if (item.Name.IndexOf("StackBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    item.TargetPath.IndexOf("StackBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    item.Name.IndexOf("Uninstall", StringComparison.OrdinalIgnoreCase) >= 0)
                    return;

                string key = $"{item.Name.Trim()}|{item.TargetPath}";
                if (seenKeys.Add(key))
                {
                    results.Add(new DiscoveredAppItem { Item = item, SourceBadge = badge });
                }
            }

            // 1. Pinned Taskbar Folder (.lnk and .url)
            try
            {
                string pinnedDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

                if (Directory.Exists(pinnedDir))
                {
                    foreach (var file in Directory.GetFiles(pinnedDir))
                    {
                        if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                            file.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                        {
                            AddIfUnique(ParseShortcut(file), "📌 Pinned");
                        }
                    }
                }
            }
            catch { }

            // 2. shell:AppsFolder via Shell.Application COM (Captures Start Menu apps & Store/UWP like Minecraft Launcher)
            try
            {
                Type? shellAppType = Type.GetTypeFromProgID("Shell.Application");
                if (shellAppType != null)
                {
                    dynamic? shell = Activator.CreateInstance(shellAppType);
                    if (shell != null)
                    {
                        dynamic? folder = shell.NameSpace("shell:AppsFolder");
                        if (folder != null)
                        {
                            foreach (dynamic item in folder.Items())
                            {
                                try
                                {
                                    string name = item.Name;
                                    string path = item.Path;
                                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(path))
                                    {
                                        lock (_shellAppNameCache)
                                        {
                                            _shellAppNameCache[path] = name;
                                        }

                                        bool isStore = path.IndexOf("!", StringComparison.OrdinalIgnoreCase) >= 0;
                                        AddIfUnique(new GroupItem
                                        {
                                            Name = name,
                                            TargetPath = $"shell:AppsFolder\\{path}",
                                            Kind = ItemKind.ShellApp
                                        }, isStore ? "🛍 Store" : "⊞ Installed");
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch { }

            // 3. Start Menu Programs folders (.lnk recursive)
            string[] startDirs =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs)
            };

            foreach (var dir in startDirs)
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
                try
                {
                    foreach (var file in EnumerateFilesSafely(dir, "*.lnk"))
                    {
                        AddIfUnique(ParseShortcut(file), "⊞ Start Menu");
                    }
                }
                catch { }
            }

            // 4. Desktop Shortcuts (Steam games, etc.)
            string[] desktopDirs =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            };

            foreach (var dir in desktopDirs)
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
                try
                {
                    foreach (var file in Directory.GetFiles(dir))
                    {
                        if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                            file.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                        {
                            AddIfUnique(ParseShortcut(file), "🖥 Desktop");
                        }
                    }
                }
                catch { }
            }

            return results;
        }

        /// <summary>Uses an app's displayed icon (including its custom icon/index) as the folder cover.</summary>
        public static string? PersistCustomFolderIcon(GroupItem item, string groupId, string appDataDir)
        {
            var bmp = IconHelper.ExtractBitmap(item.TargetPath, item.CustomIconPath, item.CustomIconIndex);
            return bmp == null ? null : SaveCustomFolderBitmap(bmp, groupId, appDataDir);
        }

        public static string? PersistCustomFolderIcon(string sourcePath, string groupId, string appDataDir)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) return null;

            string iconsDir = Path.Combine(appDataDir, "Icons");
            Directory.CreateDirectory(iconsDir);

            // Picking a file that already is a persisted cover: reuse it instead of copying onto itself
            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(sourcePath)), Path.GetFullPath(iconsDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(sourcePath).StartsWith("custom_", StringComparison.OrdinalIgnoreCase))
            {
                return sourcePath;
            }

            string ext = Path.GetExtension(sourcePath).ToLowerInvariant();

            // Direct image file (.png, .ico, .jpg, .jpeg, .bmp)
            if (ext == ".png" || ext == ".ico" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp")
            {
                if (File.Exists(sourcePath))
                {
                    string dest = NewCustomIconPath(iconsDir, groupId, ext);
                    File.Copy(sourcePath, dest, true);
                    return dest;
                }
            }

            // Executable, DLL, shortcut, or shell app: extract bitmap and save as high-res PNG
            var bmp = IconHelper.ExtractBitmap(sourcePath);
            return bmp == null ? null : SaveCustomFolderBitmap(bmp, groupId, appDataDir);
        }

        private static string SaveCustomFolderBitmap(System.Drawing.Bitmap bmp, string groupId, string appDataDir)
        {
            string iconsDir = Path.Combine(appDataDir, "Icons");
            Directory.CreateDirectory(iconsDir);
            using (bmp)
            {
                string dest = NewCustomIconPath(iconsDir, groupId, ".png");
                bmp.Save(dest, System.Drawing.Imaging.ImageFormat.Png);
                return dest;
            }
        }

        // A new file name per cover: rewriting the same path would serve the old picture from icon caches
        private static string NewCustomIconPath(string iconsDir, string groupId, string ext)
        {
            return Path.Combine(iconsDir, $"custom_{groupId}_{Guid.NewGuid().ToString("N").Substring(0, 8)}{ext}");
        }

        // Exact patterns ("custom_<id>.ext" and "custom_<id>_<version>.ext") so id "web" never matches "web-dev"
        private static IEnumerable<string> CustomIconFiles(string iconsDir, string groupId)
        {
            return Directory.GetFiles(iconsDir, $"custom_{groupId}.*")
                .Concat(Directory.GetFiles(iconsDir, $"custom_{groupId}_*"));
        }

        /// <summary>
        /// Removes a stack's previous cover images. Call only after the config that points at
        /// <paramref name="keep"/> (or at no cover) was saved, so a failed save never leaves a dangling path.
        /// </summary>
        public static void DeleteUnusedCustomIcons(string groupId, string? keep, string appDataDir)
        {
            string iconsDir = Path.Combine(appDataDir, "Icons");
            if (Directory.Exists(iconsDir)) DeleteOldCustomIcons(iconsDir, groupId, keep ?? string.Empty);
        }

        private static void DeleteOldCustomIcons(string iconsDir, string groupId, string keep)
        {
            foreach (var old in CustomIconFiles(iconsDir, groupId))
            {
                if (string.Equals(old, keep, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(old); } catch { }
            }
        }
    }
}

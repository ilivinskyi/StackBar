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
        private class ShellLink { }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
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
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint cProps);
            [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
            [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
            [PreserveSig] int Commit();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPVARIANT
        {
            public ushort vt;
            public ushort wReserved1;
            public ushort wReserved2;
            public ushort wReserved3;
            public IntPtr pwszVal;
            public IntPtr ptr2;
        }

        private static readonly PROPERTYKEY AppUserModelIdKey = new PROPERTYKEY
        {
            fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
            pid = 5
        };

        public static string CreateGroupShortcut(AppGroup group, string appDataDir)
        {
            string iconsDir = Path.Combine(appDataDir, "Icons");
            string shortcutsDir = Path.Combine(appDataDir, "Shortcuts");
            Directory.CreateDirectory(iconsDir);
            Directory.CreateDirectory(shortcutsDir);

            string icoPath = IconHelper.GenerateGroupIcoFile(group, iconsDir);
            string safeName = string.Join("_", group.Name.Split(Path.GetInvalidFileNameChars())).Trim();
            if (string.IsNullOrEmpty(safeName)) safeName = group.Id;

            string lnkPath = Path.Combine(shortcutsDir, $"{safeName} (StackBar).lnk");
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
                        pwszVal = Marshal.StringToCoTaskMemUni($"StackBar.Group.{group.Id}")
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
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        private const uint SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_FLUSH = 0x1000;

        private static string PinnedTaskbarDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

        /// <summary>
        /// Extracts the group id from shortcut arguments such as <c>--group "abc123"</c>.
        /// Exact parsing (not a substring search) so id "web" never matches "web-dev".
        /// </summary>
        public static bool TryGetGroupIdFromArgs(string? args, out string groupId)
        {
            groupId = string.Empty;
            if (string.IsNullOrWhiteSpace(args)) return false;

            const string flag = "--group";
            int idx = args!.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;

            string rest = args.Substring(idx + flag.Length).TrimStart();
            if (rest.Length == 0) return false;

            if (rest[0] == '"')
            {
                int end = rest.IndexOf('"', 1);
                groupId = end > 0 ? rest.Substring(1, end - 1) : rest.Substring(1);
            }
            else
            {
                int end = rest.IndexOfAny(new[] { ' ', '\t' });
                groupId = end > 0 ? rest.Substring(0, end) : rest;
            }

            groupId = groupId.Trim();
            return groupId.Length > 0;
        }

        private static bool IsShortcutForGroup(string args, string groupId)
        {
            return TryGetGroupIdFromArgs(args, out var id) && id.Equals(groupId, StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadLinkArguments(IShellLinkW link)
        {
            var argsSb = new StringBuilder(1024);
            link.GetArguments(argsSb, argsSb.Capacity);
            return argsSb.ToString();
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
                        if (!LinkFileMatchesGroup(file, groupId)) continue;
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
                        if (LinkFileMatchesGroup(file, groupId))
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

                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                AppLogger.Log("DeleteGroupShortcuts failed: " + ex.Message);
            }
            return allUnpinned;
        }

        private static bool LinkFileMatchesGroup(string file, string groupId)
        {
            try
            {
                var link = (IShellLinkW)new ShellLink();
                try
                {
                    ((IPersistFile)link).Load(file, 0);
                    return IsShortcutForGroup(ReadLinkArguments(link), groupId);
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

        public static void UpdatePinnedTaskbarShortcutForGroup(AppGroup group, string appDataDir)
        {
            try
            {
                string pinnedDir = PinnedTaskbarDirectory;

                if (!Directory.Exists(pinnedDir)) return;

                string? icoPath = null;
                bool updated = false;
                foreach (var file in Directory.GetFiles(pinnedDir, "*.lnk"))
                {
                    try
                    {
                        var link = (IShellLinkW)new ShellLink();
                        try
                        {
                            ((IPersistFile)link).Load(file, 0);
                            if (IsShortcutForGroup(ReadLinkArguments(link), group.Id))
                            {
                                icoPath ??= IconHelper.GenerateGroupIcoFile(group, Path.Combine(appDataDir, "Icons"));
                                link.SetPath(AppInfo.ExePath);
                                link.SetIconLocation(icoPath, 0);
                                ((IPersistFile)link).Save(file, false);
                                try { File.SetLastWriteTimeUtc(file, DateTime.UtcNow); } catch { }
                                updated = true;
                            }
                        }
                        finally
                        {
                            Marshal.FinalReleaseComObject(link);
                        }
                    }
                    catch { }
                }

                if (updated)
                {
                    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch { }
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
        /// Repairs pinned StackBar shortcuts whose exe path moved or whose icon file is gone.
        /// Shortcuts that are already correct are left untouched, so a normal start doesn't
        /// rewrite every pinned .lnk and flush the shell icon cache.
        /// </summary>
        public static void HealPinnedTaskbarShortcuts(AppConfig config, string appDataDir)
        {
            try
            {
                string pinnedDir = PinnedTaskbarDirectory;
                if (!Directory.Exists(pinnedDir)) return;

                bool changed = false;
                foreach (var file in Directory.GetFiles(pinnedDir, "*.lnk"))
                {
                    try
                    {
                        var link = (IShellLinkW)new ShellLink();
                        try
                        {
                            ((IPersistFile)link).Load(file, 0);
                            if (!TryGetGroupIdFromArgs(ReadLinkArguments(link), out var groupId)) continue;

                            var group = config.Groups.FirstOrDefault(g => g.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
                            if (group == null) continue;

                            var pathSb = new StringBuilder(1024);
                            link.GetPath(pathSb, pathSb.Capacity, IntPtr.Zero, 0);
                            var iconSb = new StringBuilder(1024);
                            link.GetIconLocation(iconSb, iconSb.Capacity, out _);

                            bool exeMoved = !string.Equals(pathSb.ToString(), AppInfo.ExePath, StringComparison.OrdinalIgnoreCase);
                            bool iconMissing = !File.Exists(Environment.ExpandEnvironmentVariables(iconSb.ToString()));
                            if (!exeMoved && !iconMissing) continue;

                            string icoPath = IconHelper.GenerateGroupIcoFile(group, Path.Combine(appDataDir, "Icons"));
                            link.SetPath(AppInfo.ExePath);
                            link.SetWorkingDirectory(Path.GetDirectoryName(AppInfo.ExePath) ?? string.Empty);
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

                if (changed)
                {
                    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch { }
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
            string name = CleanItemName(displayName ?? Path.GetFileNameWithoutExtension(sourcePath));
            return group.Items.Any(existing =>
                string.Equals(existing.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(existing.TargetPath, sourcePath, StringComparison.OrdinalIgnoreCase) ||
                // Items imported before SourcePath existed: fall back to the display name
                (existing.SourcePath == null && existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
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
        /// <returns>true if any item was changed.</returns>
        public static bool MigrateFlatLinkCopies(AppConfig config, string appDataDir)
        {
            string linksDir = Path.Combine(appDataDir, "Links");
            if (!Directory.Exists(linksDir)) return false;

            var moved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? Migrate(string? path)
            {
                if (string.IsNullOrEmpty(path)) return path;
                if (moved.TryGetValue(path!, out var already)) return already;
                if (!string.Equals(Path.GetDirectoryName(path), linksDir, StringComparison.OrdinalIgnoreCase)) return path;

                string fileName = Path.GetFileName(path);
                if (fileName.Length <= 9 || fileName[8] != '_' || !IsHexPrefix(fileName.Substring(0, 8)) || !File.Exists(path)) return path;

                string target = Path.Combine(linksDir, fileName.Substring(0, 8), fileName.Substring(9));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!File.Exists(target)) File.Copy(path, target);
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

            // Old files are deleted only after every item points at the new copy
            foreach (var oldPath in moved.Keys)
            {
                try { File.Delete(oldPath); } catch { }
            }
            return modified;
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
                    foreach (var file in Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories))
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
                    DeleteOldCustomIcons(iconsDir, groupId, dest);
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
                DeleteOldCustomIcons(iconsDir, groupId, dest);
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

using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
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
            [PreserveSig]
            int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
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

        public static void HealPinnedTaskbarShortcuts(AppConfig config, string appDataDir)
        {
            try
            {
                string pinnedDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

                if (!Directory.Exists(pinnedDir)) return;

                foreach (var file in Directory.GetFiles(pinnedDir, "*.lnk"))
                {
                    try
                    {
                        var link = (IShellLinkW)new ShellLink();
                        try
                        {
                            ((IPersistFile)link).Load(file, 0);
                            var argsSb = new StringBuilder(1024);
                            link.GetArguments(argsSb, argsSb.Capacity);
                            string args = argsSb.ToString();

                            if (args.IndexOf("--group", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                // Find matching group
                                foreach (var group in config.Groups)
                                {
                                    if (args.IndexOf(group.Id, StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        string iconsDir = Path.Combine(appDataDir, "Icons");
                                        string icoPath = IconHelper.GenerateGroupIcoFile(group, iconsDir);
                                        link.SetPath(AppInfo.ExePath);
                                        link.SetIconLocation(icoPath, 0);
                                        ((IPersistFile)link).Save(file, false);
                                        break;
                                    }
                                }
                            }
                        }
                        finally
                        {
                            Marshal.FinalReleaseComObject(link);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        public static GroupItem ImportAndPersistShortcut(string sourceFile, string appDataDir)
        {
            string linksDir = Path.Combine(appDataDir, "Links");
            Directory.CreateDirectory(linksDir);

            string name = Path.GetFileNameWithoutExtension(sourceFile);
            string ext = Path.GetExtension(sourceFile);

            // If it's a file (.exe, etc) not a shortcut, keep target path directly
            if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                return new GroupItem
                {
                    Name = name,
                    TargetPath = sourceFile,
                    WorkingDirectory = Path.GetDirectoryName(sourceFile) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    Kind = ItemKind.File
                };
            }

            // Copy shortcut to persistent Links folder so unpinning won't destroy it!
            string safeId = Guid.NewGuid().ToString("N").Substring(0, 8);
            string safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars())).Trim();
            string persistentLnkPath = Path.Combine(linksDir, $"{safeId}_{safeName}{ext}");

            try
            {
                File.Copy(sourceFile, persistentLnkPath, true);
            }
            catch
            {
                persistentLnkPath = sourceFile;
            }

            return ParseShortcut(persistentLnkPath);
        }

        public static GroupItem ParseShortcut(string lnkOrFilePath)
        {
            string name = Path.GetFileNameWithoutExtension(lnkOrFilePath);

            if (lnkOrFilePath.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
            {
                return new GroupItem
                {
                    Name = name,
                    TargetPath = lnkOrFilePath,
                    Kind = ItemKind.ShellApp
                };
            }

            if (lnkOrFilePath.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
            {
                string? urlIcon = null;
                try
                {
                    foreach (var line in File.ReadAllLines(lnkOrFilePath))
                    {
                        if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                        {
                            string candidate = Environment.ExpandEnvironmentVariables(line.Substring("IconFile=".Length).Trim());
                            if (File.Exists(candidate))
                                urlIcon = candidate;
                            break;
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
                    Kind = ItemKind.Shortcut
                };
            }

            if (lnkOrFilePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string workDir = string.Empty;
                string customIcon = lnkOrFilePath;
                try
                {
                    var link = (IShellLinkW)new ShellLink();
                    try
                    {
                        ((IPersistFile)link).Load(lnkOrFilePath, 0);
                        var workDirSb = new StringBuilder(260);
                        link.GetWorkingDirectory(workDirSb, workDirSb.Capacity);
                        workDir = workDirSb.ToString();

                        var iconSb = new StringBuilder(260);
                        link.GetIconLocation(iconSb, iconSb.Capacity, out _);
                        string rawIcon = Environment.ExpandEnvironmentVariables(iconSb.ToString());
                        if (File.Exists(rawIcon))
                            customIcon = rawIcon;
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

                string key = $"{item.Name.Trim()}|{Path.GetFileName(item.TargetPath)}";
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

            // 2. shell:AppsFolder via Shell.Application COM (Captures Start menu & Store/UWP apps like Minecraft, Calculator)
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

            // 3. Desktop Shortcuts (Steam games, etc)
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
    }
}

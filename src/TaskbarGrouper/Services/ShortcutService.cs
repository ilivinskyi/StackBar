using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using TaskbarGrouper.Models;

namespace TaskbarGrouper.Services;

public static class ShortcutService
{
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, out IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        uint GetCount(out uint cProps);
        uint GetAt(uint iProp, out PROPERTYKEY pkey);
        uint GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        uint SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        uint Commit();
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

    private static readonly PROPERTYKEY AppUserModelIdKey = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5
    };

    /// <summary>
    /// Creates a Windows .lnk shortcut for a StackBar group with its own custom .ico and unique AppUserModelID
    /// so multiple StackBar folders can be pinned side-by-side on the Windows 11 Taskbar.
    /// </summary>
    public static string CreateGroupShortcut(AppGroup group, string appDataDir)
    {
        string iconsDir = Path.Combine(appDataDir, "Icons");
        string shortcutsDir = Path.Combine(appDataDir, "Shortcuts");
        Directory.CreateDirectory(iconsDir);
        Directory.CreateDirectory(shortcutsDir);

        string icoPath = IconHelper.GenerateGroupIcoFile(group, iconsDir);
        string safeName = string.Join("_", group.Name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrEmpty(safeName)) safeName = group.Id;

        string lnkPath = Path.Combine(shortcutsDir, $"{safeName} (StackBar).lnk");
        string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "StackBar.exe";

        var link = (IShellLinkW)new ShellLink();
        link.SetPath(exePath);
        link.SetArguments($"--group \"{group.Id}\"");
        link.SetWorkingDirectory(Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory);
        link.SetDescription($"Open {group.Name} StackBar Folder");
        link.SetIconLocation(icoPath, 0);

        // Assign distinct AppUserModelID so Windows 11 treats each group shortcut as a separately pinnable taskbar item
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
        return lnkPath;
    }

    /// <summary>
    /// Scans the user's currently pinned Windows Taskbar folder for existing .lnk shortcuts.
    /// </summary>
    public static List<GroupItem> ScanPinnedTaskbarShortcuts()
    {
        var results = new List<GroupItem>();
        try
        {
            string pinnedDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

            if (!Directory.Exists(pinnedDir))
                return results;

            foreach (var file in Directory.GetFiles(pinnedDir, "*.lnk"))
            {
                try
                {
                    if (Path.GetFileName(file).Contains("StackBar", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var item = ParseShortcut(file);
                    if (item != null)
                        results.Add(item);
                }
                catch
                {
                    // Skip unreadable individual shortcuts without crashing
                }
            }
        }
        catch
        {
            // Ignore directory enumeration errors
        }

        return results;
    }

    public static GroupItem? ParseShortcut(string lnkOrFilePath)
    {
        if (!File.Exists(lnkOrFilePath))
            return null;

        string name = Path.GetFileNameWithoutExtension(lnkOrFilePath);

        if (!lnkOrFilePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            string? dir = Path.GetDirectoryName(lnkOrFilePath);
            return new GroupItem
            {
                Name = name,
                TargetPath = lnkOrFilePath,
                WorkingDirectory = !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)
                    ? dir
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
        }

        // For .lnk files, we store the .lnk path itself as TargetPath so Windows Shell launches UWP apps,
        // Steam links, and custom shortcut properties natively, while we read WorkingDirectory via WScript.Shell safely.
        string workingDir = string.Empty;
        string customIcon = lnkOrFilePath;

        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null)
            {
                dynamic? shell = Activator.CreateInstance(shellType);
                if (shell != null)
                {
                    dynamic shortcut = shell.CreateShortcut(lnkOrFilePath);
                    string? wd = shortcut.WorkingDirectory as string;
                    if (!string.IsNullOrWhiteSpace(wd))
                    {
                        workingDir = Environment.ExpandEnvironmentVariables(wd);
                    }

                    string? iconLoc = shortcut.IconLocation as string;
                    if (!string.IsNullOrWhiteSpace(iconLoc))
                    {
                        string rawIcon = Environment.ExpandEnvironmentVariables(iconLoc.Split(',')[0].Trim());
                        if (File.Exists(rawIcon))
                            customIcon = rawIcon;
                    }
                }
            }
        }
        catch
        {
            // Fallback safely to the .lnk file itself
        }

        if (string.IsNullOrWhiteSpace(workingDir) || !Directory.Exists(workingDir))
        {
            workingDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return new GroupItem
        {
            Name = name,
            TargetPath = lnkOrFilePath,
            WorkingDirectory = workingDir,
            CustomIconPath = customIcon
        };
    }
}

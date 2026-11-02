using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using TaskbarGrouper.Models;

namespace TaskbarGrouper.Services
{
    /// <summary>
    /// Turns dropped data into stack items. Explorer and the desktop drop file paths; apps dragged out of
    /// the Start menu arrive only as shell items ("Shell IDList Array"), which are resolved to their
    /// shell:AppsFolder path here.
    /// </summary>
    public static class DropImport
    {
        public const string ShellIdListFormat = "Shell IDList Array";

        private static readonly Regex AppsFolderParsingName = new Regex(
            @"^(?:shell:)?::\{4234D49B-0245-4DF3-B780-3893943456E1\}\\(?<id>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool HasDroppableContent(IDataObject? data)
        {
            if (data == null) return false;
            try
            {
                return data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(ShellIdListFormat);
            }
            catch (COMException)
            {
                return false;
            }
        }

        /// <summary>Paths ready for <see cref="ShortcutService.ImportAndPersistShortcut"/>, in drop order.</summary>
        public static IReadOnlyList<string> GetSourcePaths(IDataObject? data)
        {
            if (data == null) return Array.Empty<string>();
            try
            {
                if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    return files.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
                }

                if (data.GetDataPresent(ShellIdListFormat) && data.GetData(ShellIdListFormat) is MemoryStream stream)
                {
                    return ParseShellIdList(stream.ToArray())
                        .Select(ToItemPath)
                        .Where(p => p != null)
                        .Select(p => p!)
                        .ToList();
                }
            }
            catch (Exception ex) when (ex is COMException || ex is ExternalException || ex is ArgumentException)
            {
                AppLogger.Log("Could not read dropped data: " + ex.Message);
            }
            return Array.Empty<string>();
        }

        /// <summary>
        /// Maps a shell parsing name to something StackBar can store: Start menu apps become
        /// "shell:AppsFolder\&lt;app id&gt;", file system paths stay as they are, other virtual items are dropped.
        /// </summary>
        internal static string? ToItemPath(string parsingName)
        {
            if (string.IsNullOrWhiteSpace(parsingName)) return null;
            var match = AppsFolderParsingName.Match(parsingName.Trim());
            if (match.Success) return @"shell:AppsFolder\" + match.Groups["id"].Value;

            if (parsingName.StartsWith("::", StringComparison.Ordinal) || parsingName.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                return null;
            return Path.IsPathRooted(parsingName) ? parsingName : null;
        }

        /// <summary>
        /// Adds the paths to the group at <paramref name="insertAt"/> (clamped), skipping apps already in it.
        /// Does not save: the caller persists the group once.
        /// </summary>
        /// <returns>The items that were added.</returns>
        public static List<GroupItem> AddToGroup(AppGroup group, IEnumerable<string> paths, int insertAt, string appDataDir, out int skipped)
        {
            var added = new List<GroupItem>();
            skipped = 0;
            int index = Math.Max(0, Math.Min(insertAt, group.Items.Count));
            var duplicates = new ShortcutService.DuplicateIndex(group);
            foreach (var path in paths)
            {
                if (duplicates.Contains(path))
                {
                    skipped++;
                    continue;
                }
                var item = ShortcutService.ImportAndPersistShortcut(path, appDataDir);
                group.Items.Insert(index++, item);
                duplicates.Add(item);
                added.Add(item);
            }
            return added;
        }

        // ---------- CIDA ("Shell IDList Array") ----------

        [DllImport("shell32.dll")]
        private static extern IntPtr ILCombine(IntPtr parent, IntPtr child);

        [DllImport("shell32.dll")]
        private static extern void ILFree(IntPtr pidl);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdn, out IntPtr name);

        private const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;

        /// <summary>
        /// Parsing names of the items in a CIDA: a count, then count + 1 offsets (the parent folder's ID list,
        /// then each item's ID list relative to it).
        /// </summary>
        internal static List<string> ParseShellIdList(byte[] cida)
        {
            var names = new List<string>();
            if (cida.Length < 8) return names;
            int count = BitConverter.ToInt32(cida, 0);
            if (count <= 0 || 4 + (count + 1) * 4 > cida.Length) return names;

            IntPtr buffer = Marshal.AllocHGlobal(cida.Length);
            try
            {
                Marshal.Copy(cida, 0, buffer, cida.Length);
                int parentOffset = BitConverter.ToInt32(cida, 4);
                if (parentOffset < 0 || parentOffset >= cida.Length) return names;
                IntPtr parent = buffer + parentOffset;

                for (int i = 1; i <= count; i++)
                {
                    int childOffset = BitConverter.ToInt32(cida, 4 + i * 4);
                    if (childOffset < 0 || childOffset >= cida.Length) continue;

                    IntPtr absolute = ILCombine(parent, buffer + childOffset);
                    if (absolute == IntPtr.Zero) continue;
                    try
                    {
                        if (SHGetNameFromIDList(absolute, SIGDN_DESKTOPABSOLUTEPARSING, out IntPtr namePtr) == 0 && namePtr != IntPtr.Zero)
                        {
                            try
                            {
                                string? name = Marshal.PtrToStringUni(namePtr);
                                if (!string.IsNullOrEmpty(name)) names.Add(name!);
                            }
                            finally
                            {
                                Marshal.FreeCoTaskMem(namePtr);
                            }
                        }
                    }
                    finally
                    {
                        ILFree(absolute);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return names;
        }
    }
}

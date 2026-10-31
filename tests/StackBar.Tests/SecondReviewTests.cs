using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using TaskbarGrouper.Models;
using TaskbarGrouper.Services;
using Xunit;

namespace StackBar.Tests
{
    /// <summary>Issues found in the review of the bug-fix commit (numbered as in that review).</summary>
    [Collection("ShortcutRepair")]
    public class SecondReviewTests : IDisposable
    {
        private readonly TempDirectory _dir = new TempDirectory();

        public SecondReviewTests()
        {
            ShortcutRepairService.RegisteredShortcutFolders = () => Enumerable.Empty<string>();
            ShortcutRepairService.ResetState();
        }

        public void Dispose()
        {
            ShortcutRepairService.ResetState();
            _dir.Dispose();
        }

        [Fact]
        public void Review2_01_portable_instance_id_travels_with_the_data_folder()
        {
            string oldData = _dir.File(@"E\StackBar-Portable\data");
            string id = ShortcutService.PortableInstanceId(oldData);

            // Same folder contents under a new drive letter / location
            string newData = _dir.File(@"F\Moved\data");
            Directory.CreateDirectory(newData);
            File.Copy(Path.Combine(oldData, "instance.id"), Path.Combine(newData, "instance.id"));

            Assert.Equal(id, ShortcutService.PortableInstanceId(newData));
            Assert.Equal(8, id.Length);
            Assert.NotEqual(id, ShortcutService.PortableInstanceId(_dir.File(@"G\Other\data")));
        }

        [Fact]
        public void Review2_03_loading_does_not_check_paths_that_are_not_StackBar_data_files()
        {
            // An unreachable network path must not be probed at startup; it simply stays as it is
            File.WriteAllText(_dir.File("groups.json"),
                "{\"groups\":[{\"id\":\"g\",\"items\":[{\"name\":\"Tool\",\"targetPath\":\"\\\\\\\\offline-server\\\\share\\\\tool.exe\"}]}]}");

            var loaded = new ConfigService(_dir.Path).LoadOrCreateDefault();

            Assert.Equal(@"\\offline-server\share\tool.exe", loaded.Groups[0].Items[0].TargetPath);
        }

        [Fact]
        public void Review2_05_apps_folder_ids_with_a_known_folder_prefix_match_the_real_exe()
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string expanded = ShortcutService.ExpandKnownFolderPath("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\cmd.exe")!;

            Assert.Equal(Path.Combine(system, "cmd.exe"), expanded, ignoreCase: true);
            Assert.Contains("exe:" + expanded + "|", ShortcutService.LaunchIdentities("shell:AppsFolder\\{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\cmd.exe"));
            Assert.Null(ShortcutService.ExpandKnownFolderPath("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App"));
        }

        [Fact]
        public void Review2_05_duplicate_index_sees_items_added_earlier_in_the_same_batch()
        {
            string exe = _dir.File("app.exe");
            File.WriteAllText(exe, "x");
            var group = new AppGroup();
            var index = new ShortcutService.DuplicateIndex(group);

            Assert.False(index.Contains(exe));
            var added = new GroupItem { Name = "App", TargetPath = exe };
            group.Items.Add(added);
            index.Add(added);

            Assert.True(index.Contains(exe));
        }

        [Fact]
        public void Review2_06_icon_written_moments_ago_is_not_deleted_by_another_regeneration()
        {
            string icons = _dir.File("Icons");
            var group = new AppGroup { Id = "g1", IconMode = FolderIconMode.EmojiGlyph, IconGlyph = "A" };
            string first = IconHelper.GenerateGroupIcoFile(group, icons);

            group.IconGlyph = "B";
            string second = IconHelper.GenerateGroupIcoFile(group, icons);

            Assert.NotEqual(first, second);
            Assert.True(File.Exists(first));   // too new to delete: someone may have just pointed a pin at it
            Assert.True(File.Exists(second));

            File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddMinutes(-5));
            group.IconGlyph = "C";
            string third = IconHelper.GenerateGroupIcoFile(group, icons);
            Assert.False(File.Exists(first));  // old ones are cleaned up later
            Assert.True(File.Exists(third));
        }

        [Fact]
        public void LegacyImport_copies_stacks_and_files_and_repoints_paths()
        {
            string legacy = _dir.File(@"install\data");
            string link = Path.Combine(legacy, "Links", "ab12cd34", "App.lnk");
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            File.WriteAllText(link, "x");
            File.WriteAllText(Path.Combine(legacy, "groups.json"),
                "{\"groups\":[{\"id\":\"g\",\"name\":\"Old\",\"items\":[{\"name\":\"App\",\"targetPath\":\"" +
                link.Replace("\\", "\\\\") + "\"}]}]}");
            string appData = _dir.File("AppData");

            var imported = new ConfigService(appData).TryImportFrom(legacy);

            Assert.NotNull(imported);
            string expected = Path.Combine(appData, "Links", "ab12cd34", "App.lnk");
            Assert.Equal(expected, imported!.Groups.Single().Items.Single().TargetPath);
            Assert.True(File.Exists(expected));
            Assert.True(File.Exists(link)); // the old folder is left untouched
            Assert.Equal("Old", new ConfigService(appData).LoadOrCreateDefault().Groups.Single().Name);
        }

        // ---------- B-28: refreshing must not change special shortcuts ----------

        private string CreateTarget(string name, int size)
        {
            string path = _dir.File(name);
            File.WriteAllBytes(path, new byte[size]);
            return path;
        }

        private static void UpdateInPlace(string path, int newSize)
        {
            Thread.Sleep(50);
            File.WriteAllBytes(path, new byte[newSize]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        }

        [Fact]
        public void B28_refresh_keeps_icon_working_dir_show_command_hotkey_and_run_as_admin()
        {
            string target = CreateTarget("app.exe", 1000);
            string icon = CreateTarget("icons.dll", 10);
            string lnk = _dir.File("App.lnk");
            var link = (ShortcutService.IShellLinkW)new ShortcutService.ShellLink();
            try
            {
                link.SetPath(target);
                link.SetIconLocation(icon, 3);
                link.SetWorkingDirectory(_dir.Path);
                link.SetShowCmd(7);      // SW_SHOWMINNOACTIVE
                link.SetHotkey(0x0641);  // Ctrl+A
                var data = (ShortcutRepairService.IShellLinkDataList)link;
                data.GetFlags(out uint flags);
                data.SetFlags(flags | 0x2000); // SLDF_RUNAS_USER: "Run as administrator"
                ((IPersistFile)link).Save(lnk, true);
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
            UpdateInPlace(target, 2500);

            Assert.True(ShortcutRepairService.Refresh(lnk));

            var after = (ShortcutService.IShellLinkW)new ShortcutService.ShellLink();
            try
            {
                ((IPersistFile)after).Load(lnk, 0);
                var iconPath = new StringBuilder(1024);
                after.GetIconLocation(iconPath, iconPath.Capacity, out int iconIndex);
                var workDir = new StringBuilder(1024);
                after.GetWorkingDirectory(workDir, workDir.Capacity);
                after.GetShowCmd(out int showCmd);
                after.GetHotkey(out short hotkey);
                ((ShortcutRepairService.IShellLinkDataList)after).GetFlags(out uint flagsAfter);

                Assert.Equal(icon, iconPath.ToString(), ignoreCase: true);
                Assert.Equal(3, iconIndex);
                Assert.Equal(_dir.Path, workDir.ToString(), ignoreCase: true);
                Assert.Equal(7, showCmd);
                Assert.Equal(0x0641, hotkey);
                Assert.NotEqual(0u, flagsAfter & 0x2000);
            }
            finally
            {
                Marshal.FinalReleaseComObject(after);
            }
            Assert.False(ShortcutRepairService.IsStale(lnk));
        }

        [Fact]
        public void B28_environment_variable_target_is_kept_in_variable_form()
        {
            string target = CreateTarget("envapp.exe", 1000);
            string folder = Path.GetDirectoryName(target)!;
            Environment.SetEnvironmentVariable("STACKBAR_TEST_DIR", folder);
            try
            {
                string lnk = _dir.File("Env.lnk");
                var link = (ShortcutService.IShellLinkW)new ShortcutService.ShellLink();
                try
                {
                    link.SetPath(@"%STACKBAR_TEST_DIR%\envapp.exe");
                    ((IPersistFile)link).Save(lnk, true);
                }
                finally
                {
                    Marshal.FinalReleaseComObject(link);
                }
                UpdateInPlace(target, 2500);

                Assert.True(ShortcutRepairService.Refresh(lnk));

                var after = (ShortcutService.IShellLinkW)new ShortcutService.ShellLink();
                try
                {
                    ((IPersistFile)after).Load(lnk, 0);
                    var raw = new StringBuilder(1024);
                    after.GetPath(raw, raw.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */);
                    Assert.StartsWith("%STACKBAR_TEST_DIR%", raw.ToString(), StringComparison.OrdinalIgnoreCase);
                }
                finally
                {
                    Marshal.FinalReleaseComObject(after);
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("STACKBAR_TEST_DIR", null);
            }
        }
    }
}

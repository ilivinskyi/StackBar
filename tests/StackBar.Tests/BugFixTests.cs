using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using TaskbarGrouper.Models;
using TaskbarGrouper.Services;
using Xunit;
using Edge = TaskbarGrouper.Services.NativePositioningService.TaskbarEdge;
using Pin = TaskbarGrouper.Services.ShortcutService.PinOwnership;

namespace StackBar.Tests
{
    public class InstallLocationTests
    {
        [Fact]
        public void B01_a_non_empty_chosen_folder_gets_a_StackBar_subfolder()
        {
            using (var dir = new TempDirectory())
            {
                File.WriteAllText(dir.File("other-program.exe"), "x");
                Assert.Equal(Path.Combine(dir.Path, "StackBar"), InstallLocation.ResolveChosenFolder(dir.Path));
            }
        }

        [Fact]
        public void B01_an_empty_or_StackBar_named_folder_is_used_as_is()
        {
            using (var dir = new TempDirectory())
            {
                string empty = dir.File("Empty");
                Directory.CreateDirectory(empty);
                string named = dir.File("StackBar");
                Directory.CreateDirectory(named);
                File.WriteAllText(Path.Combine(named, "StackBar.exe"), "x");

                Assert.Equal(empty, InstallLocation.ResolveChosenFolder(empty));
                Assert.Equal(named, InstallLocation.ResolveChosenFolder(named));
            }
        }

        [Fact]
        public void B01_drive_roots_and_user_folders_are_never_used_directly_or_cleaned()
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            Assert.EndsWith("\\StackBar", InstallLocation.ResolveChosenFolder(@"C:\"));
            Assert.EndsWith("\\StackBar", InstallLocation.ResolveChosenFolder(desktop));
            Assert.False(InstallLocation.IsSafeToClean(@"C:\", out _));
            Assert.False(InstallLocation.IsSafeToClean(desktop, out _));
        }

        [Fact]
        public void B01_only_a_folder_containing_StackBar_exe_may_be_cleaned()
        {
            using (var dir = new TempDirectory())
            {
                Assert.False(InstallLocation.IsSafeToClean(dir.Path, out _));
                File.WriteAllText(dir.File("StackBar.exe"), "x");
                Assert.True(InstallLocation.IsSafeToClean(dir.Path, out _));
            }
        }

        [Fact]
        public void B01_cleanup_deletes_only_StackBar_exe_and_an_empty_folder_never_recursively()
        {
            string script = InstallLocation.BuildCleanupScript(@"D:\Apps\StackBar", 42);

            Assert.Contains(@"Remove-Item -LiteralPath 'D:\Apps\StackBar\StackBar.exe'", script);
            Assert.Contains("Get-ChildItem", script); // folder removed only when nothing is left
            Assert.DoesNotContain("-Recurse", script);
        }

        [Fact]
        public void B18_only_processes_running_from_the_install_folder_are_stopped()
        {
            Assert.True(InstallLocation.IsInFolder(@"C:\Users\me\AppData\Local\Programs\StackBar\StackBar.exe", @"C:\Users\me\AppData\Local\Programs\StackBar"));
            Assert.False(InstallLocation.IsInFolder(@"D:\dev\bin\Debug\StackBar.exe", @"C:\Users\me\AppData\Local\Programs\StackBar"));
            Assert.False(InstallLocation.IsInFolder(@"C:\Programs\StackBarOld\StackBar.exe", @"C:\Programs\StackBar"));
        }
    }

    public class PortableDetectionTests
    {
        [Fact]
        public void B05_a_data_folder_alone_does_not_mean_portable()
        {
            using (var dir = new TempDirectory())
            {
                Directory.CreateDirectory(dir.File("data"));
                Assert.False(AppInfo.DetectPortable(dir.Path, dir.File("StackBar.exe")));
            }
        }

        [Fact]
        public void B05_marker_file_or_exact_exe_name_means_portable()
        {
            using (var dir = new TempDirectory())
            {
                Assert.True(AppInfo.DetectPortable(dir.Path, dir.File("StackBar-Portable.exe")));
                Assert.False(AppInfo.DetectPortable(dir.Path, dir.File("NotPortableAtAll-Portable2.exe")));
                File.WriteAllText(dir.File(".portable"), "x");
                Assert.True(AppInfo.DetectPortable(dir.Path, dir.File("StackBar.exe")));
            }
        }
    }

    public class PinOwnershipTests
    {
        private const string Ours = @"C:\Programs\StackBar\StackBar.exe";
        private const string Prefix = "StackBar.Group.";

        [Fact]
        public void B02_pin_pointing_at_this_exe_with_matching_app_id_is_ours()
        {
            Assert.Equal(Pin.Ours, ShortcutService.ClassifyPin("--group \"web\"", Ours, "StackBar.Group.web", Ours, Prefix, out var id));
            Assert.Equal("web", id);
        }

        [Fact]
        public void B02_pin_of_another_existing_StackBar_copy_is_left_alone()
        {
            using (var dir = new TempDirectory())
            {
                string otherCopy = dir.File("StackBar.exe");
                File.WriteAllText(otherCopy, "x");
                Assert.Equal(Pin.NotOurs, ShortcutService.ClassifyPin("--group \"web\"", otherCopy, "StackBar.Group.web", Ours, Prefix, out _));
            }
        }

        [Fact]
        public void B02_pin_whose_StackBar_exe_is_gone_is_orphaned_and_can_be_healed()
        {
            Assert.Equal(Pin.Orphaned, ShortcutService.ClassifyPin("--group \"web\"", @"C:\Old\Gone\StackBar.exe", "StackBar.Group.web", Ours, Prefix, out _));
        }

        [Fact]
        public void B02_third_party_pins_and_other_editions_are_never_ours()
        {
            Assert.Equal(Pin.NotOurs, ShortcutService.ClassifyPin("--group web", @"C:\Tools\chat.exe", null, Ours, Prefix, out _));
            Assert.Equal(Pin.NotOurs, ShortcutService.ClassifyPin("--group \"web\"", @"C:\Old\Gone\StackBar.exe", "StackBar.Portable.ab12cd34.Group.web", Ours, Prefix, out _));
            Assert.Equal(Pin.NotOurs, ShortcutService.ClassifyPin("--group \"web\"", @"C:\Old\Gone\StackBar.exe", null, Ours, Prefix, out _));
        }

        [Fact]
        public void B02_portable_copy_keeps_managing_its_untagged_legacy_pins()
        {
            const string portablePrefix = "StackBar.Portable.ab12cd34.Group.";
            Assert.Equal(Pin.Ours, ShortcutService.ClassifyPin("--group \"web\"", Ours, "StackBar.Group.web", Ours, portablePrefix, out _));
            Assert.Equal(Pin.NotOurs, ShortcutService.ClassifyPin("--group \"web\"", @"C:\Old\Gone\StackBar.exe", "StackBar.Group.web", Ours, portablePrefix, out _));
        }

        [Fact]
        public void B02_group_flag_must_be_a_whole_argument()
        {
            Assert.False(ShortcutService.TryGetGroupIdFromArgs("--groups web", out _));
            Assert.False(ShortcutService.TryGetGroupIdFromArgs("--no--group web", out _));
            Assert.True(ShortcutService.TryGetGroupIdFromArgs("--x 1 --group web", out var id));
            Assert.Equal("web", id);
        }
    }

    public class ConfigPersistenceTests
    {
        public ConfigPersistenceTests()
        {
            AppLogger.Initialize(Path.Combine(Path.GetTempPath(), "StackBarTests_logs"));
        }

        [Fact]
        public void B03_paths_inside_the_data_folder_survive_moving_the_folder()
        {
            using (var oldRoot = new TempDirectory())
            using (var newRoot = new TempDirectory())
            {
                string link = Path.Combine(oldRoot.Path, "Links", "ab12cd34", "App.lnk");
                Directory.CreateDirectory(Path.GetDirectoryName(link)!);
                File.WriteAllText(link, "x");
                var config = new AppConfig
                {
                    Groups = new List<AppGroup> { new AppGroup { Id = "g", Items = new List<GroupItem> { new GroupItem { Name = "App", TargetPath = link, CustomIconPath = link } } } }
                };
                Assert.True(new ConfigService(oldRoot.Path).Save(config));
                Assert.Equal(link, config.Groups[0].Items[0].TargetPath); // live config is not left rewritten

                // "Move" the portable folder: same files, new location
                CopyDirectory(oldRoot.Path, newRoot.Path);
                var loaded = new ConfigService(newRoot.Path).LoadOrCreateDefault();

                string expected = Path.Combine(newRoot.Path, "Links", "ab12cd34", "App.lnk");
                Assert.Equal(expected, loaded.Groups[0].Items[0].TargetPath);
                Assert.Equal(expected, loaded.Groups[0].Items[0].CustomIconPath);
            }
        }

        [Fact]
        public void B03_absolute_paths_from_older_versions_are_rebased_when_their_file_moved()
        {
            using (var dir = new TempDirectory())
            {
                string link = Path.Combine(dir.Path, "Links", "ab12cd34", "App.lnk");
                Directory.CreateDirectory(Path.GetDirectoryName(link)!);
                File.WriteAllText(link, "x");
                File.WriteAllText(dir.File("groups.json"),
                    "{\"groups\":[{\"id\":\"g\",\"items\":[{\"name\":\"App\",\"targetPath\":\"E:\\\\StackBar-Portable\\\\data\\\\Links\\\\ab12cd34\\\\App.lnk\"}]}]}");

                var loaded = new ConfigService(dir.Path).LoadOrCreateDefault();

                Assert.Equal(link, loaded.Groups[0].Items[0].TargetPath);
            }
        }

        [Fact]
        public void B06_save_reports_failure_instead_of_pretending_it_worked()
        {
            using (var dir = new TempDirectory())
            {
                var service = new ConfigService(dir.Path);
                Directory.CreateDirectory(dir.File("groups.json.tmp")); // the temp file can't be written

                Assert.False(service.Save(new AppConfig()));
                Assert.False(string.IsNullOrEmpty(service.LastError));
            }
        }

        [Fact]
        public void B07_new_configs_skip_the_one_time_legacy_migration_and_old_ones_run_it()
        {
            using (var dir = new TempDirectory())
            {
                Assert.Equal(AppConfig.CurrentMigrationVersion, new ConfigService(dir.Path).LoadOrCreateDefault().MigrationVersion);
            }
            using (var dir = new TempDirectory())
            {
                File.WriteAllText(dir.File("groups.json"), "{\"groups\":[]}");
                Assert.Equal(0, new ConfigService(dir.Path).LoadOrCreateDefault().MigrationVersion);
            }
        }

        [Fact]
        public void B07_loading_never_resolves_store_app_names()
        {
            var config = new AppConfig
            {
                Groups = new List<AppGroup> { new AppGroup { Items = new List<GroupItem> { new GroupItem { Name = "com.lencx.chatgpt", TargetPath = "shell:AppsFolder\\com.lencx.chatgpt", Kind = ItemKind.ShellApp } } } }
            };

            Assert.False(ConfigService.SanitizeConfig(config));
            Assert.Equal("com.lencx.chatgpt", config.Groups[0].Items[0].Name);
            Assert.True(ConfigService.NeedsFriendlyName(config.Groups[0].Items[0]));
        }

        [Fact]
        public void B09_sidebar_count_is_refreshed_with_the_appearance()
        {
            var group = new AppGroup();
            var raised = new List<string>();
            group.PropertyChanged += (s, e) => raised.Add(e.PropertyName!);

            group.NotifyAppearanceChanged();

            Assert.Contains(nameof(AppGroup.Items), raised);
        }

        private static void CopyDirectory(string from, string to)
        {
            foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(from, to));
            foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
                File.Copy(file, file.Replace(from, to), true);
        }
    }

    public class LaunchAndPlacementTests
    {
        private static string? NoLink(string _) => null;

        [Fact]
        public void B15_run_as_administrator_only_for_programs_and_scripts()
        {
            Assert.True(LaunchRules.CanRunAsAdmin(new GroupItem { TargetPath = @"C:\a\app.exe" }, NoLink));
            Assert.True(LaunchRules.CanRunAsAdmin(new GroupItem { TargetPath = @"C:\a\run.cmd" }, NoLink));
            Assert.False(LaunchRules.CanRunAsAdmin(new GroupItem { TargetPath = @"C:\a\game.url" }, NoLink));
            Assert.False(LaunchRules.CanRunAsAdmin(new GroupItem { TargetPath = @"C:\a\notes.txt" }, NoLink));
            Assert.True(LaunchRules.CanRunAsAdmin(new GroupItem { TargetPath = @"C:\a\App.lnk" }, _ => @"C:\a\app.exe"));
            Assert.False(LaunchRules.CanRunAsAdmin(new GroupItem { TargetPath = @"C:\a\Doc.lnk" }, _ => @"C:\a\doc.pdf"));
            Assert.False(LaunchRules.CanRunAsAdmin(new GroupItem { TargetPath = "shell:AppsFolder\\x", Kind = ItemKind.ShellApp }, NoLink));
        }

        [Fact]
        public void B04_open_file_location_selects_the_app_not_StackBars_copy()
        {
            using (var dir = new TempDirectory())
            {
                string exe = dir.File("app.exe");
                File.WriteAllText(exe, "x");
                string appData = dir.File("appdata");
                string ownLnk = Path.Combine(appData, "Links", "ab12cd34", "App.lnk");

                Assert.Equal(exe, LaunchRules.FileLocationTarget(new GroupItem { TargetPath = ownLnk }, appData, _ => exe));
                Assert.Null(LaunchRules.FileLocationTarget(new GroupItem { TargetPath = ownLnk }, appData, NoLink));
                Assert.Null(LaunchRules.FileLocationTarget(new GroupItem { TargetPath = "shell:AppsFolder\\x", Kind = ItemKind.ShellApp }, appData, NoLink));
            }
        }

        [Fact]
        public void B04_copied_internet_shortcut_points_at_its_original_file()
        {
            using (var dir = new TempDirectory())
            {
                string appData = dir.File("appdata");
                string ownUrl = Path.Combine(appData, "Links", "ab12cd34", "Game.url");
                Directory.CreateDirectory(Path.GetDirectoryName(ownUrl)!);
                File.WriteAllText(ownUrl, "x");
                string original = dir.File("Game.url");
                File.WriteAllText(original, "x");

                Assert.Equal(original, LaunchRules.FileLocationTarget(new GroupItem { TargetPath = ownUrl, SourcePath = original }, appData, NoLink));
                Assert.Null(LaunchRules.FileLocationTarget(new GroupItem { TargetPath = ownUrl, SourcePath = dir.File("gone.url") }, appData, NoLink));
            }
        }

        [Fact]
        public void B16_startup_entry_for_another_exe_is_reported_as_stale()
        {
            const string exe = @"C:\Programs\StackBar\StackBar.exe";
            Assert.Equal(StartupService.StartupState.Off, StartupService.ClassifyCommand(null, exe));
            Assert.Equal(StartupService.StartupState.On, StartupService.ClassifyCommand($"\"{exe}\" --tray", exe));
            Assert.Equal(StartupService.StartupState.Stale, StartupService.ClassifyCommand("\"D:\\Old\\StackBar.exe\" --tray", exe));
        }

        [Fact]
        public void B11_a_close_caused_by_the_same_slow_pin_click_counts_as_toggle()
        {
            var now = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
            var clientStart = now.AddSeconds(-2);  // process took 2 s to send OPEN
            var closedByClick = clientStart.AddMilliseconds(-150);

            Assert.True(TaskbarGrouper.App.IsToggleClose(closedByClick, clientStart, now));
            // A flyout closed long before this click started is a fresh open
            Assert.False(TaskbarGrouper.App.IsToggleClose(clientStart.AddSeconds(-10), clientStart, now));
            // Older clients without a start time keep the 500 ms rule
            Assert.True(TaskbarGrouper.App.IsToggleClose(now.AddMilliseconds(-200), null, now));
            Assert.False(TaskbarGrouper.App.IsToggleClose(now.AddSeconds(-2), null, now));
        }

        [Fact]
        public void B17_edge_comes_from_the_taskbar_rectangle_even_when_auto_hidden()
        {
            var monitor = new Rectangle(0, 0, 1920, 1080);
            Assert.Equal(Edge.Bottom, NativePositioningService.ClassifyEdge(new Rectangle(0, 1078, 1920, 48), monitor)); // tucked away
            Assert.Equal(Edge.Top, NativePositioningService.ClassifyEdge(new Rectangle(0, -46, 1920, 48), monitor));
            Assert.Equal(Edge.Left, NativePositioningService.ClassifyEdge(new Rectangle(-60, 0, 62, 1080), monitor));
            Assert.Equal(Edge.Right, NativePositioningService.ClassifyEdge(new Rectangle(1918, 0, 62, 1080), monitor));
        }

        [Fact]
        public void B17_auto_hidden_taskbar_space_is_reserved()
        {
            var monitor = new Rectangle(0, 0, 1920, 1080);
            var usable = NativePositioningService.UsableArea(monitor, monitor, new Rectangle(0, 1032, 1920, 48), Edge.Bottom);
            Assert.Equal(1032, usable.Bottom);

            var normalWorkArea = new Rectangle(0, 0, 1920, 1032);
            Assert.Equal(normalWorkArea, NativePositioningService.UsableArea(normalWorkArea, monitor, new Rectangle(0, 1032, 1920, 48), Edge.Bottom));
        }
    }

    public class DuplicateImportTests
    {
        private static string CreateLink(string path, string target)
        {
            var link = (ShortcutService.IShellLinkW)new ShortcutService.ShellLink();
            try
            {
                link.SetPath(target);
                ((IPersistFile)link).Save(path, true);
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
            return path;
        }

        [Fact]
        public void B21_same_app_from_another_source_is_detected()
        {
            using (var dir = new TempDirectory())
            {
                string exe = dir.File("app.exe");
                File.WriteAllText(exe, "x");
                string ownCopy = CreateLink(dir.File("copy.lnk"), exe);
                string startMenuLink = CreateLink(dir.File("StartMenu.lnk"), exe);
                var group = new AppGroup { Items = new List<GroupItem> { new GroupItem { Name = "App", TargetPath = ownCopy, SourcePath = dir.File("Pinned.lnk") } } };

                Assert.True(ShortcutService.GroupContainsSource(group, startMenuLink, "Application"));
                Assert.True(ShortcutService.GroupContainsSource(group, exe, "Application"));
                Assert.False(ShortcutService.GroupContainsSource(group, CreateLink(dir.File("Other.lnk"), dir.File("other.exe")), "Other"));
            }
        }

        [Fact]
        public void B21_apps_folder_entry_matches_a_shortcut_with_the_same_app_id_or_exe()
        {
            Assert.Contains("app:Vendor.App", ShortcutService.LaunchIdentities("shell:AppsFolder\\Vendor.App"));
            Assert.Contains(@"exe:C:\a\app.exe|", ShortcutService.LaunchIdentities("shell:AppsFolder\\C:\\a\\app.exe"));
        }
    }
}

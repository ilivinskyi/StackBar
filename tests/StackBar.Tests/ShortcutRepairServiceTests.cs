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
    // Shortcut repair touches process-wide state; keep these tests from running in parallel with each other
    [Collection("ShortcutRepair")]
    public class ShortcutRepairServiceTests : IDisposable
    {
        private readonly TempDirectory _dir = new TempDirectory();

        public ShortcutRepairServiceTests()
        {
            AppLogger.Initialize(Path.Combine(Path.GetTempPath(), "StackBarTests_logs"));
            // Never scan (and possibly modify) the developer's real Start Menu: every test starts with no folders
            ShortcutRepairService.RegisteredShortcutFolders = () => Enumerable.Empty<string>();
            ShortcutRepairService.ResetState();
        }

        public void Dispose()
        {
            ShortcutRepairService.RegisteredShortcutFolders = () => Enumerable.Empty<string>();
            ShortcutRepairService.ResetState();
            _dir.Dispose();
        }

        private string AppData => _dir.File("appdata");

        private string CreateTarget(string name, int size)
        {
            string path = _dir.File(name);
            File.WriteAllBytes(path, new byte[size]);
            return path;
        }

        /// <summary>Simulates an app updating itself in place: same path, different size and timestamp.</summary>
        private static void UpdateInPlace(string path, int newSize)
        {
            Thread.Sleep(50);
            File.WriteAllBytes(path, new byte[newSize]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        }

        private string CreateLink(string relativePath, string target, string? aumid = null, string? arguments = null, string? description = null)
        {
            string lnk = _dir.File(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
            var link = (ShortcutService.IShellLinkW)new ShortcutService.ShellLink();
            try
            {
                link.SetPath(target);
                if (arguments != null) link.SetArguments(arguments);
                if (description != null) link.SetDescription(description);
                link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
                if (aumid != null)
                {
                    var store = (ShortcutService.IPropertyStore)link;
                    var key = ShortcutService.AppUserModelIdKey;
                    var value = new ShortcutService.PROPVARIANT { vt = 31, pwszVal = Marshal.StringToCoTaskMemUni(aumid) };
                    store.SetValue(ref key, ref value);
                    store.Commit();
                    Marshal.FreeCoTaskMem(value.pwszVal);
                }
                ((IPersistFile)link).Save(lnk, true);
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
            return lnk;
        }

        private static (string Arguments, string Description, string? Aumid) ReadLink(string lnk)
        {
            var link = (ShortcutService.IShellLinkW)new ShortcutService.ShellLink();
            try
            {
                ((IPersistFile)link).Load(lnk, 0);
                var args = new StringBuilder(1024);
                link.GetArguments(args, args.Capacity);
                var desc = new StringBuilder(1024);
                link.GetDescription(desc, desc.Capacity);
                return (args.ToString(), desc.ToString(), ShortcutService.ReadAppUserModelId(link));
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }

        private void UseStartMenu(string folder)
        {
            ShortcutRepairService.RegisteredShortcutFolders = () => new[] { folder };
            // Build the index up front: the launch path itself never scans
            ShortcutRepairService.FindRegisteredShortcuts("warm-up", null).ToList();
        }

        [Fact]
        public void Fresh_shortcut_is_not_stale()
        {
            string target = CreateTarget("app.exe", 1000);
            string lnk = CreateLink("App.lnk", target);

            Assert.False(ShortcutRepairService.IsStale(lnk));
            Assert.Equal(ShortcutRepairService.RepairOutcome.NotNeeded, ShortcutRepairService.RepairIfStale(lnk, _dir.File("backups")));
        }

        [Fact]
        public void Shortcut_becomes_stale_when_target_is_updated_in_place()
        {
            string target = CreateTarget("app.exe", 1000);
            string lnk = CreateLink("App.lnk", target);

            UpdateInPlace(target, 2500);

            Assert.True(ShortcutRepairService.IsStale(lnk));
        }

        [Fact]
        public void Refresh_records_the_current_target_and_keeps_all_other_properties()
        {
            string target = CreateTarget("app.exe", 1000);
            string lnk = CreateLink("App.lnk", target, aumid: "Vendor.App", arguments: "--flag", description: "My App");
            UpdateInPlace(target, 2500);

            Assert.True(ShortcutRepairService.Refresh(lnk));

            Assert.False(ShortcutRepairService.IsStale(lnk));
            var after = ReadLink(lnk);
            Assert.Equal("--flag", after.Arguments);
            Assert.Equal("My App", after.Description);
            Assert.Equal("Vendor.App", after.Aumid);
        }

        [Fact]
        public void RepairIfStale_backs_up_the_original_before_changing_it()
        {
            string target = CreateTarget("app.exe", 1000);
            string lnk = CreateLink("App.lnk", target);
            byte[] original = File.ReadAllBytes(lnk);
            UpdateInPlace(target, 2500);
            string backups = _dir.File("backups");

            Assert.Equal(ShortcutRepairService.RepairOutcome.Repaired, ShortcutRepairService.RepairIfStale(lnk, backups));

            string backup = Assert.Single(Directory.GetFiles(backups, "*.lnk"));
            Assert.EndsWith("_App.lnk", backup);
            Assert.Equal(original, File.ReadAllBytes(backup));
            Assert.Contains(Path.GetFullPath(lnk), File.ReadAllText(Path.Combine(backups, "backups.tsv")));
            Assert.False(ShortcutRepairService.IsStale(lnk));
        }

        [Fact]
        public void Backups_of_same_named_shortcuts_from_different_folders_never_overwrite_each_other()
        {
            string target = CreateTarget("app.exe", 1000);
            string startMenu = CreateLink(Path.Combine("StartMenu", "App.lnk"), target);
            string pinned = CreateLink(Path.Combine("Pinned", "App.lnk"), target);
            byte[] startMenuOriginal = File.ReadAllBytes(startMenu);
            byte[] pinnedOriginal = File.ReadAllBytes(pinned);
            UpdateInPlace(target, 2500);
            string backups = _dir.File("backups");

            ShortcutRepairService.RepairIfStale(startMenu, backups);
            ShortcutRepairService.RepairIfStale(pinned, backups);

            var saved = Directory.GetFiles(backups, "*.lnk").Select(File.ReadAllBytes).ToList();
            Assert.Equal(2, saved.Count);
            Assert.Contains(saved, b => b.SequenceEqual(startMenuOriginal));
            Assert.Contains(saved, b => b.SequenceEqual(pinnedOriginal));
        }

        [Fact]
        public void Read_only_shortcuts_are_skipped_without_a_backup_and_not_retried()
        {
            string target = CreateTarget("app.exe", 1000);
            string lnk = CreateLink("App.lnk", target);
            UpdateInPlace(target, 2500);
            File.SetAttributes(lnk, FileAttributes.ReadOnly);
            string backups = _dir.File("backups");
            try
            {
                Assert.Equal(ShortcutRepairService.RepairOutcome.Skipped, ShortcutRepairService.RepairIfStale(lnk, backups));
                Assert.Equal(ShortcutRepairService.RepairOutcome.Skipped, ShortcutRepairService.RepairIfStale(lnk, backups));
                Assert.False(Directory.Exists(backups) && Directory.GetFiles(backups, "*.lnk").Any());
            }
            finally
            {
                File.SetAttributes(lnk, FileAttributes.Normal);
            }
        }

        [Fact]
        public void PruneBackups_keeps_the_folder_bounded()
        {
            string backups = _dir.File("backups");
            Directory.CreateDirectory(backups);
            string old = Path.Combine(backups, "old.lnk");
            File.WriteAllText(old, "x");
            File.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-40));
            string recent = Path.Combine(backups, "recent.lnk");
            File.WriteAllText(recent, "x");

            ShortcutRepairService.PruneBackups(backups, DateTime.UtcNow);

            Assert.False(File.Exists(old));
            Assert.True(File.Exists(recent));
        }

        [Fact]
        public void Shortcuts_to_missing_targets_or_folders_are_never_touched()
        {
            string target = CreateTarget("gone.exe", 1000);
            string missing = CreateLink("Missing.lnk", target);
            File.Delete(target);
            string folderLink = CreateLink("Folder.lnk", _dir.Path);

            Assert.False(ShortcutRepairService.IsStale(missing));
            Assert.False(ShortcutRepairService.IsStale(folderLink));
            Assert.False(ShortcutRepairService.IsStale(_dir.File("not-a-link.txt")));
        }

        [Fact]
        public void Only_files_under_the_data_Links_folder_count_as_own_copies()
        {
            Assert.True(ShortcutRepairService.IsOwnCopy(Path.Combine(AppData, "Links", "ab12cd34", "App.lnk"), AppData));
            Assert.False(ShortcutRepairService.IsOwnCopy(_dir.File(@"Desktop\App.lnk"), AppData));
            Assert.False(ShortcutRepairService.IsOwnCopy(Path.Combine(AppData, "LinksOther", "App.lnk"), AppData));
        }

        [Fact]
        public void PrepareLaunch_repairs_the_apps_registered_shortcut_found_by_app_id()
        {
            string target = CreateTarget("app.exe", 1000);
            string registered = CreateLink(Path.Combine("StartMenu", "App.lnk"), target, aumid: "Vendor.App");
            string ownCopy = CreateLink(Path.Combine("appdata", "Links", "ab12cd34", "App.lnk"), target, aumid: "Vendor.App");
            UpdateInPlace(target, 2500);
            UseStartMenu(_dir.File("StartMenu"));

            ShortcutRepairService.PrepareLaunch(new GroupItem { TargetPath = ownCopy, Kind = ItemKind.Shortcut }, AppData, repairAppShortcuts: true);

            Assert.False(ShortcutRepairService.IsStale(ownCopy));
            Assert.False(ShortcutRepairService.IsStale(registered));
            // Only the other app's file is backed up; StackBar's own copy needs none
            Assert.Single(Directory.GetFiles(ShortcutRepairService.BackupDirectory(AppData), "*.lnk"));
        }

        [Fact]
        public void PrepareLaunch_with_the_setting_off_touches_only_StackBars_own_copy()
        {
            string target = CreateTarget("app.exe", 1000);
            string registered = CreateLink(Path.Combine("StartMenu", "App.lnk"), target, aumid: "Vendor.App");
            string ownCopy = CreateLink(Path.Combine("appdata", "Links", "ab12cd34", "App.lnk"), target, aumid: "Vendor.App");
            string desktopItem = CreateLink(Path.Combine("Desktop", "App.lnk"), target, aumid: "Vendor.App");
            UpdateInPlace(target, 2500);
            UseStartMenu(_dir.File("StartMenu"));

            ShortcutRepairService.PrepareLaunch(new GroupItem { TargetPath = ownCopy, Kind = ItemKind.Shortcut }, AppData, repairAppShortcuts: false);
            ShortcutRepairService.PrepareLaunch(new GroupItem { TargetPath = desktopItem, Kind = ItemKind.Shortcut }, AppData, repairAppShortcuts: false);

            Assert.False(ShortcutRepairService.IsStale(ownCopy));
            Assert.True(ShortcutRepairService.IsStale(registered));
            Assert.True(ShortcutRepairService.IsStale(desktopItem));
        }

        [Fact]
        public void PrepareLaunch_without_an_index_does_not_scan_and_skips_registered_shortcuts()
        {
            string target = CreateTarget("app.exe", 1000);
            string registered = CreateLink(Path.Combine("StartMenu", "App.lnk"), target, aumid: "Vendor.App");
            UpdateInPlace(target, 2500);
            ShortcutRepairService.RegisteredShortcutFolders = () => new[] { _dir.File("StartMenu") };
            ShortcutRepairService.ResetState(); // no index yet

            ShortcutRepairService.PrepareLaunch(new GroupItem { TargetPath = target, Kind = ItemKind.File }, AppData, repairAppShortcuts: true);

            Assert.True(ShortcutRepairService.IsStale(registered));
            Thread.Sleep(500); // let the background refresh it started finish before the folder is deleted
        }

        [Fact]
        public void PrepareLaunch_ignores_unrelated_shortcuts()
        {
            string target = CreateTarget("app.exe", 1000);
            string other = CreateTarget("other.exe", 1000);
            string unrelated = CreateLink(Path.Combine("StartMenu", "Other.lnk"), other, aumid: "Vendor.Other");
            UpdateInPlace(other, 2500);
            UseStartMenu(_dir.File("StartMenu"));

            ShortcutRepairService.PrepareLaunch(new GroupItem { TargetPath = target, Kind = ItemKind.File }, AppData, repairAppShortcuts: true);

            Assert.True(ShortcutRepairService.IsStale(unrelated));
        }

        [Fact]
        public void RepairAll_reports_repaired_and_skipped_shortcuts()
        {
            string target = CreateTarget("app.exe", 1000);
            string registered = CreateLink(Path.Combine("StartMenu", "App.lnk"), target);
            string readOnly = CreateLink(Path.Combine("StartMenu", "Locked.lnk"), target);
            string fresh = CreateLink(Path.Combine("StartMenu", "Fresh.lnk"), CreateTarget("fresh.exe", 10));
            UpdateInPlace(target, 2500);
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);
            ShortcutRepairService.RegisteredShortcutFolders = () => new[] { _dir.File("StartMenu") };
            try
            {
                var report = ShortcutRepairService.RepairAll(new List<string>(), AppData);

                Assert.Equal(3, report.Checked);
                Assert.Equal(new List<string> { registered }, report.Repaired);
                Assert.Equal(new List<string> { readOnly }, report.Skipped);
                Assert.Empty(report.Failed);
                Assert.False(ShortcutRepairService.IsStale(fresh));
            }
            finally
            {
                File.SetAttributes(readOnly, FileAttributes.Normal);
            }
        }
    }
}

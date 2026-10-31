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
    // Shortcut repair touches a process-wide index; keep these tests from running in parallel with each other
    [Collection("ShortcutRepair")]
    public class ShortcutRepairServiceTests : IDisposable
    {
        private readonly TempDirectory _dir = new TempDirectory();

        public ShortcutRepairServiceTests()
        {
            AppLogger.Initialize(Path.Combine(Path.GetTempPath(), "StackBarTests_logs"));
        }

        public void Dispose()
        {
            ShortcutRepairService.RegisteredShortcutFolders = () => Enumerable.Empty<string>();
            ShortcutRepairService.ResetIndex();
            _dir.Dispose();
        }

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

        private string CreateLink(string lnkName, string target, string? aumid = null, string? arguments = null, string? description = null)
        {
            string lnk = _dir.File(lnkName);
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
                string? aumid = null;
                var key = ShortcutService.AppUserModelIdKey;
                if (((ShortcutService.IPropertyStore)link).GetValue(ref key, out var value) == 0 && value.vt == 31)
                    aumid = Marshal.PtrToStringUni(value.pwszVal);
                return (args.ToString(), desc.ToString(), aumid);
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }

        [Fact]
        public void Fresh_shortcut_is_not_stale()
        {
            string target = CreateTarget("app.exe", 1000);
            string lnk = CreateLink("App.lnk", target);

            Assert.False(ShortcutRepairService.IsStale(lnk));
            Assert.Null(ShortcutRepairService.RepairIfStale(lnk, _dir.File("backups")));
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

            Assert.True(ShortcutRepairService.RepairIfStale(lnk, backups));

            string backup = Assert.Single(Directory.GetFiles(backups));
            Assert.EndsWith("_App.lnk", backup);
            Assert.Equal(original, File.ReadAllBytes(backup));
            Assert.False(ShortcutRepairService.IsStale(lnk));
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
        public void PrepareLaunch_repairs_the_apps_registered_shortcut_found_by_app_id()
        {
            string target = CreateTarget("app.exe", 1000);
            string startMenu = Path.Combine(_dir.Path, "StartMenu");
            Directory.CreateDirectory(startMenu);
            string registered = CreateLink(Path.Combine("StartMenu", "App.lnk"), target, aumid: "Vendor.App");
            string ownCopy = CreateLink("OwnCopy.lnk", target, aumid: "Vendor.App");
            UpdateInPlace(target, 2500);

            ShortcutRepairService.RegisteredShortcutFolders = () => new[] { startMenu };
            ShortcutRepairService.ResetIndex();
            string appData = _dir.File("appdata");
            ShortcutRepairService.PrepareLaunch(new GroupItem { TargetPath = ownCopy, Kind = ItemKind.Shortcut }, appData);

            Assert.False(ShortcutRepairService.IsStale(ownCopy));
            Assert.False(ShortcutRepairService.IsStale(registered));
            Assert.Single(Directory.GetFiles(ShortcutRepairService.BackupDirectory(appData))); // only the other app's file is backed up
        }

        [Fact]
        public void PrepareLaunch_ignores_unrelated_shortcuts()
        {
            string target = CreateTarget("app.exe", 1000);
            string other = CreateTarget("other.exe", 1000);
            string startMenu = Path.Combine(_dir.Path, "StartMenu");
            Directory.CreateDirectory(startMenu);
            string unrelated = CreateLink(Path.Combine("StartMenu", "Other.lnk"), other, aumid: "Vendor.Other");
            UpdateInPlace(other, 2500);

            ShortcutRepairService.RegisteredShortcutFolders = () => new[] { startMenu };
            ShortcutRepairService.ResetIndex();
            ShortcutRepairService.PrepareLaunch(new GroupItem { TargetPath = target, Kind = ItemKind.File }, _dir.File("appdata"));

            Assert.True(ShortcutRepairService.IsStale(unrelated));
        }

        [Fact]
        public void RepairAll_reports_repaired_shortcuts()
        {
            string target = CreateTarget("app.exe", 1000);
            string startMenu = Path.Combine(_dir.Path, "StartMenu");
            Directory.CreateDirectory(startMenu);
            string registered = CreateLink(Path.Combine("StartMenu", "App.lnk"), target);
            string fresh = CreateLink(Path.Combine("StartMenu", "Fresh.lnk"), CreateTarget("fresh.exe", 10));
            UpdateInPlace(target, 2500);

            ShortcutRepairService.RegisteredShortcutFolders = () => new[] { startMenu };
            var report = ShortcutRepairService.RepairAll(new AppConfig(), _dir.File("appdata"));

            Assert.Equal(2, report.Checked);
            Assert.Equal(new List<string> { registered }, report.Repaired);
            Assert.Empty(report.Failed);
            Assert.False(ShortcutRepairService.IsStale(fresh));
        }
    }
}

using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using TaskbarGrouper.Models;
using TaskbarGrouper.Services;
using Xunit;

namespace StackBar.Tests
{
    public class ShortcutServiceTests
    {
        [Theory]
        [InlineData("--group \"abc12345\"", "abc12345")]
        [InlineData("--group abc12345", "abc12345")]
        [InlineData("--GROUP \"web-dev\" --other", "web-dev")]
        [InlineData("--tray --group \"ai-tools\"", "ai-tools")]
        public void TryGetGroupIdFromArgs_parses_the_id(string args, string expected)
        {
            Assert.True(ShortcutService.TryGetGroupIdFromArgs(args, out var id));
            Assert.Equal(expected, id);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("--tray")]
        [InlineData("--group")]
        [InlineData("--group \"\"")]
        public void TryGetGroupIdFromArgs_rejects_missing_id(string? args)
        {
            Assert.False(ShortcutService.TryGetGroupIdFromArgs(args, out _));
        }

        [Fact]
        public void Group_id_is_matched_exactly_not_as_substring()
        {
            ShortcutService.TryGetGroupIdFromArgs("--group \"web-dev\"", out var id);
            Assert.NotEqual("web", id);
        }

        [Fact]
        public void GroupContainsSource_detects_reimport_of_same_source()
        {
            var group = new AppGroup
            {
                Items = new List<GroupItem>
                {
                    new GroupItem { Name = "Steam", TargetPath = @"C:\data\Links\1234abcd_Steam.lnk", SourcePath = @"C:\Pinned\Steam.lnk" }
                }
            };

            Assert.True(ShortcutService.GroupContainsSource(group, @"C:\Pinned\Steam.lnk", "Steam"));
            Assert.False(ShortcutService.GroupContainsSource(group, @"C:\Desktop\Other.lnk", "Other"));
            // A different source with the same name is a different shortcut once SourcePath is known
            Assert.False(ShortcutService.GroupContainsSource(group, @"C:\Desktop\Steam.lnk", "Steam"));
        }

        [Fact]
        public void GroupContainsSource_falls_back_to_name_for_items_imported_before_source_tracking()
        {
            var group = new AppGroup
            {
                Items = new List<GroupItem> { new GroupItem { Name = "Steam", TargetPath = @"C:\data\Links\1234abcd_Steam.lnk" } }
            };

            Assert.True(ShortcutService.GroupContainsSource(group, @"C:\Pinned\Steam.lnk", null));
            Assert.True(ShortcutService.GroupContainsSource(group, @"C:\Pinned\6d0bacd7_Steam.lnk", null));
        }

        [Fact]
        public void DeletePersistedLinkIfUnused_deletes_only_unreferenced_copies_inside_Links()
        {
            using (var dir = new TempDirectory())
            {
                string links = Path.Combine(dir.Path, "Links");
                Directory.CreateDirectory(links);
                string unused = Path.Combine(links, "aaaa1111_A.lnk");
                string shared = Path.Combine(links, "bbbb2222_B.lnk");
                string outside = dir.File("C.exe");
                File.WriteAllText(unused, "x");
                File.WriteAllText(shared, "x");
                File.WriteAllText(outside, "x");

                var stillUsing = new GroupItem { TargetPath = shared };
                var config = new AppConfig { Groups = new List<AppGroup> { new AppGroup { Items = new List<GroupItem> { stillUsing } } } };

                ShortcutService.DeletePersistedLinkIfUnused(new GroupItem { TargetPath = unused }, config, dir.Path);
                ShortcutService.DeletePersistedLinkIfUnused(new GroupItem { TargetPath = shared }, config, dir.Path);
                ShortcutService.DeletePersistedLinkIfUnused(new GroupItem { TargetPath = outside }, config, dir.Path);

                Assert.False(File.Exists(unused));
                Assert.True(File.Exists(shared));
                Assert.True(File.Exists(outside));
            }
        }

        [Fact]
        public void Persistent_link_copies_keep_the_clean_app_name_as_file_name()
        {
            using (var dir = new TempDirectory())
            {
                string first = ShortcutService.NewPersistentLinkPath(dir.Path, "6d0bacd7_Firefox", ".lnk");
                string second = ShortcutService.NewPersistentLinkPath(dir.Path, "Firefox", ".lnk");

                Assert.Equal("Firefox.lnk", Path.GetFileName(first));
                Assert.Equal("Firefox.lnk", Path.GetFileName(second));
                Assert.NotEqual(Path.GetDirectoryName(first), Path.GetDirectoryName(second));
                Assert.Equal("Shortcut.lnk", Path.GetFileName(ShortcutService.NewPersistentLinkPath(dir.Path, "  ", ".lnk")));
            }
        }

        [Fact]
        public void MigrateFlatLinkCopies_moves_old_prefixed_copies_into_clean_named_files()
        {
            using (var dir = new TempDirectory())
            {
                string links = Path.Combine(dir.Path, "Links");
                Directory.CreateDirectory(links);
                string oldCopy = Path.Combine(links, "a8b0d2a3_Firefox.lnk");
                File.WriteAllText(oldCopy, "lnk");
                string unrelated = Path.Combine(links, "notes.txt");
                File.WriteAllText(unrelated, "x");

                var item = new GroupItem { Name = "Firefox", TargetPath = oldCopy, CustomIconPath = oldCopy };
                var exeItem = new GroupItem { Name = "Tool", TargetPath = @"C:\Tools\tool.exe" };
                var config = new AppConfig { Groups = new List<AppGroup> { new AppGroup { Items = new List<GroupItem> { item, exeItem } } } };

                Assert.True(ShortcutService.MigrateFlatLinkCopies(config, dir.Path));

                string expected = Path.Combine(links, "a8b0d2a3", "Firefox.lnk");
                Assert.Equal(expected, item.TargetPath);
                Assert.Equal(expected, item.CustomIconPath);
                Assert.Equal("lnk", File.ReadAllText(expected));
                Assert.False(File.Exists(oldCopy));
                Assert.True(File.Exists(unrelated));
                Assert.Equal(@"C:\Tools\tool.exe", exeItem.TargetPath);

                // Second run: nothing left to migrate
                Assert.False(ShortcutService.MigrateFlatLinkCopies(config, dir.Path));
            }
        }

        [Fact]
        public void DeletePersistedLinkIfUnused_removes_the_empty_per_copy_folder()
        {
            using (var dir = new TempDirectory())
            {
                string copy = ShortcutService.NewPersistentLinkPath(Path.Combine(dir.Path, "Links"), "Steam", ".lnk");
                File.WriteAllText(copy, "x");
                var config = new AppConfig { Groups = new List<AppGroup> { new AppGroup() } };

                ShortcutService.DeletePersistedLinkIfUnused(new GroupItem { TargetPath = copy }, config, dir.Path);

                Assert.False(Directory.Exists(Path.GetDirectoryName(copy)));
                Assert.True(Directory.Exists(Path.Combine(dir.Path, "Links")));
            }
        }

        [Fact]
        public void PersistCustomFolderIcon_uses_a_new_file_per_cover_and_removes_the_previous_one()
        {
            using (var dir = new TempDirectory())
            {
                string png = dir.File("cover.png");
                using (var bmp = new Bitmap(4, 4)) bmp.Save(png, ImageFormat.Png);

                string? first = ShortcutService.PersistCustomFolderIcon(png, "g1", dir.Path);
                string? second = ShortcutService.PersistCustomFolderIcon(png, "g1", dir.Path);

                Assert.NotNull(first);
                Assert.NotNull(second);
                Assert.NotEqual(first, second);
                Assert.False(File.Exists(first));
                Assert.True(File.Exists(second));
            }
        }

        [Fact]
        public void PersistCustomFolderIcon_reuses_an_already_persisted_cover_instead_of_copying_onto_itself()
        {
            using (var dir = new TempDirectory())
            {
                string png = dir.File("cover.png");
                using (var bmp = new Bitmap(4, 4)) bmp.Save(png, ImageFormat.Png);
                string? persisted = ShortcutService.PersistCustomFolderIcon(png, "g1", dir.Path);

                string? again = ShortcutService.PersistCustomFolderIcon(persisted!, "g1", dir.Path);

                Assert.Equal(persisted, again);
                Assert.True(File.Exists(persisted));
            }
        }

        [Fact]
        public void PersistCustomFolderIcon_does_not_touch_other_groups_with_a_longer_id()
        {
            using (var dir = new TempDirectory())
            {
                string png = dir.File("cover.png");
                using (var bmp = new Bitmap(4, 4)) bmp.Save(png, ImageFormat.Png);
                string? other = ShortcutService.PersistCustomFolderIcon(png, "web-dev", dir.Path);

                ShortcutService.PersistCustomFolderIcon(png, "web", dir.Path);

                Assert.True(File.Exists(other));
            }
        }
    }
}

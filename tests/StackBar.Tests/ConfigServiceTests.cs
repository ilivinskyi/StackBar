using System.Collections.Generic;
using System.IO;
using System.Linq;
using TaskbarGrouper.Models;
using TaskbarGrouper.Services;
using Xunit;

namespace StackBar.Tests
{
    public class ConfigServiceTests
    {
        public ConfigServiceTests()
        {
            // Keep test log lines (e.g. "groups.json was corrupt") out of the user's real StackBar log
            AppLogger.Initialize(Path.Combine(Path.GetTempPath(), "StackBarTests_logs"));
        }

        [Fact]
        public void Save_then_load_keeps_false_booleans_and_non_default_values()
        {
            using (var dir = new TempDirectory())
            {
                var service = new ConfigService(dir.Path);
                var config = new AppConfig
                {
                    Groups = new List<AppGroup>
                    {
                        new AppGroup
                        {
                            Id = "g1",
                            Name = "Work",
                            ShowLabels = false,
                            CloseOnLaunch = false,
                            Columns = 6,
                            IconMode = FolderIconMode.EmojiGlyph,
                            Items = new List<GroupItem>
                            {
                                new GroupItem
                                {
                                    Name = "Tool",
                                    TargetPath = @"C:\tools\tool.lnk",
                                    Kind = ItemKind.Shortcut,
                                    CustomIconPath = @"C:\Windows\System32\shell32.dll",
                                    CustomIconIndex = -16,
                                    SourcePath = @"C:\Users\me\Desktop\tool.lnk"
                                }
                            }
                        }
                    }
                };

                service.Save(config);
                var loaded = new ConfigService(dir.Path).LoadOrCreateDefault();

                var group = Assert.Single(loaded.Groups);
                Assert.False(group.ShowLabels);
                Assert.False(group.CloseOnLaunch);
                Assert.Equal(6, group.Columns);
                Assert.Equal(FolderIconMode.EmojiGlyph, group.IconMode);
                var item = Assert.Single(group.Items);
                Assert.Equal(ItemKind.Shortcut, item.Kind);
                Assert.Equal(-16, item.CustomIconIndex);
                Assert.Equal(@"C:\Users\me\Desktop\tool.lnk", item.SourcePath);
            }
        }

        [Fact]
        public void Repair_shortcuts_setting_defaults_on_and_persists_off()
        {
            using (var dir = new TempDirectory())
            {
                File.WriteAllText(dir.File("groups.json"), "{\"groups\":[]}");
                Assert.True(new ConfigService(dir.Path).LoadOrCreateDefault().RepairAppShortcuts);

                var service = new ConfigService(dir.Path);
                service.Save(new AppConfig { RepairAppShortcuts = false });
                Assert.False(new ConfigService(dir.Path).LoadOrCreateDefault().RepairAppShortcuts);
            }
        }

        [Fact]
        public void Values_missing_from_old_files_get_app_defaults()
        {
            using (var dir = new TempDirectory())
            {
                File.WriteAllText(dir.File("groups.json"), "{\"groups\":[{\"id\":\"old\",\"name\":\"Old\",\"items\":[]}]}");

                var group = Assert.Single(new ConfigService(dir.Path).LoadOrCreateDefault().Groups);

                Assert.True(group.ShowLabels);
                Assert.True(group.CloseOnLaunch);
                Assert.Equal(4, group.Columns);
            }
        }

        [Fact]
        public void Legacy_capitalized_property_names_still_load()
        {
            using (var dir = new TempDirectory())
            {
                File.WriteAllText(dir.File("groups.json"),
                    "{\"Groups\":[{\"Id\":\"legacy\",\"Name\":\"Legacy\",\"ShowLabels\":false,\"Items\":[{\"Name\":\"App\",\"TargetPath\":\"C:\\\\app.exe\"}]}]}");

                var group = Assert.Single(new ConfigService(dir.Path).LoadOrCreateDefault().Groups);

                Assert.Equal("legacy", group.Id);
                Assert.False(group.ShowLabels);
                Assert.Equal("App", Assert.Single(group.Items).Name);
            }
        }

        [Fact]
        public void Corrupt_file_is_preserved_and_backup_is_restored_into_main_file()
        {
            using (var dir = new TempDirectory())
            {
                var service = new ConfigService(dir.Path);
                service.Save(new AppConfig { Groups = new List<AppGroup> { new AppGroup { Id = "good", Name = "Good" } } });
                service.Save(new AppConfig { Groups = new List<AppGroup> { new AppGroup { Id = "good", Name = "Good v2" } } });
                File.WriteAllText(dir.File("groups.json"), "{ not json");

                var loaded = service.LoadOrCreateDefault();

                Assert.Equal("Good", Assert.Single(loaded.Groups).Name);
                Assert.Single(Directory.GetFiles(dir.Path, "groups.corrupt-*.json"));
                // The main file was repaired, so the next load doesn't hit the corrupt file again
                Assert.Equal("Good", Assert.Single(new ConfigService(dir.Path).LoadOrCreateDefault().Groups).Name);
                Assert.Single(Directory.GetFiles(dir.Path, "groups.corrupt-*.json"));
            }
        }

        [Fact]
        public void Sanitize_strips_hex_link_prefix_and_reports_change()
        {
            var config = new AppConfig
            {
                Groups = new List<AppGroup>
                {
                    new AppGroup { Items = new List<GroupItem> { new GroupItem { Name = "6d0bacd7_Steam", TargetPath = @"C:\a.lnk" } } }
                }
            };

            Assert.True(ConfigService.SanitizeConfig(config));
            Assert.Equal("Steam", config.Groups.Single().Items.Single().Name);
            Assert.False(ConfigService.SanitizeConfig(config));
        }
    }
}

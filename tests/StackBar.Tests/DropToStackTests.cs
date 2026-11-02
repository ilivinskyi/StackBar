using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using TaskbarGrouper.Models;
using TaskbarGrouper.Services;
using Xunit;

namespace StackBar.Tests
{
    public class DragHoverTrackerTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

        private static DateTime At(int ms) => T0.AddMilliseconds(ms);

        [Fact]
        public void Hovering_a_stack_during_a_drag_opens_it_once_after_the_delay()
        {
            var tracker = new DragHoverTracker();

            Assert.Null(tracker.Sample(At(0), true, () => false, () => null));      // drag starts elsewhere
            Assert.Null(tracker.Sample(At(100), true, () => false, () => "dev"));   // reaches the pin
            Assert.Null(tracker.Sample(At(400), true, () => false, () => "dev"));
            Assert.Equal("dev", tracker.Sample(At(560), true, () => false, () => "dev"));
            Assert.Null(tracker.Sample(At(900), true, () => false, () => "dev"));   // not again while it stays
        }

        [Fact]
        public void Moving_to_another_stack_restarts_the_delay()
        {
            var tracker = new DragHoverTracker();
            tracker.Sample(At(0), true, () => false, () => null);
            tracker.Sample(At(100), true, () => false, () => "dev");
            Assert.Equal("dev", tracker.Sample(At(600), true, () => false, () => "dev"));

            Assert.Null(tracker.Sample(At(650), true, () => false, () => "games"));
            Assert.Null(tracker.Sample(At(900), true, () => false, () => "games"));
            Assert.Equal("games", tracker.Sample(At(1150), true, () => false, () => "games"));
        }

        [Fact]
        public void Coming_back_to_a_stack_after_leaving_it_opens_it_again()
        {
            var tracker = new DragHoverTracker();
            tracker.Sample(At(0), true, () => false, () => "dev");
            Assert.Equal("dev", tracker.Sample(At(500), true, () => false, () => "dev"));
            tracker.Sample(At(600), true, () => false, () => null);   // up into the flyout
            tracker.Sample(At(700), true, () => false, () => "dev");
            Assert.Equal("dev", tracker.Sample(At(1200), true, () => false, () => "dev"));
        }

        [Fact]
        public void A_press_that_starts_on_the_taskbar_or_in_StackBar_is_ignored()
        {
            var tracker = new DragHoverTracker();
            tracker.Sample(At(0), true, () => true, () => "dev");
            Assert.Null(tracker.Sample(At(2000), true, () => true, () => "dev"));

            // The next press is judged again
            tracker.Sample(At(2100), false, () => true, () => null);
            tracker.Sample(At(2200), true, () => false, () => "dev");
            Assert.Equal("dev", tracker.Sample(At(2700), true, () => false, () => "dev"));
        }

        [Fact]
        public void Releasing_the_button_cancels_the_hover()
        {
            var tracker = new DragHoverTracker();
            tracker.Sample(At(0), true, () => false, () => "dev");
            tracker.Sample(At(300), false, () => false, () => "dev");
            Assert.Null(tracker.Sample(At(600), false, () => false, () => "dev"));
        }

        [Fact]
        public void The_press_check_runs_once_per_press()
        {
            int calls = 0;
            var tracker = new DragHoverTracker();
            for (int ms = 0; ms < 1000; ms += 40) tracker.Sample(At(ms), true, () => { calls++; return false; }, () => null);
            Assert.Equal(1, calls);
        }
    }

    public class TaskbarAppIdTests
    {
        [Theory]
        [InlineData("Appid: StackBar.Group.dev", "StackBar.Group.dev")]
        [InlineData("Appid: Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App")]
        [InlineData("StartButton", null)]
        [InlineData("Appid: ", null)]
        [InlineData(null, null)]
        public void Taskbar_automation_id_yields_the_app_id(string? automationId, string? expected)
        {
            Assert.Equal(expected, TaskbarDragWatcher.ParseTaskbarAutomationId(automationId));
        }

        [Fact]
        public void Cursor_is_matched_to_the_button_it_is_over()
        {
            var buttons = new[]
            {
                new TaskbarDragWatcher.TaskbarButton(new System.Windows.Rect(752, 1032, 44, 48), "StackBar.Group.gaming"),
                new TaskbarDragWatcher.TaskbarButton(new System.Windows.Rect(796, 1032, 44, 48), "StackBar.Group.ai-tools"),
            };

            Assert.Equal("StackBar.Group.gaming", TaskbarDragWatcher.AppIdAt(buttons, 752, 1032));
            Assert.Equal("StackBar.Group.gaming", TaskbarDragWatcher.AppIdAt(buttons, 795, 1079));
            Assert.Equal("StackBar.Group.ai-tools", TaskbarDragWatcher.AppIdAt(buttons, 796, 1056));
            Assert.Null(TaskbarDragWatcher.AppIdAt(buttons, 840, 1056));   // right edge is exclusive
            Assert.Null(TaskbarDragWatcher.AppIdAt(buttons, 760, 1000));   // above the taskbar
        }

        [Theory]
        [InlineData("StackBar.Group.dev", "StackBar.Group.", "dev")]
        [InlineData("stackbar.group.Dev", "StackBar.Group.", "Dev")]
        [InlineData("StackBar.Portable.abc.Group.dev", "StackBar.Group.", null)]   // another edition's pin
        [InlineData("StackBar.Portable.abc.Group.dev", "StackBar.Portable.abc.Group.", "dev")]
        [InlineData("StackBar.Group.", "StackBar.Group.", null)]
        [InlineData("Vivaldi.ZUULQ", "StackBar.Group.", null)]
        public void Only_this_editions_pins_map_to_a_stack(string aumid, string prefix, string? expected)
        {
            Assert.Equal(expected, TaskbarDragWatcher.TryGetGroupIdFromAumid(aumid, prefix));
        }
    }

    public class DropImportTests
    {
        [Theory]
        [InlineData(@"::{4234D49B-0245-4DF3-B780-3893943456E1}\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
        [InlineData(@"shell:::{4234d49b-0245-4df3-b780-3893943456e1}\{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\mspaint.exe", @"shell:AppsFolder\{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\mspaint.exe")]
        [InlineData(@"C:\Tools\app.exe", @"C:\Tools\app.exe")]
        [InlineData(@"\\server\share\app.lnk", @"\\server\share\app.lnk")]
        [InlineData(@"::{21EC2020-3AEA-1069-A2DD-08002B30309D}", null)]   // Control Panel
        [InlineData(@"relative\path.exe", null)]
        [InlineData("", null)]
        public void Shell_parsing_names_map_to_storable_paths(string parsingName, string? expected)
        {
            Assert.Equal(expected, DropImport.ToItemPath(parsingName));
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

        [DllImport("shell32.dll")]
        private static extern uint ILGetSize(IntPtr pidl);

        [Fact]
        public void Shell_id_list_from_a_drag_resolves_to_file_paths()
        {
            using (var temp = new TempDirectory())
            {
                string file = temp.File("tool.exe");
                File.WriteAllText(file, "");
                Assert.Equal(0, SHParseDisplayName(file, IntPtr.Zero, out IntPtr pidl, 0, out _));
                byte[] child;
                try
                {
                    child = new byte[ILGetSize(pidl)];
                    Marshal.Copy(pidl, child, 0, child.Length);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pidl);
                }

                // CIDA: count, offsets of [parent, child], the desktop's empty ID list, then the item's
                var cida = new byte[12 + 2 + child.Length];
                BitConverter.GetBytes(1).CopyTo(cida, 0);
                BitConverter.GetBytes(12).CopyTo(cida, 4);
                BitConverter.GetBytes(14).CopyTo(cida, 8);
                child.CopyTo(cida, 14);

                var names = DropImport.ParseShellIdList(cida);

                Assert.Equal(new[] { file }, names, StringComparer.OrdinalIgnoreCase);
            }
        }

        [Theory]
        [InlineData(new byte[0])]
        [InlineData(new byte[] { 5, 0, 0, 0, 12, 0, 0, 0 })]        // count larger than the offsets present
        [InlineData(new byte[] { 1, 0, 0, 0, 200, 0, 0, 0, 12, 0, 0, 0, 0, 0 })] // offset outside the data
        public void Malformed_shell_id_lists_are_ignored(byte[] cida)
        {
            Assert.Empty(DropImport.ParseShellIdList(cida));
        }

        [Fact]
        public void Dropped_apps_are_inserted_at_the_slot_and_duplicates_skipped()
        {
            using (var temp = new TempDirectory())
            {
                string a = temp.File("Alpha.txt"), b = temp.File("Beta.txt"), c = temp.File("Gamma.txt");
                foreach (var f in new[] { a, b, c }) File.WriteAllText(f, f);
                string dataDir = temp.File("data");
                var group = new AppGroup();
                group.Items.Add(ShortcutService.ImportAndPersistShortcut(a, dataDir));

                var added = DropImport.AddToGroup(group, new[] { b, a, c }, 0, dataDir, out int skipped);

                Assert.Equal(2, added.Count);
                Assert.Equal(1, skipped);
                Assert.Equal(new[] { "Beta", "Gamma", "Alpha" }, group.Items.Select(i => i.Name));
            }
        }

        [Fact]
        public void An_out_of_range_slot_appends()
        {
            using (var temp = new TempDirectory())
            {
                string a = temp.File("Alpha.txt"), b = temp.File("Beta.txt");
                foreach (var f in new[] { a, b }) File.WriteAllText(f, f);
                string dataDir = temp.File("data");
                var group = new AppGroup();
                group.Items.Add(ShortcutService.ImportAndPersistShortcut(a, dataDir));

                DropImport.AddToGroup(group, new[] { b }, 99, dataDir, out _);

                Assert.Equal(new[] { "Alpha", "Beta" }, group.Items.Select(i => i.Name));
            }
        }
    }
}

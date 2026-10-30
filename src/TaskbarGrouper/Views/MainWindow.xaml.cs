using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using TaskbarGrouper.Models;
using TaskbarGrouper.Services;
using GroupItemModel = TaskbarGrouper.Models.GroupItem;

namespace TaskbarGrouper.Views
{
    public class ManagerItemRowViewModel
    {
        private readonly Action _onEdited;
        public GroupItemModel Model { get; set; } = null!;
        public ImageSource? Icon { get; set; }

        public ManagerItemRowViewModel(Action onEdited)
        {
            _onEdited = onEdited;
        }

        public string Name
        {
            get => Model.Name;
            set
            {
                if (Model.Name != value)
                {
                    Model.Name = value;
                    _onEdited();
                }
            }
        }

        public string TargetPath => Model.TargetPath;
    }

    public partial class MainWindow : Window
    {
        private readonly AppConfig _config;
        private readonly ConfigService _configService;
        private AppGroup? _selectedGroup;
        private bool _suppressEvents;
        private readonly DispatcherTimer _debounceTimer;

        // Custom drag & drop reorder state
        private Point _dragStartPoint;
        private bool _isDraggingItem;

        public MainWindow(AppConfig config, ConfigService configService)
        {
            InitializeComponent();
            _config = config;
            _configService = configService;

            _debounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _debounceTimer.Tick += (s, e) =>
            {
                _debounceTimer.Stop();
                _configService.Save(_config);
                RefreshLiveFolderIcon();
            };

            _suppressEvents = true;
            RunAtStartupCheck.IsChecked = StartupService.IsRunAtStartupEnabled();
            if (AppInfo.IsPortableMode)
            {
                ModeBadgeText.Text = "Portable Edition v1.1";
                RunAtStartupCheck.IsEnabled = false;
                RunAtStartupCheck.ToolTip = "Windows startup registration is disabled in Portable Mode.";
            }
            _suppressEvents = false;

            RefreshGroupsList();
            if (_config.Groups.Count > 0)
                GroupsListBox.SelectedIndex = 0;

            // Enable custom internal drag-and-drop reordering on ItemsListBox
            ItemsListBox.PreviewMouseLeftButtonDown += ItemsListBox_PreviewMouseLeftButtonDown;
            ItemsListBox.PreviewMouseMove += ItemsListBox_PreviewMouseMove;
        }

        private void ItemsListBox_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
            _isDraggingItem = false;
        }

        private void ItemsListBox_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed && !_isDraggingItem)
            {
                Point pos = e.GetPosition(null);
                if (Math.Abs(pos.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(pos.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    if (ItemsListBox.SelectedItem is ManagerItemRowViewModel row)
                    {
                        _isDraggingItem = true;
                        var data = new DataObject("StackBar.ReorderItem", row);
                        DragDrop.DoDragDrop(ItemsListBox, data, DragDropEffects.Move);
                        _isDraggingItem = false;
                    }
                }
            }
        }

        private void RefreshGroupsList()
        {
            var current = _selectedGroup;
            GroupsListBox.ItemsSource = null;
            GroupsListBox.ItemsSource = _config.Groups;
            if (current != null && _config.Groups.Contains(current))
                GroupsListBox.SelectedItem = current;
        }

        private void RefreshLiveFolderIcon()
        {
            if (_selectedGroup == null) return;
            LiveFolderIconPreview.Source = IconHelper.GenerateGroupPreviewImage(_selectedGroup);
            SelectedGroupHeader.Text = $"{_selectedGroup.IconGlyph}  {_selectedGroup.Name}";
            SelectedGroupSubtext.Text = $"{_selectedGroup.Items.Count} app(s) • {(_selectedGroup.IconMode == FolderIconMode.MiniGrid2x2 ? "iOS 2x2 Grid Icon" : "Emoji Badge Icon")}";
        }

        private void RefreshItemsList()
        {
            if (_selectedGroup == null) return;
            _suppressEvents = true;

            GroupNameBox.Text = _selectedGroup.Name;
            GroupGlyphBox.Text = _selectedGroup.IconGlyph;

            if (_selectedGroup.IconMode == FolderIconMode.EmojiGlyph)
                StyleEmojiRadio.IsChecked = true;
            else
                StyleGridRadio.IsChecked = true;

            ShowLabelsCheck.IsChecked = _selectedGroup.ShowLabels;

            string hex = _selectedGroup.AccentColorHex ?? "#3A6DF0";
            if (hex.Equals("#8B5CF6", StringComparison.OrdinalIgnoreCase)) ColorPurple.IsChecked = true;
            else if (hex.Equals("#10B981", StringComparison.OrdinalIgnoreCase)) ColorEmerald.IsChecked = true;
            else if (hex.Equals("#F59E0B", StringComparison.OrdinalIgnoreCase)) ColorAmber.IsChecked = true;
            else if (hex.Equals("#EF4444", StringComparison.OrdinalIgnoreCase)) ColorRed.IsChecked = true;
            else ColorBlue.IsChecked = true;

            ItemsListBox.ItemsSource = _selectedGroup.Items.Select(i => new ManagerItemRowViewModel(() => TriggerDebounceSave())
            {
                Model = i,
                Icon = IconHelper.ExtractIcon(i.TargetPath, i.CustomIconPath)
            }).ToList();

            EmptyStateOverlay.Visibility = _selectedGroup.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshLiveFolderIcon();

            _suppressEvents = false;
        }

        private void TriggerDebounceSave()
        {
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private void GroupsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GroupsListBox.SelectedItem is AppGroup group)
            {
                _selectedGroup = group;
                RefreshItemsList();
            }
        }

        private void GroupMeta_Changed(object sender, TextChangedEventArgs e)
        {
            if (_suppressEvents || _selectedGroup == null) return;
            _selectedGroup.Name = GroupNameBox.Text;
            _selectedGroup.IconGlyph = GroupGlyphBox.Text;
            TriggerDebounceSave();
        }

        private void StyleRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _selectedGroup == null) return;
            _selectedGroup.IconMode = StyleEmojiRadio.IsChecked == true
                ? FolderIconMode.EmojiGlyph
                : FolderIconMode.MiniGrid2x2;
            TriggerDebounceSave();
        }

        private void AccentRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _selectedGroup == null) return;
            if (sender is RadioButton rb && rb.Tag is string hex)
            {
                _selectedGroup.AccentColorHex = hex;
                TriggerDebounceSave();
            }
        }

        private void ShowLabelsCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _selectedGroup == null) return;
            _selectedGroup.ShowLabels = ShowLabelsCheck.IsChecked == true;
            TriggerDebounceSave();
        }

        private void RunAtStartupCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool enabled = RunAtStartupCheck.IsChecked == true;
            StartupService.SetRunAtStartup(enabled);
            StatusText.Text = enabled
                ? "StackBar will start automatically in your system tray on login."
                : "Removed StackBar from Windows startup.";
        }

        private void AddGroup_Click(object sender, RoutedEventArgs e)
        {
            var newGroup = new AppGroup
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                Name = $"Stack {_config.Groups.Count + 1}",
                IconGlyph = "🗂️",
                AccentColorHex = "#3A6DF0",
                IconMode = FolderIconMode.MiniGrid2x2
            };
            _config.Groups.Add(newGroup);
            _configService.Save(_config);
            RefreshGroupsList();
            GroupsListBox.SelectedItem = newGroup;
        }

        private void DeleteGroup_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGroup == null || _config.Groups.Count <= 1)
            {
                StatusText.Text = "At least one Stack folder must remain.";
                return;
            }

            var res = MessageBox.Show($"Are you sure you want to delete the '{_selectedGroup.Name}' stack?", "Delete Stack", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;

            _config.Groups.Remove(_selectedGroup);
            _selectedGroup = _config.Groups.FirstOrDefault();
            _configService.Save(_config);
            RefreshGroupsList();
            if (_selectedGroup != null)
                GroupsListBox.SelectedItem = _selectedGroup;
        }

        private void PreviewFlyout_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGroup == null) return;
            _configService.Save(_config);
            var flyout = new FlyoutWindow(_selectedGroup, onEditRequested: () => Activate());
            flyout.Show();
        }

        private void CreateShortcut_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGroup == null) return;
            _configService.Save(_config);

            try
            {
                string lnkPath = ShortcutService.CreateGroupShortcut(_selectedGroup, _configService.AppDataDirectory);
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{lnkPath}\"");
                StatusText.Text = $"Created '{Path.GetFileName(lnkPath)}'! Right-click it in Explorer -> 'Pin to taskbar'.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to create shortcut:\n{ex.Message}", "StackBar", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ImportTaskbar_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGroup == null) return;

            var dialog = new TaskbarImporterWindow { Owner = this };
            if (dialog.ShowDialog() == true && dialog.SelectedItems.Count > 0)
            {
                foreach (var item in dialog.SelectedItems)
                {
                    var persistent = ShortcutService.ImportAndPersistShortcut(item.TargetPath, _configService.AppDataDirectory);
                    if (!_selectedGroup.Items.Any(existing => existing.TargetPath.Equals(persistent.TargetPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        _selectedGroup.Items.Add(persistent);
                    }
                }
                _configService.Save(_config);
                RefreshItemsList();
                RefreshGroupsList();
                StatusText.Text = $"Imported {dialog.SelectedItems.Count} app(s) into '{_selectedGroup.Name}'!";
            }
        }

        private void AddAppFile_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGroup == null) return;

            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Applications, Shortcuts, or Files to Add",
                Filter = "All Files & Apps (*.*)|*.*",
                Multiselect = true
            };

            if (ofd.ShowDialog() == true)
            {
                foreach (var file in ofd.FileNames)
                {
                    var persistent = ShortcutService.ImportAndPersistShortcut(file, _configService.AppDataDirectory);
                    if (!_selectedGroup.Items.Any(existing => existing.TargetPath.Equals(persistent.TargetPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        _selectedGroup.Items.Add(persistent);
                    }
                }
                _configService.Save(_config);
                RefreshItemsList();
                RefreshGroupsList();
            }
        }

        private void MoveItemUp_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGroup == null || !(sender is FrameworkElement fe) || !(fe.Tag is ManagerItemRowViewModel row)) return;
            int idx = _selectedGroup.Items.IndexOf(row.Model);
            if (idx > 0)
            {
                var temp = _selectedGroup.Items[idx - 1];
                _selectedGroup.Items[idx - 1] = _selectedGroup.Items[idx];
                _selectedGroup.Items[idx] = temp;
                _configService.Save(_config);
                RefreshItemsList();
            }
        }

        private void MoveItemDown_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGroup == null || !(sender is FrameworkElement fe) || !(fe.Tag is ManagerItemRowViewModel row)) return;
            int idx = _selectedGroup.Items.IndexOf(row.Model);
            if (idx >= 0 && idx < _selectedGroup.Items.Count - 1)
            {
                var temp = _selectedGroup.Items[idx + 1];
                _selectedGroup.Items[idx + 1] = _selectedGroup.Items[idx];
                _selectedGroup.Items[idx] = temp;
                _configService.Save(_config);
                RefreshItemsList();
            }
        }

        private void RemoveRowItem_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGroup == null || !(sender is FrameworkElement fe) || !(fe.Tag is ManagerItemRowViewModel row)) return;
            _selectedGroup.Items.Remove(row.Model);
            _configService.Save(_config);
            RefreshItemsList();
            RefreshGroupsList();
        }

        private void ItemsListBox_Drop(object sender, DragEventArgs e)
        {
            if (_selectedGroup == null) return;

            // 1. Internal Drag & Drop Reordering
            if (e.Data.GetDataPresent("StackBar.ReorderItem"))
            {
                var dragged = e.Data.GetData("StackBar.ReorderItem") as ManagerItemRowViewModel;
                if (dragged != null)
                {
                    // Target drop position
                    var targetItem = GetItemUnderMouse(e.GetPosition(ItemsListBox));
                    if (targetItem != null && targetItem != dragged)
                    {
                        int oldIdx = _selectedGroup.Items.IndexOf(dragged.Model);
                        int newIdx = _selectedGroup.Items.IndexOf(targetItem.Model);
                        if (oldIdx >= 0 && newIdx >= 0)
                        {
                            _selectedGroup.Items.RemoveAt(oldIdx);
                            _selectedGroup.Items.Insert(newIdx, dragged.Model);
                            _configService.Save(_config);
                            RefreshItemsList();
                        }
                    }
                    return;
                }
            }

            // 2. Windows Explorer Drag & Drop of Files, Shortcuts, or Folders
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null) return;

                int count = 0;
                foreach (var file in files)
                {
                    var persistent = ShortcutService.ImportAndPersistShortcut(file, _configService.AppDataDirectory);
                    if (!_selectedGroup.Items.Any(existing => existing.TargetPath.Equals(persistent.TargetPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        _selectedGroup.Items.Add(persistent);
                        count++;
                    }
                }
                _configService.Save(_config);
                RefreshItemsList();
                RefreshGroupsList();
                StatusText.Text = $"Added {count} item(s) to '{_selectedGroup.Name}'.";
            }
        }

        private ManagerItemRowViewModel? GetItemUnderMouse(Point p)
        {
            HitTestResult result = VisualTreeHelper.HitTest(ItemsListBox, p);
            if (result == null) return null;

            DependencyObject current = result.VisualHit;
            while (current != null && current != ItemsListBox)
            {
                if (current is ListBoxItem item && item.DataContext is ManagerItemRowViewModel vm)
                {
                    return vm;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }
    }
}

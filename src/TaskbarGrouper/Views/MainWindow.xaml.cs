using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
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
                // While retyping, the box can be empty for a moment: keep the previous name instead of storing ""
                if (string.IsNullOrWhiteSpace(value) || Model.Name == value) return;
                Model.Name = value;
                _onEdited();
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
        // Groups edited since the last debounced save (the selection may change before the timer fires)
        private readonly HashSet<AppGroup> _dirtyGroups = new HashSet<AppGroup>();

        // Custom drag & drop reorder state
        private Point _dragStartPoint;
        private ManagerItemRowViewModel? _dragCandidate;
        private bool _isDraggingItem;

        public MainWindow(AppConfig config, ConfigService configService, string? selectGroupId = null)
        {
            InitializeComponent();
            _config = config;
            _configService = configService;

            _debounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _debounceTimer.Tick += (s, e) => FlushPendingSave();
            _configService.GroupChanged += OnGroupChangedElsewhere;
            Closed += (s, e) =>
            {
                FlushPendingSave();
                _configService.GroupChanged -= OnGroupChangedElsewhere;
            };
            // Otherwise an open picker stays up until a click outside, and that click (even on the close
            // button) only dismisses it
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.Escape || !(EmojiPickerPopup.IsOpen || AccentPickerPopup.IsOpen)) return;
                EmojiPickerPopup.IsOpen = false;
                AccentPickerPopup.IsOpen = false;
                e.Handled = true;
            };

            InitializeEmojiPalette();
            InitializeAccentPalette();

            _suppressEvents = true;
            RepairShortcutsCheck.IsChecked = _config.RepairAppShortcuts;
            if (AppInfo.IsPortableMode)
            {
                ModeBadgeText.Text = $"Portable Edition v{AppInfo.DisplayVersion}";
                // Don't show the installed copy's setting: Portable Edition never registers itself
                RunAtStartupCheck.IsChecked = false;
                RunAtStartupCheck.IsEnabled = false;
                RunAtStartupCheck.ToolTip = "Windows startup registration is disabled in Portable Mode.";
            }
            else
            {
                ModeBadgeText.Text = $"Taskbar Folder Studio v{AppInfo.DisplayVersion}";
                var startup = StartupService.GetState();
                RunAtStartupCheck.IsChecked = startup == StartupService.StartupState.On;
                if (startup == StartupService.StartupState.Stale)
                {
                    StatusText.Text = "The Windows startup entry points to an old StackBar location. Tick 'Run in Tray at Windows Startup' to fix it.";
                }
            }
            _suppressEvents = false;

            RefreshGroupsList();
            if (!string.IsNullOrEmpty(selectGroupId))
            {
                var target = _config.Groups.FirstOrDefault(g => g.Id.Equals(selectGroupId, StringComparison.OrdinalIgnoreCase));
                if (target != null)
                    GroupsListBox.SelectedItem = target;
                else if (_config.Groups.Count > 0)
                    GroupsListBox.SelectedIndex = 0;
            }
            else if (_config.Groups.Count > 0)
            {
                GroupsListBox.SelectedIndex = 0;
            }

            // Enable custom internal drag-and-drop reordering on ItemsListBox
            ItemsListBox.PreviewMouseLeftButtonDown += ItemsListBox_PreviewMouseLeftButtonDown;
            ItemsListBox.PreviewMouseMove += ItemsListBox_PreviewMouseMove;
        }

        /// <summary>
        /// Saves debounced edits (stack and app names, labels). None of them changes the stack's icon,
        /// so the pinned shortcut is not touched here; icon edits go through <see cref="SyncGroupAndTaskbarIcon"/>.
        /// </summary>
        private void FlushPendingSave()
        {
            _debounceTimer.Stop();
            if (_dirtyGroups.Count == 0) return;

            foreach (var group in _dirtyGroups)
            {
                group.Name = group.Name.Trim();
                foreach (var item in group.Items) item.Name = item.Name.Trim();
            }
            _dirtyGroups.Clear();
            SaveOrReport();
            RefreshLiveFolderIcon();
        }

        /// <returns>false (and tells the user) if the config could not be written.</returns>
        private bool SaveOrReport()
        {
            if (_configService.Save(_config)) return true;
            StatusText.Text = "Could not save your changes: " + _configService.LastError;
            MessageBox.Show(this, "StackBar could not save your changes:\n" + _configService.LastError,
                "StackBar", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        /// <summary>A flyout (or startup maintenance) changed the shared config: show it instead of keeping a stale view.</summary>
        private void OnGroupChangedElsewhere(string groupId)
        {
            if (!_config.Groups.Contains(_selectedGroup!))
            {
                _selectedGroup = null;
            }
            RefreshGroupsList();
            if (_selectedGroup != null)
            {
                RefreshItemsList();
            }
            else if (_config.Groups.Count > 0)
            {
                GroupsListBox.SelectedIndex = 0;
            }
        }

        public void SelectGroupById(string? groupId)
        {
            if (string.IsNullOrEmpty(groupId)) return;
            var target = _config.Groups.FirstOrDefault(g => g.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
            if (target != null)
            {
                GroupsListBox.SelectedItem = target;
            }
        }

        private void ItemsListBox_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
            _isDraggingItem = false;

            // Drag only from the row itself: pressing in the name box (text selection) or on a row button must not reorder
            var source = e.OriginalSource as DependencyObject;
            _dragCandidate = IsInsideInteractiveControl(source) ? null : GetItemUnderMouse(e.GetPosition(ItemsListBox));
        }

        private bool IsInsideInteractiveControl(DependencyObject? source)
        {
            for (var current = source; current != null && current != ItemsListBox; current = VisualTreeHelper.GetParent(current))
            {
                if (current is TextBox || current is System.Windows.Controls.Primitives.ButtonBase) return true;
                if (!(current is Visual)) break;
            }
            return false;
        }

        private void ItemsListBox_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed && !_isDraggingItem && _dragCandidate != null)
            {
                Point pos = e.GetPosition(null);
                if (Math.Abs(pos.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(pos.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    var row = _dragCandidate;
                    _dragCandidate = null;
                    _isDraggingItem = true;
                    try
                    {
                        var data = new DataObject("StackBar.ReorderItem", row);
                        DragDrop.DoDragDrop(ItemsListBox, data, DragDropEffects.Move);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException)
                    {
                        AppLogger.Log("Studio reorder drag failed: " + ex);
                    }
                    finally
                    {
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
            SelectedGroupGlyph.Text = _selectedGroup.IconGlyph;
            SelectedGroupGlyph.Visibility = string.IsNullOrWhiteSpace(_selectedGroup.IconGlyph) ? Visibility.Collapsed : Visibility.Visible;
            SelectedGroupHeader.Text = _selectedGroup.Name;
            string modeDesc = _selectedGroup.IconMode switch
            {
                FolderIconMode.CustomIco => "Custom Cover Icon",
                FolderIconMode.EmojiGlyph => "Emoji Badge Icon",
                _ => "iOS 2x2 Grid Icon"
            };
            SelectedGroupSubtext.Text = $"{_selectedGroup.Items.Count} app(s) • {modeDesc}";
        }

        private void UpdateIconControlVisibility()
        {
            if (_selectedGroup == null) return;

            if (_selectedGroup.IconMode == FolderIconMode.CustomIco)
            {
                CustomIconBar.Visibility = Visibility.Visible;
                EmojiPresetsBar.Visibility = Visibility.Collapsed;
                GridModeInfo.Visibility = Visibility.Collapsed;
                CustomIconPathLabel.Text = !string.IsNullOrWhiteSpace(_selectedGroup.CustomFolderIconPath)
                    ? $"Active Icon: {Path.GetFileName(_selectedGroup.CustomFolderIconPath)}"
                    : "No custom icon chosen. Pick a file (.ico, .png, .exe) or click ★ on any app below.";
            }
            else if (_selectedGroup.IconMode == FolderIconMode.EmojiGlyph)
            {
                CustomIconBar.Visibility = Visibility.Collapsed;
                EmojiPresetsBar.Visibility = Visibility.Visible;
                GridModeInfo.Visibility = Visibility.Collapsed;
            }
            else
            {
                CustomIconBar.Visibility = Visibility.Collapsed;
                EmojiPresetsBar.Visibility = Visibility.Collapsed;
                GridModeInfo.Visibility = Visibility.Visible;
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const byte VK_LWIN = 0x5B;
        private const byte VK_OEM_PERIOD = 0xBE;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private static readonly string[] PaletteEmojis =
        {
            "📁", "📂", "🗂️", "💼", "📝", "📊", "📌", "📎",
            "🎮", "🕹️", "🎲", "🎯", "👾", "🏆", "🏎️", "⚽",
            "💻", "🖥️", "⚡", "🛠️", "⚙️", "💾", "⌨️", "🌐",
            "💬", "💭", "✉️", "📞", "📢", "🔔", "👥", "💡",
            "🎨", "🖌️", "🎬", "🎵", "🎧", "📷", "📹", "🪄",
            "🤖", "🧠", "🧪", "🔭", "🚀", "🔮", "🛡️", "💎",
            "⭐", "🌟", "✨", "🔥", "❤️", "🏷️", "☕", "🍕"
        };

        private void InitializeEmojiPalette()
        {
            EmojiPalettePanel.Children.Clear();
            foreach (var emoji in PaletteEmojis)
            {
                var btn = new Button
                {
                    Content = new EmojiView { Text = emoji, EmojiSize = 18 },
                    Tag = emoji,
                    Style = (Style)FindResource("SurfaceBtn"),
                    Width = 30,
                    Height = 30,
                    Padding = new Thickness(0),
                    Margin = new Thickness(2),
                    FontSize = 14,
                    Cursor = Cursors.Hand
                };
                btn.Click += (s, e) =>
                {
                    EmojiPickerPopup.IsOpen = false;
                    ApplyGlyph(emoji);
                };
                EmojiPalettePanel.Children.Add(btn);
            }
        }

        private void EmojiPickerBtn_Click(object sender, RoutedEventArgs e)
        {
            EmojiPickerPopup.IsOpen = !EmojiPickerPopup.IsOpen;
        }

        private void ApplyGlyph(string glyph)
        {
            if (_selectedGroup == null || string.IsNullOrWhiteSpace(glyph)) return;
            _selectedGroup.IconGlyph = glyph;
            _selectedGroup.IconMode = FolderIconMode.EmojiGlyph;
            SelectedEmojiDisplay.Text = glyph;
            _suppressEvents = true;
            StyleEmojiRadio.IsChecked = true;
            _suppressEvents = false;
            UpdateIconControlVisibility();
            SyncGroupAndTaskbarIcon(_selectedGroup);
        }

        private void OpenWindowsEmojiPanel_Click(object sender, RoutedEventArgs e)
        {
            EmojiPickerPopup.IsOpen = false;
            // The emoji panel types into the focused text box, so give it an (invisible) one to type into
            EmojiCaptureBox.Text = string.Empty;
            EmojiCaptureBox.Focus();
            try
            {
                keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
                keybd_event(VK_OEM_PERIOD, 0, 0, UIntPtr.Zero);
                keybd_event(VK_OEM_PERIOD, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch { }
        }

        private void InitializeAccentPalette()
        {
            var colors = new (string hex, string name)[]
            {
                // Row 1: Reds & Crimson
                ("#EF4444", "Red"), ("#F87171", "Coral Red"), ("#DC2626", "Crimson"), ("#B91C1C", "Deep Red"), ("#991B1B", "Dark Ruby"),
                ("#E11D48", "Rose Red"), ("#F43F5E", "Watermelon"), ("#FB7185", "Blush Pink"), ("#BE123C", "Berry Red"), ("#881337", "Burgundy"),
                // Row 2: Oranges & Ambers
                ("#F97316", "Bright Orange"), ("#FB923C", "Peach Orange"), ("#EA580C", "Burnt Orange"), ("#C2410C", "Rust"), ("#9A3412", "Brick"),
                ("#F59E0B", "Amber"), ("#FBBF24", "Warm Gold"), ("#D97706", "Dark Amber"), ("#B45309", "Bronze"), ("#78350F", "Copper"),
                // Row 3: Yellows, Limes & Light Greens
                ("#EAB308", "Yellow"), ("#FACC15", "Sun Yellow"), ("#CA8A04", "Mustard"), ("#84CC16", "Lime"), ("#A3E635", "Bright Lime"),
                ("#65A30D", "Olive Lime"), ("#4D7C0F", "Avocado"), ("#22C55E", "Leaf Green"), ("#4ADE80", "Mint Green"), ("#16A34A", "Forest"),
                // Row 4: Emeralds, Teals & Cyans
                ("#10B981", "Emerald"), ("#34D399", "Seafoam"), ("#059669", "Dark Emerald"), ("#047857", "Pine"), ("#14B8A6", "Teal"),
                ("#2DD4BF", "Turquoise"), ("#0D9488", "Deep Teal"), ("#06B6D4", "Cyan"), ("#22D3EE", "Aqua Sky"), ("#0891B2", "Ocean Blue"),
                // Row 5: Blues & Indigos
                ("#0EA5E9", "Sky Blue"), ("#38BDF8", "Light Sky"), ("#0284C7", "Electric Blue"), ("#3A6DF0", "Classic Blue"), ("#60A5FA", "Ice Blue"),
                ("#2563EB", "Royal Blue"), ("#1D4ED8", "Dark Royal"), ("#6366F1", "Indigo"), ("#818CF8", "Light Indigo"), ("#4F46E5", "Deep Indigo"),
                // Row 6: Purples, Pinks & Neutrals
                ("#8B5CF6", "Neon Purple"), ("#A78BFA", "Soft Violet"), ("#7C3AED", "Deep Violet"), ("#A855F7", "Amethyst"), ("#C084FC", "Lavender"),
                ("#9333EA", "Dark Amethyst"), ("#D946EF", "Fuchsia"), ("#EC4899", "Hot Pink"), ("#F472B6", "Bubblegum"), ("#64748B", "Slate Gray")
            };

            AccentPalettePanel.Children.Clear();
            var bc = new BrushConverter();
            foreach (var c in colors)
            {
                var border = new Border
                {
                    Width = 20,
                    Height = 20,
                    CornerRadius = new CornerRadius(10),
                    Background = (Brush)bc.ConvertFromString(c.hex),
                    BorderBrush = (Brush)bc.ConvertFromString("#33FFFFFF"),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(3.5),
                    Cursor = Cursors.Hand,
                    ToolTip = $"{c.name} ({c.hex})"
                };

                string hexValue = c.hex;
                string nameValue = c.name;

                border.MouseEnter += (s, ev) =>
                {
                    HoveredColorName.Text = $"{nameValue} ({hexValue})";
                };
                border.MouseLeave += (s, ev) =>
                {
                    HoveredColorName.Text = string.Empty;
                };
                border.MouseLeftButtonDown += (s, ev) =>
                {
                    SelectAccentColor(hexValue, nameValue);
                    AccentPickerPopup.IsOpen = false;
                };

                AccentPalettePanel.Children.Add(border);
            }
        }

        private void EmojiCaptureBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string text = EmojiCaptureBox.Text.Trim();
            if (text.Length == 0) return;
            EmojiCaptureBox.Text = string.Empty;
            Keyboard.ClearFocus();
            ApplyGlyph(text);
        }

        private void AccentPickerBtn_Click(object sender, RoutedEventArgs e)
        {
            AccentPickerPopup.IsOpen = !AccentPickerPopup.IsOpen;
        }

        private void SelectAccentColor(string hex, string name)
        {
            if (_selectedGroup == null) return;
            _selectedGroup.AccentColorHex = hex;
            try
            {
                SelectedAccentSwatch.Background = (Brush)new BrushConverter().ConvertFromString(hex);
            }
            catch { }
            SelectedAccentHexText.Text = hex.ToUpperInvariant();
            SyncGroupAndTaskbarIcon(_selectedGroup);
        }

        /// <returns>false if the config could not be saved: callers must not delete files it stopped referencing.</returns>
        private bool SyncGroupAndTaskbarIcon(AppGroup? group)
        {
            if (group == null) return false;
            _dirtyGroups.Remove(group);
            bool saved = SaveOrReport();
            ShortcutService.UpdatePinnedTaskbarShortcutForGroup(group, _configService.AppDataDirectory);
            group.NotifyAppearanceChanged();
            RefreshLiveFolderIcon();
            return saved;
        }

        /// <summary>Selects the icon-style radio without firing StyleRadio_Checked (which would sync a second time).</summary>
        private void SetStyleRadio(FolderIconMode mode)
        {
            bool previous = _suppressEvents;
            _suppressEvents = true;
            if (mode == FolderIconMode.CustomIco) StyleCustomRadio.IsChecked = true;
            else if (mode == FolderIconMode.EmojiGlyph) StyleEmojiRadio.IsChecked = true;
            else StyleGridRadio.IsChecked = true;
            _suppressEvents = previous;
        }

        /// <summary>Applies a new cover image; the previous one is deleted only after the config was saved.</summary>
        private void ApplyCustomCover(string persisted)
        {
            if (_selectedGroup == null) return;
            _selectedGroup.CustomFolderIconPath = persisted;
            _selectedGroup.IconMode = FolderIconMode.CustomIco;
            SetStyleRadio(FolderIconMode.CustomIco);
            UpdateIconControlVisibility();
            if (SyncGroupAndTaskbarIcon(_selectedGroup))
            {
                ShortcutService.DeleteUnusedCustomIcons(_selectedGroup.Id, persisted, _configService.AppDataDirectory);
            }
        }

        private void RefreshItemsList()
        {
            if (_selectedGroup == null) return;
            _suppressEvents = true;

            GroupNameBox.Text = _selectedGroup.Name;
            SelectedEmojiDisplay.Text = string.IsNullOrWhiteSpace(_selectedGroup.IconGlyph) ? "📁" : _selectedGroup.IconGlyph;

            if (_selectedGroup.IconMode == FolderIconMode.CustomIco)
                StyleCustomRadio.IsChecked = true;
            else if (_selectedGroup.IconMode == FolderIconMode.EmojiGlyph)
                StyleEmojiRadio.IsChecked = true;
            else
                StyleGridRadio.IsChecked = true;

            UpdateIconControlVisibility();

            ShowLabelsCheck.IsChecked = _selectedGroup.ShowLabels;

            string hex = _selectedGroup.AccentColorHex ?? "#3A6DF0";
            try
            {
                SelectedAccentSwatch.Background = (Brush)new BrushConverter().ConvertFromString(hex);
            }
            catch
            {
                SelectedAccentSwatch.Background = (Brush)new BrushConverter().ConvertFromString("#3A6DF0");
            }
            SelectedAccentHexText.Text = hex.ToUpperInvariant();

            ItemsListBox.ItemsSource = _selectedGroup.Items.Select(i => new ManagerItemRowViewModel(() => TriggerDebounceSave())
            {
                Model = i,
                Icon = IconHelper.ExtractIcon(i)
            }).ToList();

            EmptyStateOverlay.Visibility = _selectedGroup.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshLiveFolderIcon();

            _suppressEvents = false;
        }

        private void TriggerDebounceSave()
        {
            if (_selectedGroup != null) _dirtyGroups.Add(_selectedGroup);
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
            if (string.IsNullOrWhiteSpace(GroupNameBox.Text))
            {
                // Keep the previous name until a real one is typed (an empty name breaks the flyout title and shortcut)
                StatusText.Text = "A stack needs a name.";
                return;
            }
            _selectedGroup.Name = GroupNameBox.Text;
            _selectedGroup.NotifyAppearanceChanged();
            TriggerDebounceSave();
        }

        private void StyleRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _selectedGroup == null) return;
            if (StyleCustomRadio.IsChecked == true)
                _selectedGroup.IconMode = FolderIconMode.CustomIco;
            else if (StyleEmojiRadio.IsChecked == true)
                _selectedGroup.IconMode = FolderIconMode.EmojiGlyph;
            else
                _selectedGroup.IconMode = FolderIconMode.MiniGrid2x2;

            UpdateIconControlVisibility();
            SyncGroupAndTaskbarIcon(_selectedGroup);
        }

        private void ChooseCustomIcon_Click(object sender, RoutedEventArgs e)
        {
            OpenCustomIconPicker();
        }

        private void LiveFolderIconPreview_Click(object sender, MouseButtonEventArgs e)
        {
            OpenCustomIconPicker();
        }

        private void OpenCustomIconPicker()
        {
            if (_selectedGroup == null) return;

            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose Custom Folder Icon or Image",
                Filter = "Icons & Images (*.ico;*.png;*.jpg;*.exe;*.dll;*.lnk)|*.ico;*.png;*.jpg;*.jpeg;*.exe;*.dll;*.lnk|Icons (*.ico)|*.ico|Images (*.png;*.jpg)|*.png;*.jpg;*.jpeg|Executables (*.exe;*.dll)|*.exe;*.dll|All Files (*.*)|*.*"
            };

            if (ofd.ShowDialog() == true)
            {
                string? persisted = ShortcutService.PersistCustomFolderIcon(ofd.FileName, _selectedGroup.Id, _configService.AppDataDirectory);
                if (!string.IsNullOrEmpty(persisted))
                {
                    ApplyCustomCover(persisted!);
                    StatusText.Text = $"Applied custom icon from '{Path.GetFileName(ofd.FileName)}'.";
                }
            }
        }

        private void UseFirstAppIcon_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGroup == null || _selectedGroup.Items.Count == 0)
            {
                StatusText.Text = "Add at least one app to the stack first.";
                return;
            }

            var first = _selectedGroup.Items[0];
            string? persisted = ShortcutService.PersistCustomFolderIcon(first, _selectedGroup.Id, _configService.AppDataDirectory);
            if (!string.IsNullOrEmpty(persisted))
            {
                ApplyCustomCover(persisted!);
                StatusText.Text = $"Using '{first.Name}' as folder cover icon.";
            }
        }

        private void ClearCustomIcon_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGroup == null) return;
            _selectedGroup.CustomFolderIconPath = null;
            _selectedGroup.IconMode = FolderIconMode.MiniGrid2x2;
            SetStyleRadio(FolderIconMode.MiniGrid2x2);
            UpdateIconControlVisibility();
            if (SyncGroupAndTaskbarIcon(_selectedGroup))
            {
                ShortcutService.DeleteUnusedCustomIcons(_selectedGroup.Id, null, _configService.AppDataDirectory);
            }
            StatusText.Text = "Reverted to 2x2 grid icon.";
        }

        private void EmojiPreset_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGroup == null || !(sender is FrameworkElement fe) || !(fe.Tag is string glyph)) return;
            SelectedEmojiDisplay.Text = glyph;
            _selectedGroup.IconGlyph = glyph;
            _selectedGroup.IconMode = FolderIconMode.EmojiGlyph;
            SetStyleRadio(FolderIconMode.EmojiGlyph);
            UpdateIconControlVisibility();
            SyncGroupAndTaskbarIcon(_selectedGroup);
        }

        private void SetAsFolderIcon_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGroup == null || !(sender is FrameworkElement fe) || !(fe.Tag is ManagerItemRowViewModel row)) return;
            string? persisted = ShortcutService.PersistCustomFolderIcon(row.Model, _selectedGroup.Id, _configService.AppDataDirectory);
            if (!string.IsNullOrEmpty(persisted))
            {
                ApplyCustomCover(persisted!);
                StatusText.Text = $"Set '{row.Model.Name}' icon as the cover for '{_selectedGroup.Name}'.";
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
            if (!StartupService.SetRunAtStartup(enabled))
            {
                StatusText.Text = "Could not change the Windows startup setting (see the log for details).";
                _suppressEvents = true;
                RunAtStartupCheck.IsChecked = StartupService.GetState() == StartupService.StartupState.On;
                _suppressEvents = false;
                return;
            }
            StatusText.Text = enabled
                ? "StackBar will start automatically in your system tray on login."
                : "Removed StackBar from Windows startup.";
        }

        private void RepairShortcutsCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _config.RepairAppShortcuts = RepairShortcutsCheck.IsChecked == true;
            SaveOrReport();
            if (_config.RepairAppShortcuts) ShortcutRepairService.RefreshIndexInBackground();
            StatusText.Text = _config.RepairAppShortcuts
                ? "Outdated app shortcuts will be refreshed before launching (prevents blank taskbar icons)."
                : "StackBar will no longer touch other apps' shortcuts (its own copies are still kept current).";
        }

        private async void RepairShortcutsButton_Click(object sender, RoutedEventArgs e)
        {
            RepairShortcutsButton.IsEnabled = false;
            StatusText.Text = "Checking app shortcuts...";
            try
            {
                string appDataDir = _configService.AppDataDirectory;
                // Copied on the UI thread: Studio stays editable while the repair runs
                var itemPaths = _config.Groups.SelectMany(g => g.Items).Select(i => i.TargetPath).ToList();
                var report = await System.Threading.Tasks.Task.Run(() => ShortcutRepairService.RepairAll(itemPaths, appDataDir));

                var message = new System.Text.StringBuilder();
                message.AppendLine($"Checked {report.Checked} shortcut(s).");
                message.AppendLine(report.Repaired.Count == 0
                    ? "No outdated shortcuts found."
                    : $"Refreshed {report.Repaired.Count} outdated shortcut(s):");
                foreach (var path in report.Repaired) message.AppendLine("  • " + Path.GetFileNameWithoutExtension(path));
                if (report.Skipped.Count > 0)
                {
                    message.AppendLine();
                    message.AppendLine($"Skipped {report.Skipped.Count} outdated shortcut(s) StackBar can't change (installed for all users; they need administrator rights).");
                }
                if (report.Failed.Count > 0)
                {
                    message.AppendLine();
                    message.AppendLine($"Could not refresh {report.Failed.Count} shortcut(s); details are in the log.");
                }
                if (report.Repaired.Count > 0)
                {
                    message.AppendLine();
                    message.AppendLine("Backups: " + ShortcutRepairService.BackupDirectory(appDataDir));
                    message.AppendLine("An app that is already open may need to be closed and reopened to show the fixed icon.");
                }

                StatusText.Text = $"Shortcut repair: {report.Repaired.Count} refreshed, {report.Skipped.Count} skipped, {report.Failed.Count} failed.";
                MessageBox.Show(this, message.ToString(), "Repair App Shortcuts", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.Log("Shortcut repair failed: " + ex);
                StatusText.Text = "Shortcut repair failed: " + ex.Message;
            }
            finally
            {
                RepairShortcutsButton.IsEnabled = true;
            }
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

            var res = MessageBox.Show(
                this,
                $"Are you sure you want to delete the '{_selectedGroup.Name}' stack?\n\nThis permanently deletes the folder configuration and tries to unpin it from your Windows taskbar.",
                "Delete Stack Confirmation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res != MessageBoxResult.Yes) return;

            var deleted = _selectedGroup;
            bool unpinned = ShortcutService.DeleteGroupShortcuts(deleted.Id, _configService.AppDataDirectory);

            _dirtyGroups.Remove(deleted);
            _config.Groups.Remove(deleted);
            _selectedGroup = _config.Groups.FirstOrDefault();
            if (SaveOrReport())
            {
                foreach (var item in deleted.Items)
                {
                    ShortcutService.DeletePersistedLinkIfUnused(item, _config, _configService.AppDataDirectory);
                }
            }
            RefreshGroupsList();
            if (_selectedGroup != null)
                GroupsListBox.SelectedItem = _selectedGroup;

            StatusText.Text = unpinned
                ? $"Deleted '{deleted.Name}'."
                : $"Deleted '{deleted.Name}'. Windows didn't allow unpinning it automatically: right-click its taskbar icon and choose 'Unpin from taskbar'.";
        }

        private void PreviewFlyout_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGroup == null) return;
            _configService.Save(_config);
            FlushPendingSave();
            var flyout = new FlyoutWindow(_selectedGroup, _config, _configService, onEditRequested: _ => Activate());
            flyout.Show();
        }

        private void CreateShortcut_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGroup == null) return;
            _configService.Save(_config);

            try
            {
                string name = _selectedGroup.Name.Trim();
                bool nameIsShared = _config.Groups.Count(g => string.Equals(g.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)) > 1;
                string lnkPath = ShortcutService.CreateGroupShortcut(_selectedGroup, _configService.AppDataDirectory, nameIsShared);
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
                int added = 0;
                var duplicates = new ShortcutService.DuplicateIndex(_selectedGroup);
                foreach (var item in dialog.SelectedItems)
                {
                    if (TryAddItem(item.TargetPath, item.Name, duplicates)) added++;
                }
                SyncGroupAndTaskbarIcon(_selectedGroup);
                RefreshItemsList();
                int skipped = dialog.SelectedItems.Count - added;
                StatusText.Text = skipped > 0
                    ? $"Imported {added} app(s) into '{_selectedGroup.Name}' ({skipped} already in the stack)."
                    : $"Imported {added} app(s) into '{_selectedGroup.Name}'!";
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
                var duplicates = new ShortcutService.DuplicateIndex(_selectedGroup);
                foreach (var file in ofd.FileNames)
                {
                    TryAddItem(file, null, duplicates);
                }
                SyncGroupAndTaskbarIcon(_selectedGroup);
                RefreshItemsList();
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
                SyncGroupAndTaskbarIcon(_selectedGroup);
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
                SyncGroupAndTaskbarIcon(_selectedGroup);
                RefreshItemsList();
            }
        }

        private void RemoveRowItem_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGroup == null || !(sender is FrameworkElement fe) || !(fe.Tag is ManagerItemRowViewModel row)) return;
            _selectedGroup.Items.Remove(row.Model);
            // The copied shortcut is deleted only once the config without the item is on disk
            if (SyncGroupAndTaskbarIcon(_selectedGroup))
            {
                ShortcutService.DeletePersistedLinkIfUnused(row.Model, _config, _configService.AppDataDirectory);
            }
            RefreshItemsList();
        }

        /// <summary>
        /// Imports a file/shortcut/app into the selected group unless it is already there.
        /// The duplicate check runs before the import, which copies shortcuts under a new unique name.
        /// </summary>
        private bool TryAddItem(string sourcePath, string? displayName, ShortcutService.DuplicateIndex duplicates)
        {
            if (_selectedGroup == null) return false;
            if (duplicates.Contains(sourcePath, displayName)) return false;

            var persistent = ShortcutService.ImportAndPersistShortcut(sourcePath, _configService.AppDataDirectory, displayName);
            _selectedGroup.Items.Add(persistent);
            duplicates.Add(persistent);
            return true;
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
                    var targetItem = GetItemUnderMouse(e.GetPosition(ItemsListBox));
                    if (targetItem != null && targetItem != dragged)
                    {
                        int oldIdx = _selectedGroup.Items.IndexOf(dragged.Model);
                        int newIdx = _selectedGroup.Items.IndexOf(targetItem.Model);
                        if (oldIdx >= 0 && newIdx >= 0)
                        {
                            _selectedGroup.Items.RemoveAt(oldIdx);
                            _selectedGroup.Items.Insert(newIdx, dragged.Model);
                            SyncGroupAndTaskbarIcon(_selectedGroup);
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
                var duplicates = new ShortcutService.DuplicateIndex(_selectedGroup);
                foreach (var file in files)
                {
                    if (TryAddItem(file, null, duplicates)) count++;
                }
                SyncGroupAndTaskbarIcon(_selectedGroup);
                RefreshItemsList();
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

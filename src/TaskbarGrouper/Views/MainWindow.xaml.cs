using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TaskbarGrouper.Models;
using TaskbarGrouper.Services;
using GroupItemModel = TaskbarGrouper.Models.GroupItem;

namespace TaskbarGrouper.Views;

public class ManagerItemRowViewModel
{
    private readonly Action _onEdited;
    public GroupItemModel Model { get; init; } = null!;
    public ImageSource? Icon { get; init; }

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

    private static readonly string[] AccentHexCodes =
    {
        "#3A6DF0", "#8B5CF6", "#10B981", "#F59E0B", "#EF4444", "#64748B"
    };

    public MainWindow(AppConfig config, ConfigService configService)
    {
        InitializeComponent();
        _config = config;
        _configService = configService;

        _suppressEvents = true;
        RunAtStartupCheck.IsChecked = StartupService.IsRunAtStartupEnabled();
        _suppressEvents = false;

        RefreshGroupsList();
        if (_config.Groups.Count > 0)
            GroupsListBox.SelectedIndex = 0;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        NativePositioningService.ApplyWindows11AcrylicEffects(this);
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
        IconModeCombo.SelectedIndex = _selectedGroup.IconMode == FolderIconMode.EmojiGlyph ? 1 : 0;
        ColumnsCombo.SelectedIndex = Math.Clamp(_selectedGroup.Columns - 3, 0, 3);
        ShowLabelsCheck.IsChecked = _selectedGroup.ShowLabels;

        int accentIdx = Array.FindIndex(AccentHexCodes, c => c.Equals(_selectedGroup.AccentColorHex, StringComparison.OrdinalIgnoreCase));
        AccentColorCombo.SelectedIndex = accentIdx >= 0 ? accentIdx : 0;

        ItemsListBox.ItemsSource = _selectedGroup.Items.Select(i => new ManagerItemRowViewModel(() => _configService.Save(_config))
        {
            Model = i,
            Icon = IconHelper.ExtractIcon(i.TargetPath, i.CustomIconPath)
        }).ToList();

        EmptyStateOverlay.Visibility = _selectedGroup.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshLiveFolderIcon();

        _suppressEvents = false;
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
        _configService.Save(_config);
        RefreshLiveFolderIcon();
        RefreshGroupsList();
    }

    private void IconModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _selectedGroup == null) return;
        _selectedGroup.IconMode = IconModeCombo.SelectedIndex == 1
            ? FolderIconMode.EmojiGlyph
            : FolderIconMode.MiniGrid2x2;
        _configService.Save(_config);
        RefreshLiveFolderIcon();
    }

    private void AccentColorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _selectedGroup == null) return;
        if (AccentColorCombo.SelectedIndex >= 0 && AccentColorCombo.SelectedIndex < AccentHexCodes.Length)
        {
            _selectedGroup.AccentColorHex = AccentHexCodes[AccentColorCombo.SelectedIndex];
            _configService.Save(_config);
            RefreshLiveFolderIcon();
        }
    }

    private void ColumnsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _selectedGroup == null) return;
        if (ColumnsCombo.SelectedIndex >= 0)
        {
            _selectedGroup.Columns = ColumnsCombo.SelectedIndex + 3;
            _configService.Save(_config);
        }
    }

    private void ShowLabelsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _selectedGroup == null) return;
        _selectedGroup.ShowLabels = ShowLabelsCheck.IsChecked == true;
        _configService.Save(_config);
    }

    private void RunAtStartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        bool enabled = RunAtStartupCheck.IsChecked == true;
        StartupService.SetRunAtStartup(enabled);
        StatusText.Text = enabled
            ? "StackBar will now start automatically in your system tray when Windows logs in."
            : "Removed StackBar from Windows startup.";
    }

    private void AddGroup_Click(object sender, RoutedEventArgs e)
    {
        var newGroup = new AppGroup
        {
            Id = $"stack-{Guid.NewGuid().ToString("N")[..6]}",
            Name = $"Stack {_config.Groups.Count + 1}",
            IconGlyph = "🗂️",
            AccentColorHex = AccentHexCodes[_config.Groups.Count % AccentHexCodes.Length],
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
        var flyout = new FlyoutWindow(_selectedGroup, onEditRequested: () => _ = Activate());
        flyout.Show();
    }

    private void CreateShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGroup == null) return;
        _configService.Save(_config);

        try
        {
            string appDataDir = Path.GetDirectoryName(_configService.ConfigPath)!;
            string lnkPath = ShortcutService.CreateGroupShortcut(_selectedGroup, appDataDir);

            Process.Start("explorer.exe", $"/select,\"{lnkPath}\"");
            StatusText.Text = $"Generated '{Path.GetFileName(lnkPath)}'! Right-click it in Explorer -> 'Pin to taskbar'.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to create shortcut:\n{ex.Message}", "StackBar", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportTaskbar_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGroup == null) return;

        try
        {
            var discovered = ShortcutService.ScanPinnedTaskbarShortcuts();
            if (discovered.Count == 0)
            {
                System.Windows.MessageBox.Show(
                    "No pinned `.lnk` shortcuts were found in the standard Windows Quick Launch TaskBar folder.\nYou can still drag & drop any shortcuts or .exe files directly into the list!",
                    "StackBar Importer", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new TaskbarImporterWindow(discovered) { Owner = IsLoaded ? this : null };
            if (dialog.ShowDialog() == true && dialog.SelectedItems.Count > 0)
            {
                foreach (var item in dialog.SelectedItems)
                {
                    if (!_selectedGroup.Items.Any(existing => existing.TargetPath.Equals(item.TargetPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        _selectedGroup.Items.Add(item);
                    }
                }
                _configService.Save(_config);
                RefreshItemsList();
                RefreshGroupsList();
                StatusText.Text = $"Imported {dialog.SelectedItems.Count} app(s) into '{_selectedGroup.Name}'!";
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Could not open Taskbar Importer:\n{ex.Message}", "StackBar Importer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddAppFile_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGroup == null) return;

        var ofd = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Applications or Shortcuts to Add",
            Filter = "Apps & Shortcuts (*.exe;*.lnk;*.url;*.bat)|*.exe;*.lnk;*.url;*.bat|All Files (*.*)|*.*",
            Multiselect = true
        };

        if (ofd.ShowDialog() == true)
        {
            foreach (var file in ofd.FileNames)
            {
                var parsed = ShortcutService.ParseShortcut(file);
                if (parsed != null)
                    _selectedGroup.Items.Add(parsed);
            }
            _configService.Save(_config);
            RefreshItemsList();
            RefreshGroupsList();
        }
    }

    private void MoveItemUp_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGroup == null || sender is not FrameworkElement { Tag: ManagerItemRowViewModel row }) return;
        int idx = _selectedGroup.Items.IndexOf(row.Model);
        if (idx > 0)
        {
            (_selectedGroup.Items[idx - 1], _selectedGroup.Items[idx]) = (_selectedGroup.Items[idx], _selectedGroup.Items[idx - 1]);
            _configService.Save(_config);
            RefreshItemsList();
        }
    }

    private void MoveItemDown_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGroup == null || sender is not FrameworkElement { Tag: ManagerItemRowViewModel row }) return;
        int idx = _selectedGroup.Items.IndexOf(row.Model);
        if (idx >= 0 && idx < _selectedGroup.Items.Count - 1)
        {
            (_selectedGroup.Items[idx + 1], _selectedGroup.Items[idx]) = (_selectedGroup.Items[idx], _selectedGroup.Items[idx + 1]);
            _configService.Save(_config);
            RefreshItemsList();
        }
    }

    private void RemoveRowItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGroup == null || sender is not FrameworkElement { Tag: ManagerItemRowViewModel row }) return;
        _selectedGroup.Items.Remove(row.Model);
        _configService.Save(_config);
        RefreshItemsList();
        RefreshGroupsList();
    }

    private void ItemsListBox_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (_selectedGroup == null) return;
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            var files = (string[]?)e.Data.GetData(System.Windows.DataFormats.FileDrop);
            if (files == null) return;

            foreach (var file in files)
            {
                var parsed = ShortcutService.ParseShortcut(file);
                if (parsed != null)
                    _selectedGroup.Items.Add(parsed);
            }
            _configService.Save(_config);
            RefreshItemsList();
            RefreshGroupsList();
            StatusText.Text = $"Added {files.Length} item(s) to '{_selectedGroup.Name}'.";
        }
    }
}

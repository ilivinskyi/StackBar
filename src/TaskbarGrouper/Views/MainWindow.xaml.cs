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
    public GroupItemModel Model { get; init; } = null!;
    public string Name => Model.Name;
    public string TargetPath => Model.TargetPath;
    public ImageSource? Icon { get; init; }
}

public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private readonly ConfigService _configService;
    private AppGroup? _selectedGroup;
    private bool _suppressEvents;

    public MainWindow(AppConfig config, ConfigService configService)
    {
        InitializeComponent();
        _config = config;
        _configService = configService;

        RefreshGroupsList();
        if (_config.Groups.Count > 0)
            GroupsListBox.SelectedIndex = 0;
    }

    private void RefreshGroupsList()
    {
        var current = _selectedGroup;
        GroupsListBox.ItemsSource = null;
        GroupsListBox.ItemsSource = _config.Groups;
        if (current != null && _config.Groups.Contains(current))
            GroupsListBox.SelectedItem = current;
    }

    private void RefreshItemsList()
    {
        if (_selectedGroup == null) return;
        _suppressEvents = true;

        SelectedGroupHeader.Text = $"{_selectedGroup.IconGlyph}  {_selectedGroup.Name}";
        GroupNameBox.Text = _selectedGroup.Name;
        GroupGlyphBox.Text = _selectedGroup.IconGlyph;
        IconModeCombo.SelectedIndex = _selectedGroup.IconMode == FolderIconMode.EmojiGlyph ? 1 : 0;
        ColumnsCombo.SelectedIndex = Math.Clamp(_selectedGroup.Columns - 3, 0, 3);

        ItemsListBox.ItemsSource = _selectedGroup.Items.Select(i => new ManagerItemRowViewModel
        {
            Model = i,
            Icon = IconHelper.ExtractIcon(i.TargetPath, i.CustomIconPath)
        }).ToList();

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
        SelectedGroupHeader.Text = $"{_selectedGroup.IconGlyph}  {_selectedGroup.Name}";
        _configService.Save(_config);
        RefreshGroupsList();
    }

    private void IconModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _selectedGroup == null) return;
        _selectedGroup.IconMode = IconModeCombo.SelectedIndex == 1
            ? FolderIconMode.EmojiGlyph
            : FolderIconMode.MiniGrid2x2;
        _configService.Save(_config);
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

    private void AddGroup_Click(object sender, RoutedEventArgs e)
    {
        var newGroup = new AppGroup
        {
            Id = $"stack-{Guid.NewGuid().ToString("N")[..6]}",
            Name = $"Stack {_config.Groups.Count + 1}",
            IconGlyph = "🗂️",
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
        var flyout = new FlyoutWindow(_selectedGroup);
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

            // Reveal the generated shortcut in Explorer so the user can right-click -> "Pin to taskbar" or drag it
            Process.Start("explorer.exe", $"/select,\"{lnkPath}\"");
            StatusText.Text = $"Created '{Path.GetFileName(lnkPath)}'! Right-click it in Explorer -> 'Show more options' -> 'Pin to taskbar'.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to create shortcut:\n{ex.Message}", "StackBar", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportTaskbar_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGroup == null) return;

        var discovered = ShortcutService.ScanPinnedTaskbarShortcuts();
        if (discovered.Count == 0)
        {
            System.Windows.MessageBox.Show(
                "No pinned `.lnk` shortcuts were found in the standard Windows Quick Launch TaskBar folder.\nYou can still drag & drop any shortcuts or .exe files directly into the list!",
                "StackBar Importer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new TaskbarImporterWindow(discovered) { Owner = this };
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
            StatusText.Text = $"Imported {dialog.SelectedItems.Count} app(s) from your taskbar!";
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

    private void RemoveSelectedApp_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGroup == null) return;
        if (ItemsListBox.SelectedItem is ManagerItemRowViewModel row)
        {
            _selectedGroup.Items.Remove(row.Model);
            _configService.Save(_config);
            RefreshItemsList();
            RefreshGroupsList();
        }
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

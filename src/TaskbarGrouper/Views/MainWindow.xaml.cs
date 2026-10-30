using System.IO;
using System.Windows;
using System.Windows.Controls;
using TaskbarGrouper.Models;
using TaskbarGrouper.Services;

namespace TaskbarGrouper.Views;

public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private readonly ConfigService _configService;
    private AppGroup? _selectedGroup;

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
        GroupsListBox.ItemsSource = null;
        GroupsListBox.ItemsSource = _config.Groups;
    }

    private void RefreshItemsList()
    {
        if (_selectedGroup == null) return;
        SelectedGroupHeader.Text = $"{_selectedGroup.IconGlyph}  {_selectedGroup.Name} ({_selectedGroup.Id})";
        ItemsListBox.ItemsSource = null;
        ItemsListBox.ItemsSource = _selectedGroup.Items;
    }

    private void GroupsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupsListBox.SelectedItem is AppGroup group)
        {
            _selectedGroup = group;
            RefreshItemsList();
        }
    }

    private void AddGroup_Click(object sender, RoutedEventArgs e)
    {
        var newGroup = new AppGroup
        {
            Id = $"group-{_config.Groups.Count + 1}",
            Name = $"Group {_config.Groups.Count + 1}",
            IconGlyph = "📁"
        };
        _config.Groups.Add(newGroup);
        RefreshGroupsList();
        GroupsListBox.SelectedItem = newGroup;
    }

    private void PreviewFlyout_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGroup == null) return;
        var flyout = new FlyoutWindow(_selectedGroup);
        flyout.Show();
    }

    private void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        _configService.Save(_config);
        StatusText.Text = $"Saved to {_configService.ConfigPath} at {DateTime.Now:HH:mm:ss}";
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
                _selectedGroup.Items.Add(new Models.GroupItem
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    TargetPath = file
                });
            }
            RefreshItemsList();
        }
    }
}

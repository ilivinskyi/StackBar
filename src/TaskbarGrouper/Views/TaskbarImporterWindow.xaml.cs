using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TaskbarGrouper.Services;
using GroupItemModel = TaskbarGrouper.Models.GroupItem;

namespace TaskbarGrouper.Views;

public class SelectableShortcutViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    public GroupItemModel Item { get; init; } = null!;
    public string SourceBadge { get; init; } = string.Empty;
    public ImageSource? Icon { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class TaskbarImporterWindow : Window
{
    private readonly List<SelectableShortcutViewModel> _allItems;
    private List<SelectableShortcutViewModel> _filteredItems;
    public List<GroupItemModel> SelectedItems { get; private set; } = new();

    public TaskbarImporterWindow(List<DiscoveredAppItem> discoveredApps)
    {
        InitializeComponent();
        _allItems = discoveredApps.Select(d => new SelectableShortcutViewModel
        {
            Item = d.Item,
            SourceBadge = d.SourceBadge,
            Icon = IconHelper.ExtractIcon(d.Item.TargetPath, d.Item.CustomIconPath),
            IsSelected = false
        }).ToList();

        _filteredItems = _allItems;
        ShortcutsList.ItemsSource = _filteredItems;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string q = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(q))
        {
            _filteredItems = _allItems;
        }
        else
        {
            _filteredItems = _allItems
                .Where(x => x.Item.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                            x.SourceBadge.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        ShortcutsList.ItemsSource = _filteredItems;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        bool anyUnchecked = _filteredItems.Any(x => !x.IsSelected);
        foreach (var item in _filteredItems)
            item.IsSelected = anyUnchecked;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        SelectedItems = _allItems.Where(x => x.IsSelected).Select(x => x.Item).ToList();
        DialogResult = true;
        Close();
    }
}

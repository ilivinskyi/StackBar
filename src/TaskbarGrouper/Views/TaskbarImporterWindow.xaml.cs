using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using TaskbarGrouper.Services;
using GroupItemModel = TaskbarGrouper.Models.GroupItem;

namespace TaskbarGrouper.Views;

public class SelectableShortcutViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    public GroupItemModel Item { get; init; } = null!;
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
    private readonly List<SelectableShortcutViewModel> _items;
    public List<GroupItemModel> SelectedItems { get; private set; } = new();

    public TaskbarImporterWindow(List<GroupItemModel> discoveredShortcuts)
    {
        InitializeComponent();
        _items = discoveredShortcuts.Select(s => new SelectableShortcutViewModel
        {
            Item = s,
            Icon = IconHelper.ExtractIcon(s.TargetPath, s.CustomIconPath),
            IsSelected = false
        }).ToList();

        ShortcutsList.ItemsSource = _items;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        bool anyUnchecked = _items.Any(x => !x.IsSelected);
        foreach (var item in _items)
            item.IsSelected = anyUnchecked;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        SelectedItems = _items.Where(x => x.IsSelected).Select(x => x.Item).ToList();
        DialogResult = true;
        Close();
    }
}

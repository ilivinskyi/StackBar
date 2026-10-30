using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TaskbarGrouper.Services;
using GroupItemModel = TaskbarGrouper.Models.GroupItem;

namespace TaskbarGrouper.Views
{
    public class SelectableShortcutViewModel : INotifyPropertyChanged
    {
        private bool _isSelected;
        public GroupItemModel Item { get; set; } = null!;
        public string SourceBadge { get; set; } = string.Empty;
        public ImageSource? Icon { get; set; }

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
        private List<SelectableShortcutViewModel> _allItems = new List<SelectableShortcutViewModel>();
        private List<SelectableShortcutViewModel> _filteredItems = new List<SelectableShortcutViewModel>();
        public List<GroupItemModel> SelectedItems { get; private set; } = new List<GroupItemModel>();

        public TaskbarImporterWindow()
        {
            InitializeComponent();
            Loaded += TaskbarImporterWindow_Loaded;
        }

        private async void TaskbarImporterWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadingOverlay.Visibility = Visibility.Visible;

            var discovered = await Task.Run(() => ShortcutService.ScanAllTaskbarAndSystemApps());

            var vms = new List<SelectableShortcutViewModel>();
            foreach (var d in discovered)
            {
                var icon = IconHelper.ExtractIcon(d.Item.TargetPath, d.Item.CustomIconPath);
                vms.Add(new SelectableShortcutViewModel
                {
                    Item = d.Item,
                    SourceBadge = d.SourceBadge,
                    Icon = icon,
                    IsSelected = false
                });
            }

            _allItems = vms;
            _filteredItems = _allItems;
            ShortcutsList.ItemsSource = _filteredItems;
            LoadingOverlay.Visibility = Visibility.Collapsed;
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
                    .Where(x => x.Item.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                x.SourceBadge.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
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
}

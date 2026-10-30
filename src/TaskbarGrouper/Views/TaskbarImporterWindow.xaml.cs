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

            // Scanning and icon extraction (hundreds of apps) both run off the UI thread;
            // the icons are frozen BitmapImages, so they can be handed to the UI thread afterwards.
            var vms = await Task.Run(() =>
                ShortcutService.ScanAllTaskbarAndSystemApps()
                    .Select(d => new SelectableShortcutViewModel
                    {
                        Item = d.Item,
                        SourceBadge = d.SourceBadge,
                        Icon = IconHelper.ExtractIcon(d.Item),
                        IsSelected = false
                    })
                    .ToList());

            if (!IsLoaded) return; // closed while scanning

            _allItems = vms;
            _filteredItems = _allItems;
            ShortcutsList.ItemsSource = _filteredItems;
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }

        private static string NormalizeForSearch(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new System.Text.StringBuilder(s.Length);
            char last = '\0';
            foreach (char c in s.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                {
                    if (c != last)
                    {
                        sb.Append(c);
                        last = c;
                    }
                }
            }
            return sb.ToString();
        }

        private static bool MatchesSearch(SelectableShortcutViewModel vm, string query, string normQuery)
        {
            string name = vm.Item.Name ?? string.Empty;
            string target = vm.Item.TargetPath ?? string.Empty;
            string badge = vm.SourceBadge ?? string.Empty;

            if (name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                target.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                badge.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(normQuery))
            {
                string normName = NormalizeForSearch(name);
                if (normName.IndexOf(normQuery, StringComparison.OrdinalIgnoreCase) >= 0) return true;

                string normTarget = NormalizeForSearch(target);
                if (normTarget.IndexOf(normQuery, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }

            return false;
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
                string normQ = NormalizeForSearch(q);
                _filteredItems = _allItems
                    .Where(x => MatchesSearch(x, q, normQ))
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

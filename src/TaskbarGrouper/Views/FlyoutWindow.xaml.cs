using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TaskbarGrouper.Models;
using TaskbarGrouper.Services;
using StackBar.Common;

namespace TaskbarGrouper.Views
{
    public class FlyoutItemViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(prop));

        public string Name { get; set; } = string.Empty;
        public string ToolTipText { get; set; } = string.Empty;
        public string HotkeyHint { get; set; } = string.Empty;
        public double TileHeight { get; set; } = 82;
        public Visibility LabelVisibility { get; set; } = Visibility.Visible;
        public Visibility AdminOptionVisibility { get; set; } = Visibility.Visible;
        public Visibility FileLocationVisibility { get; set; } = Visibility.Visible;
        public TaskbarGrouper.Models.GroupItem Item { get; set; } = null!;
        public ImageSource? Icon { get; set; }

        private bool _isJiggling;
        public bool IsJiggling
        {
            get => _isJiggling;
            set
            {
                if (_isJiggling != value)
                {
                    _isJiggling = value;
                    OnPropertyChanged(nameof(IsJiggling));
                    OnPropertyChanged(nameof(DeleteBadgeVisibility));
                }
            }
        }

        public Visibility DeleteBadgeVisibility => IsJiggling ? Visibility.Visible : Visibility.Collapsed;
    }

    public partial class FlyoutWindow : Window
    {
        private readonly AppGroup _group;
        private readonly int? _cursorX;
        private readonly int? _cursorY;
        private readonly Action<string>? _onEditRequested;
        private readonly List<FlyoutItemViewModel> _viewModels = new List<FlyoutItemViewModel>();
        private List<TaskbarGrouper.Models.GroupItem> _currentItems = new List<TaskbarGrouper.Models.GroupItem>();
        private bool _isClosing;
        private bool _isJiggleMode;
        private System.Windows.Threading.DispatcherTimer? _longPressTimer;
        private Point _longPressStartPos;

        public int Columns => Polyfills.Clamp(_group.Columns, 2, 8);

        public FlyoutWindow(AppGroup group, int? cursorX = null, int? cursorY = null, Action<string>? onEditRequested = null)
        {
            InitializeComponent();
            _group = group;
            _cursorX = cursorX;
            _cursorY = cursorY;
            _onEditRequested = onEditRequested;
            DataContext = this;

            GroupTitleText.Text = $"{_group.IconGlyph}  {_group.Name}";
            ItemCountBadge.Text = $"{_group.Items.Count}";

            ApplyAccentTheme(_group.AccentColorHex);

            PopulateViewModels(_group.Items);
        }

        private void PopulateViewModels(List<TaskbarGrouper.Models.GroupItem> items)
        {
            _currentItems = items ?? new List<TaskbarGrouper.Models.GroupItem>();
            _viewModels.Clear();
            if (items == null || items.Count == 0)
            {
                EmptyNotice.Visibility = Visibility.Visible;
            }
            else
            {
                EmptyNotice.Visibility = Visibility.Collapsed;
                for (int idx = 0; idx < items.Count; idx++)
                {
                    var item = items[idx];
                    bool isShell = item.Kind == ItemKind.ShellApp;
                    bool isFolder = item.Kind == ItemKind.Folder || Directory.Exists(item.TargetPath);

                    _viewModels.Add(new FlyoutItemViewModel
                    {
                        Name = item.Name,
                        ToolTipText = idx < 9 ? $"{item.Name} (Press {idx + 1})" : item.Name,
                        HotkeyHint = idx < 9 ? $"{idx + 1}" : string.Empty,
                        TileHeight = _group.ShowLabels ? 82 : 62,
                        LabelVisibility = _group.ShowLabels ? Visibility.Visible : Visibility.Collapsed,
                        AdminOptionVisibility = (isShell || isFolder) ? Visibility.Collapsed : Visibility.Visible,
                        FileLocationVisibility = isShell ? Visibility.Collapsed : Visibility.Visible,
                        Item = item,
                        Icon = IconHelper.ExtractIcon(item.TargetPath, item.CustomIconPath)
                    });
                }
            }

            ItemsGrid.ItemsSource = null;
            ItemsGrid.ItemsSource = _viewModels;
        }

        private void ApplyAccentTheme(string? hex)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(hex)) return;
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                AccentDot.Background = new SolidColorBrush(color);
                BorderAccentStop.Color = System.Windows.Media.Color.FromArgb(130, color.R, color.G, color.B);
                TopGradStop.Color = System.Windows.Media.Color.FromArgb(
                    244,
                    (byte)Math.Min(255, 24 + color.R / 7),
                    (byte)Math.Min(255, 26 + color.G / 7),
                    (byte)Math.Min(255, 36 + color.B / 7));
            }
            catch { }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            bool isTaskbarTop;
            double caretOffsetDip = NativePositioningService.PositionNearTaskbarCursorWithCaret(this, _cursorX, _cursorY, out isTaskbarTop);

            // Subtract Grid Margin (14 DIPs) and polygon half-width (8 DIPs) so arrow tip aligns exactly with cursor click
            double caretCanvasX = caretOffsetDip - 14 - 8;
            double cardWidth = RootBorder.ActualWidth > 0 ? RootBorder.ActualWidth : (ActualWidth - 28);
            double clampedX = Polyfills.Clamp(caretCanvasX, 16, Math.Max(20, cardWidth - 32));

            if (isTaskbarTop)
            {
                TopCaretCanvas.Visibility = Visibility.Visible;
                BottomCaretCanvas.Visibility = Visibility.Collapsed;
                Canvas.SetLeft(TopCaretPolygon, clampedX);
            }
            else
            {
                TopCaretCanvas.Visibility = Visibility.Collapsed;
                BottomCaretCanvas.Visibility = Visibility.Visible;
                Canvas.SetLeft(BottomCaretPolygon, clampedX);
            }

            Activate();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                IpcService.SetForegroundWindow(hwnd);
            }

            PlayEntranceAnimation();
        }

        private void PlayEntranceAnimation()
        {
            var duration = TimeSpan.FromMilliseconds(130);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            RootBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
            PopupScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1.0, duration) { EasingFunction = ease });
            PopupScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1.0, duration) { EasingFunction = ease });
            PopupTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, duration) { EasingFunction = ease });
        }

        private void Window_Deactivated(object? sender, EventArgs e)
        {
            if (!_isClosing && IsVisible)
                Close();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _isClosing = true;
            _longPressTimer?.Stop();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (_isJiggleMode)
                {
                    ExitJiggleMode();
                    e.Handled = true;
                    return;
                }
                Close();
                return;
            }

            if (_isJiggleMode) return;

            int index = -1;
            if (e.Key >= Key.D1 && e.Key <= Key.D9)
                index = e.Key - Key.D1;
            else if (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9)
                index = e.Key - Key.NumPad1;

            if (index >= 0 && index < _viewModels.Count)
            {
                bool asAdmin = (Keyboard.Modifiers & ModifierKeys.Control) != 0 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                LaunchItem(_viewModels[index], asAdmin);
            }
        }

        private void EditStackButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
            _onEditRequested?.Invoke(_group.Id);
        }

        private void EnterJiggleMode()
        {
            _isJiggleMode = true;
            DoneButton.Visibility = Visibility.Visible;
            EditStackButton.Visibility = Visibility.Collapsed;
            foreach (var vm in _viewModels)
            {
                vm.IsJiggling = true;
            }
        }

        private void ExitJiggleMode()
        {
            _isJiggleMode = false;
            DoneButton.Visibility = Visibility.Collapsed;
            EditStackButton.Visibility = Visibility.Visible;
            foreach (var vm in _viewModels)
            {
                vm.IsJiggling = false;
            }
        }

        private void DoneButton_Click(object sender, RoutedEventArgs e)
        {
            ExitJiggleMode();
        }

        private void ContextRearrange_Click(object sender, RoutedEventArgs e)
        {
            EnterJiggleMode();
        }

        private void ItemButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _longPressStartPos = e.GetPosition(this);
            if (_isJiggleMode)
            {
                if (sender is FrameworkElement fe && fe.DataContext is FlyoutItemViewModel vm)
                {
                    var data = new DataObject("StackBar.FlyoutReorder", vm);
                    DragDrop.DoDragDrop(fe, data, DragDropEffects.Move);
                }
                e.Handled = true;
                return;
            }

            _longPressTimer?.Stop();
            _longPressTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _longPressTimer.Tick += (s, ev) =>
            {
                _longPressTimer?.Stop();
                EnterJiggleMode();
            };
            _longPressTimer.Start();
        }

        private void ItemButton_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && _longPressTimer != null && _longPressTimer.IsEnabled)
            {
                Point cur = e.GetPosition(this);
                if (Math.Abs(cur.X - _longPressStartPos.X) > 6 || Math.Abs(cur.Y - _longPressStartPos.Y) > 6)
                {
                    _longPressTimer.Stop();
                }
            }
        }

        private void ItemButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _longPressTimer?.Stop();
        }

        private void DeleteBadge_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is FlyoutItemViewModel vm)
            {
                var res = MessageBox.Show($"Remove '{vm.Name}' from this stack?", "Remove from Stack", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res == MessageBoxResult.Yes)
                {
                    _group.Items.Remove(vm.Item);
                    var cs = new ConfigService();
                    var cfg = cs.LoadOrCreateDefault();
                    var matched = cfg.Groups.FirstOrDefault(g => g.Id == _group.Id);
                    if (matched != null)
                    {
                        matched.Items = _group.Items;
                        cs.Save(cfg);
                    }
                    ShortcutService.UpdatePinnedTaskbarShortcutForGroup(_group, cs.AppDataDirectory);
                    PopulateViewModels(_group.Items);
                    ItemCountBadge.Text = $"{_group.Items.Count}";
                    if (_group.Items.Count == 0)
                        ExitJiggleMode();
                    else
                    {
                        foreach (var m in _viewModels) m.IsJiggling = true;
                    }
                }
            }
        }

        private void ItemButton_Drop(object sender, DragEventArgs e)
        {
            if (!_isJiggleMode) return;
            if (e.Data.GetDataPresent("StackBar.FlyoutReorder"))
            {
                var sourceVm = e.Data.GetData("StackBar.FlyoutReorder") as FlyoutItemViewModel;
                if (sourceVm != null && sender is FrameworkElement targetFe && targetFe.DataContext is FlyoutItemViewModel targetVm)
                {
                    if (sourceVm != targetVm)
                    {
                        int oldIdx = _group.Items.IndexOf(sourceVm.Item);
                        int newIdx = _group.Items.IndexOf(targetVm.Item);
                        if (oldIdx >= 0 && newIdx >= 0)
                        {
                            _group.Items.RemoveAt(oldIdx);
                            _group.Items.Insert(newIdx, sourceVm.Item);
                            var cs = new ConfigService();
                            var cfg = cs.LoadOrCreateDefault();
                            var matched = cfg.Groups.FirstOrDefault(g => g.Id == _group.Id);
                            if (matched != null)
                            {
                                matched.Items = _group.Items;
                                cs.Save(cfg);
                            }
                            ShortcutService.UpdatePinnedTaskbarShortcutForGroup(_group, cs.AppDataDirectory);
                            PopulateViewModels(_group.Items);
                            foreach (var m in _viewModels) m.IsJiggling = true;
                        }
                    }
                }
            }
        }

        private void ItemButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isJiggleMode) return;
            if (sender is FrameworkElement fe && fe.Tag is FlyoutItemViewModel vm)
            {
                bool asAdmin = (Keyboard.Modifiers & ModifierKeys.Control) != 0 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                LaunchItem(vm, asAdmin);
            }
        }

        private void ContextLaunch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.CommandParameter is FlyoutItemViewModel vm)
                LaunchItem(vm, asAdmin: false);
        }

        private void ContextRunAdmin_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.CommandParameter is FlyoutItemViewModel vm)
                LaunchItem(vm, asAdmin: true);
        }

        private void ContextOpenLocation_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.CommandParameter is FlyoutItemViewModel vm)
            {
                string target = vm.Item.TargetPath;
                if (Directory.Exists(target))
                {
                    Process.Start("explorer.exe", $"\"{target}\"");
                    Close();
                    return;
                }

                if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    string? realTarget = ShortcutService.ParseShortcut(target).TargetPath;
                    if (!string.IsNullOrEmpty(realTarget) && File.Exists(realTarget))
                        target = realTarget;
                }

                if (File.Exists(target))
                {
                    Process.Start("explorer.exe", $"/select,\"{target}\"");
                    Close();
                }
            }
        }

        private void LaunchItem(FlyoutItemViewModel vm, bool asAdmin)
        {
            try
            {
                // Custom Folder: Open in Windows Explorer directly!
                if (vm.Item.Kind == ItemKind.Folder || Directory.Exists(vm.Item.TargetPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{vm.Item.TargetPath}\"",
                        UseShellExecute = true
                    });
                    if (_group.CloseOnLaunch)
                        Close();
                    return;
                }

                // Shell Apps (Store/UWP)
                if (vm.Item.Kind == ItemKind.ShellApp || vm.Item.TargetPath.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{vm.Item.TargetPath}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    string workDir = vm.Item.WorkingDirectory;
                    if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir))
                    {
                        workDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName = vm.Item.TargetPath,
                        Arguments = vm.Item.Arguments,
                        WorkingDirectory = workDir,
                        UseShellExecute = true
                    };
                    if (asAdmin)
                        psi.Verb = "runas";

                    Process.Start(psi);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not launch '{vm.Name}':\n{ex.Message}", "StackBar Launch Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            if (_group.CloseOnLaunch)
                Close();
        }
    }
}

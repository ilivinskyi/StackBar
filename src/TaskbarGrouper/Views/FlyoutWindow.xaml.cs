using System;
using System.IO;
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
    public class FlyoutItemViewModel
    {
        public string Name { get; set; } = string.Empty;
        public string ToolTipText { get; set; } = string.Empty;
        public string HotkeyHint { get; set; } = string.Empty;
        public double TileHeight { get; set; } = 82;
        public Visibility LabelVisibility { get; set; } = Visibility.Visible;
        public Visibility AdminOptionVisibility { get; set; } = Visibility.Visible;
        public Visibility FileLocationVisibility { get; set; } = Visibility.Visible;
        public TaskbarGrouper.Models.GroupItem Item { get; set; } = null!;
        public ImageSource? Icon { get; set; }
    }

    public partial class FlyoutWindow : Window
    {
        private readonly AppGroup _group;
        private readonly int? _cursorX;
        private readonly int? _cursorY;
        private readonly Action? _onEditRequested;
        private readonly List<FlyoutItemViewModel> _viewModels = new List<FlyoutItemViewModel>();
        private bool _isClosing;

        public int Columns => Polyfills.Clamp(_group.Columns, 2, 8);

        public FlyoutWindow(AppGroup group, int? cursorX = null, int? cursorY = null, Action? onEditRequested = null)
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

            if (_group.Items.Count == 0)
            {
                EmptyNotice.Visibility = Visibility.Visible;
            }
            else
            {
                for (int idx = 0; idx < _group.Items.Count; idx++)
                {
                    var item = _group.Items[idx];
                    bool isShell = item.Kind == ItemKind.ShellApp;
                    _viewModels.Add(new FlyoutItemViewModel
                    {
                        Name = item.Name,
                        ToolTipText = idx < 9 ? $"{item.Name} (Press {idx + 1})" : item.Name,
                        HotkeyHint = idx < 9 ? $"{idx + 1}" : string.Empty,
                        TileHeight = _group.ShowLabels ? 82 : 62,
                        LabelVisibility = _group.ShowLabels ? Visibility.Visible : Visibility.Collapsed,
                        AdminOptionVisibility = isShell ? Visibility.Collapsed : Visibility.Visible,
                        FileLocationVisibility = isShell ? Visibility.Collapsed : Visibility.Visible,
                        Item = item,
                        Icon = IconHelper.ExtractIcon(item.TargetPath, item.CustomIconPath)
                    });
                }
            }

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

            if (isTaskbarTop)
            {
                TopCaretCanvas.Visibility = Visibility.Visible;
                BottomCaretCanvas.Visibility = Visibility.Collapsed;
                Canvas.SetLeft(TopCaretPolygon, Polyfills.Clamp(caretOffsetDip - 8, 24, Math.Max(30, ActualWidth - 40)));
            }
            else
            {
                TopCaretCanvas.Visibility = Visibility.Collapsed;
                BottomCaretCanvas.Visibility = Visibility.Visible;
                Canvas.SetLeft(BottomCaretPolygon, Polyfills.Clamp(caretOffsetDip - 8, 24, Math.Max(30, ActualWidth - 40)));
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
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                return;
            }

            int index = -1;
            if (e.Key >= Key.D1 && e.Key <= Key.D9)
                index = e.Key - Key.D1;
            else if (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9)
                index = e.Key - Key.NumPad1;

            if (index >= 0 && index < _viewModels.Count)
            {
                // Windows standard: Ctrl + Shift to run as administrator
                bool asAdmin = (Keyboard.Modifiers & ModifierKeys.Control) != 0 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                LaunchItem(_viewModels[index], asAdmin);
            }
        }

        private void EditStackButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
            _onEditRequested?.Invoke();
        }

        private void ItemButton_Click(object sender, RoutedEventArgs e)
        {
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
                if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    // Resolve actual destination for Open File Location
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

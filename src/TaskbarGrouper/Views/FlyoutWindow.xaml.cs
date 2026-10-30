using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TaskbarGrouper.Models;
using TaskbarGrouper.Services;
using models_GroupItem = TaskbarGrouper.Models.GroupItem;

namespace TaskbarGrouper.Views;

public class FlyoutItemViewModel
{
    public string Name { get; init; } = string.Empty;
    public string ToolTipText { get; init; } = string.Empty;
    public string HotkeyHint { get; init; } = string.Empty;
    public double TileHeight { get; init; } = 82;
    public Visibility LabelVisibility { get; init; } = Visibility.Visible;
    public models_GroupItem Item { get; init; } = null!;
    public ImageSource? Icon { get; init; }
}

public partial class FlyoutWindow : Window
{
    private readonly AppGroup _group;
    private readonly int? _cursorX;
    private readonly int? _cursorY;
    private readonly Action? _onEditRequested;
    private readonly List<FlyoutItemViewModel> _viewModels;

    public int Columns => Math.Clamp(_group.Columns, 2, 8);

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

        _viewModels = _group.Items.Select((item, idx) => new FlyoutItemViewModel
        {
            Name = item.Name,
            ToolTipText = idx < 9 ? $"{item.Name} (Press {idx + 1})" : item.Name,
            HotkeyHint = idx < 9 ? $"{idx + 1}" : string.Empty,
            TileHeight = _group.ShowLabels ? 82 : 62,
            LabelVisibility = _group.ShowLabels ? Visibility.Visible : Visibility.Collapsed,
            Item = item,
            Icon = IconHelper.ExtractIcon(item.TargetPath, item.CustomIconPath)
        }).ToList();

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
        catch
        {
            // Keep default accent
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        double caretOffsetDip = NativePositioningService.PositionNearTaskbarCursorWithCaret(this, _cursorX, _cursorY);
        Canvas.SetLeft(CaretPolygon, Math.Clamp(caretOffsetDip - 8, 28, Math.Max(40, ActualWidth - 48)));
        Activate();
        PlayEntranceAnimation();
    }

    private void PlayEntranceAnimation()
    {
        var duration = TimeSpan.FromMilliseconds(140);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        RootBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        PopupScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.90, 1.0, duration) { EasingFunction = ease });
        PopupScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.90, 1.0, duration) { EasingFunction = ease });
        PopupTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, duration) { EasingFunction = ease });
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (IsVisible)
            Close();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            return;
        }

        // Number hotkeys 1..9 to launch apps immediately
        int index = -1;
        if (e.Key >= Key.D1 && e.Key <= Key.D9)
            index = e.Key - Key.D1;
        else if (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9)
            index = e.Key - Key.NumPad1;

        if (index >= 0 && index < _viewModels.Count)
        {
            LaunchItem(_viewModels[index], asAdmin: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
        }
    }

    private void EditStackButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
        if (_onEditRequested != null)
        {
            _onEditRequested();
        }
        else
        {
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe))
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
    }

    private void ItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FlyoutItemViewModel vm })
        {
            bool asAdmin = (Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
                           (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            LaunchItem(vm, asAdmin);
        }
    }

    private void ContextLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: FlyoutItemViewModel vm })
            LaunchItem(vm, asAdmin: false);
    }

    private void ContextRunAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: FlyoutItemViewModel vm })
            LaunchItem(vm, asAdmin: true);
    }

    private void ContextOpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: FlyoutItemViewModel vm })
        {
            var full = IconHelper.ResolveFullPath(vm.Item.TargetPath);
            if (!string.IsNullOrEmpty(full) && File.Exists(full))
            {
                Process.Start("explorer.exe", $"/select,\"{full}\"");
                Close();
            }
        }
    }

    private void LaunchItem(FlyoutItemViewModel vm, bool asAdmin)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = vm.Item.TargetPath,
                Arguments = vm.Item.Arguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(vm.Item.WorkingDirectory)
                    ? Environment.CurrentDirectory
                    : vm.Item.WorkingDirectory,
                UseShellExecute = true
            };
            if (asAdmin)
                psi.Verb = "runas";

            Process.Start(psi);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Could not launch '{vm.Name}':\n{ex.Message}",
                "StackBar Launch Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        if (_group.CloseOnLaunch)
            Close();
    }
}

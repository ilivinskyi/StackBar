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
    public models_GroupItem Item { get; init; } = null!;
    public ImageSource? Icon { get; init; }
}

public partial class FlyoutWindow : Window
{
    private readonly AppGroup _group;
    private readonly int? _cursorX;
    private readonly int? _cursorY;
    public int Columns => Math.Clamp(_group.Columns, 2, 8);

    public FlyoutWindow(AppGroup group, int? cursorX = null, int? cursorY = null)
    {
        InitializeComponent();
        _group = group;
        _cursorX = cursorX;
        _cursorY = cursorY;
        DataContext = this;

        GroupTitleText.Text = $"{_group.IconGlyph}  {_group.Name}";
        ItemCountBadge.Text = $"{_group.Items.Count} {(_group.Items.Count == 1 ? "app" : "apps")}";

        var viewModels = _group.Items.Select(item => new FlyoutItemViewModel
        {
            Name = item.Name,
            Item = item,
            Icon = IconHelper.ExtractIcon(item.TargetPath, item.CustomIconPath)
        }).ToList();

        ItemsGrid.ItemsSource = viewModels;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        NativePositioningService.PositionNearTaskbarCursor(this, _cursorX, _cursorY);
        Activate();
        PlayEntranceAnimation();
    }

    private void PlayEntranceAnimation()
    {
        var duration = TimeSpan.FromMilliseconds(135);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        RootBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        PopupScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1.0, duration) { EasingFunction = ease });
        PopupScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1.0, duration) { EasingFunction = ease });
        PopupTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, duration) { EasingFunction = ease });
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (IsVisible)
            Close();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }

    private void ItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FlyoutItemViewModel vm })
        {
            LaunchItem(vm, asAdmin: false);
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

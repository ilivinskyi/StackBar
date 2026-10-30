using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TaskbarGrouper.Models;
using TaskbarGrouper.Services;

namespace TaskbarGrouper.Views;

public class FlyoutItemViewModel
{
    public string Name { get; init; } = string.Empty;
    public GroupItem Item { get; init; } = null!;
    public ImageSource? Icon { get; init; }
}

public partial class FlyoutWindow : Window
{
    private readonly AppGroup _group;

    public FlyoutWindow(AppGroup group)
    {
        InitializeComponent();
        _group = group;
        GroupTitleText.Text = $"{_group.IconGlyph}  {_group.Name}";

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
        NativePositioningService.PositionNearTaskbarCursor(this);
        Activate();
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
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Could not launch '{vm.Name}':\n{ex.Message}",
                    "Launch Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            if (_group.CloseOnLaunch)
                Close();
        }
    }
}

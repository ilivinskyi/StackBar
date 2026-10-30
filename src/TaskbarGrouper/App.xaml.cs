using System.Windows;
using TaskbarGrouper.Models;
using TaskbarGrouper.Services;
using TaskbarGrouper.Views;

namespace TaskbarGrouper;

public partial class App : System.Windows.Application
{
    private ConfigService _configService = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _configService = new ConfigService();
        var config = _configService.LoadOrCreateDefault();

        // Check command line args: e.g., --group "gaming"
        string? groupId = null;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i].Equals("--group", StringComparison.OrdinalIgnoreCase) && i + 1 < e.Args.Length)
            {
                groupId = e.Args[i + 1];
                break;
            }
        }

        if (!string.IsNullOrEmpty(groupId))
        {
            var group = config.Groups.FirstOrDefault(g => g.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))
                        ?? config.Groups.First();
            var flyout = new FlyoutWindow(group);
            flyout.Closed += (_, _) => Shutdown();
            flyout.Show();
        }
        else
        {
            var manager = new MainWindow(config, _configService);
            manager.Closed += (_, _) => Shutdown();
            manager.Show();
        }
    }
}

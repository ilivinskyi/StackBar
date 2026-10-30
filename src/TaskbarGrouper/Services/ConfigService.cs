using System.IO;
using System.Text.Json;
using TaskbarGrouper.Models;

namespace TaskbarGrouper.Services;

public class ConfigService
{
    private readonly string _configPath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ConfigService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "TaskbarGrouper");
        Directory.CreateDirectory(dir);
        _configPath = Path.Combine(dir, "groups.json");
    }

    public string ConfigPath => _configPath;

    public AppConfig LoadOrCreateDefault()
    {
        if (File.Exists(_configPath))
        {
            try
            {
                var json = File.ReadAllText(_configPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (cfg != null && cfg.Groups.Count > 0)
                    return cfg;
            }
            catch
            {
                // Fallback to default on corrupt file
            }
        }

        var defaultConfig = new AppConfig
        {
            Groups = new List<AppGroup>
            {
                new()
                {
                    Id = "gaming",
                    Name = "Gaming",
                    IconGlyph = "🎮",
                    Columns = 4,
                    Items = new List<GroupItem>
                    {
                        new() { Name = "Notepad (Demo)", TargetPath = "notepad.exe" },
                        new() { Name = "Calculator", TargetPath = "calc.exe" }
                    }
                },
                new()
                {
                    Id = "ai-tools",
                    Name = "AI & Web",
                    IconGlyph = "✨",
                    Columns = 4,
                    Items = new List<GroupItem>
                    {
                        new() { Name = "Explorer", TargetPath = "explorer.exe" },
                        new() { Name = "Command Prompt", TargetPath = "cmd.exe" }
                    }
                }
            }
        };

        Save(defaultConfig);
        return defaultConfig;
    }

    public void Save(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(_configPath, json);
    }
}

using System;
using System.IO;
using System.Text;
using System.Xml;
using System.Runtime.Serialization.Json;
using TaskbarGrouper.Models;
using System.Collections.Generic;

namespace TaskbarGrouper.Services
{
    public class ConfigService
    {
        private readonly string _configPath;
        private readonly string _backupPath;
        private readonly string _dir;

        public ConfigService()
        {
            if (AppInfo.IsPortableMode)
            {
                // Portable Mode: store everything in ./data right next to the exe!
                _dir = Path.Combine(AppInfo.BaseDirectory, "data");
            }
            else
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                _dir = Path.Combine(appData, "StackBar");
            }

            Directory.CreateDirectory(_dir);
            _configPath = Path.Combine(_dir, "groups.json");
            _backupPath = Path.Combine(_dir, "groups.json.bak");

            // Migration from TaskbarGrouper legacy folder if exists (only in installed mode)
            if (!AppInfo.IsPortableMode)
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var oldPath = Path.Combine(appData, "TaskbarGrouper", "groups.json");
                if (!File.Exists(_configPath) && File.Exists(oldPath))
                {
                    try
                    {
                        File.Copy(oldPath, _configPath, true);
                    }
                    catch { }
                }
            }
        }

        public string ConfigPath => _configPath;
        public string AppDataDirectory => _dir;

        public AppConfig LoadOrCreateDefault()
        {
            if (File.Exists(_configPath))
            {
                var cfg = TryDeserializeFile(_configPath);
                if (cfg != null) return cfg;

                // Corrupted file! Preserve and try backup
                try
                {
                    string corruptName = $"groups.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json";
                    File.Copy(_configPath, Path.Combine(_dir, corruptName), true);
                }
                catch { }

                if (File.Exists(_backupPath))
                {
                    var backupCfg = TryDeserializeFile(_backupPath);
                    if (backupCfg != null) return backupCfg;
                }
            }

            // Defaults: clean, empty categories
            var defaultConfig = new AppConfig
            {
                LastExePath = AppInfo.ExePath,
                Groups = new List<AppGroup>
                {
                    new AppGroup
                    {
                        Id = "gaming",
                        Name = "Gaming",
                        IconGlyph = "🎮",
                        AccentColorHex = "#3A6DF0",
                        Columns = 4,
                        Items = new List<GroupItem>()
                    },
                    new AppGroup
                    {
                        Id = "ai-tools",
                        Name = "AI & Dev",
                        IconGlyph = "✨",
                        AccentColorHex = "#8B5CF6",
                        Columns = 4,
                        Items = new List<GroupItem>()
                    },
                    new AppGroup
                    {
                        Id = "web",
                        Name = "Web",
                        IconGlyph = "🌐",
                        AccentColorHex = "#10B981",
                        Columns = 4,
                        Items = new List<GroupItem>()
                    }
                }
            };

            Save(defaultConfig);
            return defaultConfig;
        }

        private AppConfig? TryDeserializeFile(string path)
        {
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(AppConfig));
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var cfg = serializer.ReadObject(fs) as AppConfig;
                    if (cfg != null && cfg.Groups != null)
                        return cfg;
                }
            }
            catch
            {
            }
            return null;
        }

        public void Save(AppConfig config)
        {
            try
            {
                config.LastExePath = AppInfo.ExePath;
                string tempPath = Path.Combine(_dir, "groups.json.tmp");

                var serializer = new DataContractJsonSerializer(typeof(AppConfig));
                using (var ms = new MemoryStream())
                {
                    using (var writer = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, true, true))
                    {
                        serializer.WriteObject(writer, config);
                        writer.Flush();
                    }

                    File.WriteAllBytes(tempPath, ms.ToArray());
                }

                if (File.Exists(_configPath))
                {
                    try
                    {
                        File.Replace(tempPath, _configPath, _backupPath);
                    }
                    catch
                    {
                        File.Copy(_configPath, _backupPath, true);
                        File.Copy(tempPath, _configPath, true);
                        File.Delete(tempPath);
                    }
                }
                else
                {
                    File.Move(tempPath, _configPath);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log("Failed to save config: " + ex.Message);
            }
        }
    }
}

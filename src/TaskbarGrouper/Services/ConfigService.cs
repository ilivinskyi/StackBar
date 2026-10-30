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

        public ConfigService() : this(DefaultDataDirectory(), migrateLegacy: !AppInfo.IsPortableMode)
        {
        }

        public ConfigService(string dataDirectory, bool migrateLegacy = false)
        {
            _dir = dataDirectory;
            Directory.CreateDirectory(_dir);
            _configPath = Path.Combine(_dir, "groups.json");
            _backupPath = Path.Combine(_dir, "groups.json.bak");

            if (migrateLegacy)
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

        private static string DefaultDataDirectory()
        {
            // Portable Mode keeps everything in ./data next to the exe
            return AppInfo.IsPortableMode
                ? Path.Combine(AppInfo.BaseDirectory, "data")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StackBar");
        }

        /// <summary>
        /// Raised when a window other than Studio (e.g. a flyout) changed a group in the shared config,
        /// so Studio can refresh instead of later saving a stale copy.
        /// </summary>
        public event Action<string>? GroupChanged;

        public void RaiseGroupChanged(string groupId) => GroupChanged?.Invoke(groupId);

        public string ConfigPath => _configPath;
        public string AppDataDirectory => _dir;

        public AppConfig LoadOrCreateDefault()
        {
            if (File.Exists(_configPath))
            {
                var cfg = TryDeserializeFile(_configPath);
                if (cfg != null) return SanitizeAndPersist(cfg);

                // Corrupted file: preserve it once, then recover from the backup
                try
                {
                    string corruptName = $"groups.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json";
                    File.Copy(_configPath, Path.Combine(_dir, corruptName), true);
                    AppLogger.Log("groups.json was corrupt and was preserved as " + corruptName);
                }
                catch { }

                if (File.Exists(_backupPath))
                {
                    var backupCfg = TryDeserializeFile(_backupPath);
                    if (backupCfg != null)
                    {
                        // Restore the main file from the backup so the next load doesn't hit the corrupt file again
                        // and the next save doesn't rotate the corrupt file into the backup slot.
                        try { File.Copy(_backupPath, _configPath, true); } catch { }
                        return SanitizeAndPersist(backupCfg);
                    }
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

        private AppConfig SanitizeAndPersist(AppConfig cfg)
        {
            // Persist cleaned-up names so the (slow) shell name lookup doesn't run again on every load
            if (SanitizeConfig(cfg)) Save(cfg);
            return cfg;
        }

        /// <returns>true if anything was changed.</returns>
        internal static bool SanitizeConfig(AppConfig cfg)
        {
            bool changed = false;
            if (cfg?.Groups == null) return false;
            foreach (var group in cfg.Groups)
            {
                if (group.Items == null) continue;
                foreach (var item in group.Items)
                {
                    if (item.Name != null && item.Name.Length > 9 && item.Name[8] == '_' && ShortcutService.IsHexPrefix(item.Name.Substring(0, 8)))
                    {
                        item.Name = item.Name.Substring(9).Trim();
                        changed = true;
                    }

                    if (item.Kind == ItemKind.ShellApp || (item.TargetPath != null && item.TargetPath.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase)))
                    {
                        string current = item.Name ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(current) ||
                            current.Contains("!") ||
                            current.StartsWith("com.", StringComparison.OrdinalIgnoreCase) ||
                            current.StartsWith("electron.", StringComparison.OrdinalIgnoreCase) ||
                            current.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                            current.StartsWith("Claude_", StringComparison.OrdinalIgnoreCase))
                        {
                            string? friendly = ShortcutService.ResolveShellAppDisplayName(item.TargetPath!);
                            if (!string.IsNullOrWhiteSpace(friendly) && friendly != item.Name)
                            {
                                item.Name = friendly!;
                                changed = true;
                            }
                        }
                    }
                }
            }
            return changed;
        }
    }
}

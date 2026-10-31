using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Runtime.Serialization.Json;
using TaskbarGrouper.Models;
using System.Collections.Generic;

namespace TaskbarGrouper.Services
{
    public class ConfigService
    {
        /// <summary>
        /// Stands for the data folder in stored paths, so Portable Edition keeps working when its folder
        /// moves or the USB drive gets another letter (and the installed edition when %AppData% moves).
        /// </summary>
        internal const string DataDirToken = "%STACKBAR_DATA%";

        private readonly string _configPath;
        private readonly string _backupPath;
        private readonly string _dir;
        private readonly string? _exePath;

        public ConfigService() : this(DefaultDataDirectory(), migrateLegacy: !AppInfo.IsPortableMode, exePath: AppInfo.ExePath)
        {
        }

        /// <param name="exePath">Recorded as the config's LastExePath; null (tests) leaves it untouched.</param>
        public ConfigService(string dataDirectory, bool migrateLegacy = false, string? exePath = null)
        {
            _dir = Path.GetFullPath(dataDirectory).TrimEnd('\\');
            _exePath = exePath;
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

        /// <summary>Why the last <see cref="Save"/> failed (null after a successful save).</summary>
        public string? LastError { get; private set; }

        public AppConfig LoadOrCreateDefault()
        {
            if (File.Exists(_configPath))
            {
                var cfg = TryDeserializeFile(_configPath);
                if (cfg != null) return PrepareLoaded(cfg);

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
                        return PrepareLoaded(backupCfg);
                    }
                }
            }

            // Defaults: clean, empty categories
            var defaultConfig = new AppConfig
            {
                LastExePath = _exePath,
                MigrationVersion = AppConfig.CurrentMigrationVersion,
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

        private AppConfig PrepareLoaded(AppConfig cfg)
        {
            ExpandDataPaths(cfg);
            if (SanitizeConfig(cfg)) Save(cfg);
            return cfg;
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

        /// <returns>
        /// false if the config could not be written (see <see cref="LastError"/>). Callers must not delete
        /// files the new config stops referencing unless this returned true.
        /// </returns>
        public bool Save(AppConfig config)
        {
            try
            {
                if (_exePath != null) config.LastExePath = _exePath;
                string tempPath = Path.Combine(_dir, "groups.json.tmp");

                var serializer = new DataContractJsonSerializer(typeof(AppConfig));
                using (var ms = new MemoryStream())
                {
                    // Paths are written relative to the data folder and restored right after writing
                    var restore = CollapseDataPaths(config);
                    try
                    {
                        using (var writer = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, true, true))
                        {
                            serializer.WriteObject(writer, config);
                            writer.Flush();
                        }
                    }
                    finally
                    {
                        restore();
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

                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                AppLogger.Log("Failed to save config: " + ex);
                return false;
            }
        }

        private static IEnumerable<(Func<string?> Get, Action<string?> Set)> PathFields(AppConfig config)
        {
            foreach (var group in config.Groups)
            {
                var g = group;
                yield return (() => g.CustomFolderIconPath, v => g.CustomFolderIconPath = v);
                if (g.Items == null) continue;
                foreach (var item in g.Items)
                {
                    var i = item;
                    yield return (() => i.TargetPath, v => i.TargetPath = v ?? string.Empty);
                    yield return (() => i.CustomIconPath, v => i.CustomIconPath = v);
                }
            }
        }

        /// <summary>Replaces the data-folder prefix with the token; returns an action that undoes it.</summary>
        private Action CollapseDataPaths(AppConfig config)
        {
            string prefix = _dir + "\\";
            var undo = new List<Action>();
            foreach (var (get, set) in PathFields(config).ToList())
            {
                string? value = get();
                if (value == null || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                set(DataDirToken + "\\" + value.Substring(prefix.Length));
                undo.Add(() => set(value));
            }
            return () => undo.ForEach(u => u());
        }

        /// <summary>
        /// Expands the token, and rebases absolute paths written by older versions (or by a portable copy on
        /// another drive letter) whose file now lives under this data folder.
        /// </summary>
        internal void ExpandDataPaths(AppConfig config)
        {
            foreach (var (get, set) in PathFields(config).ToList())
            {
                string? value = get();
                if (string.IsNullOrEmpty(value)) continue;

                if (value!.StartsWith(DataDirToken, StringComparison.OrdinalIgnoreCase))
                {
                    set(_dir + value.Substring(DataDirToken.Length));
                    continue;
                }

                if (File.Exists(value)) continue;
                foreach (var marker in new[] { "\\Links\\", "\\Icons\\" })
                {
                    int idx = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) continue;
                    string candidate = _dir + value.Substring(idx);
                    if (File.Exists(candidate))
                    {
                        set(candidate);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Cheap clean-ups done on every load (no COM, no disk scans: this runs before the first flyout).
        /// </summary>
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
                }
            }
            return changed;
        }

        /// <summary>
        /// Store apps imported by older versions can carry a package id ("com.lencx.chatgpt") as their name.
        /// Resolving the friendly name enumerates the whole AppsFolder, so it runs in the background.
        /// </summary>
        public static bool NeedsFriendlyName(GroupItem item)
        {
            if (!(item.Kind == ItemKind.ShellApp ||
                  (item.TargetPath != null && item.TargetPath.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))))
            {
                return false;
            }
            string current = item.Name ?? string.Empty;
            return string.IsNullOrWhiteSpace(current) ||
                   current.Contains("!") ||
                   current.StartsWith("com.", StringComparison.OrdinalIgnoreCase) ||
                   current.StartsWith("electron.", StringComparison.OrdinalIgnoreCase) ||
                   current.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                   current.StartsWith("Claude_", StringComparison.OrdinalIgnoreCase);
        }
    }
}

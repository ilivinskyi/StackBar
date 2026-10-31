using System;
using System.Runtime.Serialization;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;

namespace TaskbarGrouper.Models
{
    public enum FolderIconMode
    {
        [EnumMember]
        MiniGrid2x2,
        [EnumMember]
        EmojiGlyph,
        [EnumMember]
        CustomIco
    }

    public enum ItemKind
    {
        [EnumMember]
        File,
        [EnumMember]
        Shortcut,
        [EnumMember]
        ShellApp,
        [EnumMember]
        Folder
    }

    [DataContract]
    public class AppConfig
    {
        [DataMember(Name = "lastExePath", EmitDefaultValue = false)]
        public string? LastExePath { get; set; }

        [DataMember(Name = "groups", EmitDefaultValue = false)]
        public List<AppGroup> Groups { get; set; } = new List<AppGroup>();

        // Fallback property mapping for legacy capital 'Groups' - only read, never emit duplicate
        [DataMember(Name = "Groups", EmitDefaultValue = false)]
        internal List<AppGroup>? LegacyGroups
        {
            get => null;
            set { if (value != null && value.Count > 0) Groups = value; }
        }

        /// <summary>
        /// Refresh outdated app shortcuts (Start Menu / pinned) before launching, so Windows doesn't draw
        /// a blank taskbar icon for apps that updated themselves in place.
        /// </summary>
        [DataMember(Name = "repairAppShortcuts")]
        public bool RepairAppShortcuts { get; set; } = true;

        /// <summary>One-time startup migrations already applied (0 for configs from older versions).</summary>
        [DataMember(Name = "migrationVersion", EmitDefaultValue = false)]
        public int MigrationVersion { get; set; }

        public const int CurrentMigrationVersion = 1;

        [OnDeserializing]
        internal void OnDeserializing(StreamingContext context)
        {
            Groups = new List<AppGroup>();
            RepairAppShortcuts = true;
            MigrationVersion = 0;
        }
    }

    [DataContract]
    public class AppGroup : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Tells bound views (sidebar name, thumbnail) that the group's look changed.</summary>
        public void NotifyAppearanceChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            // The sidebar binds Items.Count; re-raising Items refreshes it after adds/removes
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Items)));
        }

        [DataMember(Name = "id", EmitDefaultValue = false)]
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        [DataMember(Name = "Id", EmitDefaultValue = false)]
        internal string? LegacyId { get => null; set { if (value != null) Id = value; } }

        [DataMember(Name = "name", EmitDefaultValue = false)]
        public string Name { get; set; } = "New Stack";

        [DataMember(Name = "Name", EmitDefaultValue = false)]
        internal string? LegacyName { get => null; set { if (value != null) Name = value; } }

        [DataMember(Name = "iconGlyph", EmitDefaultValue = false)]
        public string IconGlyph { get; set; } = "📁";

        [DataMember(Name = "IconGlyph", EmitDefaultValue = false)]
        internal string? LegacyIconGlyph { get => null; set { if (value != null) IconGlyph = value; } }

        [DataMember(Name = "iconMode")]
        public FolderIconMode IconMode { get; set; } = FolderIconMode.MiniGrid2x2;

        [DataMember(Name = "IconMode", EmitDefaultValue = false)]
        internal FolderIconMode? LegacyIconMode { get => null; set { if (value.HasValue) IconMode = value.Value; } }

        [DataMember(Name = "customFolderIconPath", EmitDefaultValue = false)]
        public string? CustomFolderIconPath { get; set; }

        [DataMember(Name = "CustomFolderIconPath", EmitDefaultValue = false)]
        internal string? LegacyCustomFolderIconPath { get => null; set { if (value != null) CustomFolderIconPath = value; } }

        [DataMember(Name = "accentColorHex", EmitDefaultValue = false)]
        public string AccentColorHex { get; set; } = "#3A6DF0";

        [DataMember(Name = "AccentColorHex", EmitDefaultValue = false)]
        internal string? LegacyAccentColorHex { get => null; set { if (value != null) AccentColorHex = value; } }

        [DataMember(Name = "columns")]
        public int Columns { get; set; } = 4;

        [DataMember(Name = "Columns", EmitDefaultValue = false)]
        internal int? LegacyColumns { get => null; set { if (value.HasValue) Columns = value.Value; } }

        [DataMember(Name = "showLabels")]
        public bool ShowLabels { get; set; } = true;

        [DataMember(Name = "ShowLabels", EmitDefaultValue = false)]
        internal bool? LegacyShowLabels { get => null; set { if (value.HasValue) ShowLabels = value.Value; } }

        [DataMember(Name = "closeOnLaunch")]
        public bool CloseOnLaunch { get; set; } = true;

        [DataMember(Name = "CloseOnLaunch", EmitDefaultValue = false)]
        internal bool? LegacyCloseOnLaunch { get => null; set { if (value.HasValue) CloseOnLaunch = value.Value; } }

        [DataMember(Name = "items", EmitDefaultValue = false)]
        public List<GroupItem> Items { get; set; } = new List<GroupItem>();

        [DataMember(Name = "Items", EmitDefaultValue = false)]
        internal List<GroupItem>? LegacyItems
        {
            get => null;
            set { if (value != null) Items = value; }
        }

        [IgnoreDataMember]
        public System.Windows.Media.ImageSource? Thumbnail => Services.IconHelper.GenerateGroupPreviewImage(this);

        /// <summary>
        /// Independent copy for background work (icon generation, shortcut checks): the UI thread may edit
        /// the live group, and saving briefly rewrites its paths, while the copy is being read.
        /// </summary>
        public AppGroup CloneForReading()
        {
            return new AppGroup
            {
                Id = Id,
                Name = Name,
                IconGlyph = IconGlyph,
                IconMode = IconMode,
                CustomFolderIconPath = CustomFolderIconPath,
                AccentColorHex = AccentColorHex,
                Columns = Columns,
                ShowLabels = ShowLabels,
                CloseOnLaunch = CloseOnLaunch,
                Items = Items.Select(i => new GroupItem
                {
                    Name = i.Name,
                    TargetPath = i.TargetPath,
                    Arguments = i.Arguments,
                    WorkingDirectory = i.WorkingDirectory,
                    CustomIconPath = i.CustomIconPath,
                    CustomIconIndex = i.CustomIconIndex,
                    Kind = i.Kind,
                    SourcePath = i.SourcePath
                }).ToList()
            };
        }

        [OnDeserializing]
        internal void OnDeserializing(StreamingContext context)
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 8);
            Name = "New Stack";
            IconGlyph = "📁";
            IconMode = FolderIconMode.MiniGrid2x2;
            AccentColorHex = "#3A6DF0";
            Columns = 4;
            ShowLabels = true;
            CloseOnLaunch = true;
            Items = new List<GroupItem>();
        }
    }

    [DataContract]
    public class GroupItem
    {
        [DataMember(Name = "name", EmitDefaultValue = false)]
        public string Name { get; set; } = string.Empty;

        [DataMember(Name = "Name", EmitDefaultValue = false)]
        internal string? LegacyName { get => null; set { if (value != null) Name = value; } }

        [DataMember(Name = "targetPath", EmitDefaultValue = false)]
        public string TargetPath { get; set; } = string.Empty;

        [DataMember(Name = "TargetPath", EmitDefaultValue = false)]
        internal string? LegacyTargetPath { get => null; set { if (value != null) TargetPath = value; } }

        [DataMember(Name = "arguments", EmitDefaultValue = false)]
        public string Arguments { get; set; } = string.Empty;

        [DataMember(Name = "Arguments", EmitDefaultValue = false)]
        internal string? LegacyArguments { get => null; set { if (value != null) Arguments = value; } }

        [DataMember(Name = "workingDirectory", EmitDefaultValue = false)]
        public string WorkingDirectory { get; set; } = string.Empty;

        [DataMember(Name = "WorkingDirectory", EmitDefaultValue = false)]
        internal string? LegacyWorkingDirectory { get => null; set { if (value != null) WorkingDirectory = value; } }

        [DataMember(Name = "customIconPath", EmitDefaultValue = false)]
        public string? CustomIconPath { get; set; }

        [DataMember(Name = "CustomIconPath", EmitDefaultValue = false)]
        internal string? LegacyCustomIconPath { get => null; set { if (value != null) CustomIconPath = value; } }

        [DataMember(Name = "kind")]
        public ItemKind Kind { get; set; } = ItemKind.File;

        /// <summary>Icon resource index inside CustomIconPath (e.g. shell32.dll,-16). 0 = first icon.</summary>
        [DataMember(Name = "customIconIndex", EmitDefaultValue = false)]
        public int CustomIconIndex { get; set; }

        /// <summary>Original file the item was imported from, used to detect duplicate imports.</summary>
        [DataMember(Name = "sourcePath", EmitDefaultValue = false)]
        public string? SourcePath { get; set; }

        [DataMember(Name = "Kind", EmitDefaultValue = false)]
        internal ItemKind? LegacyKind { get => null; set { if (value.HasValue) Kind = value.Value; } }

        public bool IsBroken { get; set; }

        [OnDeserializing]
        internal void OnDeserializing(StreamingContext context)
        {
            Name = string.Empty;
            TargetPath = string.Empty;
            Arguments = string.Empty;
            WorkingDirectory = string.Empty;
            Kind = ItemKind.File;
            CustomIconIndex = 0;
            SourcePath = null;
        }
    }
}

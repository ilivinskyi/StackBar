using System;
using System.Runtime.Serialization;
using System.Collections.Generic;

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

        // Fallback property mapping for legacy capital 'Groups'
        [DataMember(Name = "Groups", EmitDefaultValue = false)]
        internal List<AppGroup> LegacyGroups
        {
            get => Groups;
            set { if (value != null && value.Count > 0) Groups = value; }
        }

        [OnDeserializing]
        internal void OnDeserializing(StreamingContext context)
        {
            Groups = new List<AppGroup>();
        }
    }

    [DataContract]
    public class AppGroup
    {
        [DataMember(Name = "id", EmitDefaultValue = false)]
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        [DataMember(Name = "Id", EmitDefaultValue = false)]
        internal string LegacyId { get => Id; set => Id = value; }

        [DataMember(Name = "name", EmitDefaultValue = false)]
        public string Name { get; set; } = "New Stack";

        [DataMember(Name = "Name", EmitDefaultValue = false)]
        internal string LegacyName { get => Name; set => Name = value; }

        [DataMember(Name = "iconGlyph", EmitDefaultValue = false)]
        public string IconGlyph { get; set; } = "📁";

        [DataMember(Name = "IconGlyph", EmitDefaultValue = false)]
        internal string LegacyIconGlyph { get => IconGlyph; set => IconGlyph = value; }

        [DataMember(Name = "iconMode", EmitDefaultValue = false)]
        public FolderIconMode IconMode { get; set; } = FolderIconMode.MiniGrid2x2;

        [DataMember(Name = "IconMode", EmitDefaultValue = false)]
        internal FolderIconMode LegacyIconMode { get => IconMode; set => IconMode = value; }

        [DataMember(Name = "customFolderIconPath", EmitDefaultValue = false)]
        public string? CustomFolderIconPath { get; set; }

        [DataMember(Name = "CustomFolderIconPath", EmitDefaultValue = false)]
        internal string? LegacyCustomFolderIconPath { get => CustomFolderIconPath; set => CustomFolderIconPath = value; }

        [DataMember(Name = "accentColorHex", EmitDefaultValue = false)]
        public string AccentColorHex { get; set; } = "#3A6DF0";

        [DataMember(Name = "AccentColorHex", EmitDefaultValue = false)]
        internal string LegacyAccentColorHex { get => AccentColorHex; set => AccentColorHex = value; }

        [DataMember(Name = "columns", EmitDefaultValue = false)]
        public int Columns { get; set; } = 4;

        [DataMember(Name = "Columns", EmitDefaultValue = false)]
        internal int LegacyColumns { get => Columns; set => Columns = value; }

        [DataMember(Name = "showLabels", EmitDefaultValue = false)]
        public bool ShowLabels { get; set; } = true;

        [DataMember(Name = "ShowLabels", EmitDefaultValue = false)]
        internal bool LegacyShowLabels { get => ShowLabels; set => ShowLabels = value; }

        [DataMember(Name = "closeOnLaunch", EmitDefaultValue = false)]
        public bool CloseOnLaunch { get; set; } = true;

        [DataMember(Name = "CloseOnLaunch", EmitDefaultValue = false)]
        internal bool LegacyCloseOnLaunch { get => CloseOnLaunch; set => CloseOnLaunch = value; }

        [DataMember(Name = "items", EmitDefaultValue = false)]
        public List<GroupItem> Items { get; set; } = new List<GroupItem>();

        [DataMember(Name = "Items", EmitDefaultValue = false)]
        internal List<GroupItem> LegacyItems
        {
            get => Items;
            set { if (value != null) Items = value; }
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
        internal string LegacyName { get => Name; set => Name = value; }

        [DataMember(Name = "targetPath", EmitDefaultValue = false)]
        public string TargetPath { get; set; } = string.Empty;

        [DataMember(Name = "TargetPath", EmitDefaultValue = false)]
        internal string LegacyTargetPath { get => TargetPath; set => TargetPath = value; }

        [DataMember(Name = "arguments", EmitDefaultValue = false)]
        public string Arguments { get; set; } = string.Empty;

        [DataMember(Name = "Arguments", EmitDefaultValue = false)]
        internal string LegacyArguments { get => Arguments; set => Arguments = value; }

        [DataMember(Name = "workingDirectory", EmitDefaultValue = false)]
        public string WorkingDirectory { get; set; } = string.Empty;

        [DataMember(Name = "WorkingDirectory", EmitDefaultValue = false)]
        internal string LegacyWorkingDirectory { get => WorkingDirectory; set => WorkingDirectory = value; }

        [DataMember(Name = "customIconPath", EmitDefaultValue = false)]
        public string? CustomIconPath { get; set; }

        [DataMember(Name = "CustomIconPath", EmitDefaultValue = false)]
        internal string? LegacyCustomIconPath { get => CustomIconPath; set => CustomIconPath = value; }

        [DataMember(Name = "kind", EmitDefaultValue = false)]
        public ItemKind Kind { get; set; } = ItemKind.File;

        [DataMember(Name = "Kind", EmitDefaultValue = false)]
        internal ItemKind LegacyKind { get => Kind; set => Kind = value; }

        public bool IsBroken { get; set; }

        [OnDeserializing]
        internal void OnDeserializing(StreamingContext context)
        {
            Name = string.Empty;
            TargetPath = string.Empty;
            Arguments = string.Empty;
            WorkingDirectory = string.Empty;
            Kind = ItemKind.File;
        }
    }
}

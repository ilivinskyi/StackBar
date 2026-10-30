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
        ShellApp
    }

    [DataContract]
    public class AppConfig
    {
        [DataMember(Name = "lastExePath")]
        public string? LastExePath { get; set; }

        [DataMember(Name = "groups")]
        public List<AppGroup> Groups { get; set; } = new List<AppGroup>();

        [OnDeserializing]
        internal void OnDeserializing(StreamingContext context)
        {
            Groups = new List<AppGroup>();
        }
    }

    [DataContract]
    public class AppGroup
    {
        [DataMember(Name = "id")]
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        [DataMember(Name = "name")]
        public string Name { get; set; } = "New Stack";

        [DataMember(Name = "iconGlyph")]
        public string IconGlyph { get; set; } = "📁";

        [DataMember(Name = "iconMode")]
        public FolderIconMode IconMode { get; set; } = FolderIconMode.MiniGrid2x2;

        [DataMember(Name = "customFolderIconPath")]
        public string? CustomFolderIconPath { get; set; }

        [DataMember(Name = "accentColorHex")]
        public string AccentColorHex { get; set; } = "#3A6DF0";

        [DataMember(Name = "columns")]
        public int Columns { get; set; } = 4;

        [DataMember(Name = "showLabels")]
        public bool ShowLabels { get; set; } = true;

        [DataMember(Name = "closeOnLaunch")]
        public bool CloseOnLaunch { get; set; } = true;

        [DataMember(Name = "items")]
        public List<GroupItem> Items { get; set; } = new List<GroupItem>();

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
        [DataMember(Name = "name")]
        public string Name { get; set; } = string.Empty;

        [DataMember(Name = "targetPath")]
        public string TargetPath { get; set; } = string.Empty;

        [DataMember(Name = "arguments")]
        public string Arguments { get; set; } = string.Empty;

        [DataMember(Name = "workingDirectory")]
        public string WorkingDirectory { get; set; } = string.Empty;

        [DataMember(Name = "customIconPath")]
        public string? CustomIconPath { get; set; }

        [DataMember(Name = "kind")]
        public ItemKind Kind { get; set; } = ItemKind.File;

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

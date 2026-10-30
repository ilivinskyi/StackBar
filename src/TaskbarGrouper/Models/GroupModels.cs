namespace TaskbarGrouper.Models;

public enum FolderIconMode
{
    MiniGrid2x2,
    EmojiGlyph,
    CustomIco
}

public class AppConfig
{
    public List<AppGroup> Groups { get; set; } = new();
}

public class AppGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "New Stack";
    public string IconGlyph { get; set; } = "📁";
    public FolderIconMode IconMode { get; set; } = FolderIconMode.MiniGrid2x2;
    public string? CustomFolderIconPath { get; set; }
    public int Columns { get; set; } = 4;
    public bool CloseOnLaunch { get; set; } = true;
    public List<GroupItem> Items { get; set; } = new();
}

public class GroupItem
{
    public string Name { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string? CustomIconPath { get; set; }
}


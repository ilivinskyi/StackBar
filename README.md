# Windows Taskbar Grouper

A lightweight Windows 11 desktop utility that groups rarely-used taskbar icons into clean, macOS/iOS-style folder popups anchored directly to your taskbar.

## Features (Planned & Skeleton)
- **Taskbar-Anchored Flyout (`--group <id>`)**: Borderless, auto-dismissing popup grid that opens right above the clicked taskbar icon.
- **Group Manager UI**: Configure groups (e.g., *Gaming*, *AI*, *Web*, *Media*), drag-and-drop shortcuts/executables, and customize icons.
- **Composite Folder Icons**: Dynamically generate 2x2 mini-grid `.ico` files (like iOS folders) or custom category icons to pin directly to the Windows taskbar.

## Quick Start

```powershell
# Build the project
dotnet build

# Open the Group Manager UI
dotnet run --project src/TaskbarGrouper

# Open a specific group flyout directly (as a pinned taskbar shortcut does)
dotnet run --project src/TaskbarGrouper -- --group gaming
```

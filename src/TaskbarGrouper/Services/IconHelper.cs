using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TaskbarGrouper.Services;

public static class IconHelper
{
    public static ImageSource? ExtractIcon(string targetPath, string? customIconPath = null)
    {
        var path = !string.IsNullOrWhiteSpace(customIconPath) && File.Exists(customIconPath)
            ? customIconPath
            : ResolveFullPath(targetPath);

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        try
        {
            using Icon? icon = Icon.ExtractAssociatedIcon(path);
            if (icon == null)
                return null;

            return Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveFullPath(string fileName)
    {
        if (File.Exists(fileName))
            return Path.GetFullPath(fileName);

        var values = Environment.GetEnvironmentVariable("PATH");
        if (values == null) return null;

        foreach (var path in values.Split(Path.PathSeparator))
        {
            try
            {
                var fullPath = Path.Combine(path, fileName);
                if (File.Exists(fullPath))
                    return fullPath;
            }
            catch
            {
                // Ignore invalid path entries
            }
        }
        return null;
    }
}

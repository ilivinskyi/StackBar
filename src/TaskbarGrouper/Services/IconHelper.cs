using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskbarGrouper.Models;

namespace TaskbarGrouper.Services;

public static class IconHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    public static ImageSource? ExtractIcon(string targetPath, string? customIconPath = null)
    {
        using var bmp = ExtractBitmap(targetPath, customIconPath);
        if (bmp == null) return null;
        return BitmapToImageSource(bmp);
    }

    public static Bitmap? ExtractBitmap(string targetPath, string? customIconPath = null)
    {
        var path = !string.IsNullOrWhiteSpace(customIconPath) && File.Exists(customIconPath)
            ? customIconPath
            : ResolveFullPath(targetPath);

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        try
        {
            var shinfo = new SHFILEINFO();
            IntPtr res = SHGetFileInfo(path, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_LARGEICON);
            if (res != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
            {
                try
                {
                    using var ico = Icon.FromHandle(shinfo.hIcon);
                    return ico.ToBitmap();
                }
                finally
                {
                    DestroyIcon(shinfo.hIcon);
                }
            }

            using Icon? fallback = Icon.ExtractAssociatedIcon(path);
            return fallback?.ToBitmap();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Generates a crisp 256x256 PNG-compressed .ico file for a StackBar group, either as an iOS 2x2 mini-grid
    /// of the first 4 apps inside the folder or as a sleek rounded badge with the group's emoji glyph.
    /// </summary>
    public static string GenerateGroupIcoFile(AppGroup group, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        string icoPath = Path.Combine(outputDirectory, $"stackbar_{group.Id}.ico");

        if (group.IconMode == FolderIconMode.CustomIco &&
            !string.IsNullOrWhiteSpace(group.CustomFolderIconPath) &&
            File.Exists(group.CustomFolderIconPath))
        {
            return group.CustomFolderIconPath;
        }

        const int size = 256;
        using var canvas = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(System.Drawing.Color.Transparent);

            // Draw dark glassmorphic iOS-style squircle container
            using (var bgPath = CreateRoundedRect(new Rectangle(12, 12, size - 24, size - 24), 56))
            using (var bgBrush = new System.Drawing.Drawing2D.LinearGradientBrush(
                       new Rectangle(12, 12, size - 24, size - 24),
                       System.Drawing.Color.FromArgb(235, 38, 40, 52),
                       System.Drawing.Color.FromArgb(245, 22, 23, 30),
                       LinearGradientMode.Vertical))
            using (var borderPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(90, 255, 255, 255), 4f))
            {
                g.FillPath(bgBrush, bgPath);
                g.DrawPath(borderPen, bgPath);
            }

            bool drewMiniGrid = false;
            if (group.IconMode == FolderIconMode.MiniGrid2x2 && group.Items.Count > 0)
            {
                var subIcons = group.Items
                    .Take(4)
                    .Select(it => ExtractBitmap(it.TargetPath, it.CustomIconPath))
                    .Where(b => b != null)
                    .ToList();

                if (subIcons.Count > 0)
                {
                    drewMiniGrid = true;
                    Rectangle[] slots =
                    {
                        new(38, 38, 82, 82),
                        new(136, 38, 82, 82),
                        new(38, 136, 82, 82),
                        new(136, 136, 82, 82)
                    };

                    for (int i = 0; i < Math.Min(4, subIcons.Count); i++)
                    {
                        using var subBmp = subIcons[i]!;
                        g.DrawImage(subBmp, slots[i]);
                    }
                }
            }

            if (!drewMiniGrid)
            {
                // Draw centered Emoji/Glyph
                string glyph = string.IsNullOrWhiteSpace(group.IconGlyph) ? "📁" : group.IconGlyph;
                using var font = new Font("Segoe UI Emoji", 108, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(System.Drawing.Color.White);
                var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString(glyph, font, brush, new RectangleF(12, 20, size - 24, size - 24), format);
            }
        }

        SaveBitmapAsPngIco(canvas, icoPath);
        return icoPath;
    }

    private static void SaveBitmapAsPngIco(Bitmap bmp, string icoPath)
    {
        using var pngStream = new MemoryStream();
        bmp.Save(pngStream, ImageFormat.Png);
        byte[] pngBytes = pngStream.ToArray();

        using var fs = new FileStream(icoPath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        // ICONDIR header (6 bytes)
        bw.Write((short)0);   // Reserved
        bw.Write((short)1);   // Type: 1 = ICO
        bw.Write((short)1);   // Count: 1 image

        // ICONDIRENTRY (16 bytes)
        bw.Write((byte)0);    // Width: 0 means 256
        bw.Write((byte)0);    // Height: 0 means 256
        bw.Write((byte)0);    // Color count
        bw.Write((byte)0);    // Reserved
        bw.Write((short)1);   // Color planes
        bw.Write((short)32);  // Bits per pixel
        bw.Write(pngBytes.Length); // Size of image data
        bw.Write(22);         // Offset to image data (6 + 16 = 22)

        // Image payload
        bw.Write(pngBytes);
    }

    private static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
    {
        int diameter = radius * 2;
        var path = new GraphicsPath();
        var arc = new Rectangle(bounds.Location, new System.Drawing.Size(diameter, diameter));

        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static ImageSource BitmapToImageSource(Bitmap bitmap)
    {
        using var memory = new MemoryStream();
        bitmap.Save(memory, ImageFormat.Png);
        memory.Position = 0;

        var bitmapImage = new BitmapImage();
        bitmapImage.BeginInit();
        bitmapImage.StreamSource = memory;
        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
        bitmapImage.EndInit();
        bitmapImage.Freeze();
        return bitmapImage;
    }

    public static string? ResolveFullPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

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

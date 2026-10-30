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

    [DllImport("shell32.dll", EntryPoint = "#727")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
        int ReplaceIcon(int i, IntPtr hicon, ref int pi);
        int SetOverlayImage(int iImage, int iOverlay);
        int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
        int Draw(IntPtr pimldp);
        int Remove(int i);
        int GetIcon(int i, int flags, out IntPtr picon);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SYSICONINDEX = 0x000004000;
    private const int SHIL_JUMBO = 0x4;      // 256x256
    private const int SHIL_EXTRALARGE = 0x2; // 48x48

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
            // Try ExtraLarge (48x48) / Jumbo (256x256) Shell Image List first for ultra-crisp icons
            var shinfo = new SHFILEINFO();
            IntPtr res = SHGetFileInfo(path, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_SYSICONINDEX);
            if (res != IntPtr.Zero)
            {
                Guid iidImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");
                if (SHGetImageList(SHIL_EXTRALARGE, ref iidImageList, out var imgList) == 0 && imgList != null)
                {
                    if (imgList.GetIcon(shinfo.iIcon, 1, out IntPtr hIcon) == 0 && hIcon != IntPtr.Zero)
                    {
                        try
                        {
                            using var ico = Icon.FromHandle(hIcon);
                            return new Bitmap(ico.ToBitmap());
                        }
                        finally
                        {
                            DestroyIcon(hIcon);
                        }
                    }
                }
            }

            // Fallback to standard SHGFI_LARGEICON
            res = SHGetFileInfo(path, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_LARGEICON);
            if (res != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
            {
                try
                {
                    using var ico = Icon.FromHandle(shinfo.hIcon);
                    return new Bitmap(ico.ToBitmap());
                }
                finally
                {
                    DestroyIcon(shinfo.hIcon);
                }
            }

            using Icon? fallback = Icon.ExtractAssociatedIcon(path);
            return fallback != null ? new Bitmap(fallback.ToBitmap()) : null;
        }
        catch
        {
            return null;
        }
    }

    public static ImageSource GenerateGroupPreviewImage(AppGroup group)
    {
        using var bmp = RenderGroupIconBitmap(group);
        return BitmapToImageSource(bmp);
    }

    public static Bitmap RenderGroupIconBitmap(AppGroup group)
    {
        const int size = 256;
        var canvas = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(canvas);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(System.Drawing.Color.Transparent);

        System.Drawing.Color accent = System.Drawing.Color.FromArgb(255, 58, 109, 240);
        try
        {
            if (!string.IsNullOrWhiteSpace(group.AccentColorHex))
                accent = System.Drawing.ColorTranslator.FromHtml(group.AccentColorHex);
        }
        catch { }

        // Draw dark glassmorphic iOS-style squircle container with subtle accent glow
        using (var bgPath = CreateRoundedRect(new Rectangle(10, 10, size - 20, size - 20), 58))
        using (var bgBrush = new System.Drawing.Drawing2D.LinearGradientBrush(
                   new Rectangle(10, 10, size - 20, size - 20),
                   System.Drawing.Color.FromArgb(245,
                       Math.Min(255, 28 + accent.R / 7),
                       Math.Min(255, 30 + accent.G / 7),
                       Math.Min(255, 42 + accent.B / 7)),
                   System.Drawing.Color.FromArgb(250, 18, 19, 26),
                   LinearGradientMode.ForwardDiagonal))
        using (var borderPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(135, accent.R, accent.G, accent.B), 5f))
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
                    new(36, 36, 84, 84),
                    new(136, 36, 84, 84),
                    new(36, 136, 84, 84),
                    new(136, 136, 84, 84)
                };

                for (int i = 0; i < Math.Min(4, subIcons.Count); i++)
                {
                    using var subBmp = subIcons[i]!;
                    // Draw subtle mini-tile pill behind each icon
                    using var slotPath = CreateRoundedRect(slots[i], 18);
                    using var slotBrush = new SolidBrush(System.Drawing.Color.FromArgb(55, 255, 255, 255));
                    g.FillPath(slotBrush, slotPath);

                    var inner = Rectangle.Inflate(slots[i], -8, -8);
                    g.DrawImage(subBmp, inner);
                }
            }
        }

        if (!drewMiniGrid)
        {
            string glyph = string.IsNullOrWhiteSpace(group.IconGlyph) ? "📁" : group.IconGlyph;
            using var font = new Font("Segoe UI Emoji", 110, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(System.Drawing.Color.White);
            var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString(glyph, font, brush, new RectangleF(10, 18, size - 20, size - 20), format);
        }

        return canvas;
    }

    /// <summary>
    /// Generates a crisp 256x256 PNG-compressed .ico file for a StackBar group.
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

        using var canvas = RenderGroupIconBitmap(group);
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

        bw.Write((short)0);
        bw.Write((short)1);
        bw.Write((short)1);

        bw.Write((byte)0);
        bw.Write((byte)0);
        bw.Write((byte)0);
        bw.Write((byte)0);
        bw.Write((short)1);
        bw.Write((short)32);
        bw.Write(pngBytes.Length);
        bw.Write(22);

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
            }
        }
        return null;
    }
}

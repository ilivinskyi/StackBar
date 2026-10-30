using System;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskbarGrouper.Models;
using System.Collections.Generic;

namespace TaskbarGrouper.Services
{
    public static class IconHelper
    {
        [Flags]
        private enum SIIGBF
        {
            SIIGBF_RESIZETOFIT = 0x00,
            SIIGBF_BIGGERSIZEOK = 0x01,
            SIIGBF_MEMORYONLY = 0x02,
            SIIGBF_ICONONLY = 0x04,
            SIIGBF_THUMBNAILONLY = 0x08,
            SIIGBF_INCACHEONLY = 0x10
        }

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(
                [In, MarshalAs(UnmanagedType.Struct)] SIZE size,
                [In] SIIGBF flags,
                [Out] out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
            public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            [In, MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            [In] IntPtr pbc,
            [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [Out, MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

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

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_LARGEICON = 0x000000000;

        private static readonly Guid IShellItemImageFactoryGuid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
        private static readonly Dictionary<string, ImageSource> _iconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public static ImageSource? ExtractIcon(string targetPath, string? customIconPath = null)
        {
            string key = (!string.IsNullOrWhiteSpace(customIconPath) ? customIconPath : targetPath) ?? string.Empty;
            if (string.IsNullOrEmpty(key)) return null;

            if (_iconCache.TryGetValue(key, out var cached))
                return cached;

            using (var bmp = ExtractBitmap(targetPath, customIconPath))
            {
                if (bmp == null) return null;
                var src = BitmapToImageSource(bmp);
                _iconCache[key] = src;
                return src;
            }
        }

        public static Bitmap? ExtractBitmap(string targetPath, string? customIconPath = null)
        {
            string path = !string.IsNullOrWhiteSpace(customIconPath) && File.Exists(customIconPath)
                ? customIconPath!
                : targetPath;

            if (string.IsNullOrWhiteSpace(path)) return null;

            // Directory/Folder: Use SHGetFileInfo for clean 32bpp alpha icon without black background
            if (Directory.Exists(path))
            {
                try
                {
                    var shinfo = new SHFILEINFO();
                    IntPtr hImg = SHGetFileInfo(path, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_LARGEICON);
                    if (hImg != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
                    {
                        try
                        {
                            using (var ico = Icon.FromHandle(shinfo.hIcon))
                            {
                                return ico.ToBitmap();
                            }
                        }
                        finally
                        {
                            DestroyIcon(shinfo.hIcon);
                        }
                    }
                }
                catch { }
            }

            // 0. Direct Image formats (.png, .jpg, .jpeg, .bmp, .ico)
            string ext = Path.GetExtension(path);
            if (!string.IsNullOrEmpty(ext))
            {
                if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            using (var img = Image.FromStream(fs))
                            {
                                return new Bitmap(img);
                            }
                        }
                    }
                    catch { }
                }
                else if (ext.Equals(".ico", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                {
                    try
                    {
                        using (var ico = new Icon(path, 256, 256))
                        {
                            return ico.ToBitmap();
                        }
                    }
                    catch
                    {
                        try
                        {
                            using (var ico = new Icon(path))
                            {
                                return ico.ToBitmap();
                            }
                        }
                        catch { }
                    }
                }
            }

            // 1. High-Res via IShellItemImageFactory (handles files, .lnk, shell:AppsFolder)
            try
            {
                int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, IShellItemImageFactoryGuid, out var factory);
                if (hr == 0 && factory != null)
                {
                    try
                    {
                        IntPtr hBitmap;
                        int hrImg = factory.GetImage(new SIZE(64, 64), SIIGBF.SIIGBF_BIGGERSIZEOK | SIIGBF.SIIGBF_ICONONLY, out hBitmap);
                        if (hrImg == 0 && hBitmap != IntPtr.Zero)
                        {
                            try
                            {
                                using (var rawBmp = Image.FromHbitmap(hBitmap))
                                {
                                    return new Bitmap(rawBmp);
                                }
                            }
                            finally
                            {
                                DeleteObject(hBitmap);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FinalReleaseComObject(factory);
                    }
                }
            }
            catch { }

            // 2. Fallback to Icon.ExtractAssociatedIcon
            try
            {
                string? resolved = ResolveFullPath(path);
                if (resolved != null && File.Exists(resolved))
                {
                    using (var ico = Icon.ExtractAssociatedIcon(resolved))
                    {
                        if (ico != null) return ico.ToBitmap();
                    }
                }
            }
            catch { }

            return null;
        }

        public static ImageSource GenerateGroupPreviewImage(AppGroup group)
        {
            using (var bmp = RenderGroupIconBitmap(group))
            {
                return BitmapToImageSource(bmp);
            }
        }

        public static Bitmap RenderGroupIconBitmap(AppGroup group)
        {
            const int size = 256;
            var canvas = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(System.Drawing.Color.Transparent);

                System.Drawing.Color accent = System.Drawing.Color.FromArgb(255, 58, 109, 240);
                try
                {
                    if (!string.IsNullOrWhiteSpace(group.AccentColorHex))
                        accent = ColorTranslator.FromHtml(group.AccentColorHex);
                }
                catch { }

                // Outer squircle container
                using (var bgPath = CreateRoundedRect(new Rectangle(10, 10, size - 20, size - 20), 58))
                using (var bgBrush = new System.Drawing.Drawing2D.LinearGradientBrush(
                           new Rectangle(10, 10, size - 20, size - 20),
                           System.Drawing.Color.FromArgb(245, Math.Min(255, 28 + accent.R / 7), Math.Min(255, 30 + accent.G / 7), Math.Min(255, 42 + accent.B / 7)),
                           System.Drawing.Color.FromArgb(250, 18, 19, 26),
                           LinearGradientMode.ForwardDiagonal))
                using (var borderPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(135, accent.R, accent.G, accent.B), 5f))
                {
                    g.FillPath(bgBrush, bgPath);
                    g.DrawPath(borderPen, bgPath);
                }

                bool drewContent = false;

                // 1. Custom Icon Mode
                if (group.IconMode == FolderIconMode.CustomIco && !string.IsNullOrWhiteSpace(group.CustomFolderIconPath))
                {
                    var customBmp = ExtractBitmap(group.CustomFolderIconPath!);
                    if (customBmp != null)
                    {
                        using (customBmp)
                        {
                            drewContent = true;
                            // Inner squircle is 10..246 (width 236), so 164x164 centered at (46, 46) gives a gorgeous macOS-style folder emblem
                            var dest = new Rectangle(46, 46, 164, 164);
                            g.DrawImage(customBmp, dest);
                        }
                    }
                }

                // 2. MiniGrid 2x2 Mode
                if (!drewContent && group.IconMode == FolderIconMode.MiniGrid2x2 && group.Items != null && group.Items.Count > 0)
                {
                    var subIcons = new List<Bitmap>();
                    for (int i = 0; i < group.Items.Count && subIcons.Count < 4; i++)
                    {
                        var b = ExtractBitmap(group.Items[i].TargetPath, group.Items[i].CustomIconPath);
                        if (b != null) subIcons.Add(b);
                    }

                    if (subIcons.Count > 0)
                    {
                        drewContent = true;
                        Rectangle[] slots =
                        {
                            new Rectangle(36, 36, 84, 84),
                            new Rectangle(136, 36, 84, 84),
                            new Rectangle(36, 136, 84, 84),
                            new Rectangle(136, 136, 84, 84)
                        };

                        for (int i = 0; i < subIcons.Count && i < 4; i++)
                        {
                            using (var subBmp = subIcons[i])
                            {
                                using (var slotPath = CreateRoundedRect(slots[i], 18))
                                using (var slotBrush = new SolidBrush(System.Drawing.Color.FromArgb(55, 255, 255, 255)))
                                {
                                    g.FillPath(slotBrush, slotPath);
                                }
                                var inner = Rectangle.Inflate(slots[i], -8, -8);
                                g.DrawImage(subBmp, inner);
                            }
                        }
                    }
                }

                // 3. Emoji Glyph Mode (or fallback)
                if (!drewContent)
                {
                    string glyph = string.IsNullOrWhiteSpace(group.IconGlyph) ? "📁" : group.IconGlyph;
                    using (var font = new Font("Segoe UI Emoji", 110, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(System.Drawing.Color.White))
                    {
                        var format = new StringFormat
                        {
                            Alignment = StringAlignment.Center,
                            LineAlignment = StringAlignment.Center
                        };
                        g.DrawString(glyph, font, brush, new RectangleF(10, 18, size - 20, size - 20), format);
                    }
                }
            }

            return canvas;
        }

        public static string GenerateGroupIcoFile(AppGroup group, string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);

            using (var canvas = RenderGroupIconBitmap(group))
            {
                byte[] pngBytes;
                using (var ms = new MemoryStream())
                {
                    canvas.Save(ms, ImageFormat.Png);
                    pngBytes = ms.ToArray();
                }

                // Versioned icon name based on hash of PNG bytes (solves Windows icon caching bug)
                string hash;
                using (var sha = System.Security.Cryptography.SHA1.Create())
                {
                    hash = BitConverter.ToString(sha.ComputeHash(pngBytes)).Replace("-", "").Substring(0, 8);
                }

                string icoPath = Path.Combine(outputDirectory, $"stackbar_{group.Id}_{hash}.ico");

                if (!File.Exists(icoPath))
                {
                    // Clean up older icons for this group
                    try
                    {
                        foreach (var old in Directory.GetFiles(outputDirectory, $"stackbar_{group.Id}_*.ico"))
                        {
                            File.Delete(old);
                        }
                    }
                    catch { }

                    SavePngBytesAsIco(pngBytes, icoPath);
                }

                return icoPath;
            }
        }

        private static void SavePngBytesAsIco(byte[] pngBytes, string icoPath)
        {
            using (var fs = new FileStream(icoPath, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
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
            using (var memory = new MemoryStream())
            {
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
        }

        public static string? ResolveFullPath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            if (File.Exists(fileName)) return Path.GetFullPath(fileName);

            var values = Environment.GetEnvironmentVariable("PATH");
            if (values == null) return null;

            foreach (var path in values.Split(Path.PathSeparator))
            {
                try
                {
                    var fullPath = Path.Combine(path, fileName);
                    if (File.Exists(fullPath)) return fullPath;
                }
                catch { }
            }
            return null;
        }
    }
}

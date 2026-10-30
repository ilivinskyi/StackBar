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

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHDefExtractIcon(string pszIconFile, int iIndex, uint uFlags, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIconSize);

        private static readonly Guid IShellItemImageFactoryGuid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");

        // Accessed from the UI thread and from the importer's background scan
        private static readonly Dictionary<string, ImageSource> _iconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ImageSource> _groupPreviewCache = new Dictionary<string, ImageSource>(StringComparer.Ordinal);

        public static ImageSource? ExtractIcon(GroupItem item) => ExtractIcon(item.TargetPath, item.CustomIconPath, item.CustomIconIndex);

        public static ImageSource? ExtractIcon(string targetPath, string? customIconPath = null, int iconIndex = 0)
        {
            string key = (!string.IsNullOrWhiteSpace(customIconPath) ? customIconPath : targetPath) ?? string.Empty;
            if (string.IsNullOrEmpty(key)) return null;
            key += "," + iconIndex;

            lock (_iconCache)
            {
                if (_iconCache.TryGetValue(key, out var cached))
                    return cached;
            }

            using (var bmp = ExtractBitmap(targetPath, customIconPath, iconIndex))
            {
                if (bmp == null) return null;
                var src = BitmapToImageSource(bmp);
                lock (_iconCache)
                {
                    _iconCache[key] = src;
                }
                return src;
            }
        }

        public static Bitmap? ExtractBitmap(string targetPath, string? customIconPath = null, int iconIndex = 0)
        {
            bool useCustom = !string.IsNullOrWhiteSpace(customIconPath) && File.Exists(customIconPath);
            string path = useCustom ? customIconPath! : targetPath;
            if (!useCustom) iconIndex = 0;

            if (string.IsNullOrWhiteSpace(path)) return null;

            // Icon resources inside a binary: the shell item image is only the file's own (first) icon,
            // so a specific index (e.g. shell32.dll,-16) or a DLL/icon library needs explicit extraction.
            string resourceExt = Path.GetExtension(path);
            bool isIconLibrary = resourceExt.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                                 resourceExt.Equals(".icl", StringComparison.OrdinalIgnoreCase) ||
                                 resourceExt.Equals(".cpl", StringComparison.OrdinalIgnoreCase);
            bool isBinary = isIconLibrary || resourceExt.Equals(".exe", StringComparison.OrdinalIgnoreCase);
            if (isBinary && (iconIndex != 0 || isIconLibrary) && File.Exists(path))
            {
                var fromResource = ExtractIconResource(path, iconIndex);
                if (fromResource != null) return fromResource;
            }

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
                                return HBitmapToBitmapPreservingAlpha(hBitmap);
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

            // 2. Fallback for .lnk shortcuts: Try extracting from target .exe
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                try
                {
                    var item = ShortcutService.ParseShortcut(path);
                    if (!string.IsNullOrWhiteSpace(item.CustomIconPath) && !item.CustomIconPath!.Equals(path, StringComparison.OrdinalIgnoreCase) && File.Exists(item.CustomIconPath))
                    {
                        var bmp = ExtractBitmap(item.CustomIconPath!, item.CustomIconPath, item.CustomIconIndex);
                        if (bmp != null) return bmp;
                    }
                }
                catch { }
            }

            // 3. Fallback to SHGetFileInfo (queries Windows Explorer's registered icon)
            try
            {
                if (File.Exists(path))
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
            }
            catch { }

            // 4. Fallback to Icon.ExtractAssociatedIcon
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

        private static Bitmap? ExtractIconResource(string path, int iconIndex)
        {
            try
            {
                const uint size = 64;
                int hr = SHDefExtractIcon(path, iconIndex, 0, out IntPtr hLarge, out IntPtr hSmall, size | (16u << 16));
                if (hSmall != IntPtr.Zero) DestroyIcon(hSmall);
                if (hr != 0 || hLarge == IntPtr.Zero) return null;
                try
                {
                    using (var ico = Icon.FromHandle(hLarge))
                    {
                        return ico.ToBitmap();
                    }
                }
                finally
                {
                    DestroyIcon(hLarge);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Image.FromHbitmap labels a 32-bit DIB as Format32bppRgb and so drops the alpha channel, which turns
        /// transparent icon pixels black. The pixel bytes still contain the (premultiplied) alpha, so reinterpret them.
        /// </summary>
        internal static Bitmap HBitmapToBitmapPreservingAlpha(IntPtr hBitmap)
        {
            using (var raw = Image.FromHbitmap(hBitmap))
            {
                if (Image.GetPixelFormatSize(raw.PixelFormat) < 32)
                    return new Bitmap(raw);

                var rect = new Rectangle(0, 0, raw.Width, raw.Height);
                var data = raw.LockBits(rect, ImageLockMode.ReadOnly, raw.PixelFormat);
                try
                {
                    if (!HasAnyAlpha(data))
                        return new Bitmap(raw); // genuinely opaque bitmap: alpha bytes are all zero

                    using (var premultiplied = new Bitmap(data.Width, data.Height, data.Stride, System.Drawing.Imaging.PixelFormat.Format32bppPArgb, data.Scan0))
                    {
                        var result = new Bitmap(data.Width, data.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        using (var g = Graphics.FromImage(result))
                        {
                            g.DrawImage(premultiplied, new Rectangle(0, 0, data.Width, data.Height));
                        }
                        return result;
                    }
                }
                finally
                {
                    raw.UnlockBits(data);
                }
            }
        }

        internal static bool HasAnyAlpha(BitmapData data)
        {
            var row = new byte[Math.Abs(data.Stride)];
            for (int y = 0; y < data.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                for (int x = 3; x < data.Width * 4; x += 4)
                {
                    if (row[x] != 0) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Group thumbnail for the Studio sidebar. Cached by everything that affects the picture, because
        /// the sidebar binding asks for it on every refresh and rendering extracts up to four app icons.
        /// </summary>
        public static ImageSource GenerateGroupPreviewImage(AppGroup group)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(group.IconMode).Append('|').Append(group.AccentColorHex).Append('|').Append(group.IconGlyph)
              .Append('|').Append(group.CustomFolderIconPath);
            if (group.Items != null)
            {
                for (int i = 0; i < group.Items.Count && i < 4; i++)
                {
                    var it = group.Items[i];
                    sb.Append('|').Append(it.TargetPath).Append(',').Append(it.CustomIconPath).Append(',').Append(it.CustomIconIndex);
                }
            }
            string key = sb.ToString();

            lock (_groupPreviewCache)
            {
                if (_groupPreviewCache.TryGetValue(key, out var cached)) return cached;
            }

            ImageSource image;
            using (var bmp = RenderGroupIconBitmap(group))
            {
                image = BitmapToImageSource(bmp);
            }

            lock (_groupPreviewCache)
            {
                if (_groupPreviewCache.Count > 64) _groupPreviewCache.Clear();
                _groupPreviewCache[key] = image;
            }
            return image;
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
                        var b = ExtractBitmap(group.Items[i].TargetPath, group.Items[i].CustomIconPath, group.Items[i].CustomIconIndex);
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

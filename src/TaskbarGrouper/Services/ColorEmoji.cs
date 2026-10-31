using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace TaskbarGrouper.Services
{
    /// <summary>
    /// Draws emoji in color. WPF on .NET Framework and GDI+ only render the monochrome outline of
    /// Segoe UI Emoji, so this reads the font's own color layers (COLR v0 + CPAL) and fills each
    /// layer glyph with its palette color. Sequences the font builds through ligatures (ZWJ, flags,
    /// keycaps) have no single base glyph: callers fall back to plain text for those.
    /// </summary>
    public static class ColorEmoji
    {
        public readonly struct Layer
        {
            public Layer(Geometry geometry, Color? color)
            {
                Geometry = geometry;
                Color = color;
            }

            public Geometry Geometry { get; }
            /// <summary>Null means "draw in the text color" (palette index 0xFFFF).</summary>
            public Color? Color { get; }
        }

        private const double DesignSize = 100;
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, IReadOnlyList<Layer>?> _layerCache = new Dictionary<string, IReadOnlyList<Layer>?>();
        private static readonly Dictionary<string, ImageSource?> _imageCache = new Dictionary<string, ImageSource?>();
        private static bool _loaded;
        private static GlyphTypeface? _typeface;
        private static byte[]? _colr;
        private static Color[]? _palette;
        private static int _baseCount;
        private static int _baseOffset;
        private static int _layerOffset;

        internal static string FontPath { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "seguiemj.ttf");

        /// <summary>
        /// The color layers of <paramref name="emoji"/> at a 100-unit em size, frozen, or null when the emoji
        /// has no color form this renderer can draw (or the font is missing).
        /// </summary>
        public static IReadOnlyList<Layer>? GetLayers(string? emoji)
        {
            if (string.IsNullOrWhiteSpace(emoji)) return null;
            lock (_lock)
            {
                if (_layerCache.TryGetValue(emoji!, out var cached)) return cached;
                IReadOnlyList<Layer>? layers = null;
                try
                {
                    layers = BuildLayers(emoji!);
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"Color emoji rendering failed for '{emoji}': {ex.Message}");
                }
                _layerCache[emoji!] = layers;
                return layers;
            }
        }

        /// <summary>A frozen vector image of the emoji, or null to fall back to text.</summary>
        public static ImageSource? GetImage(string? emoji)
        {
            if (string.IsNullOrWhiteSpace(emoji)) return null;
            lock (_lock)
            {
                if (_imageCache.TryGetValue(emoji!, out var cached)) return cached;
            }

            ImageSource? image = null;
            var layers = GetLayers(emoji);
            if (layers != null)
            {
                var group = new DrawingGroup();
                // A transparent em square keeps every emoji the same size and centered, whatever its own bounds
                var box = SquareAround(Bounds(layers));
                group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(box)));
                foreach (var layer in layers)
                {
                    var brush = new SolidColorBrush(layer.Color ?? Colors.White);
                    brush.Freeze();
                    group.Children.Add(new GeometryDrawing(brush, null, layer.Geometry));
                }
                group.Freeze();
                var drawingImage = new DrawingImage(group);
                drawingImage.Freeze();
                image = drawingImage;
            }

            lock (_lock)
            {
                _imageCache[emoji!] = image;
            }
            return image;
        }

        /// <summary>The union of all layer bounds, in the 100-unit design space.</summary>
        public static Rect Bounds(IReadOnlyList<Layer> layers)
        {
            Rect bounds = Rect.Empty;
            foreach (var layer in layers) bounds.Union(layer.Geometry.Bounds);
            return bounds;
        }

        public static Rect SquareAround(Rect bounds)
        {
            double side = Math.Max(bounds.Width, bounds.Height);
            return new Rect(bounds.X + (bounds.Width - side) / 2, bounds.Y + (bounds.Height - side) / 2, side, side);
        }

        private static IReadOnlyList<Layer>? BuildLayers(string emoji)
        {
            if (!EnsureLoaded()) return null;

            // U+FE0F only asks for the emoji presentation, which a color glyph already is
            string text = emoji.Trim().Replace("️", string.Empty);
            if (text.Length == 0) return null;
            bool pair = char.IsSurrogatePair(text, 0);
            if (char.IsSurrogate(text[0]) && !pair) return null;
            if (text.Length != (pair ? 2 : 1)) return null; // a sequence, not one code point
            int codePoint = char.ConvertToUtf32(text, 0);

            if (!_typeface!.CharacterToGlyphMap.TryGetValue(codePoint, out ushort baseGlyph)) return null;
            if (!TryFindBaseRecord(baseGlyph, out int firstLayer, out int layerCount) || layerCount == 0) return null;

            var layers = new List<Layer>(layerCount);
            for (int i = 0; i < layerCount; i++)
            {
                int record = _layerOffset + (firstLayer + i) * 4;
                ushort glyph = ReadUInt16(_colr!, record);
                ushort paletteIndex = ReadUInt16(_colr!, record + 2);

                var geometry = _typeface.GetGlyphOutline(glyph, DesignSize, DesignSize);
                if (geometry == null || geometry.IsEmpty()) continue;
                geometry.Freeze();

                Color? color = paletteIndex == 0xFFFF || paletteIndex >= _palette!.Length
                    ? (Color?)null
                    : _palette[paletteIndex];
                layers.Add(new Layer(geometry, color));
            }
            return layers.Count > 0 ? layers : null;
        }

        private static bool TryFindBaseRecord(ushort glyph, out int firstLayer, out int layerCount)
        {
            firstLayer = layerCount = 0;
            int lo = 0, hi = _baseCount - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                int record = _baseOffset + mid * 6;
                ushort id = ReadUInt16(_colr!, record);
                if (id == glyph)
                {
                    firstLayer = ReadUInt16(_colr!, record + 2);
                    layerCount = ReadUInt16(_colr!, record + 4);
                    return true;
                }
                if (id < glyph) lo = mid + 1; else hi = mid - 1;
            }
            return false;
        }

        private static bool EnsureLoaded()
        {
            if (_loaded) return _typeface != null;
            _loaded = true;
            try
            {
                if (!File.Exists(FontPath)) return false;
                var typeface = new GlyphTypeface(new Uri(FontPath));

                byte[]? colr, cpal;
                using (var stream = typeface.GetFontStream())
                {
                    colr = ReadTable(stream, "COLR");
                    cpal = ReadTable(stream, "CPAL");
                }
                if (colr == null || cpal == null || colr.Length < 14 || cpal.Length < 12) return false;

                _baseCount = ReadUInt16(colr, 2);
                _baseOffset = (int)ReadUInt32(colr, 4);
                _layerOffset = (int)ReadUInt32(colr, 8);

                int entries = ReadUInt16(cpal, 2);
                int recordsOffset = (int)ReadUInt32(cpal, 8);
                int firstRecord = ReadUInt16(cpal, 12); // palette 0
                var palette = new Color[entries];
                for (int i = 0; i < entries; i++)
                {
                    int p = recordsOffset + (firstRecord + i) * 4; // stored as B, G, R, A
                    palette[i] = Color.FromArgb(cpal[p + 3], cpal[p + 2], cpal[p + 1], cpal[p]);
                }

                _colr = colr;
                _palette = palette;
                _typeface = typeface;
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Log("Color emoji font could not be loaded: " + ex.Message);
                return false;
            }
        }

        private static byte[]? ReadTable(Stream stream, string tag)
        {
            var header = new byte[12];
            stream.Position = 0;
            if (stream.Read(header, 0, 12) != 12) return null;
            int tableCount = ReadUInt16(header, 4);

            var directory = new byte[tableCount * 16];
            if (stream.Read(directory, 0, directory.Length) != directory.Length) return null;
            for (int i = 0; i < tableCount; i++)
            {
                int entry = i * 16;
                string entryTag = System.Text.Encoding.ASCII.GetString(directory, entry, 4);
                if (entryTag != tag) continue;

                long offset = ReadUInt32(directory, entry + 8);
                int length = (int)ReadUInt32(directory, entry + 12);
                var table = new byte[length];
                stream.Position = offset;
                int read = 0;
                while (read < length)
                {
                    int n = stream.Read(table, read, length - read);
                    if (n <= 0) return null;
                    read += n;
                }
                return table;
            }
            return null;
        }

        private static ushort ReadUInt16(byte[] data, int offset) => (ushort)(data[offset] << 8 | data[offset + 1]);

        private static uint ReadUInt32(byte[] data, int offset) =>
            (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);
    }
}

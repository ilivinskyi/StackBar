using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using TaskbarGrouper.Services;
using Xunit;

namespace StackBar.Tests
{
    public class ColorEmojiTests
    {
        [Fact]
        public void Emoji_is_built_from_several_colored_layers()
        {
            var layers = ColorEmoji.GetLayers("🎮");

            Assert.NotNull(layers);
            Assert.True(layers!.Count > 1);
            Assert.True(layers.Where(l => l.Color.HasValue).Select(l => l.Color!.Value).Distinct().Count() > 1);
        }

        [Fact]
        public void Emoji_presentation_selector_is_ignored()
        {
            var plain = ColorEmoji.GetLayers("🛠");
            var withSelector = ColorEmoji.GetLayers("🛠️");

            Assert.NotNull(withSelector);
            Assert.Equal(plain!.Count, withSelector!.Count);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("  ")]
        [InlineData("A")]
        [InlineData("ab")]
        [InlineData("👨‍💻")] // ZWJ sequence: built by ligature, no single base glyph
        [InlineData("\uD83C")] // lone surrogate
        public void Text_without_a_single_color_glyph_falls_back(string? text)
        {
            Assert.Null(ColorEmoji.GetLayers(text));
            Assert.Null(ColorEmoji.GetImage(text));
        }

        [Fact]
        public void Image_is_a_frozen_square()
        {
            var image = ColorEmoji.GetImage("🚀");

            Assert.NotNull(image);
            Assert.True(image!.IsFrozen);
            Assert.Equal(image.Width, image.Height, 3);
        }

        [Fact]
        public void Taskbar_icon_drawing_paints_the_emoji_in_color()
        {
            using (var bmp = new Bitmap(64, 64, PixelFormat.Format32bppArgb))
            {
                bool drawn;
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    drawn = IconHelper.DrawColorEmoji(g, "🔥", new RectangleF(0, 0, 64, 64));
                }

                Assert.True(drawn);
                bool hasColor = false;
                for (int y = 0; y < 64 && !hasColor; y++)
                for (int x = 0; x < 64 && !hasColor; x++)
                {
                    var p = bmp.GetPixel(x, y);
                    hasColor = p.A == 255 && p.R - p.B > 80; // the flame is orange-red, the old outline was white
                }
                Assert.True(hasColor);
            }
        }

        [Fact]
        public void Taskbar_icon_drawing_reports_when_it_cannot_draw()
        {
            using (var bmp = new Bitmap(8, 8))
            using (var g = Graphics.FromImage(bmp))
            {
                Assert.False(IconHelper.DrawColorEmoji(g, "ab", new RectangleF(0, 0, 8, 8)));
            }
        }
    }
}

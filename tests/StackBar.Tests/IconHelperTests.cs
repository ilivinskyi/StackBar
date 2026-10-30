using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using TaskbarGrouper.Services;
using Xunit;

namespace StackBar.Tests
{
    public class IconHelperTests
    {
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [Fact]
        public void HBitmap_conversion_keeps_transparent_pixels_transparent()
        {
            using (var source = new Bitmap(8, 8, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(source))
                {
                    g.Clear(Color.Transparent);
                    g.FillRectangle(Brushes.Red, 0, 0, 4, 8);
                }

                IntPtr hBitmap = source.GetHbitmap(Color.FromArgb(0, 0, 0, 0));
                try
                {
                    using (var result = IconHelper.HBitmapToBitmapPreservingAlpha(hBitmap))
                    {
                        Assert.Equal(0, result.GetPixel(6, 4).A);
                        var opaque = result.GetPixel(1, 4);
                        Assert.Equal(255, opaque.A);
                        Assert.Equal(255, opaque.R);
                    }
                }
                finally
                {
                    DeleteObject(hBitmap);
                }
            }
        }

        [Fact]
        public void HBitmap_conversion_keeps_opaque_bitmaps_without_alpha_visible()
        {
            using (var source = new Bitmap(4, 4, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(source)) g.Clear(Color.Blue);

                IntPtr hBitmap = source.GetHbitmap();
                try
                {
                    using (var result = IconHelper.HBitmapToBitmapPreservingAlpha(hBitmap))
                    {
                        var pixel = result.GetPixel(2, 2);
                        Assert.Equal(255, pixel.A);
                        Assert.Equal(255, pixel.B);
                    }
                }
                finally
                {
                    DeleteObject(hBitmap);
                }
            }
        }
    }
}

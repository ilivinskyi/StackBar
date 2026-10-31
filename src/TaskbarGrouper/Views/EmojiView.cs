using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TaskbarGrouper.Services;

namespace TaskbarGrouper.Views
{
    /// <summary>
    /// Shows an emoji in color (see <see cref="ColorEmoji"/>), or as plain text when it has no color form.
    /// </summary>
    public sealed class EmojiView : ContentControl
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(EmojiView), new PropertyMetadata(string.Empty, (d, _) => ((EmojiView)d).Refresh()));

        public static readonly DependencyProperty EmojiSizeProperty = DependencyProperty.Register(
            nameof(EmojiSize), typeof(double), typeof(EmojiView), new PropertyMetadata(16.0, (d, _) => ((EmojiView)d).Refresh()));

        public EmojiView()
        {
            Focusable = false;
            IsTabStop = false;
            VerticalAlignment = VerticalAlignment.Center;
            Refresh();
        }

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        /// <summary>Side of the square the emoji is drawn in, in device-independent pixels.</summary>
        public double EmojiSize
        {
            get => (double)GetValue(EmojiSizeProperty);
            set => SetValue(EmojiSizeProperty, value);
        }

        private void Refresh()
        {
            var image = ColorEmoji.GetImage(Text);
            if (image != null)
            {
                Content = new Image
                {
                    Source = image,
                    Width = EmojiSize,
                    Height = EmojiSize,
                    Stretch = Stretch.Uniform,
                    SnapsToDevicePixels = true
                };
                return;
            }

            // Font size that draws a glyph about EmojiSize tall
            Content = new TextBlock
            {
                Text = Text,
                FontFamily = new FontFamily("Segoe UI Emoji"),
                FontSize = EmojiSize * 0.85,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
    }
}

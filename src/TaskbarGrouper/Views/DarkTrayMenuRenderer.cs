using System.Drawing;
using System.Windows.Forms;

namespace TaskbarGrouper.Views
{
    /// <summary>Dark colors for the tray icon's WinForms menu, matching the WPF theme in Themes/Dark.xaml.</summary>
    internal sealed class DarkTrayMenuRenderer : ToolStripProfessionalRenderer
    {
        private static readonly Color Background = Color.FromArgb(0x1C, 0x1E, 0x2A);
        private static readonly Color Highlight = Color.FromArgb(0x30, 0x33, 0x44);
        private static readonly Color Border = Color.FromArgb(0x44, 0x46, 0x55);
        private static readonly Color Text = Color.FromArgb(0xEE, 0xEE, 0xEE);

        public DarkTrayMenuRenderer() : base(new DarkColors())
        {
            RoundedEdges = false;
        }

        public static void Apply(ContextMenuStrip menu)
        {
            menu.Renderer = new DarkTrayMenuRenderer();
            menu.BackColor = Background;
            menu.ForeColor = Text;
            menu.ShowImageMargin = false;
            menu.Padding = new Padding(2, 4, 2, 4);
            foreach (ToolStripItem item in menu.Items)
            {
                item.ForeColor = Text;
                item.Padding = new Padding(4, 3, 4, 3);
            }
        }

        // The base renderer ignores the color table for the hovered item and paints the light system highlight
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var bounds = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
            using (var brush = new SolidBrush(e.Item.Selected && e.Item.Enabled ? Highlight : Background))
            {
                e.Graphics.FillRectangle(brush, bounds);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var pen = new Pen(Border))
            {
                e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
            }
        }

        private sealed class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Background;
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => Highlight;
            public override Color MenuItemSelected => Highlight;
            public override Color MenuItemSelectedGradientBegin => Highlight;
            public override Color MenuItemSelectedGradientEnd => Highlight;
            public override Color MenuItemPressedGradientBegin => Highlight;
            public override Color MenuItemPressedGradientEnd => Highlight;
            public override Color ImageMarginGradientBegin => Background;
            public override Color ImageMarginGradientMiddle => Background;
            public override Color ImageMarginGradientEnd => Background;
            public override Color SeparatorDark => Border;
            public override Color SeparatorLight => Background;
        }
    }
}

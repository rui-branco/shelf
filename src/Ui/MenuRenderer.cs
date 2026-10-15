using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Shelf
{
    /// <summary>
    /// Context-menu renderer in the Windows 11 style: flat themed background,
    /// one icon column of Fluent glyphs so every label starts at the same
    /// place, rounded selection inset from the edges, and separators inset to
    /// exactly the same width as the selection.
    /// </summary>
    public class MenuRenderer : ToolStripProfessionalRenderer
    {
        /// <summary>
        /// Whether to draw the menu's edge ourselves. Off on Windows 11, which
        /// rounds the window and draws a rounded edge of its own; a square edge
        /// drawn inside that one was what showed in the corners.
        /// </summary>
        public bool DrawBorder = true;

        /// <summary>The selection's inset from the item's sides; separators match it.</summary>
        const int Inset = 2;

        // The row layout, in item coordinates, measured off a rendered menu:
        // the icon's ink starts 12px from the menu's edge and every label
        // starts 40px in, whatever WinForms would have done with its margins.
        const int IconLeft = 12;
        const int IconSize = 16;
        const int TextLeft = 36;

        public MenuRenderer() : base(new MenuColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(Theme.MenuBack))
                e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (!DrawBorder) return;
            using (Pen p = new Pen(Theme.MenuBorder))
                e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // Same as the background: no separate gutter.
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(Inset, 1, e.Item.Width - Inset * 2, e.Item.Height - 2);
            Theme.FillRounded(e.Graphics, Theme.MenuHover, r, 4);
        }

        /// <summary>
        /// Drawn here rather than where WinForms places it: WinForms puts the
        /// label after an image margin plus its own text padding, which left it
        /// floating off to the right of a column it did not line up with.
        /// </summary>
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // Solid, not the translucent faint colour: GDI text drops alpha and
            // would draw a disabled item as bright as an enabled one.
            Color c = e.Item.Enabled ? Theme.Text : Blend(Theme.TextFaint, Theme.MenuBack);
            if (!(e.Item is ToolStripMenuItem) || !(e.ToolStrip is ToolStripDropDown))
            {
                e.TextColor = c;
                base.OnRenderItemText(e);
                return;
            }

            Rectangle r = new Rectangle(TextLeft, 0, e.Item.Width - TextLeft - 8, e.Item.Height);
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, r, c,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                | TextFormatFlags.EndEllipsis | TextFormatFlags.HidePrefix);
        }

        static Color Blend(Color top, Color under)
        {
            int a = top.A;
            return Color.FromArgb(
                (top.R * a + under.R * (255 - a)) / 255,
                (top.G * a + under.G * (255 - a)) / 255,
                (top.B * a + under.B * (255 - a)) / 255);
        }

        static RectangleF IconRect(ToolStripItem item)
        {
            return new RectangleF(IconLeft, (item.Height - IconSize) / 2f, IconSize, IconSize);
        }

        /// <summary>
        /// The icon column. Items carry their glyph in Tag and a blank image
        /// that only reserves the space (see Theme.MenuItem); the glyph is drawn
        /// here instead, in the current theme's colour.
        /// </summary>
        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
        {
            string glyph = e.Item.Tag as string;
            if (string.IsNullOrEmpty(glyph)) return;

            Theme.DrawGlyph(e.Graphics, glyph, 16f, e.Item.Enabled ? Theme.Text : Theme.TextFaint, IconRect(e.Item));
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Theme.DrawGlyph(e.Graphics, Theme.Glyph(0xE73E), 14f, Theme.Accent, IconRect(e.Item));
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (Pen p = new Pen(Theme.MenuBorder))
                e.Graphics.DrawLine(p, Inset, y, e.Item.Width - Inset, y);
        }
    }

    class MenuColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return Theme.MenuHover; } }
        public override Color MenuBorder { get { return Theme.MenuBorder; } }
        public override Color MenuItemBorder { get { return Color.Transparent; } }
        public override Color ImageMarginGradientBegin { get { return Theme.MenuBack; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.MenuBack; } }
        public override Color ImageMarginGradientEnd { get { return Theme.MenuBack; } }
        public override Color SeparatorDark { get { return Theme.MenuBorder; } }
        public override Color SeparatorLight { get { return Theme.MenuBorder; } }
        public override Color ToolStripDropDownBackground { get { return Theme.MenuBack; } }
        public override Color CheckBackground { get { return Color.Transparent; } }
        public override Color CheckSelectedBackground { get { return Color.Transparent; } }
        public override Color CheckPressedBackground { get { return Color.Transparent; } }
    }
}

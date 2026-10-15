using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Shelf
{
    /// <summary>
    /// The Shelf mark: an arrow dropping into a tray. One drawing, used for the
    /// taskbar pill and, through tools/IconGen.cs, for the exe icon and the
    /// README logo, so the three can never drift apart.
    ///
    /// Self-contained on purpose (System.Drawing only) so the icon generator can
    /// compile this file on its own without dragging WinForms along.
    ///
    /// Everything is a fraction of the box, and the shapes are deliberately
    /// chunky: the mark spends most of its life 24 pixels tall on the taskbar,
    /// where anything thinner than a couple of pixels turns to mush.
    /// </summary>
    public static class Logo
    {
        static readonly Color ArrowTop = Color.FromArgb(0x6B, 0xE4, 0xFF);
        static readonly Color ArrowBottom = Color.FromArgb(0x21, 0x86, 0xFF);
        static readonly Color TrayTop = Color.FromArgb(0x8F, 0x80, 0xFF);
        static readonly Color TrayBottom = Color.FromArgb(0x51, 0x40, 0xE8);
        static readonly Color InsideTop = Color.FromArgb(0x2A, 0x1F, 0x96);
        static readonly Color InsideBottom = Color.FromArgb(0x45, 0x36, 0xD0);

        public static void Draw(Graphics g, RectangleF box)
        {
            float s = Math.Min(box.Width, box.Height);
            float x = box.X + (box.Width - s) / 2f;
            float y = box.Y + (box.Height - s) / 2f;

            SmoothingMode oldSmooth = g.SmoothingMode;
            PixelOffsetMode oldOffset = g.PixelOffsetMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            try
            {
                // 1. The inside of the tray: the opening the arrow drops into.
                // Shaded darker at the back so it reads as depth rather than
                // as two dark blobs either side of the arrow.
                RectangleF inside = new RectangleF(x + 0.13f * s, y + 0.53f * s, 0.74f * s, 0.16f * s);
                using (GraphicsPath p = Rounded(inside, 0.08f * s, 0.08f * s, 0, 0))
                using (LinearGradientBrush b = Vertical(inside, InsideTop, InsideBottom))
                    g.FillPath(b, p);

                // 2. The arrow. Filled, then stroked in the same brush with a
                // round join, which softens every corner without a curve per
                // corner.
                RectangleF arrowBounds = new RectangleF(x, y + 0.03f * s, s, 0.78f * s);
                using (GraphicsPath p = Arrow(x, y, s))
                using (LinearGradientBrush b = Vertical(arrowBounds, ArrowTop, ArrowBottom))
                using (Pen soft = new Pen(b, Math.Max(1f, 0.08f * s)))
                {
                    soft.LineJoin = LineJoin.Round;
                    g.FillPath(b, p);
                    g.DrawPath(soft, p);
                }

                // 3. The tray front, drawn over the arrow's tip so the arrow
                // goes into the tray instead of resting on it.
                RectangleF front = new RectangleF(x + 0.05f * s, y + 0.60f * s, 0.90f * s, 0.35f * s);
                using (GraphicsPath p = Rounded(front, 0.06f * s, 0.06f * s, 0.15f * s, 0.15f * s))
                using (LinearGradientBrush b = Vertical(front, TrayTop, TrayBottom))
                    g.FillPath(b, p);

                // The lip catches the light. Below 20px it is under a pixel and
                // only muddies the edge.
                if (s >= 20)
                {
                    float w = Math.Max(1f, s / 64f);
                    using (Pen hi = new Pen(Color.FromArgb(120, 255, 255, 255), w))
                        g.DrawLine(hi, front.X + 0.08f * s, front.Y + w / 2f,
                                       front.Right - 0.08f * s, front.Y + w / 2f);
                }
            }
            finally
            {
                g.SmoothingMode = oldSmooth;
                g.PixelOffsetMode = oldOffset;
            }
        }

        static GraphicsPath Arrow(float x, float y, float s)
        {
            float cx = x + 0.5f * s;
            float shaft = 0.12f * s;      // half the shaft width
            float head = 0.29f * s;       // half the head width
            float top = y + 0.07f * s;
            float headTop = y + 0.41f * s;
            float tip = y + 0.76f * s;

            GraphicsPath p = new GraphicsPath();
            p.AddPolygon(new PointF[]
            {
                new PointF(cx - shaft, top),
                new PointF(cx + shaft, top),
                new PointF(cx + shaft, headTop),
                new PointF(cx + head, headTop),
                new PointF(cx, tip),
                new PointF(cx - head, headTop),
                new PointF(cx - shaft, headTop),
            });
            return p;
        }

        /// <summary>
        /// A vertical gradient that covers its rectangle exactly. Padded by a
        /// pixel each way: GDI+ tiles a gradient past its end points, so a
        /// stroke that pokes out of the box picks up the opposite colour as a
        /// hard band along the edge.
        /// </summary>
        static LinearGradientBrush Vertical(RectangleF r, Color top, Color bottom)
        {
            LinearGradientBrush b = new LinearGradientBrush(
                new PointF(r.X, r.Y - 1f), new PointF(r.X, r.Bottom + 1f), top, bottom);
            b.WrapMode = WrapMode.TileFlipXY;
            return b;
        }

        static GraphicsPath Rounded(RectangleF r, float tl, float tr, float br, float bl)
        {
            GraphicsPath p = new GraphicsPath();
            Corner(p, r.X, r.Y, tl, 180);
            Corner(p, r.Right - tr * 2, r.Y, tr, 270);
            Corner(p, r.Right - br * 2, r.Bottom - br * 2, br, 0);
            Corner(p, r.X, r.Bottom - bl * 2, bl, 90);
            p.CloseFigure();
            return p;
        }

        static void Corner(GraphicsPath p, float x, float y, float radius, float start)
        {
            // With no radius the arc's box collapses onto the corner itself, so
            // (x, y) already is the corner point.
            if (radius > 0) p.AddArc(x, y, radius * 2, radius * 2, start, 90);
            else p.AddLine(x, y, x, y);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Shelf
{
    /// <summary>
    /// Colours, fonts and shapes, following the Windows 11 flyout look and the
    /// user's own light/dark and accent settings. Everything here is read at
    /// paint time, so flipping Windows between light and dark only needs a
    /// Refresh and a repaint, not new windows.
    /// </summary>
    public static class Theme
    {
        /// <summary>Apps use the light theme: drives the panel and menus.</summary>
        public static bool Light { get; private set; }

        /// <summary>The system (taskbar) uses the light theme: drives the pill.</summary>
        public static bool LightTaskbar { get; private set; }

        static Color _accentDark = Color.FromArgb(0x00, 0x5F, 0xB8);   // Windows' default Dark 1
        static Color _accentLight = Color.FromArgb(0x60, 0xCD, 0xFF);  // and Light 2

        static Theme()
        {
            Refresh();
        }

        /// <summary>
        /// Re-reads light/dark and the accent colour. Cheap; called on start and
        /// whenever Windows broadcasts a settings change.
        /// </summary>
        public static void Refresh()
        {
            const string personalize = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            Light = ReadDword(personalize, "AppsUseLightTheme", 0) != 0;
            LightTaskbar = ReadDword(personalize, "SystemUsesLightTheme", 0) != 0;

            // The palette Windows derives from the accent: Light 3..1, the
            // accent itself, Dark 1..3, as RGBA quads. Controls use Light 2 on
            // dark surfaces and Dark 1 on light ones, so the accent stays
            // readable whichever way round the theme is.
            try
            {
                byte[] p = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent",
                    "AccentPalette", null) as byte[];
                if (p != null && p.Length >= 32)
                {
                    _accentLight = Color.FromArgb(p[4], p[5], p[6]);
                    _accentDark = Color.FromArgb(p[16], p[17], p[18]);
                }
            }
            catch { }
        }

        static int ReadDword(string key, string name, int fallback)
        {
            try
            {
                object v = Registry.GetValue(key, name, null);
                if (v is int) return (int)v;
            }
            catch { }
            return fallback;
        }

        // ---------- panel ----------

        /// <summary>The accent as it should appear on the panel.</summary>
        public static Color Accent { get { return Light ? _accentDark : _accentLight; } }

        /// <summary>The accent as it should appear on the taskbar, which has its own theme.</summary>
        public static Color TaskbarAccent { get { return LightTaskbar ? _accentDark : _accentLight; } }

        /// <summary>Laid over the blurred backdrop. Heavy enough to keep labels legible over anything.</summary>
        public static Color PanelTint
        {
            get { return Light ? Color.FromArgb(214, 243, 243, 243) : Color.FromArgb(212, 32, 32, 32); }
        }

        /// <summary>Solid stand-in when the backdrop could not be captured.</summary>
        public static Color PanelSolid
        {
            get { return Light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(40, 40, 40); }
        }

        public static Color Text
        {
            get { return Light ? Color.FromArgb(0x1B, 0x1B, 0x1B) : Color.White; }
        }

        public static Color TextDim
        {
            get { return Light ? Color.FromArgb(158, 0, 0, 0) : Color.FromArgb(166, 255, 255, 255); }
        }

        public static Color TextFaint
        {
            get { return Light ? Color.FromArgb(105, 0, 0, 0) : Color.FromArgb(110, 255, 255, 255); }
        }

        public static Color Hover
        {
            get { return Light ? Color.FromArgb(10, 0, 0, 0) : Color.FromArgb(16, 255, 255, 255); }
        }

        public static Color Pressed
        {
            get { return Light ? Color.FromArgb(6, 0, 0, 0) : Color.FromArgb(10, 255, 255, 255); }
        }

        public static Color Divider
        {
            get { return Light ? Color.FromArgb(20, 0, 0, 0) : Color.FromArgb(22, 255, 255, 255); }
        }

        /// <summary>Hairline just outside the panel, separating it from what is behind.</summary>
        public static Color OuterStroke
        {
            get { return Light ? Color.FromArgb(30, 0, 0, 0) : Color.FromArgb(120, 0, 0, 0); }
        }

        /// <summary>Hairline just inside the panel edge, the lit rim Windows 11 surfaces have.</summary>
        public static Color InnerStroke
        {
            get { return Light ? Color.FromArgb(150, 255, 255, 255) : Color.FromArgb(24, 255, 255, 255); }
        }

        public static int ShadowAlpha { get { return Light ? 48 : 110; } }

        public static Color Danger
        {
            get { return Light ? Color.FromArgb(0xC4, 0x2B, 0x1C) : Color.FromArgb(0xFF, 0x99, 0xA4); }
        }

        // ---------- taskbar pill ----------

        public static Color TaskbarHover
        {
            get { return LightTaskbar ? Color.FromArgb(12, 0, 0, 0) : Color.FromArgb(18, 255, 255, 255); }
        }

        public static Color TaskbarActive
        {
            get { return LightTaskbar ? Color.FromArgb(18, 0, 0, 0) : Color.FromArgb(26, 255, 255, 255); }
        }

        public static Color TaskbarPressed
        {
            get { return LightTaskbar ? Color.FromArgb(8, 0, 0, 0) : Color.FromArgb(10, 255, 255, 255); }
        }

        // ---------- menus ----------

        public static Color MenuBack
        {
            get { return Light ? Color.FromArgb(0xF9, 0xF9, 0xF9) : Color.FromArgb(0x2C, 0x2C, 0x2C); }
        }

        public static Color MenuHover
        {
            get { return Light ? Color.FromArgb(0xEB, 0xEB, 0xEB) : Color.FromArgb(0x3A, 0x3A, 0x3A); }
        }

        public static Color MenuBorder
        {
            get { return Light ? Color.FromArgb(0xE0, 0xE0, 0xE0) : Color.FromArgb(0x40, 0x40, 0x40); }
        }

        // ---------- fonts ----------

        static readonly Dictionary<string, Font> _fonts = new Dictionary<string, Font>();

        static readonly string[] TextFamilies = { "Segoe UI Variable Text", "Segoe UI" };
        static readonly string[] StrongFamilies = { "Segoe UI Variable Text Semibold", "Segoe UI Semibold" };
        static readonly string[] GlyphFamilies = { "Segoe Fluent Icons", "Segoe MDL2 Assets" };

        /// <summary>Body text at a pixel size. Cached: callers must not dispose it.</summary>
        public static Font Font(float px)
        {
            return Cached(TextFamilies, px, FontStyle.Regular);
        }

        /// <summary>Semibold text at a pixel size. Cached: callers must not dispose it.</summary>
        public static Font Strong(float px)
        {
            return Cached(StrongFamilies, px, FontStyle.Regular);
        }

        /// <summary>The system icon font at a pixel size. Cached: callers must not dispose it.</summary>
        public static Font Glyphs(float px)
        {
            return Cached(GlyphFamilies, px, FontStyle.Regular);
        }

        static Font Cached(string[] families, float px, FontStyle style)
        {
            px = (float)Math.Round(px * 2) / 2f;     // half-pixel steps keep the cache small
            string key = families[0] + "|" + px.ToString("0.0") + "|" + (int)style;

            Font f;
            if (_fonts.TryGetValue(key, out f)) return f;

            foreach (string name in families)
            {
                try
                {
                    f = new Font(name, px, style, GraphicsUnit.Pixel);
                    if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) break;
                    f.Dispose();
                    f = null;
                }
                catch { f = null; }
            }
            if (f == null) f = new Font(FontFamily.GenericSansSerif, px, style, GraphicsUnit.Pixel);

            _fonts[key] = f;
            return f;
        }

        // ---------- shapes ----------

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 0)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRounded(Graphics g, Color c, RectangleF r, float radius)
        {
            using (GraphicsPath p = Rounded(r, radius))
            using (SolidBrush b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        /// <summary>Draws one icon-font glyph centred in a box.</summary>
        public static void DrawGlyph(Graphics g, string glyph, float px, Color color, RectangleF box)
        {
            TextRenderingHint old = g.TextRenderingHint;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (SolidBrush b = new SolidBrush(color))
            using (StringFormat f = new StringFormat(StringFormat.GenericTypographic))
            {
                f.Alignment = StringAlignment.Center;
                f.LineAlignment = StringAlignment.Center;
                g.DrawString(glyph, Glyphs(px), b, box, f);
            }
            g.TextRenderingHint = old;
        }

        // ---------- window chrome ----------

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWA_BORDER_COLOR = 34;
        const int DWMWCP_ROUND = 2;

        /// <summary>Asks Windows 11 for rounded corners. A no-op anywhere older.</summary>
        public static bool RoundCorners(IntPtr hwnd)
        {
            try
            {
                int pref = DWMWCP_ROUND;
                return DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)) == 0;
            }
            catch { return false; }
        }

        /// <summary>Colours the edge Windows 11 draws around a rounded window.</summary>
        static void SetBorderColor(IntPtr hwnd, Color c)
        {
            try
            {
                int colorref = c.R | (c.G << 8) | (c.B << 16);
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorref, sizeof(int));
            }
            catch { }
        }

        /// <summary>One character of the icon font, by code point.</summary>
        public static string Glyph(int codepoint)
        {
            return ((char)codepoint).ToString();
        }

        static readonly Bitmap MenuIconSpace = new Bitmap(16, 16);

        /// <summary>
        /// A menu item with an icon-font glyph in the icon column (0 for none).
        /// The blank image only reserves the column; MenuRenderer draws the
        /// glyph from Tag, in whatever the theme is when the menu opens.
        /// </summary>
        public static ToolStripMenuItem MenuItem(string text, int glyph)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Image = MenuIconSpace;
            item.ImageScaling = ToolStripItemImageScaling.None;
            if (glyph != 0) item.Tag = Glyph(glyph);
            return item;
        }

        /// <summary>
        /// Gives a context menu the Windows 11 look: themed colours, roomier
        /// rows, one icon column so every label lines up, a rounded window with
        /// Windows' own edge, and rounded selection.
        /// </summary>
        public static void StyleMenu(ContextMenuStrip menu)
        {
            MenuRenderer renderer = new MenuRenderer();
            menu.Renderer = renderer;
            menu.Padding = new Padding(4, 4, 4, 4);
            // Glyphs and the check for a toggle share one column. A separate
            // check margin as well left every label after an empty gutter.
            menu.ShowImageMargin = true;
            menu.ShowCheckMargin = false;
            menu.ImageScalingSize = new Size(16, 16);
            menu.Font = new Font(TextFamilies[1], 9.75f);
            menu.DropShadowEnabled = true;
            menu.HandleCreated += delegate
            {
                // Windows 11 rounds the menu and draws its edge; anywhere older
                // the renderer draws a square one.
                renderer.DrawBorder = !RoundCorners(menu.Handle);
            };
            menu.Opening += delegate
            {
                menu.BackColor = MenuBack;
                menu.ForeColor = Text;
                if (menu.IsHandleCreated && !renderer.DrawBorder) SetBorderColor(menu.Handle, MenuBorder);
                foreach (ToolStripItem item in menu.Items)
                {
                    // Room for the label to start where MenuRenderer draws it, and air after it.
                    if (item is ToolStripMenuItem) item.Padding = new Padding(4, 6, 20, 6);
                }
            };
        }
    }
}

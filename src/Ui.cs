using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Shelf
{
    public static class Theme
    {
        public static readonly Color Back = ColorTranslator.FromHtml("#1C1D20");
        public static readonly Color Card = ColorTranslator.FromHtml("#232529");
        public static readonly Color CardHi = ColorTranslator.FromHtml("#2E3034");
        public static readonly Color Hover = ColorTranslator.FromHtml("#292B2F");
        public static readonly Color Text = ColorTranslator.FromHtml("#F1F3F4");
        public static readonly Color Dim = ColorTranslator.FromHtml("#9AA0A6");
        public static readonly Color Dimmer = ColorTranslator.FromHtml("#6E7378");
        public static readonly Color Accent = ColorTranslator.FromHtml("#8AB4F8");
        public static readonly Color AccentDark = ColorTranslator.FromHtml("#4D7FD1");
        public static readonly Color Border = ColorTranslator.FromHtml("#34363B");
        public static readonly Color Good = ColorTranslator.FromHtml("#81C995");
        public static readonly Color Warn = ColorTranslator.FromHtml("#FDD663");
        public static readonly Color OnAccent = ColorTranslator.FromHtml("#16212E");
        public static readonly Color Danger = ColorTranslator.FromHtml("#F28B82");

        public static Font Font(float size, FontStyle style)
        {
            string[] prefs = new string[] { "Segoe UI Variable Display", "Segoe UI" };
            foreach (string name in prefs)
            {
                try
                {
                    Font f = new Font(name, size, style);
                    if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
                    f.Dispose();
                }
                catch { }
            }
            return new System.Drawing.Font(FontFamily.GenericSansSerif, size, style);
        }

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>
        /// Returns a font from Segoe MDL2 Assets at the given pixel size, or null
        /// if the font is not available.
        /// </summary>
        public static Font IconFont(float px)
        {
            try
            {
                Font f = new Font("Segoe MDL2 Assets", px, GraphicsUnit.Pixel);
                if (string.Equals(f.Name, "Segoe MDL2 Assets", StringComparison.OrdinalIgnoreCase))
                    return f;
                f.Dispose();
            }
            catch { }
            return null;
        }
    }

    /// <summary>
    /// DPI helpers. The app as a whole is DPI-unaware, but individual windows can
    /// opt into per-monitor awareness for the duration of their creation and
    /// painting, so they render at true device pixels rather than being stretched.
    /// </summary>
    public static class Dpi
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);

        [DllImport("shcore.dll")]
        static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dx, out uint dy);

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromPoint(int x, int y, int flags);

        [DllImport("gdi32.dll")]
        static extern int GetDeviceCaps(IntPtr hdc, int index);

        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

        // -4 is DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.
        static readonly IntPtr PerMonitorV2 = new IntPtr(-4);
        static bool _dpiApi = true;
        static bool _dpiQuery = true;

        const int MONITOR_DEFAULTTONEAREST = 2;

        /// <summary>
        /// Enter per-monitor DPI awareness for the calling thread. Returns the
        /// previous context, which must be passed to LeaveDpi when done.
        /// </summary>
        public static IntPtr EnterDpi()
        {
            if (!_dpiApi) return IntPtr.Zero;
            try { return SetThreadDpiAwarenessContext(PerMonitorV2); }
            catch { _dpiApi = false; return IntPtr.Zero; }
        }

        public static void LeaveDpi(IntPtr prev)
        {
            if (prev == IntPtr.Zero) return;
            try { SetThreadDpiAwarenessContext(prev); }
            catch { }
        }

        /// <summary>The scale factor for the monitor at the given screen point.</summary>
        public static float ScaleAt(int x, int y)
        {
            IntPtr mon = MonitorFromPoint(x, y, MONITOR_DEFAULTTONEAREST);
            return ScaleFor(mon);
        }

        /// <summary>The scale factor for the monitor a window is on.</summary>
        public static float ScaleForWindow(IntPtr hwnd)
        {
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            return ScaleFor(mon);
        }

        public static float ScaleFor(IntPtr mon)
        {
            if (_dpiQuery && mon != IntPtr.Zero)
            {
                try
                {
                    uint dx, dy;
                    if (GetDpiForMonitor(mon, 0 /* MDT_EFFECTIVE_DPI */, out dx, out dy) == 0 && dx > 0)
                        return dx / 96f;
                }
                catch { _dpiQuery = false; }
            }
            try
            {
                IntPtr dc = GetDC(IntPtr.Zero);
                int dpi = GetDeviceCaps(dc, 88 /* LOGPIXELSX */);
                ReleaseDC(IntPtr.Zero, dc);
                if (dpi > 0) return dpi / 96f;
            }
            catch { }
            return 1f;
        }
    }

    /// <summary>
    /// Dark-themed context menu renderer. Replaces the default system look with
    /// one that matches the rest of the app.
    /// </summary>
    public class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors()) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            Rectangle r = new Rectangle(Point.Empty, e.Item.Size);
            Color c = e.Item.Selected ? Theme.CardHi : Theme.Card;
            using (SolidBrush b = new SolidBrush(c))
                e.Graphics.FillRectangle(b, r);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Dimmer;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = new Rectangle(e.ImageRectangle.X + 2, e.ImageRectangle.Y + 2,
                                         e.ImageRectangle.Width - 4, e.ImageRectangle.Height - 4);
            using (SolidBrush b = new SolidBrush(Theme.Accent))
            using (Pen p = new Pen(Theme.OnAccent, 1.5f))
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillEllipse(b, r);
                // checkmark
                e.Graphics.DrawLines(p, new PointF[] {
                    new PointF(r.X + 3, r.Y + r.Height / 2f),
                    new PointF(r.X + r.Width / 2f - 1, r.Y + r.Height - 4),
                    new PointF(r.X + r.Width - 3, r.Y + 4)
                });
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (Pen p = new Pen(Theme.Border))
                e.Graphics.DrawLine(p, 4, y, e.Item.Width - 4, y);
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(Theme.Card))
                e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (Pen p = new Pen(Theme.Border))
                e.Graphics.DrawRectangle(p, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }
    }

    class DarkMenuColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return Theme.CardHi; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color MenuItemBorder { get { return Theme.Border; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Border; } }
        public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
    }
}

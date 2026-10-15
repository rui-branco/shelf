using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Shelf
{
    /// <summary>
    /// The Quick Look window: a floating, rounded preview of one file with a
    /// slim title bar (icon, name, Open, close).
    ///
    /// It never takes focus (WS_EX_NOACTIVATE). That is the whole trick behind
    /// browsing with the arrow keys: Explorer, or the panel, keeps the keyboard
    /// and moves its own selection, and the preview just follows. It also
    /// means clicking into it never steals focus from whatever opened it, so
    /// the panel does not close underneath it.
    /// </summary>
    class PeekForm : Form
    {
        const int WS_EX_TOOLWINDOW = 0x80;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOPMOST = 0x8;
        const int CS_DROPSHADOW = 0x20000;
        const int WM_MOUSEACTIVATE = 0x0021;
        const int WM_NCHITTEST = 0x0084;
        const int WM_NCLBUTTONDBLCLK = 0x00A3;
        const int MA_NOACTIVATE = 3;
        const int HTCLIENT = 1;
        const int HTCAPTION = 2;

        enum Button { None, Open, Close }

        readonly PreviewPane _pane = new PreviewPane();
        string _path;
        float _s = 1f;
        Bitmap _icon;
        Rectangle _openBtn;
        Rectangle _closeBtn;
        Button _hot;
        Button _down;
        ZoomGhost _ghost;      // the zoom out of the icon, while it is flying

        public PeekForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            Controls.Add(_pane);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // Topmost as a style from creation, never through the TopMost
                // property: WinForms applies that with a SetWindowPos that
                // activates, NOACTIVATE or not - which took the focus from the
                // panel, and the panel closes when it loses focus.
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
                cp.ClassStyle |= CS_DROPSHADOW;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        /// <summary>The file being previewed.</summary>
        public string FilePath
        {
            get { return _path; }
        }

        int TitleH
        {
            get { return R(44); }
        }

        int R(float px)
        {
            return (int)Math.Round(px * _s);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.RoundCorners(Handle);
        }

        /// <summary>
        /// Shows <paramref name="path"/>, sized to suit it, right beside
        /// <paramref name="origin"/> - the file's icon - and kept inside
        /// <paramref name="area"/> (screen coordinates). The first time, it
        /// zooms out of that icon. <paramref name="preferAbove"/> is for the
        /// panel, which sits at the bottom of the screen with its tiles in it.
        /// </summary>
        public void ShowFile(string path, Rectangle area, Rectangle origin, bool preferAbove)
        {
            _path = path;
            Point focus = origin.IsEmpty
                ? new Point(area.Left + area.Width / 2, area.Top + area.Height / 2)
                : new Point(origin.Left + origin.Width / 2, origin.Top + origin.Height / 2);
            _s = Dpi.ScaleAt(focus);
            BackColor = Theme.PanelSolid;

            if (_icon != null) _icon.Dispose();
            _icon = Shell.GetIcon(path, R(20));

            // A preview, not a takeover: on an ultrawide, "most of the screen"
            // was a window wider than most monitors, nowhere near the file.
            int maxW = Math.Min((int)(area.Width * 0.6), R(1100));
            int maxH = Math.Min((int)(area.Height * 0.8), R(820)) - TitleH;

            Size natural;
            PreviewShape shape = _pane.Load(path, _s, new Size(maxW, maxH), out natural);

            Size content;
            switch (shape)
            {
                case PreviewShape.Picture:
                    float k = Math.Min(1f, Math.Min((float)maxW / Math.Max(1, natural.Width), (float)maxH / Math.Max(1, natural.Height)));
                    content = new Size(Math.Max(R(420), (int)(natural.Width * k)), Math.Max(R(280), (int)(natural.Height * k)));
                    break;
                case PreviewShape.Page:
                    content = new Size(Math.Min(maxW, R(760)), maxH);
                    break;
                default:
                    content = new Size(Math.Min(maxW, R(480)), Math.Min(maxH, R(340)));
                    break;
            }

            Size size = new Size(content.Width, content.Height + TitleH);

            // Already up (arrowing to the next file): stay put and only change
            // size, around the same centre, rather than chase every file.
            if (Visible || (_ghost != null && _ghost.Flying))
            {
                Point c = new Point(Left + Width / 2, Top + Height / 2);
                Bounds = Clamp(new Rectangle(c.X - size.Width / 2, c.Y - size.Height / 2, size.Width, size.Height), area);
            }
            else
            {
                Bounds = Place(size, area, origin, preferAbove);
            }
            _pane.Bounds = new Rectangle(0, TitleH, size.Width, size.Height - TitleH);

            int bs = R(32);
            int by = (TitleH - bs) / 2;
            _closeBtn = new Rectangle(size.Width - R(8) - bs, by, bs, bs);
            _openBtn = new Rectangle(_closeBtn.Left - R(4) - R(80), by, R(80), bs);

            Invalidate();
            if (Visible || (_ghost != null && _ghost.Flying)) return;

            // Laid out and drawn while still hidden, so the snapshot that
            // flies is exactly what lands; then the real window takes over.
            IntPtr handle = Handle;
            Rectangle to = Bounds;
            Rectangle from = origin.IsEmpty ? Shrink(to, 0.9f) : origin;
            _ghost = ZoomGhost.Fly(Snapshot(false), from, to, true, delegate
            {
                if (!IsDisposed && !Visible) Show();
            });
        }

        /// <summary>
        /// Zooms back into <paramref name="target"/> (the file's icon, or
        /// nowhere in particular) and closes. A live capture flies this time,
        /// so even a PDF shrinks back as itself.
        /// </summary>
        public void CloseAnimated(Rectangle target)
        {
            if (Visible && (_ghost == null || !_ghost.Flying))
            {
                try
                {
                    Bitmap shot = Snapshot(true);
                    Rectangle from = Bounds;
                    Hide();
                    ZoomGhost.Fly(shot, from, target.IsEmpty ? Shrink(from, 0.9f) : target, false, null);
                }
                catch (Exception ex)
                {
                    Program.LogError(ex);
                }
            }
            Close();
        }

        /// <summary>
        /// Next to the icon: the first side it fits on whole, without covering
        /// the icon. Explorer tries right, left, below, above; the panel tries
        /// above first (centred on the tile, clear of the panel), then the sides.
        /// With no icon to go by, centred in the area.
        /// </summary>
        Rectangle Place(Size size, Rectangle area, Rectangle origin, bool preferAbove)
        {
            if (origin.IsEmpty)
                return Clamp(new Rectangle(area.Left + (area.Width - size.Width) / 2,
                                           area.Top + (area.Height - size.Height) / 2, size.Width, size.Height), area);

            int gap = R(16);
            int cx = origin.Left + origin.Width / 2 - size.Width / 2;
            int cy = origin.Top + origin.Height / 2 - size.Height / 2;

            Rectangle right = new Rectangle(origin.Right + gap, cy, size.Width, size.Height);
            Rectangle left = new Rectangle(origin.Left - gap - size.Width, cy, size.Width, size.Height);
            Rectangle above = new Rectangle(cx, Math.Min(origin.Top - gap, area.Bottom) - size.Height, size.Width, size.Height);
            Rectangle below = new Rectangle(cx, origin.Bottom + gap, size.Width, size.Height);

            Rectangle[] order = preferAbove
                ? new Rectangle[] { above, right, left, below }
                : new Rectangle[] { right, left, below, above };

            foreach (Rectangle r in order)
            {
                bool sideways = r == right || r == left;
                Rectangle c = sideways
                    ? new Rectangle(r.X, Math.Max(area.Top, Math.Min(r.Y, area.Bottom - r.Height)), r.Width, r.Height)
                    : Clamp(r, area);
                if (area.Contains(c) && !c.IntersectsWith(origin)) return c;
            }

            // Nowhere beside it fits: centred on the icon, kept on screen.
            return Clamp(new Rectangle(cx, cy, size.Width, size.Height), area);
        }

        static Rectangle Clamp(Rectangle r, Rectangle area)
        {
            int x = Math.Max(area.Left, Math.Min(r.X, area.Right - r.Width));
            int y = Math.Max(area.Top, Math.Min(r.Y, area.Bottom - r.Height));
            return new Rectangle(x, y, r.Width, r.Height);
        }

        static Rectangle Shrink(Rectangle r, float k)
        {
            int w = (int)(r.Width * k), h = (int)(r.Height * k);
            return new Rectangle(r.Left + (r.Width - w) / 2, r.Top + (r.Height - h) / 2, w, h);
        }

        /// <summary>
        /// A picture of the window with its corners rounded off. Live, it is a
        /// capture of what is on screen, preview handlers included. Not yet
        /// shown, it is our own painting, with the file's big thumbnail standing
        /// in where a preview handler will draw.
        /// </summary>
        Bitmap Snapshot(bool live)
        {
            Bitmap flat = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
            if (live)
            {
                using (Graphics g = Graphics.FromImage(flat))
                {
                    IntPtr hdc = g.GetHdc();
                    try { PrintWindow(Handle, hdc, PW_RENDERFULLCONTENT); }
                    finally { g.ReleaseHdc(hdc); }
                }
            }
            else
            {
                using (Graphics g = Graphics.FromImage(flat))
                {
                    PaintChrome(g);
                    g.TranslateTransform(_pane.Left, _pane.Top);
                    _pane.PaintSnapshot(g);
                }
            }

            Bitmap rounded = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(rounded))
            using (TextureBrush tb = new TextureBrush(flat, WrapMode.Clamp))
            using (GraphicsPath p = Theme.Rounded(new RectangleF(0, 0, Width, Height), 8 * _s))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                g.FillPath(tb, p);
            }
            flat.Dispose();
            return rounded;
        }

        const int PW_RENDERFULLCONTENT = 2;

        [DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, int flags);

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = new IntPtr(MA_NOACTIVATE);
                return;
            }
            if (m.Msg == WM_NCLBUTTONDBLCLK)
            {
                return;     // a borderless window has nothing sensible to maximize to
            }

            base.WndProc(ref m);

            if (m.Msg == WM_NCHITTEST && m.Result.ToInt32() == HTCLIENT)
            {
                // The title bar moves the window, except over its buttons.
                Point p = PointToClient(new Point((short)(m.LParam.ToInt32() & 0xFFFF), (short)((m.LParam.ToInt32() >> 16) & 0xFFFF)));
                if (p.Y < TitleH && !_openBtn.Contains(p) && !_closeBtn.Contains(p))
                    m.Result = new IntPtr(HTCAPTION);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintChrome(e.Graphics);
        }

        /// <summary>The title bar and background, on screen or into a snapshot.</summary>
        void PaintChrome(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (SolidBrush b = new SolidBrush(BackColor))
                g.FillRectangle(b, ClientRectangle);
            using (Pen line = new Pen(Theme.Divider))
                g.DrawLine(line, 0, TitleH - 1, Width, TitleH - 1);

            int x = R(14);
            if (_icon != null)
            {
                int iw = Math.Min(_icon.Width, R(20)), ih = Math.Min(_icon.Height, R(20));
                g.DrawImage(_icon, new Rectangle(x, (TitleH - ih) / 2, iw, ih));
            }

            using (SolidBrush b = new SolidBrush(Theme.Text))
            using (StringFormat f = new StringFormat())
            {
                f.LineAlignment = StringAlignment.Center;
                f.Trimming = StringTrimming.EllipsisPath;
                f.FormatFlags = StringFormatFlags.NoWrap;
                RectangleF r = new RectangleF(x + R(28), 0, _openBtn.Left - x - R(36), TitleH);
                g.DrawString(Format.FolderName(_path ?? ""), Theme.Strong(13 * _s), b, r, f);
            }

            DrawButton(g, _openBtn, Button.Open);
            DrawButton(g, _closeBtn, Button.Close);
        }

        void DrawButton(Graphics g, Rectangle r, Button which)
        {
            if (_hot == which)
                Theme.FillRounded(g, _down == which ? Theme.Pressed : Theme.Hover, r, 4 * _s);

            if (which == Button.Close)
            {
                Theme.DrawGlyph(g, Theme.Glyph(0xE8BB), 11 * _s, Theme.Text, r);
                return;
            }

            using (SolidBrush b = new SolidBrush(Theme.Text))
            using (StringFormat f = new StringFormat())
            {
                f.Alignment = StringAlignment.Center;
                f.LineAlignment = StringAlignment.Center;
                g.DrawString("Open", Theme.Font(13 * _s), b, r, f);
            }
        }

        Button HitButton(Point p)
        {
            if (_openBtn.Contains(p)) return Button.Open;
            if (_closeBtn.Contains(p)) return Button.Close;
            return Button.None;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Button hot = HitButton(e.Location);
            if (hot != _hot)
            {
                _hot = hot;
                Invalidate(new Rectangle(0, 0, Width, TitleH));
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hot = Button.None;
            Invalidate(new Rectangle(0, 0, Width, TitleH));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _down = HitButton(e.Location);
            Invalidate(new Rectangle(0, 0, Width, TitleH));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Button up = HitButton(e.Location);
            Button down = _down;
            _down = Button.None;
            if (up != down || up == Button.None) return;

            if (up == Button.Open)
            {
                try { Process.Start(_path); }
                catch (Exception ex) { Program.LogError(ex); }
            }
            Peek.Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (_ghost != null && _ghost.Flying) _ghost.Cancel();
            _pane.Unload();
            if (_icon != null) { _icon.Dispose(); _icon = null; }
        }
    }
}

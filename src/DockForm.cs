using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Shelf
{
    /// <summary>
    /// The dock pill: a small rounded always-on-top button that sits above the
    /// taskbar, shows the Downloads folder glyph and a count badge, and toggles
    /// the StackForm on click.
    ///
    /// It uses WS_EX_TOOLWINDOW so it stays out of Alt-Tab. The position is
    /// draggable and persisted to config.
    /// </summary>
    public class DockForm : Form
    {
        const int WS_EX_TOOLWINDOW = 0x80;

        // Metrics at 100% DPI; scaled by the monitor factor.
        const int BaseSize = 56;
        const int Radius = 14;
        const int BadgeRadius = 9;
        const float IconPx = 24f;
        const float BadgeFontPt = 7.5f;

        AppConfig _config;
        Downloads _downloads;
        StackForm _stack;
        float _scale = 1f;

        // Drag state
        bool _dragging;
        Point _dragStart;
        Point _formStart;

        // Bounce animation when a new file arrives
        Timer _bounce;
        int _bounceFrame;
        int _bounceOffset;

        public DockForm(AppConfig config, Downloads downloads)
        {
            _config = config;
            _downloads = downloads;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Back;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            _downloads.Changed += delegate { Invalidate(); };
            _downloads.NewFileArrived += delegate { StartBounce(); };

            _bounce = new Timer();
            _bounce.Interval = 8;
            _bounce.Tick += delegate(object s, EventArgs e) { StepBounce(); };

            ApplyScale();
            PositionOnScreen();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        void ApplyScale()
        {
            IntPtr ctx = Dpi.EnterDpi();
            try
            {
                // Use the scale of the monitor where the dock will appear.
                // If we have a saved position, use that; otherwise bottom-right.
                int x = _config.DockX >= 0 ? _config.DockX : Screen.PrimaryScreen.WorkingArea.Right - 80;
                int y = _config.DockY >= 0 ? _config.DockY : Screen.PrimaryScreen.WorkingArea.Bottom - 80;
                _scale = Dpi.ScaleAt(x, y);

                int sz = (int)Math.Round(BaseSize * _scale);
                Size = new Size(sz, sz);
            }
            finally { Dpi.LeaveDpi(ctx); }
        }

        void PositionOnScreen()
        {
            IntPtr ctx = Dpi.EnterDpi();
            try
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;

                if (_config.DockX >= 0 && _config.DockY >= 0)
                {
                    // Restore saved position, but clamp to current work area in case
                    // the monitor layout changed.
                    int x = _config.DockX;
                    int y = _config.DockY;
                    if (x < wa.Left) x = wa.Left;
                    if (y < wa.Top) y = wa.Top;
                    if (x + Width > wa.Right) x = wa.Right - Width;
                    if (y + Height > wa.Bottom) y = wa.Bottom - Height;
                    Location = new Point(x, y);
                }
                else
                {
                    // Default: bottom-right, just above the taskbar
                    int gap = (int)Math.Round(16 * _scale);
                    Location = new Point(wa.Right - Width - gap, wa.Bottom - Height - gap);
                }
            }
            finally { Dpi.LeaveDpi(ctx); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            IntPtr ctx = Dpi.EnterDpi();
            try { PaintCore(e.Graphics); }
            finally { Dpi.LeaveDpi(ctx); }
        }

        void PaintCore(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            float s = _scale;
            int w = Width;
            int h = Height;

            // Apply bounce offset
            int offsetY = _bounceOffset;

            // Rounded background
            RectangleF r = new RectangleF(0.5f, 0.5f + offsetY, w - 1.5f, h - 1.5f - Math.Abs(offsetY));
            using (GraphicsPath path = Theme.Rounded(r, Radius * s))
            {
                using (SolidBrush b = new SolidBrush(Theme.Card))
                    g.FillPath(b, path);
                using (Pen p = new Pen(Theme.Border, 1f))
                    g.DrawPath(p, path);
            }

            // Downloads folder glyph (Segoe MDL2 Assets U+E896 = folder, U+E118 = download)
            // Using U+E896 for a folder icon
            Font icon = Theme.IconFont(IconPx * s);
            if (icon != null)
            {
                using (icon)
                using (SolidBrush b = new SolidBrush(Theme.Text))
                using (StringFormat fmt = new StringFormat())
                {
                    fmt.Alignment = StringAlignment.Center;
                    fmt.LineAlignment = StringAlignment.Center;
                    // U+E896 = Folder, U+E118 = Download arrow
                    g.DrawString("", icon, b, w / 2f, h / 2f + offsetY, fmt);
                }
            }

            // Count badge
            int count = _downloads.Items.Count;
            if (count > 0)
            {
                float br = BadgeRadius * s;
                float bx = w - br - 4 * s;
                float by = 4 * s + offsetY;

                using (SolidBrush b = new SolidBrush(Theme.Accent))
                    g.FillEllipse(b, bx, by, br * 2, br * 2);

                string text = count > 99 ? "99+" : count.ToString();
                using (Font f = Theme.Font(BadgeFontPt * s, FontStyle.Bold))
                using (SolidBrush b = new SolidBrush(Theme.OnAccent))
                using (StringFormat fmt = new StringFormat())
                {
                    fmt.Alignment = StringAlignment.Center;
                    fmt.LineAlignment = StringAlignment.Center;
                    g.DrawString(text, f, b, bx + br, by + br, fmt);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _dragStart = e.Location;
                _formStart = Location;
                _dragging = false;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (e.Button != MouseButtons.Left) return;

            if (!_dragging)
            {
                // Only start dragging once past the system threshold
                if (Math.Abs(e.X - _dragStart.X) < SystemInformation.DragSize.Width &&
                    Math.Abs(e.Y - _dragStart.Y) < SystemInformation.DragSize.Height)
                    return;
                _dragging = true;
            }

            int dx = e.X - _dragStart.X;
            int dy = e.Y - _dragStart.Y;
            Location = new Point(_formStart.X + dx, _formStart.Y + dy);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;

            if (_dragging)
            {
                // Save the new position
                _config.DockX = Left;
                _config.DockY = Top;
                _config.Save();
                _dragging = false;
            }
            else
            {
                // Plain click: toggle the stack
                ToggleStack();
            }
        }

        void ToggleStack()
        {
            if (_stack != null && !_stack.IsDisposed && _stack.Visible)
            {
                _stack.Close();
                _stack = null;
            }
            else
            {
                _stack = new StackForm(_config, _downloads, this);
                _stack.Closed += delegate { _stack = null; };

                // Position above the dock pill
                IntPtr ctx = Dpi.EnterDpi();
                try
                {
                    int x = Left + (Width - _stack.Width) / 2;
                    int y = Top - _stack.Height - (int)Math.Round(8 * _scale);

                    // Keep on screen
                    Rectangle wa = Screen.FromControl(this).WorkingArea;
                    if (x < wa.Left) x = wa.Left;
                    if (x + _stack.Width > wa.Right) x = wa.Right - _stack.Width;
                    if (y < wa.Top) y = wa.Top;

                    _stack.Location = new Point(x, y);
                }
                finally { Dpi.LeaveDpi(ctx); }

                _stack.Show();
            }
        }

        /// <summary>
        /// Closes the stack popup if it is open. Called when starting a drag from
        /// StackForm so the popup does not vanish mid-drag.
        /// </summary>
        public void CloseStack()
        {
            if (_stack != null && !_stack.IsDisposed)
            {
                _stack.Close();
                _stack = null;
            }
        }

        public bool IsStackOpen
        {
            get { return _stack != null && !_stack.IsDisposed && _stack.Visible; }
        }

        void StartBounce()
        {
            _bounceFrame = 0;
            _bounce.Start();
        }

        void StepBounce()
        {
            // Simple bounce: rise up then settle back down, eased
            _bounceFrame++;

            // ~20 frames total at 8ms = 160ms
            if (_bounceFrame > 20)
            {
                _bounce.Stop();
                _bounceOffset = 0;
                Invalidate();
                return;
            }

            // Sine-ish ease: peak at frame 5, settle by frame 20
            double t = _bounceFrame / 20.0;
            double ease = Math.Sin(t * Math.PI) * (1 - t);
            _bounceOffset = -(int)Math.Round(8 * _scale * ease);
            Invalidate();
        }
    }
}

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Shelf
{
    /// <summary>
    /// The icon that follows the cursor while a file is being dragged out.
    ///
    /// The shell has its own mechanism for this - IDragSourceHelper - but it
    /// only draws anything when the *drop target* cooperates by calling
    /// IDropTargetHelper. Explorer does; plenty of applications do not, and
    /// over the desktop or a non-participating window the image simply never
    /// appears. Since the whole point is that you can see what you are
    /// carrying, we draw it ourselves and put it exactly where the cursor is.
    ///
    /// It is a layered, click-through, never-activating window: WS_EX_TRANSPARENT
    /// is essential here, because a window sitting under the cursor mid-drag
    /// would otherwise swallow the drop.
    /// </summary>
    public class DragGhost : IDisposable
    {
        const int WS_EX_LAYERED = 0x80000;
        const int WS_EX_TRANSPARENT = 0x20;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x80;

        class GhostForm : Form
        {
            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT
                                | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                    return cp;
                }
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            public GhostForm()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                StartPosition = FormStartPosition.Manual;
                AutoScaleMode = AutoScaleMode.None;
            }
        }

        GhostForm _form;
        Bitmap _frame;
        int _hotX, _hotY;

        // GDI handles held for the lifetime of the drag. Rebuilding these on
        // every mouse move is what made returning to the pill feel sticky:
        // GetHbitmap allocates a fresh bitmap each call, and GiveFeedback fires
        // continuously. Created once, reused for every frame.
        IntPtr _screenDC = IntPtr.Zero;
        IntPtr _memDC = IntPtr.Zero;
        IntPtr _hBmp = IntPtr.Zero;
        IntPtr _oldBmp = IntPtr.Zero;
        int _lastX = int.MinValue, _lastY = int.MinValue;

        /// <summary>
        /// Builds the ghost from an icon. <paramref name="opacity"/> keeps it
        /// translucent so whatever is underneath - the drop target you are
        /// aiming at - stays readable through it.
        /// </summary>
        public DragGhost(Bitmap icon, byte opacity)
        {
            if (icon == null) return;

            int w = icon.Width;
            int h = icon.Height;

            _frame = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(_frame))
            {
                g.Clear(Color.Transparent);
                g.DrawImageUnscaled(icon, 0, 0);
            }

            _hotX = w / 2;
            _hotY = h / 2;

            _alpha = opacity;

            _form = new GhostForm();
            _form.Size = new Size(w, h);
            _form.Show();

            // Build the GDI side once.
            _screenDC = GetDC(IntPtr.Zero);
            _memDC = CreateCompatibleDC(_screenDC);
            _hBmp = _frame.GetHbitmap(Color.FromArgb(0));
            _oldBmp = SelectObject(_memDC, _hBmp);

            MoveTo(Cursor.Position);
        }

        byte _alpha = 255;

        /// <summary>
        /// Puts the ghost under the cursor. Called from GiveFeedback, which
        /// fires on every mouse move during the drag, so this has to stay
        /// cheap: one UpdateLayeredWindow call and nothing allocated.
        ///
        /// Note there is no separate Location assignment. UpdateLayeredWindow
        /// moves the window itself via the destination point, and setting
        /// Location as well meant two window operations per mouse move.
        /// </summary>
        public void MoveTo(Point screenPoint)
        {
            if (_form == null || _form.IsDisposed || !_form.IsHandleCreated) return;
            if (_frame == null || _hBmp == IntPtr.Zero) return;

            int x = screenPoint.X - _hotX;
            int y = screenPoint.Y - _hotY;
            if (x == _lastX && y == _lastY) return;   // nothing moved; skip the blit
            _lastX = x; _lastY = y;

            try
            {
                SIZE size = new SIZE();
                size.cx = _frame.Width; size.cy = _frame.Height;
                POINT src = new POINT();
                POINT dst = new POINT();
                dst.X = x; dst.Y = y;

                BLENDFUNCTION blend = new BLENDFUNCTION();
                blend.BlendOp = 0;                  // AC_SRC_OVER
                blend.SourceConstantAlpha = _alpha;
                blend.AlphaFormat = 1;              // AC_SRC_ALPHA

                UpdateLayeredWindow(_form.Handle, _screenDC, ref dst, ref size,
                                    _memDC, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
            }
            catch { }
        }

        public void Dispose()
        {
            if (_memDC != IntPtr.Zero)
            {
                if (_oldBmp != IntPtr.Zero) SelectObject(_memDC, _oldBmp);
                DeleteDC(_memDC);
                _memDC = IntPtr.Zero;
                _oldBmp = IntPtr.Zero;
            }
            if (_hBmp != IntPtr.Zero)
            {
                DeleteObject(_hBmp);
                _hBmp = IntPtr.Zero;
            }
            if (_screenDC != IntPtr.Zero)
            {
                ReleaseDC(IntPtr.Zero, _screenDC);
                _screenDC = IntPtr.Zero;
            }
            if (_form != null)
            {
                try { _form.Close(); _form.Dispose(); }
                catch { }
                _form = null;
            }
            if (_frame != null)
            {
                _frame.Dispose();
                _frame = null;
            }
        }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION
        {
            public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
        }

        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT dst,
            ref SIZE size, IntPtr hdcSrc, ref POINT src, int key,
            ref BLENDFUNCTION blend, int flags);
    }
}

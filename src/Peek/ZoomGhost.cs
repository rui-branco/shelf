using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Shelf
{
    /// <summary>
    /// The picture that flies between a file's icon and the Quick Look window:
    /// out of the icon when the preview opens, back into it when it closes,
    /// the way macOS zooms a preview out of the file you pressed Space on.
    ///
    /// The real preview window cannot be animated itself - it hosts other
    /// processes' preview windows, and resizing those every frame would stutter
    /// and flicker - so a snapshot of it flies instead, in a click-through
    /// layered window, and the real one takes over at the end.
    /// </summary>
    sealed class ZoomGhost : Form
    {
        const int WS_EX_LAYERED = 0x80000;
        const int WS_EX_TRANSPARENT = 0x20;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x80;
        const int WS_EX_TOPMOST = 0x8;

        // Quick, so it reads as the preview arriving rather than as a wait.
        const int OpenMs = 170;
        const int CloseMs = 150;

        readonly LayeredSurface _surface = new LayeredSurface();
        readonly Stopwatch _clock = new Stopwatch();
        readonly Timer _timer = new Timer();
        Bitmap _image;
        RectangleF _from;
        RectangleF _to;
        bool _opening;
        MethodInvoker _done;
        int _lingers;

        /// <summary>
        /// Flies <paramref name="image"/> from one screen rectangle to another.
        /// <paramref name="done"/> runs when it lands. The ghost takes
        /// ownership of the image.
        /// </summary>
        public static ZoomGhost Fly(Bitmap image, Rectangle from, Rectangle to, bool opening, MethodInvoker done)
        {
            ZoomGhost g = new ZoomGhost();
            g._image = image;
            g._from = from;
            g._to = to;
            g._opening = opening;
            g._done = done;
            g.Bounds = from;
            g.Show();
            g._clock.Start();
            g.Step();
            g._timer.Start();
            return g;
        }

        ZoomGhost()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            _timer.Interval = 10;
            _timer.Tick += delegate { Step(); };
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
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        /// <summary>True until it has landed.</summary>
        public bool Flying
        {
            get { return _done != null || _clock.IsRunning; }
        }

        void Step()
        {
            if (IsDisposed) return;

            // After landing, stay a couple of frames: the real window needs a
            // frame to appear, and leaving at once shows a blink of nothing.
            if (!_clock.IsRunning)
            {
                if (++_lingers > 2) Finish();
                return;
            }

            double t = Math.Min(1.0, _clock.ElapsedMilliseconds / (double)(_opening ? OpenMs : CloseMs));
            // Opening decelerates into place; closing accelerates away, into the icon.
            double e = _opening ? 1 - Math.Pow(1 - t, 3) : t * t * t;

            RectangleF r = new RectangleF(
                Lerp(_from.X, _to.X, e), Lerp(_from.Y, _to.Y, e),
                Lerp(_from.Width, _to.Width, e), Lerp(_from.Height, _to.Height, e));

            // Solid enough to read as the window itself, faint at the icon end.
            double a = _opening ? 0.35 + 0.65 * e : 1 - 0.8 * e;
            Draw(r, (byte)Math.Round(255 * a));

            if (t >= 1)
            {
                _clock.Stop();
                MethodInvoker done = _done;
                _done = null;
                if (!_opening)
                {
                    Finish();
                    return;
                }
                if (done != null)
                {
                    try { done(); }
                    catch (Exception ex) { Program.LogError(ex); }
                }
            }
        }

        void Draw(RectangleF r, byte alpha)
        {
            int w = Math.Max(1, (int)Math.Round(r.Width)), h = Math.Max(1, (int)Math.Round(r.Height));
            int maxW = (int)Math.Ceiling(Math.Max(_from.Width, _to.Width));
            int maxH = (int)Math.Ceiling(Math.Max(_from.Height, _to.Height));

            // One buffer at the largest size the flight reaches, drawn into its
            // top-left corner each frame and presented at this frame's size, so
            // nothing is allocated per frame.
            using (Graphics g = _surface.Begin(Math.Max(maxW, w), Math.Max(maxH, h)))
            {
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(_image, new Rectangle(0, 0, w, h));
            }
            _surface.Present(Handle, new Point((int)Math.Round(r.X), (int)Math.Round(r.Y)), new Size(w, h), alpha);
        }

        static float Lerp(float a, float b, double t)
        {
            return (float)(a + (b - a) * t);
        }

        void Finish()
        {
            _timer.Stop();
            Close();
        }

        /// <summary>Lands at once, for when the preview changes or closes mid-flight.</summary>
        public void Cancel()
        {
            _done = null;
            Finish();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            _timer.Dispose();
            _surface.Dispose();
            if (_image != null) { _image.Dispose(); _image = null; }
        }
    }
}

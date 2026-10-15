using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Shelf
{
    public partial class DockForm
    {
        // ----------------------------------------------------------
        // Rendering
        // ----------------------------------------------------------

        /// <summary>
        /// Draws the pill and pushes it to the layered window. Call this instead
        /// of Invalidate() whenever the visual state changes.
        /// </summary>
        void Render()
        {
            if (!IsHandleCreated || IsDisposed) return;

            int w = Width, h = Height;
            if (w < 1 || h < 1) return;

            try
            {
                using (Graphics g = _surface.Begin(w, h))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    PaintContent(g, w, h);
                }
                _surface.Present(Handle, Location, 255);
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
        }

        /// <summary>
        /// The pill as a Windows 11 taskbar button: a soft plate on hover,
        /// press and while open, the mark at the taskbar's own icon size, the
        /// accent indicator under it while the stack is open, and a dot while
        /// there are downloads you have not looked at yet.
        /// </summary>
        void PaintContent(Graphics g, int w, int h)
        {
            float s = _scale;
            bool open = IsStackOpen;

            Color plate = Color.Empty;
            if (_pressed) plate = Theme.TaskbarPressed;
            else if (open || _dropHover) plate = Theme.TaskbarActive;
            else if (_hover) plate = Theme.TaskbarHover;

            float inset = PlateInset * s;
            if (!plate.IsEmpty)
            {
                Theme.FillRounded(g, plate,
                    new RectangleF(inset, inset, w - inset * 2, h - inset * 2), PlateRadius * s);
            }

            // Pressed, the icon shrinks a little the way the real ones do.
            int iconPx = (int)Math.Round(IconPx * s);
            Bitmap logo = EnsureLogo(iconPx);
            float size = _pressed ? iconPx * 0.86f : iconPx;
            float x = (w - size) / 2f;
            float y = (h - size) / 2f + _bounceOffset;

            if (size == iconPx && _bounceOffset == 0)
                g.DrawImageUnscaled(logo, (int)Math.Round(x), (int)Math.Round(y));
            else
                g.DrawImage(logo, new RectangleF(x, y, size, size));

            if (open)
            {
                float iw = 16 * s, ih = 3 * s;
                RectangleF ind = new RectangleF((w - iw) / 2f, h - inset - ih - 1 * s, iw, ih);
                Theme.FillRounded(g, Theme.TaskbarAccent, ind, ih / 2f);
            }
            else if (_downloads.HasUnseen)
            {
                float d = 7 * s;
                float cx = (w + iconPx) / 2f - d * 0.25f;
                float cy = (h - iconPx) / 2f + d * 0.25f + _bounceOffset;
                RectangleF dot = new RectangleF(cx - d / 2f, cy - d / 2f, d, d);
                using (SolidBrush b = new SolidBrush(Theme.TaskbarAccent))
                    g.FillEllipse(b, dot);
                using (Pen ring = new Pen(Theme.LightTaskbar ? Color.FromArgb(200, 255, 255, 255) : Color.FromArgb(200, 32, 32, 32), 1.2f * s))
                    g.DrawEllipse(ring, dot);
            }
        }

        Bitmap EnsureLogo(int px)
        {
            if (_logo != null && _logo.Width == px) return _logo;
            if (_logo != null) _logo.Dispose();

            _logo = new Bitmap(px, px, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(_logo))
            {
                g.Clear(Color.Transparent);
                Logo.Draw(g, new RectangleF(0, 0, px, px));
            }
            return _logo;
        }

        // ----------------------------------------------------------
        // Bounce
        // ----------------------------------------------------------

        void StartBounce()
        {
            if (!Visible) return;
            _bounce.Reset();
            _bounce.Start();
            _anim.Start();
        }

        void StepBounce()
        {
            double t = _bounce.ElapsedMilliseconds / (double)BounceMs;
            if (t >= 1)
            {
                _anim.Stop();
                _bounce.Reset();
                _bounceOffset = 0;
                Render();
                return;
            }

            // Two hops, the second smaller: |sin| for the hops, a decaying
            // envelope so it settles instead of stopping dead.
            double hop = Math.Abs(Math.Sin(t * Math.PI * 2));
            double decay = Math.Pow(1 - t, 1.6);
            _bounceOffset = -(float)(7 * _scale * hop * decay);
            Render();
        }
    }
}
